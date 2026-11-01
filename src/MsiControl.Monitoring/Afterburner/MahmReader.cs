using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MsiControl.Monitoring.Afterburner;

/// <summary>MAHM 共享内存中一个监控条目的快照（诊断用）。</summary>
/// <param name="SourceName">数据源名（例如 "Core clock"）。</param>
/// <param name="Units">单位（例如 "MHz"）。</param>
/// <param name="Data">最近轮询值；不可用时为 <see cref="float.MaxValue"/>。</param>
/// <param name="GpuIndex">所属 GPU 索引（全局数据源为 0xFFFFFFFF）。</param>
/// <param name="SourceId">数据源 ID。</param>
public sealed record MahmEntrySnapshot(
    string SourceName,
    string Units,
    float Data,
    uint GpuIndex,
    uint SourceId);

/// <summary>
/// MSI Afterburner 共享内存（MAHM）的进程内读取器。
/// </summary>
public sealed class MahmReader : IMahmReader
{
    /// <summary>
    /// <c>FILE_MAP_READ</c>（<c>SECTION_MAP_READ</c>）：映射对象的只读访问权。
    /// </summary>
    /// <remarks>
    /// <b>注意</b>：OpenFileMapping 的参数是访问权（FILE_MAP_*），不是页面保护常量
    /// （PAGE_*）。二者数值不同，混用会导致句柄权限与视图权限不匹配
    /// （实测 MapViewOfFile 返回错误码 5）。
    /// </remarks>
    private const uint FileMapRead = 0x0004;

    /// <summary>共享内存对象名。</summary>
    public const string MappingName = "MAHMSharedMemory";

    private readonly object _gate = new();
    private bool _probed;
    private string? _availabilityReason;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle OpenFileMapping(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        SafeFileHandle mappingHandle,
        uint desiredAccess,
        uint fileOffsetHigh,
        uint fileOffsetLow,
        IntPtr bytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

    /// <summary>
    /// 是否检测到 Afterburner 共享内存存在。
    /// </summary>
    /// <remarks>首次调用触发一次探测并缓存结果，避免每秒重复打开映射对象。</remarks>
    public bool IsAvailable(out string? reason)
    {
        lock (_gate)
        {
            if (_probed)
            {
                reason = _availabilityReason;
                return reason is null;
            }

            _probed = true;

            SafeFileHandle? mapping = null;
            try
            {
                mapping = OpenFileMapping(FileMapRead, false, MappingName);
                if (mapping.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error();
                    _availabilityReason =
                        $"未检测到 MSI Afterburner 共享内存（{MappingName}）。" +
                        $"请确认 Afterburner 正在运行且已启用 GPU 监控项。Win32 错误码 {error}。";
                    reason = _availabilityReason;
                    return false;
                }

                // 打开成功即认为接口可达；头部大小在 ReadEntries 中按实际签名校验。
                _availabilityReason = null;
                reason = null;
                return true;
            }
            finally
            {
                mapping?.Dispose();
            }
        }
    }

    /// <summary>
    /// 枚举共享内存中的全部监控条目（诊断用）。
    /// </summary>
    /// <param name="entries">成功时为全部条目快照。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    public bool TryReadAll(out IReadOnlyList<MahmEntrySnapshot> entries, out string? error)
    {
        entries = Array.Empty<MahmEntrySnapshot>();
        error = null;

        if (!IsAvailable(out error))
        {
            return false;
        }

        SafeFileHandle? mapping = null;
        IntPtr view = IntPtr.Zero;

        try
        {
            mapping = OpenFileMapping(FileMapRead, false, MappingName);
            if (mapping.IsInvalid)
            {
                error = $"打开共享内存失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            view = MapViewOfFile(mapping, FileMapRead, 0, 0, IntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                error = $"映射共享内存视图失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            var header = Marshal.PtrToStructure<MahmHeader>(view);
            if (header.Signature != MahmLayout.SignatureValid)
            {
                error = $"共享内存签名不符（0x{header.Signature:X8}）。";
                return false;
            }

            var entrySize = Marshal.SizeOf<MahmEntry>();
            if (header.EntrySize < entrySize)
            {
                error = $"条目大小 {header.EntrySize} 小于本实现预期 {entrySize}，布局不兼容。";
                return false;
            }

            var entriesBase = IntPtr.Add(view, (int)header.HeaderSize);
            var list = new List<MahmEntrySnapshot>((int)header.EntryCount);

            for (var i = 0; i < header.EntryCount; i++)
            {
                var entry = Marshal.PtrToStructure<MahmEntry>(IntPtr.Add(entriesBase, i * (int)header.EntrySize));
                list.Add(new MahmEntrySnapshot(
                    entry.SourceName,
                    entry.SourceUnits,
                    entry.Data,
                    entry.GpuIndex,
                    entry.SourceId));
            }

            entries = list;
            return true;
        }
        finally
        {
            if (view != IntPtr.Zero)
            {
                UnmapViewOfFile(view);
            }

            mapping?.Dispose();
        }
    }

    /// <summary>
    /// 读取一条指定 ID 的 GPU 监控读数。
    /// </summary>
    /// <param name="sourceId">数据源 ID（<see cref="MonitoringSourceIds"/>）。</param>
    /// <param name="gpuIndex">逻辑 GPU 索引。为 <see langword="null"/> 时取第一个 GPU 专属条目。</param>
    /// <param name="value">成功时为读数值（已按官方约定处理 <c>float.MaxValue</c> 表示不可用）。</param>
    /// <param name="sourceName">数据源名（例如 "Core clock"）。</param>
    /// <param name="units">单位（例如 "MHz"）。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    public bool TryRead(
        uint sourceId,
        uint? gpuIndex,
        out float value,
        out string? sourceName,
        out string? units,
        out string? error)
    {
        value = 0;
        sourceName = null;
        units = null;
        error = null;

        if (!IsAvailable(out error))
        {
            return false;
        }

        SafeFileHandle? mapping = null;
        IntPtr view = IntPtr.Zero;

        try
        {
            mapping = OpenFileMapping(FileMapRead, false, MappingName);
            if (mapping.IsInvalid)
            {
                error = $"打开共享内存失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            view = MapViewOfFile(mapping, FileMapRead, 0, 0, IntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                error = $"映射共享内存视图失败，Win32 错误码 {Marshal.GetLastWin32Error()}。";
                return false;
            }

            var header = Marshal.PtrToStructure<MahmHeader>(view);
            if (header.Signature != MahmLayout.SignatureValid)
            {
                error = $"共享内存签名不符（0x{header.Signature:X8}，应为 'MAHM'=0x{MahmLayout.SignatureValid:X8}），" +
                        "Afterburner 可能正在释放监控内存或版本布局已改变。";
                return false;
            }

            if (header.EntrySize < Marshal.SizeOf<MahmEntry>())
            {
                error = $"条目大小 {header.EntrySize} 小于本实现预期 {Marshal.SizeOf<MahmEntry>()}，布局不兼容。";
                return false;
            }

            var entriesBase = IntPtr.Add(view, (int)header.HeaderSize);

            for (var i = 0; i < header.EntryCount; i++)
            {
                var entryPtr = IntPtr.Add(entriesBase, i * (int)header.EntrySize);
                var entry = Marshal.PtrToStructure<MahmEntry>(entryPtr);

                if (entry.SourceId != sourceId)
                {
                    continue;
                }

                // 限定到指定 GPU；未指定时跳过全局条目（framerate 等）。
                if (gpuIndex.HasValue)
                {
                    if (entry.GpuIndex != gpuIndex.Value)
                    {
                        continue;
                    }
                }
                else if (entry.GpuIndex == 0xFFFFFFFF)
                {
                    continue;
                }

                if (entry.Data >= float.MaxValue - 1)
                {
                    error = $"Afterburner 报告「{entry.SourceName}」当前不可用（data=FLT_MAX）。";
                    return false;
                }

                value = entry.Data;
                sourceName = entry.SourceName;
                units = entry.SourceUnits;
                return true;
            }

            error = $"共享内存中未找到数据源 ID 0x{sourceId:X8}。";
            return false;
        }
        finally
        {
            if (view != IntPtr.Zero)
            {
                UnmapViewOfFile(view);
            }

            mapping?.Dispose();
        }
    }
}
