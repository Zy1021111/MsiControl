using System.Runtime.InteropServices;
using MsiControl.Core;

namespace MsiControl.Graphics;

/// <summary>
/// <c>MsiDCVarData</c> 的 GPU 字节（byte[5]）解码结果。
/// </summary>
/// <remarks>
/// <para>
/// <b>字段布局依据（2026-10-30，M-Control 3.x 逆向 + 本机实测）：</b>
/// <list type="bullet">
/// <item>bit0-1：目标模式（0=混合 MSHybrid，1=独显直连 dGPU，2=核显 UMA，3=保留）。</item>
/// <item>bit2-3：固件自报的当前模式（仅当 bit4=0 时可信；bit4=1 时由 EC 0x2E 判定）。</item>
/// <item>bit4：新 EC 切换入口可用（Feature Manager 路径）。</item>
/// <item>bit5：支持 UMA（核显）切换。</item>
/// <item>bit6：置 1 表示**不支持**独显直连（取反语义，逆向实现如此）。</item>
/// </list>
/// 本机实测 byte[5]=0x30 → 目标=混合、bit4=1、bit5=1、独显可用。
/// </para>
/// <para>
/// <b>byte[1] 是初始化标志，与显卡模式无关</b>（逆向 <c>UefiVariable.ProbeGpuModeVariables</c>
/// 的字段注释）。此前实现误写 byte[1]，导致目标模式从未真正下发给固件——这是
/// 「切换后重启无效」的根因之一。
/// </para>
/// </remarks>
/// <param name="Raw">byte[5] 原始值。</param>
public readonly record struct MsiDcGpuByte(byte Raw)
{
    /// <summary>目标模式对应的 MSI 编码（0/1/2）；保留值 3 时为 <see langword="null"/>。</summary>
    public int RequestedCode => Raw & 0x03;

    /// <summary>固件自报的当前模式编码；保留值 3 时为 <see langword="null"/>。</summary>
    public int? ReportedCurrentCode
    {
        get
        {
            var code = (Raw & 0x0C) >> 2;
            return code <= 2 ? code : null;
        }
    }

    /// <summary>是否支持 Feature Manager 新 EC 切换入口。</summary>
    public bool NewSwitchSupported => (Raw & 0x10) != 0;

    /// <summary>是否声明支持 UMA（核显）切换。</summary>
    public bool UmaSupported => (Raw & 0x20) != 0;

    /// <summary>是否声明支持独显直连（bit6 取反）。</summary>
    public bool DiscreteSupported => (Raw & 0x40) == 0;

    /// <summary>目标模式；保留值时为 <see langword="null"/>。</summary>
    public GraphicsMode? Requested => MsiGpuModeCodec.FromCode(RequestedCode);

    /// <summary>固件自报的当前模式；不可用或保留值时为 <see langword="null"/>。</summary>
    public GraphicsMode? ReportedCurrent =>
        ReportedCurrentCode is { } code ? MsiGpuModeCodec.FromCode(code) : null;

    /// <summary>把目标模式写入低位两个 bit，保留其余位。</summary>
    /// <param name="mode">目标模式。</param>
    /// <returns>写入后的 byte[5] 值。</returns>
    public byte WithRequested(GraphicsMode mode)
        => (byte)((Raw & 0xFC) | (MsiGpuModeCodec.ToCode(mode) & 0x03));
}

/// <summary>本应用 <see cref="GraphicsMode"/> 与 MSI GPU 字节编码的映射（0=混合、1=独显、2=核显）。</summary>
public static class MsiGpuModeCodec
{
    /// <summary>枚举 → MSI 编码。</summary>
    /// <param name="mode">模式。</param>
    /// <returns>0=混合、1=独显直连、2=核显。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未定义的枚举值。</exception>
    public static byte ToCode(GraphicsMode mode) => mode switch
    {
        GraphicsMode.Hybrid => 0,
        GraphicsMode.Discrete => 1,
        GraphicsMode.Integrated => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知的显卡模式")
    };

    /// <summary>MSI 编码 → 枚举；保留值 3 返回 <see langword="null"/>。</summary>
    /// <param name="code">编码。</param>
    /// <returns>对应模式；无法识别时为 <see langword="null"/>。</returns>
    public static GraphicsMode? FromCode(int code) => code switch
    {
        0 => GraphicsMode.Hybrid,
        1 => GraphicsMode.Discrete,
        2 => GraphicsMode.Integrated,
        _ => null
    };
}

/// <summary>
/// UEFI 变量 <c>MsiDCVarData</c>（GUID <c>DD96BAAF-145E-4F56-B1CF-193256298E99</c>）的访问器。
/// </summary>
/// <remarks>
/// <para>
/// <b>协议依据：</b>M-Control 3.x（<c>MControl.Core.MsiGpuModeController</c> 与
/// <c>EcIoInterface.SetGpuMode</c>，逆向）：
/// 该变量的 <c>byte[5]</c> 是 GPU 字节，目标模式位于 bit0-1，其余位（bit2-7）必须原样保留。
/// 原 <c>byte[1]</c> 为初始化标志，与显卡模式无关。
/// 本机实测（2026-10-30）：变量 20 字节，byte[5]=0x30（目标混合、新 EC 入口可用、支持 UMA 与独显）。
/// </para>
/// <para>
/// <b>写入属性：</b>先按默认属性写；失败时以 attributes=7
/// （NV | BOOTSERVICE_ACCESS | RUNTIME_ACCESS）重试——逆向实现的同一回退策略。
/// </para>
/// <para>
/// <b>权限：</b>读写均要求进程持有 <c>SeSystemEnvironmentPrivilege</c>（管理员）；
/// 首次访问时尝试启用。失败一律以返回值如实上报，不静默降级。
/// </para>
/// </remarks>
public sealed class MsiDcUefiVariable : IMsiDcVariable
{
    /// <summary>变量名。</summary>
    public const string VariableName = "MsiDCVarData";

    /// <summary>变量 GUID（含花括号形式）。</summary>
    public const string VariableGuid = "{DD96BAAF-145E-4F56-B1CF-193256298E99}";

    /// <summary>GPU 字节在变量数据中的偏移。</summary>
    public const int GpuByteOffset = 5;

    /// <summary>变量最小长度（含 GPU 字节）。</summary>
    public const int MinimumLength = GpuByteOffset + 1;

    private const uint EfiVariableAttributes = 7;

    private static readonly object PrivilegeGate = new();
    private static bool _privilegeAttempted;

    /// <summary>读取 GPU 字节（byte[5]）。</summary>
    /// <param name="gpu">解码后的 GPU 字节。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>变量存在且长度足够时返回 <see langword="true"/>。</returns>
    public bool TryRead(out MsiDcGpuByte gpu, out string? error)
    {
        gpu = default;
        error = null;

        EnsurePrivilege();

        var buffer = new byte[64];
        var result = GetFirmwareEnvironmentVariable(VariableName, VariableGuid, buffer, (uint)buffer.Length);
        if (result == 0)
        {
            var win32 = Marshal.GetLastWin32Error();
            error = win32 switch
            {
                203 => $"UEFI 变量 {VariableName} 不存在：本机固件不含该记录，切换将只经 EC 通道（不写 UEFI 目标）。",
                1314 => "读取 UEFI 变量需要管理员权限（SeSystemEnvironmentPrivilege 未持有）。",
                _ => $"读取 UEFI 变量 {VariableName} 失败（Win32 错误 {win32}）。"
            };
            return false;
        }

        if (result < MinimumLength)
        {
            error = $"UEFI 变量 {VariableName} 长度异常（{result} 字节，无法读取 byte[{GpuByteOffset}]）。";
            return false;
        }

        gpu = new MsiDcGpuByte(buffer[GpuByteOffset]);
        return true;
    }

    /// <summary>只更新 byte[5] 的目标模式位（bit0-1），其余字节与位原样保留。</summary>
    /// <param name="mode">目标模式。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>是否写入成功。</returns>
    public bool TryWrite(GraphicsMode mode, out string? error)
    {
        error = null;

        EnsurePrivilege();

        var buffer = new byte[64];
        var result = GetFirmwareEnvironmentVariable(VariableName, VariableGuid, buffer, (uint)buffer.Length);
        if (result == 0)
        {
            var win32 = Marshal.GetLastWin32Error();
            error = win32 switch
            {
                203 => $"UEFI 变量 {VariableName} 不存在，跳过 UEFI 写入（EC 通道仍可切换）。",
                1314 => "写入 UEFI 变量需要管理员权限（SeSystemEnvironmentPrivilege 未持有）。",
                _ => $"读取 UEFI 变量 {VariableName} 失败（Win32 错误 {win32}），跳过 UEFI 写入。"
            };
            return false;
        }

        if (result < MinimumLength)
        {
            error = $"UEFI 变量 {VariableName} 长度异常（{result} 字节），拒绝写入。";
            return false;
        }

        var before = new MsiDcGpuByte(buffer[GpuByteOffset]);
        buffer[GpuByteOffset] = before.WithRequested(mode);

        if (SetFirmwareEnvironmentVariable(VariableName, VariableGuid, buffer, result))
        {
            return true;
        }

        var firstError = Marshal.GetLastWin32Error();
        if (SetFirmwareEnvironmentVariableEx(VariableName, VariableGuid, buffer, result, EfiVariableAttributes))
        {
            return true;
        }

        error = $"写入 UEFI 变量 {VariableName} 失败（Win32 错误 {firstError}/{Marshal.GetLastWin32Error()}）。";
        return false;
    }

    private static void EnsurePrivilege()
    {
        lock (PrivilegeGate)
        {
            if (_privilegeAttempted)
            {
                return;
            }

            _ = EnablePrivilege("SeSystemEnvironmentPrivilege");
            _privilegeAttempted = true;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariable(
        string lpName, string lpGuid, byte[] pBuffer, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFirmwareEnvironmentVariable(
        string lpName, string lpGuid, byte[] pValue, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetFirmwareEnvironmentVariableEx(
        string lpName, string lpGuid, byte[] pValue, uint nSize, uint dwAttributes);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(
        string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle,
        bool DisableAllPrivileges,
        ref TokenPrivileges NewState,
        uint BufferLength,
        IntPtr PreviousState,
        IntPtr ReturnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    private static bool EnablePrivilege(string privilegeName)
    {
        const uint TokenAdjustPrivileges = 0x0020;
        const uint TokenQuery = 0x0008;
        const uint SePrivilegeEnabled = 0x00000002;

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return false;
        }

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out var luid))
            {
                return false;
            }

            var state = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };
            return AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }
}
