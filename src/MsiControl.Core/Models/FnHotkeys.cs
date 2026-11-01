namespace MsiControl.Core.Models;

/// <summary>
/// Fn 组合键对应的功能动作。
/// </summary>
/// <remarks>
/// <para>
/// <b>本枚举只描述「刚按下哪个键」，不表示动作已经完成。</b>固件是否顺带执行了动作，
/// 要按项区分（2026-10-31 实测修正，HWE §32）：
/// </para>
/// <list type="bullet">
/// <item>
/// <b>固件/系统已执行</b>：<see cref="SpeakerMute"/>、<see cref="VolumeDown"/>、
/// <see cref="VolumeUp"/>、<see cref="BrightnessDown"/>、<see cref="BrightnessUp"/>——
/// 这些键会产生标准多媒体虚拟键（如 <c>VK_VOLUME_MUTE</c>），由 Windows 原生处理，
/// 应用只能读回状态、不能再执行一次（否则双倍步进）。
/// </item>
/// <item>
/// <b>固件不执行，必须由应用执行</b>：<see cref="MicrophoneMute"/>、<see cref="Touchpad"/>、
/// <see cref="PerformanceStage"/>、<see cref="BluetoothToggle"/>——固件只上报事件，
/// 不做任何状态变更；这也正是 M-Control 自己调用
/// <c>HandleMicrophoneMuteShortcut</c>、<c>SetTouchpadEnabled</c>、
/// <c>ToggleScenarioModeFromShortcut</c> 的原因。
/// </item>
/// </list>
/// <para>
/// 码值来源与归属见 HWE §29（M-Control 1.4.7 实测码表）。
/// </para>
/// </remarks>
public enum FnHotkeyAction
{
    /// <summary>无法识别的码值。只能触发状态刷新，不得猜测具体功能。</summary>
    Unknown = 0,

    /// <summary>扬声器静音（F1）。</summary>
    SpeakerMute = 1,

    /// <summary>音量降低（F2）。</summary>
    VolumeDown = 2,

    /// <summary>音量提高（F3）。</summary>
    VolumeUp = 3,

    /// <summary>触控板切换（F4）。</summary>
    Touchpad = 4,

    /// <summary>麦克风静音（F5）。</summary>
    MicrophoneMute = 5,

    /// <summary>蓝牙设置页（F6）。</summary>
    BluetoothSettings = 6,

    /// <summary>性能情景模式（F7）。</summary>
    PerformanceStage = 7,

    /// <summary>键盘背光级别（F8）。</summary>
    KeyboardBacklight = 8,

    /// <summary>屏幕亮度降低（F9）。</summary>
    BrightnessDown = 9,

    /// <summary>屏幕亮度提高（F10）。</summary>
    BrightnessUp = 10,

    /// <summary>显示输出切换（F11）。</summary>
    DisplayOutput = 11,

    /// <summary>Fn Lock 切换（Fn+Esc）。</summary>
    FnLock = 12,

    /// <summary>蓝牙射频开关切换（F6）。</summary>
    /// <remarks>
    /// 与 <see cref="BluetoothSettings"/> 分开：前者是「切换射频」，后者是「打开设置页」。
    /// 两者对应的 WMI 码值不同，不得混用。
    /// </remarks>
    BluetoothToggle = 13
}

/// <summary>
/// 一次 Fn 热键事件。
/// </summary>
/// <param name="Action">归一化后的动作。</param>
/// <param name="RawCode">原始事件码，保留用于诊断与后续补码表。</param>
/// <param name="Source">事件来源标识（WMI 事件类名等）。</param>
public sealed record FnHotkeyEvent(FnHotkeyAction Action, int RawCode, string Source)
{
    /// <summary>
    /// 构造事件。
    /// </summary>
    /// <param name="action">动作。</param>
    /// <param name="rawCode">原始码值。</param>
    /// <param name="source">来源说明。</param>
    /// <exception cref="ArgumentException">来源说明为空。</exception>
    public static FnHotkeyEvent Create(FnHotkeyAction action, int rawCode, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new FnHotkeyEvent(action, rawCode, source);
    }
}
