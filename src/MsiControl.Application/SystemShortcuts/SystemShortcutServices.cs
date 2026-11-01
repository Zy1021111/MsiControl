using MsiControl.Core.Interfaces;
using MsiControl.Hardware.Msi.Hotkeys;
using MsiControl.Hardware.Msi.SystemControl;

namespace MsiControl.Application.SystemShortcuts;

/// <summary>
/// 系统快捷功能的运行时聚合（音量/麦克风、亮度、触控板、设置页、Fn 事件源）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么放在应用层</b>：桌面壳按 design.md §13.1 只消费接口、不接触硬件实现
/// （<c>Desktop → Application → Core ← Hardware.Msi</c>）。由应用层创建这些实现，
/// 桌面壳只持有接口，分层才不会被绕过。
/// </para>
/// <para>
/// 这些对象只提供「技术通道」，是否允许调用由调用点的能力键门控决定；
/// 未适配机型上界面应说明「无证据、已禁用」，而不是静默失败。
/// </para>
/// </remarks>
public sealed class SystemShortcutServices : IDisposable
{
    private bool _disposed;
    private readonly IDisposable? _disposableDeviceControl;
    private readonly SystemKeyHookChannel _keyboardChannel;

    private SystemShortcutServices(
        IAudioEndpointControl audio,
        IDisplayBrightnessControl brightness,
        IPointingDeviceControl touchpad,
        ISystemShortcutLauncher launcher,
        IBluetoothRadioControl bluetooth,
        IFnHotkeySource fnHotkeys,
        SystemDeviceControls devices,
        SystemKeyHookChannel keyboardChannel,
        IDisposable? disposableDeviceControl = null)
    {
        Audio = audio ?? throw new ArgumentNullException(nameof(audio));
        Brightness = brightness ?? throw new ArgumentNullException(nameof(brightness));
        Touchpad = touchpad ?? throw new ArgumentNullException(nameof(touchpad));
        Launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        Bluetooth = bluetooth ?? throw new ArgumentNullException(nameof(bluetooth));
        FnHotkeys = fnHotkeys ?? throw new ArgumentNullException(nameof(fnHotkeys));
        Devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _keyboardChannel = keyboardChannel ?? throw new ArgumentNullException(nameof(keyboardChannel));
        _disposableDeviceControl = disposableDeviceControl;
    }

    /// <summary>扬声器音量与静音（Core Audio 默认播放端点）。</summary>
    public IAudioEndpointControl Audio { get; }

    /// <summary>内屏亮度。</summary>
    public IDisplayBrightnessControl Brightness { get; }

    /// <summary>触控板开关（Ctrl+Win+F24 固件路径）。</summary>
    public IPointingDeviceControl Touchpad { get; }

    /// <summary>系统设置页启动器。</summary>
    public ISystemShortcutLauncher Launcher { get; }

    /// <summary>蓝牙射频开关（WinRT Radio，与系统快速设置同一状态源）。</summary>
    public IBluetoothRadioControl Bluetooth { get; }

    /// <summary>Fn 组合键事件源。</summary>
    public IFnHotkeySource FnHotkeys { get; }

    /// <summary>
    /// 设备控制包（键盘背光、Win/Fn 交换、USB 共享供电、显示省电、Win 键屏蔽）。
    /// </summary>
    /// <remarks>
    /// 这些能力走 MSI ACPI Data 命令（HWE §31），与本类的系统接口不同源；
    /// 打包在这里是为了让桌面壳只需要认识一个「系统快捷服务」对象。
    /// </remarks>
    public SystemDeviceControls Devices { get; }

    /// <summary>
    /// 本类负责创建的 Fn 键输入通道（键盘虚拟键）。
    /// </summary>
    /// <remarks>
    /// EC 热键码通道由 <c>ApplicationHost</c> 装配（它持有唯一的 EC 访问器），
    /// 不在这里重复创建——两套 EC 通道会互相争抢端口事务。
    /// </remarks>
    public IFnKeyChannel KeyChannel => _keyboardChannel;

    /// <summary>
    /// 装配真实实现。
    /// </summary>
    /// <remarks>
    /// 全部走 Windows 提供的接口，不涉及 EC 写入，因此不依赖 PawnIO 通道是否可用。
    /// </remarks>
    public static SystemShortcutServices Create()
    {
        // Win 键屏蔽与 Fn 键观察都是进程内低级键盘钩子，必须随宿主释放，
        // 且都必须在 UI 线程安装（钩子依赖安装线程的消息泵）。
        var windowsKeyLock = new WindowsKeyLock();

        return new SystemShortcutServices(
            new CoreAudioEndpointControl(),
            new WmiMonitorBrightnessControl(),
            new MsiTouchpadToggle(),
            new WindowsSettingsLauncher(),
            new WindowsBluetoothRadio(),
            new MsiWmiHotkeySource(),
            new SystemDeviceControls(
                keyboardBacklight: new Ms15M3KeyboardBacklight(),
                winFnSwap: new Ms15M3WinFnSwap(),
                usbPowerShare: new Ms15M3UsbPowerShare(),
                displayPowerSaver: new DisplayRefreshRatePowerSaver(),
                windowsKeyLock: windowsKeyLock),
            new SystemKeyHookChannel(),
            windowsKeyLock);
    }

    /// <summary>
    /// 以注入的部件构造（供测试替换真实硬件依赖）。
    /// </summary>
    public static SystemShortcutServices CreateForTest(
        IAudioEndpointControl audio,
        IDisplayBrightnessControl brightness,
        IPointingDeviceControl touchpad,
        ISystemShortcutLauncher launcher,
        IFnHotkeySource fnHotkeys,
        SystemDeviceControls? devices = null,
        IBluetoothRadioControl? bluetooth = null,
        SystemKeyHookChannel? keyboardChannel = null)
        => new(
            audio,
            brightness,
            touchpad,
            launcher,
            bluetooth ?? new WindowsBluetoothRadio(),
            fnHotkeys,
            devices ?? new SystemDeviceControls(),
            keyboardChannel ?? new SystemKeyHookChannel());

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            FnHotkeys.Dispose();
        }
        catch (Exception)
        {
            // 释放订阅失败不应影响退出流程。
        }

        try
        {
            _keyboardChannel.Dispose();
        }
        catch (Exception)
        {
            // 钩子卸载失败不应阻塞退出。
        }

        try
        {
            _disposableDeviceControl?.Dispose();
        }
        catch (Exception)
        {
            // 钩子卸载失败不应阻塞退出。
        }
    }
}
