namespace MsiControl.Core.Interfaces;

/// <summary>
/// 设备控制相关的运行时对象集合。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>FanCurveServices</c>、<c>SystemShortcutRuntime</c> 同一思路：把一组相关实现
/// 打包成一个引用交给桌面壳，避免 <c>ShellContext</c> 的构造参数无限膨胀，
/// 也让窗口只需要认识「设备控制包」这一个概念。
/// </para>
/// <para>
/// <b>全部成员可空</b>：未适配机型或装配缺项时对应接口为 <see langword="null"/>，
/// 界面必须显示「未接入」而不是假装开关可用（设计定稿 §10）。
/// </para>
/// </remarks>
public sealed class SystemDeviceControls
{
    /// <summary>构造集合。所有成员可选。</summary>
    /// <param name="keyboardBacklight">键盘背光总开关。</param>
    /// <param name="winFnSwap">Win/Fn 交换。</param>
    /// <param name="usbPowerShare">USB 共享供电。</param>
    /// <param name="displayPowerSaver">显示省电（刷新率切换）。</param>
    /// <param name="windowsKeyLock">Win 键屏蔽（进程内钩子）。</param>
    public SystemDeviceControls(
        IKeyboardBacklightPowerControl? keyboardBacklight = null,
        IWinFnSwapControl? winFnSwap = null,
        IUsbPowerShareControl? usbPowerShare = null,
        IDisplayRefreshRatePowerSaver? displayPowerSaver = null,
        IWindowsKeyLock? windowsKeyLock = null)
    {
        KeyboardBacklight = keyboardBacklight;
        WinFnSwap = winFnSwap;
        UsbPowerShare = usbPowerShare;
        DisplayPowerSaver = displayPowerSaver;
        WindowsKeyLock = windowsKeyLock;
    }

    /// <summary>键盘背光总开关。</summary>
    public IKeyboardBacklightPowerControl? KeyboardBacklight { get; }

    /// <summary>Win/Fn 交换。</summary>
    public IWinFnSwapControl? WinFnSwap { get; }

    /// <summary>USB 关机共享供电。</summary>
    public IUsbPowerShareControl? UsbPowerShare { get; }

    /// <summary>显示省电（刷新率切换）。</summary>
    public IDisplayRefreshRatePowerSaver? DisplayPowerSaver { get; }

    /// <summary>Win 键屏蔽。</summary>
    public IWindowsKeyLock? WindowsKeyLock { get; }
}
