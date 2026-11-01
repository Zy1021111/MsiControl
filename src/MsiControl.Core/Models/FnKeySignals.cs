namespace MsiControl.Core.Models;

/// <summary>
/// 一条 Fn 键的原始信号（来自某条输入通道）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="FnHotkeyEvent"/> 的区别：后者是「已经归一化成动作」的结论，
/// 本记录保留**通道 + 原始码**，用于在码表尚未建立时如实记录、事后补表。
/// </para>
/// <para>
/// 本机（MS-15M3）实测有三条互不重叠的通道，同一个物理键可能只走其中一条：
/// </para>
/// <list type="bullet">
/// <item><see cref="Channels.Wmi"/>：<c>MSI_Event</c> 事件码（F5=25、F7=29、F6=120）。</item>
/// <item><see cref="Channels.Ec"/>：EC 寄存器 <c>0xC0</c> 的热键码（M-Control 采样同一寄存器）。</item>
/// <item><see cref="Channels.Keyboard"/>：低级键盘钩子看到的虚拟键（F1 实测为 <c>VK_VOLUME_MUTE</c>）。</item>
/// </list>
/// </remarks>
/// <param name="Channel">通道标识，取值见 <see cref="Channels"/>。</param>
/// <param name="Code">该通道内的原始码值。</param>
/// <param name="Description">原始码的可读描述（虚拟键名等），未知时为十六进制。</param>
public sealed record FnKeySignal(string Channel, int Code, string Description)
{
    /// <summary>
    /// 通道标识常量。
    /// </summary>
    public static class Channels
    {
        /// <summary>ACPI WMI 事件（<c>MSI_Event</c> 等）。</summary>
        public const string Wmi = "wmi";

        /// <summary>EC 热键码寄存器 <c>0xC0</c>（只读轮询）。</summary>
        public const string Ec = "ec";

        /// <summary>低级键盘钩子（虚拟键）。</summary>
        public const string Keyboard = "keyboard";
    }

    /// <summary>构造一条信号。</summary>
    /// <param name="channel">通道标识。</param>
    /// <param name="code">原始码值。</param>
    /// <param name="description">可读描述。</param>
    /// <exception cref="ArgumentException">通道标识为空。</exception>
    public static FnKeySignal Create(string channel, int code, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        return new FnKeySignal(
            channel,
            code,
            string.IsNullOrWhiteSpace(description) ? $"0x{code:X}" : description);
    }

    /// <summary>诊断用的单行描述。</summary>
    public string Describe() => $"{Channel}:{Description}(0x{Code:X})";
}
