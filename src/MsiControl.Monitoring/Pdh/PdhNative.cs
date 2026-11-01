using System.Runtime.InteropServices;

namespace MsiControl.Monitoring.Pdh;

/// <summary>PDH 格式化的计数器值（<c>PDH_FMT_COUNTERVALUE</c>）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValue
{
    /// <summary>该项的状态码（<c>CStatus</c>）。</summary>
    public uint CStatus;

    /// <summary>补齐 8 字节对齐。</summary>
    public uint Padding;

    /// <summary>以 <c>PDH_FMT_DOUBLE</c> 格式读取时的数值。</summary>
    public double DoubleValue;
}

/// <summary>PDH 格式化计数器数组项（<c>PDH_FMT_COUNTERVALUE_ITEM_W</c>）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PdhFmtCounterValueItem
{
    /// <summary>实例名（<c>LPWSTR</c>）。</summary>
    public IntPtr Name;

    /// <summary>格式化值。</summary>
    public PdhFmtCounterValue Value;
}

/// <summary>
/// PDH（Performance Data Helper，<c>pdh.dll</c>）原生入口。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 PDH 而不是 <c>Win32_PerfFormattedData_*</c>（实机依据 2026-10-16）：</b>
/// WMI 的性能计数类每次访问会等待提供程序刷新，实机实测约 **270 ms 阻塞**（本机 26 个处理器实例）。
/// PDH 是同一套计数器的原生低开销通道，热路径单次读取在亚毫秒级，是 LibreHardwareMonitor /
/// HWiNFO 等工具的标准做法，且全程进程内调用、不创建外部进程（任务书 §13.4）。
/// </para>
/// <para>
/// <b>计数器名用英文：</b><c>PdhAddEnglishCounterW</c>（Vista 起可用）保证与系统语言无关，
/// 不依赖本地化计数器名。
/// </para>
/// <para>
/// <b>只读：</b>本类型只读取计数器，不写入任何配置。
/// </para>
/// </remarks>
internal static class PdhNative
{
    /// <summary>以 double 返回。</summary>
    public const uint FmtDouble = 0x200;

    /// <summary>操作成功。</summary>
    public const uint ErrorSuccess = 0;

    /// <summary>计数器值有效（<c>PDH_CSTATUS_VALID_DATA</c>）。</summary>
    public const uint CStatusValidData = 0;

    /// <summary>计数器值有效且为新数据（<c>PDH_CSTATUS_NEW_DATA</c>）。</summary>
    public const uint CStatusNewData = 1;

    /// <summary>计数器值尚未可用（需要两次采样，<c>PDH_CSTATUS_INVALID_DATA</c>）。</summary>
    public const uint CStatusInvalidData = 0xC0000BBA;

    /// <summary>计数器不存在。</summary>
    public const uint CStatusNoCounter = 0xC0000BB9;

    /// <summary>对象不存在。</summary>
    public const uint CStatusNoObject = 0xC0000BB8;

    /// <summary>缓冲区不足，需按返回大小重试。</summary>
    public const uint MoreData = 0x800007D2;

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint PdhOpenQueryW(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter,
        uint format,
        ref uint bufferSize,
        out uint itemCount,
        byte[] buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhCloseQuery(IntPtr query);

    /// <summary>把 PDH 返回码转成可读文本。</summary>
    public static string Describe(uint code)
    {
        var name = code switch
        {
            ErrorSuccess => "PDH 成功",
            CStatusNewData => "PDH_CSTATUS_NEW_DATA",
            CStatusInvalidData => "PDH_CSTATUS_INVALID_DATA",
            CStatusNoCounter => "PDH_CSTATUS_NO_COUNTER",
            CStatusNoObject => "PDH_CSTATUS_NO_OBJECT",
            MoreData => "PDH_MORE_DATA",
            _ => $"0x{code:X8}"
        };

        return $"{name}（0x{code:X8}）";
    }
}
