using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 音频端点（播放或录音）的静音与音量控制。
/// </summary>
/// <remarks>
/// <para>
/// 实现基于 Windows Core Audio 的默认端点（<c>IMMDeviceEnumerator</c> →
/// <c>IAudioEndpointVolume</c>），与系统音量合成器读写的是同一份状态，
/// 因此不存在「应用侧假状态」的可能。
/// </para>
/// <para>
/// <b>为什么复用系统接口而不是模拟按键</b>：Fn+F1/F2/F3 已由固件直接执行，
/// 软件再去注入一次音量键会造成双倍步进。本接口只服务于「应用内等效命令」，
/// 以及界面读取真实状态。
/// </para>
/// </remarks>
public interface IAudioEndpointControl
{
    /// <summary>
    /// 读取默认端点的静音与音量状态。
    /// </summary>
    /// <param name="kind">端点方向。</param>
    /// <returns>读取失败时返回 <see langword="null"/>，调用方不得以默认值代替。</returns>
    AudioEndpointState? Read(AudioEndpointKind kind);

    /// <summary>
    /// 设置指定端点方向的静音状态，并读回校验。
    /// </summary>
    /// <param name="kind">端点方向。</param>
    /// <param name="muted">目标静音状态。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ControlResult> SetMuteAsync(AudioEndpointKind kind, bool muted, CancellationToken cancellationToken);

    /// <summary>
    /// 按系统设定的步长调整默认播放端点的音量。
    /// </summary>
    /// <param name="steps">正数增大，负数减小。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <remarks>
    /// 步长取系统「按键调整音量时」的配置，与 Fn+F2/F3 和音量键行为一致，
    /// 不自造固定百分比。
    /// </remarks>
    Task<ControlResult> StepVolumeAsync(int steps, CancellationToken cancellationToken);
}

/// <summary>
/// 内屏亮度控制。
/// </summary>
/// <remarks>
/// 只覆盖笔记本内屏面板（WMI <c>WmiMonitorBrightnessMethods</c>）。
/// 外接显示器不在此接口职责内，也无 DDC/CI 通道；实现必须如实说明这一点，
/// 不得把「内屏已调」表述为「所有屏幕已调」。
/// </remarks>
public interface IDisplayBrightnessControl
{
    /// <summary>读取当前内屏亮度百分比。无可用内屏时返回 <see langword="null"/>。</summary>
    int? Read();

    /// <summary>读取内屏支持的亮度范围与步进。无可用内屏时返回 <see langword="null"/>。</summary>
    BrightnessRange? ReadRange();

    /// <summary>设置内屏亮度百分比，并读回校验。</summary>
    Task<ControlResult> SetAsync(int percent, CancellationToken cancellationToken);
}

/// <summary>
/// 指向设备（触控板）启用状态控制。
/// </summary>
/// <remarks>
/// <para>
/// 切换走 MSI 固件提供的隐藏组合 <c>Ctrl+Win+F24</c>（与 M-Control 1.4.7 的实现路径一致，
/// 见 HWE §29），**不禁用设备节点**：禁用设备需要管理员权限、会重枚举设备、
/// 且失败时可能导致触控板无法恢复。
/// </para>
/// <para>
/// 状态以 Windows 精密触控板的官方状态位为准
/// （<c>HKCU\Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad\Status</c> 的 <c>Enabled</c>），
/// 不缓存写请求值。
/// </para>
/// </remarks>
public interface IPointingDeviceControl
{
    /// <summary>读取触控板真实启用状态。无可用状态源时返回 <see langword="null"/>。</summary>
    PointingDeviceState? Read();

    /// <summary>切换触控板启用状态，并在切换后读回校验。</summary>
    Task<ControlResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// 蓝牙射频（Radio）开关控制。
/// </summary>
/// <remarks>
/// <para>
/// 实现基于 WinRT <c>Windows.Devices.Radios</c>，与 Windows 快速设置面板读写同一份
/// 射频状态；切换后用 <c>GetRadiosAsync</c> 重新读回，成败以读回值为准。
/// </para>
/// <para>
/// <b>不用禁用设备节点冒充关闭</b>：那需要管理员权限、会重枚举整个蓝牙栈，
/// 还可能丢失配对上下文，属于「看起来关了」而非真正的射频开关。
/// </para>
/// </remarks>
public interface IBluetoothRadioControl
{
    /// <summary>读取蓝牙射频状态。没有蓝牙射频或读取失败时返回 <see langword="null"/>。</summary>
    BluetoothRadioState? Read();

    /// <summary>把蓝牙射频切换到指定状态，并读回校验。</summary>
    /// <param name="on">目标状态。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ControlResult> SetAsync(bool on, CancellationToken cancellationToken);
}

/// <summary>
/// 打开系统设置页等「非硬件写入」类快捷动作。
/// </summary>
/// <remarks>
/// 蓝牙开关走 <see cref="IBluetoothRadioControl"/>；本接口只负责「打开设置页」这类
/// 无法用 API 表达的动作，两者不互相替代。
/// </remarks>
public interface ISystemShortcutLauncher
{
    /// <summary>打开指定的系统设置页。</summary>
    Task<ControlResult> OpenAsync(SystemShortcutTarget target, CancellationToken cancellationToken);
}

/// <summary>音频端点方向。</summary>
public enum AudioEndpointKind
{
    /// <summary>播放（扬声器）。</summary>
    Playback = 0,

    /// <summary>录音（麦克风）。</summary>
    Recording = 1
}

/// <summary>系统设置页目标。</summary>
public enum SystemShortcutTarget
{
    /// <summary>蓝牙设置（<c>ms-settings:bluetooth</c>）。</summary>
    BluetoothSettings = 0
}
