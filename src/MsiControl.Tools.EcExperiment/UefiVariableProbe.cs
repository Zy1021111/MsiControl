using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MsiControl.Tools.EcExperiment;

/// <summary>
/// UEFI 变量 <c>MsiDCVarData</c> 的最小读写封装（实验工具专用）。
/// </summary>
/// <remarks>
/// <para>
/// 与产品内的 <c>MsiDcUefiVariable</c> 同源（同一变量名、同一 GUID、同一 byte[1] 语义），
/// 但独立实现：实验工具不引用 <c>MsiControl.Graphics</c>，避免把待验证的写入方
/// 与被验证的对象耦合在一起。
/// </para>
/// <para>
/// 读取/写入 UEFI 变量需要 <c>SeSystemEnvironmentPrivilege</c>；未启用时
/// <c>GetFirmwareEnvironmentVariableW</c> 返回 1314（ERROR_PRIVILEGE_NOT_HELD）。
/// </para>
/// </remarks>
internal static class UefiVariableProbe
{
    /// <summary>显卡模式目标变量名。</summary>
    public const string VariableName = "MsiDCVarData";

    /// <summary>变量 GUID（含花括号）。</summary>
    public const string VariableGuid = "{DD96BAAF-145E-4F56-B1CF-193256298E99}";

    /// <summary>GPU 字节偏移（目标模式在低两位）。</summary>
    /// <remarks>
    /// 2026-10-30 修正：旧值 1 是初始化标志，写它等于什么都没写（HWE §30）。
    /// </remarks>
    public const int ModeByteOffset = 5;

    private const int TokenAdjustPrivileges = 0x0020;
    private const int TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;

    /// <summary>启用 UEFI 环境变量特权；返回是否成功。</summary>
    public static bool TryEnablePrivilege(out string? error)
    {
        error = null;

        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            error = $"OpenProcessToken 失败（Win32 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeSystemEnvironmentPrivilege", out var luid))
            {
                error = $"LookupPrivilegeValue 失败（Win32 {Marshal.GetLastWin32Error()}）。";
                return false;
            }

            var privileges = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled }
            };

            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                error = $"AdjustTokenPrivileges 失败（Win32 {Marshal.GetLastWin32Error()}）。";
                return false;
            }

            // AdjustTokenPrivileges 在「特权不存在」时仍返回 true，必须再看 LastError。
            var lastError = Marshal.GetLastWin32Error();
            if (lastError != 0)
            {
                error = $"AdjustTokenPrivileges 部分成功（Win32 {lastError}）：特权可能不存在。";
                return false;
            }

            return true;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    /// <summary>读取变量完整内容；失败返回 <see langword="null"/> 并给出原因。</summary>
    public static byte[]? Read(out string? error)
    {
        error = null;
        var buffer = new byte[256];
        var size = GetFirmwareEnvironmentVariableW(VariableName, VariableGuid, buffer, (uint)buffer.Length);
        if (size == 0)
        {
            error = $"读取 {VariableName} 失败（Win32 {Marshal.GetLastWin32Error()}）。";
            return null;
        }

        return buffer[..(int)size];
    }

    /// <summary>只改 byte[1]（目标模式），其余字节原样写回。</summary>
    public static bool WriteMode(byte mode, out string? error)
    {
        error = null;
        var data = Read(out var readError);
        if (data is null)
        {
            error = readError;
            return false;
        }

        if (data.Length <= ModeByteOffset)
        {
            error = $"{VariableName} 长度异常（{data.Length} 字节）。";
            return false;
        }

        // 只改低两位，其余位（bit2-7 的支持位与固件自报当前态）必须原样保留。
        data[ModeByteOffset] = (byte)((data[ModeByteOffset] & 0xFC) | (mode & 0x03));
        if (!SetFirmwareEnvironmentVariableW(VariableName, VariableGuid, data, (uint)data.Length))
        {
            error = $"写入 {VariableName} 失败（Win32 {Marshal.GetLastWin32Error()}）。";
            return false;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        int bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFirmwareEnvironmentVariableW(
        string name,
        string guid,
        byte[] buffer,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFirmwareEnvironmentVariableW(
        string name,
        string guid,
        byte[] value,
        uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public LuidAndAttributes Privileges;
    }
}
