using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Presentation;
using MsiControl.Persistence;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 桌面壳的共享上下文。
/// </summary>
/// <remarks>
/// <para>
/// 正常窗口、迷你窗口、托盘与设置窗口共用同一个实例：
/// design.md §4.4 要求「两种窗口共用数据、采样和控制核心，切换窗口不重复初始化硬件」。
/// </para>
/// <para>
/// 本类型是界面层与硬件层的**唯一接缝**：能力等级、采样流、控制出口都在此处注入。
/// 它不知道数据从哪来，也不执行任何硬件访问。
/// </para>
/// </remarks>
public sealed class ShellContext : IDisposable
{
    private bool _disposed;

    /// <summary>构造上下文。</summary>
    public ShellContext(
        AppPaths paths,
        ISettingsStore settingsStore,
        IRotatingLog log,
        SettingsLoadResult settingsLoadResult,
        DeviceIdentity device,
        DeviceCapabilities capabilities,
        ISnapshotStream snapshots,
        IControlRequestSink control,
        AppSettings settings,
        IHardwareStateReader? stateReader = null,
        FanCurveServices? fanCurve = null,
        IAudioEndpointControl? audioControl = null,
        IDisplayBrightnessControl? brightnessControl = null,
        IPointingDeviceControl? touchpadControl = null,
        ISystemShortcutLauncher? systemShortcutLauncher = null,
        IBluetoothRadioControl? bluetoothControl = null,
        IFnHotkeySource? fnHotkeys = null,
        SystemDeviceControls? deviceControls = null,
        IReadOnlyList<IFnKeyChannel>? fnKeyChannels = null)
    {
        Paths = paths ?? throw new ArgumentNullException(nameof(paths));
        SettingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        Log = log ?? throw new ArgumentNullException(nameof(log));
        SettingsLoadResult = settingsLoadResult ?? throw new ArgumentNullException(nameof(settingsLoadResult));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        Snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        Control = control ?? throw new ArgumentNullException(nameof(control));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        StateReader = stateReader;
        FanCurve = fanCurve;
        AudioControl = audioControl;
        BrightnessControl = brightnessControl;
        TouchpadControl = touchpadControl;
        SystemShortcutLauncher = systemShortcutLauncher;
        BluetoothControl = bluetoothControl;
        FnHotkeys = fnHotkeys;
        DeviceControls = deviceControls ?? new SystemDeviceControls();
        FnKeyChannels = fnKeyChannels ?? Array.Empty<IFnKeyChannel>();
    }

    /// <summary>数据路径。</summary>
    public AppPaths Paths { get; }

    /// <summary>设置存储。</summary>
    public ISettingsStore SettingsStore { get; }

    /// <summary>有界轮转日志。</summary>
    public IRotatingLog Log { get; }

    /// <summary>本次设置的加载结果，含降级与修正说明。</summary>
    public SettingsLoadResult SettingsLoadResult { get; }

    /// <summary>本机设备标识。</summary>
    /// <remarks>任务 02 不自行探测硬件；未注入时为 <see cref="DeviceIdentity.Unknown"/>。</remarks>
    public DeviceIdentity Device { get; }

    /// <summary>本机能力集合。</summary>
    /// <remarks>由宿主注入，界面不自行硬编码等级（docs/接口说明.md §3.7）。</remarks>
    public DeviceCapabilities Capabilities { get; }

    /// <summary>共享采样流。</summary>
    public ISnapshotStream Snapshots { get; }

    /// <summary>控制请求出口。</summary>
    public IControlRequestSink Control { get; }

    /// <summary>
    /// 硬件状态读回源（当前风扇控制模式、实际性能档位）。
    /// </summary>
    /// <remarks>
    /// 由宿主注入。为 <see langword="null"/> 时（未适配装配）桌面壳不做状态读回，
    /// 界面保持「等待读回」的初始文案；为非空时桌面壳随采样节拍读取并推送各窗口。
    /// </remarks>
    public IHardwareStateReader? StateReader { get; }

    /// <summary>
    /// 风扇曲线功能包（软件曲线引擎 + 配置档存储）。
    /// </summary>
    /// <remarks>
    /// 由宿主注入。为 <see langword="null"/> 时（未适配装配）风扇曲线窗口
    /// 保持「待验证」禁用并说明原因；非空时窗口仍受
    /// <c>fan.curve</c> 能力门控（<see cref="Capabilities"/>）约束。
    /// </remarks>
    public FanCurveServices? FanCurve { get; }

    /// <summary>
    /// 扬声器音量与静音（Core Audio 默认播放端点）。
    /// </summary>
    /// <remarks>为 <see langword="null"/> 时等效命令一律报「未接入」，不做任何替换实现。</remarks>
    public IAudioEndpointControl? AudioControl { get; }

    /// <summary>内屏亮度。为 <see langword="null"/> 时等效命令报「未接入」。</summary>
    public IDisplayBrightnessControl? BrightnessControl { get; }

    /// <summary>触控板开关（Ctrl+Win+F24 固件路径）。</summary>
    public IPointingDeviceControl? TouchpadControl { get; }

    /// <summary>系统设置页启动器（需要时可拉起设置页）。</summary>
    public ISystemShortcutLauncher? SystemShortcutLauncher { get; }

    /// <summary>蓝牙射频开关（WinRT Radio）。为 <see langword="null"/> 时蓝牙动作报「未接入」。</summary>
    public IBluetoothRadioControl? BluetoothControl { get; }

    /// <summary>
    /// Fn 键的原始信号通道（EC 热键码 / 键盘虚拟键）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FnHotkeys"/> 互补：后者只覆盖 WMI 能上报的键（本机为 F5/F6/F7），
    /// 前者补齐音量、亮度、触控板这些不走 WMI 的键。两者可能对同一个键同时上报，
    /// 由使用方按「动作 + 时间窗」去重。
    /// </remarks>
    public IReadOnlyList<IFnKeyChannel> FnKeyChannels { get; }

    /// <summary>触控板状态在应用之外发生变化（物理 Fn+F4、Windows 设置等）时发布。</summary>
    /// <remarks>
    /// 事件载体放在壳上下文而不是桌面主上下文：设置页只认识 <see cref="ShellContext"/>，
    /// 让它为了一个勾选状态去引用主上下文会倒置依赖方向。
    /// </remarks>
    public event EventHandler? TouchpadStateChanged;

    /// <summary>由桌面主上下文发布触控板状态变化（内部接缝，界面只订阅）。</summary>
    internal void RaiseTouchpadStateChanged() => TouchpadStateChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Fn 组合键事件源。
    /// </summary>
    /// <remarks>
    /// 这是「监听」而非「接管」：事件到达时固件动作已完成，本对象只用于让界面
    /// 重新读回真实状态（HWE §28）。为 <see langword="null"/> 或订阅失败时，
    /// 界面按抽样节拍刷新即可，功能不受影响。
    /// </remarks>
    public IFnHotkeySource? FnHotkeys { get; }

    /// <summary>
    /// 设备控制包（键盘背光 / Win-Fn 交换 / USB 共享供电 / 显示省电 / Win 键屏蔽）。
    /// </summary>
    /// <remarks>
    /// 未接入的成员为 <see langword="null"/>，界面据此显示「未接入」并禁用开关；
    /// 是否允许调用仍由 <see cref="Capabilities"/> 按能力等级二次门控。
    /// </remarks>
    public SystemDeviceControls DeviceControls { get; }

    /// <summary>当前设置。</summary>
    public AppSettings Settings { get; private set; }

    /// <summary>设置发生变化。</summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    /// <summary>写入一行日志。</summary>
    public void WriteLog(LogLevel level, string category, string message)
        => Log.Write(level, category, message);

    /// <summary>
    /// 更新设置：先落盘再广播，保证界面状态与磁盘一致。
    /// </summary>
    public void UpdateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Settings = settings;
        SettingsStore.Save(settings);
        WriteLog(LogLevel.Info, "Settings", "设置已保存。");
        SettingsChanged?.Invoke(this, settings);
    }

    /// <summary>
    /// 更新窗口位置记录并落盘。
    /// </summary>
    /// <remarks>
    /// 刻意不触发 <see cref="SettingsChanged"/>：窗口位置变化与用户设置变更语义不同，
    /// 若一并广播会导致快捷键被反复重新注册。
    /// </remarks>
    public void UpdateWindowPlacements(WindowPlacement? normal, WindowPlacement? mini)
    {
        Settings = Settings with
        {
            NormalWindowPlacement = normal ?? Settings.NormalWindowPlacement,
            MiniWindowPlacement = mini ?? Settings.MiniWindowPlacement
        };

        SettingsStore.Save(Settings);
    }

    /// <summary>
    /// 保存性能档位 CPU 功耗目标并落盘。
    /// </summary>
    /// <param name="targets">五档目标快照。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>是否已写入设置文件。</returns>
    /// <remarks>
    /// <para>
    /// 与 <see cref="UpdateSettings"/> 一样「先落盘再改内存」：写失败时内存保持原值，
    /// 界面显示的配置目标与磁盘不会脱节。
    /// </para>
    /// <para>
    /// 刻意不触发 <see cref="SettingsChanged"/>：该事件的含义是「需要重新注册快捷键/重建采样」，
    /// 档位功耗目标与两者无关。
    /// </para>
    /// </remarks>
    public bool TryUpdateStageTargets(IReadOnlyList<StagePowerTarget> targets, out string? error)
    {
        ArgumentNullException.ThrowIfNull(targets);

        var next = Settings with { StageTargets = targets };
        try
        {
            SettingsStore.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            WriteLog(LogLevel.Warning, "Settings", $"性能档位功耗目标保存失败：{ex.Message}");
            return false;
        }

        Settings = next;
        error = null;
        WriteLog(
            LogLevel.Info,
            "Settings",
            "性能档位功耗目标已保存：" + string.Join("，", targets.Select(t => t.ToString())) + "。");
        return true;
    }

    /// <summary>
    /// 本会话内是否已经由本软件写过性能场景（含启动恢复与用户手动确认）。
    /// </summary>
    /// <remarks>
    /// 退出时的「恢复固件默认场景」只在本标记为真时执行：
    /// 若本会话从未改过性能模式，退出时再写一次 0xC1 就是对固件状态的无谓改动。
    /// </remarks>
    public bool SessionStageApplied { get; private set; }

    /// <summary>标记本会话已写过性能场景（启动恢复成功后调用）。</summary>
    public void MarkSessionStageApplied() => SessionStageApplied = true;

    /// <summary>
    /// 记录用户手动确认的档位并落盘。
    /// </summary>
    /// <param name="stage">已写入并读回确认的档位。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>是否已写入设置文件。</returns>
    /// <remarks>
    /// <para>
    /// 与 <see cref="UpdateSettings"/> 一样「先落盘再改内存」：写失败时内存保持原值，
    /// 下次启动仍按旧记录恢复，不出现「界面记住了、磁盘没记住」。
    /// </para>
    /// <para>
    /// 刻意不触发 <see cref="SettingsChanged"/>：该事件的含义是「需要重新注册快捷键/重建采样」，
    /// 档位记录与两者无关（与 <see cref="TryUpdateStageTargets"/> 同理）。
    /// </para>
    /// </remarks>
    public bool TryRecordConfirmedStage(PerformanceStage stage, out string? error)
    {
        var next = Settings with { LastConfirmedStage = stage };
        try
        {
            SettingsStore.Save(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            WriteLog(LogLevel.Warning, "Settings", $"上次确认档位保存失败：{ex.Message}");
            return false;
        }

        Settings = next;
        SessionStageApplied = true;
        error = null;
        WriteLog(
            LogLevel.Info,
            "Settings",
            $"已记录用户确认的档位：{ModeFormatter.StageText(stage)}（下次启动按此恢复；退出时先恢复固件基准场景）。");
        return true;
    }

    /// <summary>该能力键是否允许写入。</summary>    public bool IsWriteEnabled(string key) => Capabilities.AllowsWrite(key);

    /// <summary>该能力键是否允许只读展示。</summary>
    public bool IsReadEnabled(string key) => Capabilities.AllowsRead(key);

    /// <summary>描述一个能力入口。</summary>
    public CapabilityEntry DescribeCapability(string key, bool requiresWrite)
        => CapabilityPresenter.Describe(Capabilities, key, requiresWrite);

    /// <summary>
    /// 把一次控制结果逐项写入日志。
    /// </summary>
    /// <remarks>
    /// 任务书 §8.5 要求逐项记录请求值、接口结果与读回值；日志是唯一持久化的追溯处。
    /// </remarks>
    public void LogControlResult(ControlResult result, string operation)
    {
        ArgumentNullException.ThrowIfNull(result);

        WriteLog(
            result.IsSuccess ? LogLevel.Info : LogLevel.Warning,
            "Control",
            $"{operation} 整体结果：{result.Status}，共 {result.Settings.Count} 项。");

        foreach (var setting in result.Settings)
        {
            WriteLog(
                setting.Status == OperationStatus.Succeeded ? LogLevel.Info : LogLevel.Warning,
                "Control",
                $"  · {setting.Setting}：请求={setting.Requested ?? "-"}；读回={setting.ReadBack ?? "-"}；" +
                $"状态={setting.Status}；确认依据={setting.ConfirmationBasis ?? "-"}；消息={setting.Message ?? "-"}");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Snapshots.Dispose();
        Log.Write(LogLevel.Info, "Shell", "桌面壳已释放。");
    }
}
