using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Hotkeys;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Shell;
using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Tray;
using MsiControl.Desktop.Windows;
using MsiControl.Desktop.Windows.Osd;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop;

/// <summary>
/// 桌面壳的宿主上下文：管理两种窗口、托盘、快捷键与退出语义。
/// </summary>
/// <remarks>
/// <para>
/// 关键约定（任务书 §10、§13.4）：
/// <list type="bullet">
/// <item>两种窗口共用同一份采样与控制核心，切换窗口不重复初始化硬件。</item>
/// <item>默认只显示一个窗口。</item>
/// <item>「关闭到托盘」与「完全退出」是两个不同操作，且<b>不以隐藏进程冒充完全退出</b>。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class DesktopApplicationContext : ApplicationContext
{
    private const string ActionToggleWindow = "window.toggle";
    private const string ActionToggleFullSpeed = "fan.fullspeed";

    /// <summary>退出前恢复默认性能场景的最长等待时间。</summary>
    private static readonly TimeSpan ExitStageRestoreTimeout = TimeSpan.FromSeconds(8);

    // Fn 九项功能的「等效命令」。Fn 键本身由 EC 固件直接执行、软件无法接管（HWE §28），
    // 这些动作提供的是「不按 Fn 也能做到同一件事」的入口，并让界面读回真实状态。
    private const string ActionSpeakerMute = "audio.speakerMute";
    private const string ActionVolumeDown = "audio.volumeDown";
    private const string ActionVolumeUp = "audio.volumeUp";
    private const string ActionBrightnessDown = "display.brightnessDown";
    private const string ActionBrightnessUp = "display.brightnessUp";
    private const string ActionMicrophoneMute = "audio.microphoneMute";
    private const string ActionToggleTouchpad = "input.touchpad";

    /// <summary>
    /// 触控板「已被系统切换」的观察动作（VK_F24 信号）。
    /// </summary>
    /// <remarks>
    /// <b>这个动作绝不能再执行切换</b>：物理 Fn+F4 由固件合成真实 Ctrl+Win+F24（2026-10-31
    /// 键盘层取证，flags 无 INJECTED 位），Windows 自己完成切换；应用只负责读回状态并反馈。
    /// 若在这里再切一次，用户按一下会被切两次，等于没切。
    /// </remarks>
    private const string ActionTouchpadObserved = "touchpad.observed";
    private const string ActionToggleBluetooth = "system.bluetoothToggle";
    private const string ActionCycleStage = "performance.cycleStage";

    private readonly ShellContext _context;
    private readonly MainForm _mainForm;
    private readonly MiniForm _miniForm;
    private readonly TrayHost _tray;
    private readonly HotkeyManager _hotkeys = new();
    private readonly OsdOverlayForm _osd = new();
    private readonly IDisposable _themeSubscription;
    private readonly SynchronizationContext _sync;

    /// <summary>Fn 键线路去重窗口：同一动作在此时间内只执行一次。</summary>
    private static readonly TimeSpan FnSignalDedupeWindow = TimeSpan.FromMilliseconds(700);

    /// <summary>等待「系统/固件是否已执行」的观察窗口。</summary>
    private static readonly TimeSpan ExternalEffectWait = TimeSpan.FromMilliseconds(180);

    private readonly Dictionary<string, DateTime> _lastFnSignalUtc = new();
    private readonly object _fnSignalLock = new();

    /// <summary>上一次已知的触控板状态。用于区分「固件已切换」与「需要本程序切换」。</summary>
    private bool? _lastKnownTouchpad;

    /// <summary>触控板状态轮询间隔。Windows 切换完成后注册表立即变化，2 秒足够跟手。</summary>
    private static readonly TimeSpan TouchpadWatchInterval = TimeSpan.FromSeconds(2);

    private System.Threading.Timer? _touchpadWatchTimer;

    private SettingsForm? _settingsForm;
    private ThemeMode _themeMode;
    private WindowMode _lastWindowMode = WindowMode.Normal;
    private HotkeyBinding _lastShowHideHotkey = HotkeyBinding.Disabled();
    private HotkeyBinding _lastFullSpeedHotkey = HotkeyBinding.Disabled();
    private bool _fullSpeedActive;
    private bool _exiting;
    private bool _disposed;
    private int _readbackInFlight;

    /// <summary>构造宿主。</summary>
    public DesktopApplicationContext(ShellContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        _themeMode = WindowsTheme.Read(out var themeNote);
        if (themeNote is not null)
        {
            _context.WriteLog(LogLevel.Warning, "Theme", themeNote);
        }

        _mainForm = new MainForm(_context);
        _miniForm = new MiniForm(_context);

        // 在窗体创建之后再取同步上下文：此时 WinForms 已安装 WindowsFormsSynchronizationContext。
        _sync = SynchronizationContext.Current ?? new SynchronizationContext();

        _mainForm.MiniRequested += (_, _) => ShowWindow(WindowMode.Mini);
        _mainForm.SettingsRequested += (_, _) => ShowSettings();
        _mainForm.ExitRequested += (_, _) => ExitApplication();

        // 迷你窗口不再提供「返回正常窗口 / 退出程序」按钮（2026-10-31 精简）：
        // 返回与退出由托盘菜单承担，窗口关闭仍按「关闭到托盘」设置走。
        _miniForm.ExitRequested += (_, _) => ExitApplication();

        _tray = new TrayHost(_context, ShowWindow, ExitApplication);
        _hotkeys.Triggered += OnHotkeyTriggered;

        RestorePlacements();
        ApplyTheme();
        ApplyHotkeys();
        ReconcileStartupRegistration();

        // 记录触控板初始状态：Fn+F4 是切换型组合，必须有一个已知起点才能判断
        // 「固件已经切过」还是「固件没做、需要本程序补上」。读不到就留空，
        // 此后第一次 Fn+F4 会直接执行切换（宁可切一次，也不假装同步）。
        _lastKnownTouchpad = _context.TouchpadControl?.Read()?.Enabled;

        // 触控板状态观察（HWE §32.7）：物理 Fn+F4 由固件合成 Ctrl+Win+F24、Windows 自己完成
        // 切换，唯一的观测面是注册表状态位。轮询它是 Fn+F4 出现反馈、设置页勾选不陈旧的
        // 前提；这也是 M-Control PollObservedMsiTouchpadState 的同款做法。
        _touchpadWatchTimer = new System.Threading.Timer(
            PollTouchpadState, null, TouchpadWatchInterval, TouchpadWatchInterval);

        _context.SettingsChanged += OnSettingsChanged;
        _themeSubscription = WindowsTheme.Subscribe(OnUserPreferenceChanged);

        // 硬件状态读回（风扇模式 EC 0xD4 / 性能档位 EC 0xD2）：随采样节拍读取并推送窗口。
        // 未注入读回源（未适配装配）时不订阅，界面保持「等待读回」的初始文案。
        if (_context.StateReader is not null)
        {
            _context.Snapshots.Updated += OnSnapshotReadback;
        }

        _context.Snapshots.Start();
        _context.WriteLog(LogLevel.Info, "Shell", "共享采样已启动。");

        StartFnHotkeyListener();

        if (ShouldStartHidden())
        {
            // 托盘已在上文创建：进程会因托盘常驻继续存活，硬件监控与快捷键照常工作。
            _context.WriteLog(LogLevel.Info, "Shell", "按设置「开机后静默驻留后台」启动：窗口保持隐藏，仅托盘可用。");
        }
        else
        {
            ShowWindow(_context.Settings.StartupWindow);
        }
    }

    /// <summary>
    /// 启动 Fn 组合键监听。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「监听」不是「接管」：事件到达时固件早已把动作做完，这里只负责让界面知道
    /// 「状态可能已经变了」，避免界面显示与硬件真实状态脱节（HWE §28）。
    /// </para>
    /// <para>
    /// 订阅失败不阻断启动，也不弹窗：Fn 键本身照常工作，只是界面不会实时跟随；
    /// 按抽样节拍的读回仍然有效。失败原因写入日志供诊断。
    /// </para>
    /// </remarks>
    private void StartFnHotkeyListener()
    {
        var source = _context.FnHotkeys;
        if (source is null)
        {
            _context.WriteLog(LogLevel.Info, "Hotkey", "未装配 Fn 热键事件源，跳过监听。");
            return;
        }

        if (!_context.Capabilities.AllowsRead(CapabilityKeys.HotkeyFn))
        {
            _context.WriteLog(LogLevel.Info, "Hotkey", "能力 hotkey.fn 未达可读等级，跳过 Fn 监听。");
            return;
        }

        source.Pressed += OnFnHotkeyPressed;
        _ = StartFnHotkeyClientAsync(source);
        StartFnKeyChannels();
    }

    /// <summary>
    /// 启动 Fn 键的原始信号通道（EC 热键码 / 键盘虚拟键）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// WMI <c>MSI_Event</c> 在本机只上报 F5/F6/F7 三个键，音量、亮度、触控板在它上面
    /// 一个事件都没有（HWE §33）。这两条通道就是为补齐剩下的键而存在的：
    /// 键盘钩子能看到 <c>VK_VOLUME_MUTE</c>／<c>VK_F20</c>，EC <c>0xC0</c> 能看到 EC 侧热键码。
    /// </para>
    /// <para>
    /// 通道启动失败不阻断启动：把原因写进日志，界面不装作「已监听」。
    /// </para>
    /// </remarks>
    private void StartFnKeyChannels()
    {
        var channels = _context.FnKeyChannels;
        if (channels.Count == 0)
        {
            _context.WriteLog(
                LogLevel.Warning,
                "Hotkey",
                "未装配 Fn 键原始信号通道：音量/亮度/触控板这些不走 WMI 的键将无法被本程序观察。");
            return;
        }

        foreach (var channel in channels)
        {
            channel.Signaled += OnFnKeySignaled;
            bool started;
            try
            {
                started = channel.Start();
            }
            catch (Exception ex)
            {
                _context.WriteLog(LogLevel.Warning, "Hotkey", $"Fn 键通道 {channel.Channel} 启动异常：{ex.Message}");
                continue;
            }

            var reason = channel.StartupError;
            _context.WriteLog(
                started ? LogLevel.Info : LogLevel.Warning,
                "Hotkey",
                started
                    ? $"Fn 键通道已启动：{channel.Channel}。"
                    : $"Fn 键通道未能启动：{channel.Channel}"
                      + (string.IsNullOrWhiteSpace(reason) ? "。" : $"；{reason}"));
        }
    }

    /// <summary>
    /// 收到一条 Fn 键原始信号：记日志 → 映射动作 → 去重 → 执行。
    /// </summary>
    /// <remarks>
    /// 未知码一律只记日志。码表不全时补表靠的正是这些日志——擅自猜测码值语义
    /// 会导致按 F9 却去开蓝牙这类错误（本机 WMI 的 120 与 EC 的 120 语义就不同）。
    /// </remarks>
    private void OnFnKeySignaled(object? sender, FnKeySignal signal)
    {
        if (_disposed)
        {
            return;
        }

        if (!TryMapFnSignal(signal, out var action, out var displayName))
        {
            _context.WriteLog(
                LogLevel.Info,
                "Hotkey",
                $"Fn 键信号：{signal.Describe()}（通道 {signal.Channel}）；码值未登记，仅记录、不执行。");
            return;
        }

        if (!TryBeginFnSignal(action))
        {
            return;
        }

        // VK_F24：固件合成的官方组合键，Windows 已完成切换（HWE §32.7）。
        // 这里只做「读回 + 反馈」，绝不重放切换；读回由观察路径统一做，避免双倍切换。
        if (action == ActionTouchpadObserved)
        {
            _context.WriteLog(
                LogLevel.Info,
                "Hotkey",
                $"Fn 键信号：{signal.Describe()} → 触控板已由系统切换，本程序读回状态并同步显示。");
            _sync.Post(_ => _ = ObserveTouchpadChangeAsync("Fn+F4（VK_F24）"), null);
            return;
        }

        _context.WriteLog(
            LogLevel.Info,
            "Hotkey",
            $"Fn 键信号：{signal.Describe()} → {displayName}。");
        _sync.Post(_ => _ = HandleFnSignalAsync(action, displayName), null);
    }

    /// <summary>
    /// 把原始信号映射为动作。
    /// </summary>
    /// <remarks>
    /// EC 码表来自 M-Control 1.4.7 的 <c>OfficialShortcut</c>（HWE §33）：
    /// 112 静音、113 音量−、114 音量+、8/131/190 触控板、116 麦克风、
    /// 117 蓝牙、118 情景模式、120 亮度−、121 亮度+。
    /// 键盘通道只用系统已定义的虚拟键，语义确定。
    /// </remarks>
    internal static bool TryMapFnSignal(FnKeySignal signal, out string action, out string displayName)
    {
        if (signal.Channel == FnKeySignal.Channels.Ec)
        {
            switch (signal.Code)
            {
                case 112: action = ActionSpeakerMute; displayName = "扬声器静音"; return true;
                case 113: action = ActionVolumeDown; displayName = "音量降低"; return true;
                case 114: action = ActionVolumeUp; displayName = "音量提高"; return true;
                case 8:
                case 131:
                case 190: action = ActionToggleTouchpad; displayName = "触控板开关"; return true;
                case 116: action = ActionMicrophoneMute; displayName = "麦克风静音"; return true;
                case 117: action = ActionToggleBluetooth; displayName = "蓝牙开关"; return true;
                case 118: action = ActionCycleStage; displayName = "性能模式切换"; return true;
                case 120: action = ActionBrightnessDown; displayName = "亮度降低"; return true;
                case 121: action = ActionBrightnessUp; displayName = "亮度提高"; return true;
            }
        }
        else if (signal.Channel == FnKeySignal.Channels.Keyboard)
        {
            switch (signal.Code)
            {
                case 0xAD: action = ActionSpeakerMute; displayName = "扬声器静音"; return true;
                case 0xAE: action = ActionVolumeDown; displayName = "音量降低"; return true;
                case 0xAF: action = ActionVolumeUp; displayName = "音量提高"; return true;
                case 0x83: action = ActionToggleTouchpad; displayName = "触控板开关"; return true;
                case 0x87: action = ActionTouchpadObserved; displayName = "触控板开关"; return true;
            }
        }

        action = string.Empty;
        displayName = string.Empty;
        return false;
    }

    /// <summary>
    /// 同一动作在去重窗口内只执行一次。
    /// </summary>
    /// <remarks>
    /// 一个物理按键可能同时出现在多条通道上（例如触控板既有 EC 码又产生 <c>VK_F20</c>）。
    /// 不去重会出现「切两次等于没切」。
    /// </remarks>
    private bool TryBeginFnSignal(string action)
    {
        var now = DateTime.UtcNow;
        lock (_fnSignalLock)
        {
            if (_lastFnSignalUtc.TryGetValue(action, out var last) && now - last < FnSignalDedupeWindow)
            {
                return false;
            }

            _lastFnSignalUtc[action] = now;
            return true;
        }
    }

    /// <summary>
    /// 执行一条 Fn 键动作。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么要「先观察再决定」</b>：音量/亮度这类键是否已被系统或固件执行，从信号本身看不出来。
    /// 因此先取快照，等一小段时间复读：值变了说明外部已经做过，只显示结果；
    /// 没变才由本程序下发。这样既不会漏做，也不会做两遍（双倍步进）。
    /// </para>
    /// <para>
    /// 麦克风/触控板/蓝牙/档位四项在本机已实证固件不执行（HWE §32），因此直接下发。
    /// </para>
    /// </remarks>
    private async Task HandleFnSignalAsync(string action, string displayName)
    {
        if (_disposed)
        {
            return;
        }

        if (IsExternallyExecutable(action))
        {
            var before = ReadNativeState(action);
            await Task.Delay(ExternalEffectWait).ConfigureAwait(true);
            var after = ReadNativeState(action);
            if (before is not null && after is not null && !string.Equals(before, after, StringComparison.Ordinal))
            {
                _context.WriteLog(
                    LogLevel.Info,
                    "Hotkey",
                    $"{displayName}：系统/固件已执行（{before} → {after}），本程序不重复执行。");
                var message = BuildNativeStateOsd(action, after);
                if (message is not null)
                {
                    ShowOsd(message);
                }

                return;
            }
        }

        OnHotkeyTriggered(this, action);
    }

    /// <summary>该动作是否可能已被系统或固件执行（需要先观察再决定）。</summary>
    private static bool IsExternallyExecutable(string action)
        => action is ActionSpeakerMute or ActionVolumeUp or ActionVolumeDown
            or ActionBrightnessUp or ActionBrightnessDown;

    /// <summary>读取动作对应的当前状态；读不到返回 <see langword="null"/>。</summary>
    private string? ReadNativeState(string action)
    {
        switch (action)
        {
            case ActionSpeakerMute:
                return _context.AudioControl?.Read(AudioEndpointKind.Playback)?.Muted.ToString();

            case ActionVolumeUp:
            case ActionVolumeDown:
                var playback = _context.AudioControl?.Read(AudioEndpointKind.Playback);
                return playback is null ? null : playback.VolumePercent.ToString("0");

            case ActionBrightnessUp:
            case ActionBrightnessDown:
                return _context.BrightnessControl?.Read()?.ToString("0");

            default:
                return null;
        }
    }

    /// <summary>把外部已执行后的状态转成提示。</summary>
    private static OsdMessage? BuildNativeStateOsd(string action, string state) => action switch
    {
        ActionSpeakerMute => OsdMessage.SpeakerMute(bool.TryParse(state, out var muted) && muted),
        ActionVolumeUp or ActionVolumeDown =>
            int.TryParse(state, out var percent) ? OsdMessage.Volume(percent) : null,
        ActionBrightnessUp or ActionBrightnessDown =>
            int.TryParse(state, out var value) ? OsdMessage.Brightness(value) : null,
        _ => null
    };

    private async Task StartFnHotkeyClientAsync(IFnHotkeySource source)
    {
        try
        {
            var started = await source.StartAsync(CancellationToken.None);
            _context.WriteLog(
                started ? LogLevel.Info : LogLevel.Warning,
                "Hotkey",
                started
                    ? "Fn 组合键监听已启动（只读事件，不拦截固件动作）。"
                    : "Fn 组合键监听未能建立：WMI 事件类均订阅失败，界面不会实时跟随 Fn 动作，"
                      + "但 Fn 键与固件功能不受影响。");
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Warning, "Hotkey", $"Fn 组合键监听启动异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 收到一次 Fn 组合键事件。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>本机固件的真实分工（2026-10-31 实测修正，HWE §32）</b>：
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// <b>系统原生处理</b>：音量、亮度、扬声器静音会产生标准多媒体虚拟键并由 Windows 执行，
    /// 这里只能读回状态后显示，**再执行一次就会双倍步进**。
    /// </item>
    /// <item>
    /// <b>固件只发事件、不执行动作</b>：麦克风、触控板、性能档位、蓝牙射频。
    /// 实测证据：15:35–15:36 连按五次 Fn+F5，系统录音端点始终未静音；
    /// 15:33/15:35 两次 Fn+F7，EC 0xD2 仍为 0xC1（平衡）。
    /// 因此这些动作必须由本程序下发——与 M-Control 1.4.7 的
    /// <c>HandleMicrophoneMuteShortcut</c>／<c>SetTouchpadEnabled</c>／
    /// <c>ToggleScenarioModeFromShortcut</c> 完全同构。
    /// </item>
    /// </list>
    /// <para>
    /// 事件来自 WMI 回调线程，执行路径统一转到 UI 线程，与「等效热键」共用同一段代码，
    /// 避免两条入口各自演化出不同的判据。
    /// </para>
    /// </remarks>
    private void OnFnHotkeyPressed(object? sender, FnHotkeyEvent e)
    {
        if (e.Action == FnHotkeyAction.Unknown)
        {
            _context.WriteLog(
                LogLevel.Info,
                "Hotkey",
                $"Fn 事件：未识别码值 0x{e.RawCode:X}（仅记录，不猜测键位），来源 {e.Source}。");
            return;
        }

        if (TryMapToEquivalentAction(e.Action, out var action))
        {
            // 触控板是唯一「固件可能也做了」的动作：切换型组合无法从事件本身判断是否已被执行，
            // 用上一次已知状态做差集，变化即说明固件已经切过，此时只同步显示，避免又切回去。
            if (e.Action == FnHotkeyAction.Touchpad && FirmwareAlreadyToggledTouchpad())
            {
                _context.WriteLog(
                    LogLevel.Info,
                    "Hotkey",
                    $"Fn 事件：触控板（码值 0x{e.RawCode:X}）；读回状态已变化，判定固件已完成切换，本程序不重复执行。");
                return;
            }

            _context.WriteLog(
                LogLevel.Info,
                "Hotkey",
                $"Fn 事件：{e.Action}（码值 0x{e.RawCode:X}），来源 {e.Source}；"
                + "该动作固件不执行，转由本程序下发。");
            _sync.Post(_ => OnHotkeyTriggered(this, action), null);
            return;
        }

        _context.WriteLog(
            LogLevel.Info,
            "Hotkey",
            $"Fn 事件：{e.Action}（码值 0x{e.RawCode:X}），来源 {e.Source}；"
            + "该动作已由系统原生处理，本程序只同步状态。");

        var message = BuildOsdForFnEvent(e.Action);
        if (message is not null)
        {
            _sync.Post(_ => ShowOsd(message), null);
        }
    }

    /// <summary>
    /// 把「固件不执行、需应用下发」的 Fn 动作映射到等效命令的动作名。
    /// </summary>
    /// <returns>需要应用执行时返回 <see langword="true"/>。</returns>
    private static bool TryMapToEquivalentAction(FnHotkeyAction action, out string hotkeyAction)
    {
        switch (action)
        {
            case FnHotkeyAction.MicrophoneMute:
                hotkeyAction = ActionMicrophoneMute;
                return true;

            case FnHotkeyAction.Touchpad:
                hotkeyAction = ActionToggleTouchpad;
                return true;

            case FnHotkeyAction.PerformanceStage:
                hotkeyAction = ActionCycleStage;
                return true;

            case FnHotkeyAction.BluetoothToggle:
                hotkeyAction = ActionToggleBluetooth;
                return true;

            default:
                hotkeyAction = string.Empty;
                return false;
        }
    }

    /// <summary>
    /// 判断触控板状态是否已被固件改过（与上一次已知值比较）。
    /// </summary>
    /// <returns>状态已变化时返回 <see langword="true"/>，表示不必再切换一次。</returns>
    private bool FirmwareAlreadyToggledTouchpad()
    {
        var current = _context.TouchpadControl?.Read();
        if (current is null)
        {
            return false;
        }

        var changed = _lastKnownTouchpad is { } last && last != current.Enabled;
        _lastKnownTouchpad = current.Enabled;
        return changed;
    }

    /// <summary>
    /// 触控板状态轮询回调（线程池线程）。
    /// </summary>
    private void PollTouchpadState(object? state)
    {
        if (_disposed)
        {
            return;
        }

        var current = _context.TouchpadControl?.Read();
        if (current is null)
        {
            return;
        }

        var known = _lastKnownTouchpad;
        if (known is not null && known.Value == current.Enabled)
        {
            return;
        }

        _sync.Post(_ => OnTouchpadStateObserved(current.Enabled, "状态轮询"), null);
    }

    /// <summary>
    /// 处理一次观察到的触控板状态变化：更新已知值、弹桌面提示、通知订阅方。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 来源有两类：物理 Fn+F4 的 VK_F24 信号（立即读回，延迟约 200 ms 等 Windows 完成切换）
    /// 与状态轮询（兜底，覆盖钩子没接到的路径）。<c>_lastKnownTouchpad</c> 是两者的
    /// 共享判重依据——先到者更新，后到者看到值已一致便不再提示。
    /// </para>
    /// <para>
    /// 读回与已知值一致时不弹提示：那说明变化已被其它路径反馈过，重复提示是噪音。
    /// </para>
    /// </remarks>
    private async Task ObserveTouchpadChangeAsync(string source)
    {
        const int settleWaitMs = 200;
        const int maxWaitMs = 900;

        // Windows 在 F24 键按下时完成切换；此处小步等待状态位落地，避免读到旧值。
        var waited = 0;
        bool? latest = null;
        while (waited <= maxWaitMs)
        {
            latest = _context.TouchpadControl?.Read()?.Enabled;
            if (latest is null || latest != _lastKnownTouchpad)
            {
                break;
            }

            await Task.Delay(settleWaitMs).ConfigureAwait(true);
            waited += settleWaitMs;
        }

        if (latest is null || latest == _lastKnownTouchpad)
        {
            return;
        }

        OnTouchpadStateObserved(latest.Value, source);
    }

    private void OnTouchpadStateObserved(bool enabled, string source)
    {
        _lastKnownTouchpad = enabled;
        ShowOsd(OsdMessage.Touchpad(enabled));
        _context.WriteLog(
            LogLevel.Info,
            "Device",
            $"触控板状态变化（来源 {source}）：{(enabled ? "已启用" : "已禁用")}。");
        _context.RaiseTouchpadStateChanged();
    }

    /// <summary>
    /// 系统原生执行的动作：只读回真实状态生成提示，不做任何写入。
    /// </summary>
    /// <returns>读不到状态时返回 <see langword="null"/>（宁可不显示，也不给可能错误的结论）。</returns>
    private OsdMessage? BuildOsdForFnEvent(FnHotkeyAction action)
    {
        switch (action)
        {
            case FnHotkeyAction.SpeakerMute:
                var speaker = _context.AudioControl?.Read(AudioEndpointKind.Playback);
                return speaker is null ? null : OsdMessage.SpeakerMute(speaker.Muted);

            case FnHotkeyAction.VolumeDown:
            case FnHotkeyAction.VolumeUp:
                var volume = _context.AudioControl?.Read(AudioEndpointKind.Playback);
                return volume is null ? null : OsdMessage.Volume(volume.VolumePercent);

            case FnHotkeyAction.BrightnessDown:
            case FnHotkeyAction.BrightnessUp:
                var brightness = _context.BrightnessControl?.Read();
                return brightness is null ? null : OsdMessage.Brightness(brightness.Value);

            default:
                return null;
        }
    }

    /// <summary>
    /// 显示桌面提示（必须在 UI 线程调用）。
    /// </summary>
    private void ShowOsd(OsdMessage message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _osd.Show(message);
        }
        catch (Exception ex)
        {
            // 提示条是附加反馈，绝不能因为它出错而影响功能本身。
            _context.WriteLog(LogLevel.Warning, "Osd", $"桌面提示显示失败：{ex.Message}");
        }
    }

    /// <summary>显示指定窗口（另一窗口隐藏，保证默认只显示一个）。</summary>
    public void ShowWindow(WindowMode mode)
    {
        if (_disposed)
        {
            return;
        }

        _lastWindowMode = mode;
        var showNormal = mode == WindowMode.Normal;

        _mainForm.Visible = showNormal;
        _miniForm.Visible = !showNormal;

        var form = showNormal ? (Form)_mainForm : _miniForm;
        if (form.WindowState == FormWindowState.Minimized)
        {
            form.WindowState = FormWindowState.Normal;
        }

        form.Show();
        form.BringToFront();
        form.Activate();

        // 被隐藏期间窗口不重绘（任务书 §20），因此重新显示后立即用最近一次采样渲染，
        // 避免出现最长一个刷新间隔的空窗期。
        if (showNormal)
        {
            _mainForm.RefreshFromLatest();
        }
        else
        {
            _miniForm.RefreshFromLatest();
        }

        _context.WriteLog(LogLevel.Debug, "Shell", $"已显示{ModeFormatter.WindowModeText(mode)}。");
    }

    /// <summary>
    /// 本次启动是否应当不显示窗口。
    /// </summary>
    /// <remarks>
    /// 两个条件同时成立才隐藏：用户勾选了「开机后静默驻留后台」，
    /// <b>且</b>本次确实由开机自启拉起（命令行带 <c>--autostart</c>）。
    /// 只看设置会让手动双击图标没有任何反应，那属于假死而不是静默驻留。
    /// </remarks>
    private bool ShouldStartHidden()
        => _context.Settings.StartMinimizedOnStartup
           && Environment.GetCommandLineArgs().Any(
               arg => string.Equals(arg, StartupRegistration.AutostartArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>由第二个实例的启动请求唤醒：把现有窗口带到前台。</summary>
    public void ActivateFromOtherInstance()
        => _sync.Post(_ => ShowWindow(_lastWindowMode), null);

    /// <summary>完全退出：停止采样、注销快捷键、保存窗口位置。</summary>
    public void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        if (!ConfirmExit())
        {
            _context.WriteLog(LogLevel.Info, "Shell", "用户在退出确认中选择了「否」，程序继续运行。");
            return;
        }

        _exiting = true;
        _context.WriteLog(LogLevel.Info, "Shell", "请求完全退出：停止采样、注销快捷键、保存窗口位置。");
        _context.WriteLog(
            LogLevel.Info,
            "Shell",
            "退出后仅保留固件可持久执行的设置；依赖持续进程的软件调速不会继续生效。");

        // 退出前把性能场景退回固件基准（只写 EC 0xD2，不碰功耗墙与风扇曲线）。
        // 必须早于 Snapshots.Stop()：EC 通道此时仍然打开。
        RestoreDefaultStageOnExit();

        SavePlacements();
        _hotkeys.Dispose();
        _context.Snapshots.Stop();
        _tray.Dispose();

        _mainForm.AllowClose();
        _miniForm.AllowClose();
        _mainForm.Close();
        _miniForm.Close();

        ExitThread();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (_disposed)
        {
            base.Dispose(disposing);
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _context.SettingsChanged -= OnSettingsChanged;
            _context.Snapshots.Updated -= OnSnapshotReadback;
            _themeSubscription.Dispose();
            // 触控板状态轮询先停：回调可能还在排队，避免退出后再弹提示。
            _touchpadWatchTimer?.Dispose();
            _touchpadWatchTimer = null;

            // Fn 键通道由装配方持有并释放，这里只退订，避免退出后仍有回调打到已释放的窗。
            foreach (var channel in _context.FnKeyChannels)
            {
                channel.Signaled -= OnFnKeySignaled;
            }

            if (_context.FnHotkeys is { } fn)
            {
                fn.Pressed -= OnFnHotkeyPressed;
            }

            _hotkeys.Triggered -= OnHotkeyTriggered;
            _hotkeys.Dispose();

            // 提示浮层是独立顶层窗口，必须显式关闭，否则进程退不掉（只剩一个不可见窗体）。
            _osd.HideImmediately();
            _osd.Dispose();

            // Fn 事件源由组合根持有并释放，这里只解除订阅，避免回调指向已释放的窗口。
            if (_context.FnHotkeys is not null)
            {
                _context.FnHotkeys.Pressed -= OnFnHotkeyPressed;
            }
            _tray.Dispose();
            _mainForm.Dispose();
            _miniForm.Dispose();
            _settingsForm?.Dispose();
            _context.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 构造等效命令的固定绑定：<c>Ctrl+Alt+数字</c>。
    /// </summary>
    /// <remarks>
    /// 用字符串键名与 <see cref="HotkeyBinding"/> 的持久化格式保持一致，
    /// 使注册路径与可配置快捷键走同一套解析（<see cref="HotkeyManager.TryParseKey"/>）。
    /// </remarks>
    private static HotkeyBinding Equivalent(Keys key)
        => new(Enabled: true, Control: true, Alt: true, Shift: false, Windows: false, KeyName: key.ToString());

    private void OnHotkeyTriggered(object? sender, string action)
    {
        switch (action)
        {
            case ActionToggleWindow:
                ToggleWindowVisibility();
                break;

            case ActionToggleFullSpeed:
                _ = ToggleFullSpeedAsync();
                break;

            case ActionSpeakerMute:
                RunShortcut(
                    "扬声器静音",
                    CapabilityKeys.AudioVolume,
                    async ct =>
                    {
                        var audio = _context.AudioControl;
                        if (audio is null)
                        {
                            return NotWired("扬声器静音");
                        }

                        var current = audio.Read(AudioEndpointKind.Playback);
                        if (current is null)
                        {
                            return Failed("扬声器静音", "无法读取默认播放端点状态，未发起写入。");
                        }

                        return await audio.SetMuteAsync(AudioEndpointKind.Playback, !current.Muted, ct);
                    },
                    result => ResultOsd("扬声器", result, () =>
                    {
                        var state = _context.AudioControl?.Read(AudioEndpointKind.Playback);
                        return state is null ? null : OsdMessage.SpeakerMute(state.Muted);
                    }));
                break;

            case ActionVolumeDown:
                RunShortcut(
                    "音量降低",
                    CapabilityKeys.AudioVolume,
                    ct => _context.AudioControl is null
                        ? Task.FromResult(NotWired("音量降低"))
                        : _context.AudioControl.StepVolumeAsync(-1, ct),
                    result => ResultOsd("音量", result, ReadVolumeOsd));
                break;

            case ActionVolumeUp:
                RunShortcut(
                    "音量提高",
                    CapabilityKeys.AudioVolume,
                    ct => _context.AudioControl is null
                        ? Task.FromResult(NotWired("音量提高"))
                        : _context.AudioControl.StepVolumeAsync(1, ct),
                    result => ResultOsd("音量", result, ReadVolumeOsd));
                break;

            case ActionMicrophoneMute:
                RunShortcut(
                    "麦克风静音",
                    CapabilityKeys.AudioMicrophone,
                    async ct =>
                    {
                        var audio = _context.AudioControl;
                        if (audio is null)
                        {
                            return NotWired("麦克风静音");
                        }

                        var current = audio.Read(AudioEndpointKind.Recording);
                        if (current is null)
                        {
                            return Failed("麦克风静音", "无法读取默认录音端点状态，未发起写入。");
                        }

                        return await audio.SetMuteAsync(AudioEndpointKind.Recording, !current.Muted, ct);
                    },
                    result => ResultOsd("麦克风", result, () =>
                    {
                        var state = _context.AudioControl?.Read(AudioEndpointKind.Recording);
                        return state is null ? null : OsdMessage.Microphone(state.Muted);
                    }));
                break;

            case ActionBrightnessDown:
                RunShortcut(
                    "亮度降低",
                    CapabilityKeys.DisplayBrightness,
                    ct => StepBrightnessAsync(-1, ct),
                    result => ResultOsd("亮度", result, ReadBrightnessOsd));
                break;

            case ActionBrightnessUp:
                RunShortcut(
                    "亮度提高",
                    CapabilityKeys.DisplayBrightness,
                    ct => StepBrightnessAsync(1, ct),
                    result => ResultOsd("亮度", result, ReadBrightnessOsd));
                break;

            case ActionToggleTouchpad:
                RunShortcut(
                    "触控板开关",
                    CapabilityKeys.InputTouchpad,
                    async ct =>
                    {
                        var touchpad = _context.TouchpadControl;
                        if (touchpad is null)
                        {
                            return NotWired("触控板开关");
                        }

                        var current = touchpad.Read();
                        if (current is null)
                        {
                            return ControlResult.Single(SettingResult.Unsupported(
                                "input.touchpad",
                                "未读到 Windows PrecisionTouchPad 状态位，无法确定当前状态，未发起切换。"));
                        }

                        return await touchpad.SetEnabledAsync(!current.Enabled, ct);
                    },
                    result => ResultOsd("触摸板", result, ReadTouchpadOsd));
                break;

            case ActionToggleBluetooth:
                RunShortcut(
                    "蓝牙开关",
                    CapabilityKeys.SystemBluetooth,
                    async ct =>
                    {
                        var radio = _context.BluetoothControl;
                        if (radio is null)
                        {
                            return NotWired("蓝牙开关");
                        }

                        var current = radio.Read();
                        if (current is null)
                        {
                            return ControlResult.Single(SettingResult.Unsupported(
                                "system.bluetooth",
                                "未枚举到蓝牙射频，无法确定当前状态，未发起切换。"));
                        }

                        if (current.IsOn is not { } on)
                        {
                            return ControlResult.Single(SettingResult.Unsupported(
                                "system.bluetooth",
                                $"蓝牙射频当前为「{current.Describe()}」，不是可切换的开/关状态。"));
                        }

                        return await radio.SetAsync(!on, ct);
                    },
                    result => ResultOsd("蓝牙", result, ReadBluetoothOsd));
                break;

            case ActionCycleStage:
                RunShortcut(
                    "性能模式切换",
                    CapabilityKeys.PerformanceStage,
                    CycleStageAsync,

                    // 整组必然 PartiallySucceeded（本机 GPU 功耗上限永久不受支持），
                    // 因此提示按「档位项是否读完确认」判定，而不是整组状态：
                    // 否则用户明明看到档位变了，屏幕上却写「未完成」（HWE §32.5）。
                    result => StageApplied(result)
                        ? ReadStageOsd() ?? new OsdMessage("性能模式", "已切换", OsdTone.Success)
                        : OsdMessage.Failure("性能模式切换"),
                    StageApplied);
                break;
        }
    }

    /// <summary>
    /// 统一的等效命令执行外壳：能力门控 → 执行 → 结果反馈（日志 + 桌面提示）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 能力未达可写等级时**不发起请求**并说明原因（任务书 §16.1）。
    /// 成功与失败都给出桌面提示：此前只有日志，用户按下去屏幕毫无变化，
    /// 会直接判定「快捷键不工作」——这正是本次修复的核心问题。
    /// </para>
    /// <para>
    /// 提示文案由 <paramref name="osd"/> 依据**操作结果**生成，而不是依据请求值：
    /// 例如音量提示里的百分比来自读回，不是「我以为调到了多少」。
    /// </para>
    /// <para>
    /// <paramref name="applied"/> 用于「整组里含本机永久不支持项」的动作（目前只有档位切换）：
    /// 由调用方按主目标的实际确认结果判定，而不是整组 <see cref="ControlResult.IsSuccess"/>。
    /// 未提供时仍按整组状态判定，行为与之前一致。
    /// </para>
    /// </remarks>
    private void RunShortcut(
        string displayName,
        string capabilityKey,
        Func<CancellationToken, Task<ControlResult>> action,
        Func<ControlResult, OsdMessage> osd,
        Func<ControlResult, bool>? applied = null)
    {
        if (!_context.Capabilities.AllowsWrite(capabilityKey))
        {
            var entry = _context.DescribeCapability(capabilityKey, requiresWrite: true);
            _tray.ShowTip($"{displayName}不可用", entry.Reason, warning: true);
            ShowOsd(OsdMessage.Failure(displayName));
            _context.WriteLog(
                LogLevel.Warning,
                "Hotkey",
                $"{displayName}被触发，但能力 {capabilityKey} 未达可写等级，未发起任何请求：{entry.Reason}");
            return;
        }

        _ = ExecuteAsync();

        async Task ExecuteAsync()
        {
            try
            {
                var result = await action(CancellationToken.None);
                var message = osd(result);
                ShowOsd(message);

                var succeeded = applied?.Invoke(result) ?? result.IsSuccess;
                if (succeeded)
                {
                    _context.WriteLog(LogLevel.Info, "Hotkey", $"{displayName}已完成：{DescribeSettings(result)}");

                    // 主目标已达成时，剩余未达成项不再升级为失败提示：
                    // 它们通常是本机永久不支持的通道（如 GPU 功耗上限），
                    // 逐项如实记一行日志即可，既不隐瞒、也不惊动用户。
                    if (result.Problems.Count > 0)
                    {
                        _context.WriteLog(
                            LogLevel.Info,
                            "Hotkey",
                            $"{displayName}：主目标已达成，以下通道本机不承载，未变更："
                            + string.Join("；", result.Problems.Select(p => $"{p.Setting}（{p.Status}）")));
                    }

                    return;
                }

                var detail = result.Problems.Count == 0
                    ? "操作未成功，且未返回具体原因。"
                    : string.Join("；", result.Problems.Select(p => p.Message ?? p.Status.ToString()));
                _tray.ShowTip($"{displayName}未完成", detail, warning: true);
                _context.WriteLog(LogLevel.Warning, "Hotkey", $"{displayName}未完成：{detail}");
            }
            catch (Exception ex)
            {
                ShowOsd(OsdMessage.Failure(displayName));
                _tray.ShowTip($"{displayName}失败", ex.Message, warning: true);
                _context.WriteLog(LogLevel.Error, "Hotkey", $"{displayName}执行异常：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 按读回值生成提示；读不到时退回「已切换」，不编造具体数值。
    /// </summary>
    private static OsdMessage ResultOsd(string title, ControlResult result, Func<OsdMessage?> readBack)
    {
        if (!result.IsSuccess)
        {
            return OsdMessage.Failure(title);
        }

        return readBack() ?? new OsdMessage(title, "已切换", OsdTone.Neutral);
    }

    private OsdMessage? ReadVolumeOsd()
    {
        var state = _context.AudioControl?.Read(AudioEndpointKind.Playback);
        return state is null ? null : OsdMessage.Volume(state.VolumePercent);
    }

    private OsdMessage? ReadBrightnessOsd()
    {
        var brightness = _context.BrightnessControl?.Read();
        return brightness is null ? null : OsdMessage.Brightness(brightness.Value);
    }

    /// <summary>读回触控板状态，并同步「上次已知状态」（Fn 事件判重用）。</summary>
    private OsdMessage? ReadTouchpadOsd()
    {
        var state = _context.TouchpadControl?.Read();
        if (state is null)
        {
            return null;
        }

        _lastKnownTouchpad = state.Enabled;
        return OsdMessage.Touchpad(state.Enabled);
    }

    /// <summary>读回蓝牙射频状态；状态不确定（Disabled/Unknown）时不显示结论。</summary>
    private OsdMessage? ReadBluetoothOsd()
    {
        var radio = _context.BluetoothControl?.Read();
        return radio?.IsOn is { } on ? OsdMessage.Bluetooth(on) : null;
    }

    /// <summary>读回实际性能档位名。</summary>
    private OsdMessage? ReadStageOsd()
    {
        var stage = ReadStageFromHardware();
        return stage is null ? null : OsdMessage.PerformanceStage(ModeFormatter.StageText(stage.Value));
    }

    /// <summary>
    /// 从 EC 0xD2 读回当前档位，并把「安静」与「节能」区分开。
    /// </summary>
    /// <remarks>
    /// 两者共用编码 0xC2（HWE §21.3），单看寄存器无法分辨。
    /// 只有在「上次经协调器确认应用的是安静、且当前编码仍是节能」时才沿用安静语义，
    /// 其余情况一律按寄存器解码结果呈现——宁可显示编码语义，也不猜。
    /// 消歧事实源是 <see cref="IControlRequestSink.LastConfirmedStage"/>：
    /// 所有写入入口（窗口按钮、托盘、等效热键、Fn 事件、启动恢复）都汇聚到协调器，
    /// 桌面壳不再自建副本——否则界面按钮切到安静后，循环热键不知道，会把安静当节能再切一次。
    /// </remarks>
    private PerformanceStage? ReadStageFromHardware()
    {
        var state = _context.StateReader?.ReadPerformanceStateAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        return DisambiguateStage(state)?.ActiveStage;
    }

    /// <summary>
    /// 对读回的档位做安静/节能消歧；无法消歧时原样返回。
    /// </summary>
    /// <remarks>
    /// 供两条消费路径共用：档位循环的「当前档」计算与主窗口的实际档位显示。
    /// 此前只有循环用了消歧，界面仍显示节能——用户看到的就是
    /// 「节能切不到安静，直接跳到平衡」（切到安静后界面显示不变，下一跳直接前进）。
    /// </remarks>
    private PerformanceHardwareState? DisambiguateStage(PerformanceHardwareState? state)
    {
        var corrected = ApplyStageDisambiguation(state?.ActiveStage, _context.Control?.LastConfirmedStage);
        if (corrected is null || state is null || corrected == state.ActiveStage)
        {
            return state;
        }

        return state with { ActiveStage = corrected };
    }

    /// <summary>
    /// 安静/节能消歧的纯逻辑核心（可测试）。
    /// </summary>
    /// <returns>消歧后的档位；输入为空时原样返回。</returns>
    /// <remarks>
    /// 判据：仅当「上次确认档位 ≠ 解码结果、且两者编码相同」时沿用上次确认的语义。
    /// 编码不同说明固件场景真的变了（外部改动或用户经其它入口切换），以读回为准；
    /// 编码相同且相同（如上次确认的就是节能）则无事可做。
    /// </remarks>
    internal static PerformanceStage? ApplyStageDisambiguation(
        PerformanceStage? decoded,
        PerformanceStage? lastConfirmed)
    {
        if (decoded is null || lastConfirmed is null)
        {
            return decoded;
        }

        return lastConfirmed.Value != decoded.Value
            && PerformanceShiftMap.Encode(lastConfirmed.Value) == PerformanceShiftMap.Encode(decoded.Value)
            ? lastConfirmed
            : decoded;
    }

    /// <summary>
    /// Fn+F7／等效命令：读回实际档位，切到循环中的下一档。
    /// </summary>
    /// <remarks>
    /// <para>
    /// MSI 固件在 Fn+F7 上只上报 <c>ShiftingSpeed</c> 事件，不写 EC 0xD2
    /// （2026-10-31 实测：两次按键后寄存器仍为 0xC1）。切换动作由本程序完成，
    /// 与 M-Control 的 <c>ToggleScenarioModeFromShortcut</c> 同构。
    /// </para>
    /// <para>
    /// 循环序取自 <see cref="PerformanceShiftMap.Cycle"/>：节能 → 安静 → 平衡 → 高性能 → 狂暴 → 节能。
    /// 读不到当前档位时从循环首档开始，日志里会留下「起点未知」的说明。
    /// </para>
    /// </remarks>
    private async Task<ControlResult> CycleStageAsync(CancellationToken cancellationToken)
    {
        if (_context.Control is null)
        {
            return NotWired("性能模式切换");
        }

        var current = ReadStageFromHardware();
        var next = PerformanceShiftMap.NextStage(current);
        var result = await _context.Control.RequestPerformanceStageAsync(next, cancellationToken)
            .ConfigureAwait(false);

        // 判据是「档位项读完确认」，不是整组 IsSuccess。
        // 本机 GPU 功耗上限永久不受支持，整组恒为 PartiallySucceeded；用整组状态判断会让
        // LastConfirmedStage 永不更新 → 消歧失效、循环卡死（HWE §32.5）。
        // 协调器已按同一判据维护 LastConfirmedStage，这里不再另记副本。
        if (StageApplied(result))
        {
            _context.WriteLog(
                LogLevel.Info,
                "Hotkey",
                $"性能模式：已切到 {ModeFormatter.StageText(next)}（码值 "
                + $"{result.Settings.FirstOrDefault(s => s.Setting == PerformanceShiftMap.StageSettingKey)?.ReadBack ?? "读回未提供"}）。");
        }

        if (current is null)
        {
            _context.WriteLog(
                LogLevel.Warning,
                "Hotkey",
                "切换前未读到 EC 0xD2 的档位编码，起点未知，已按循环首档（节能）发起请求；"
                + "若要精确循环请先确认档位读回通道可用。");
        }

        return result;
    }


    /// <summary>
    /// 档位项是否已确认生效（读回一致）。
    /// </summary>
    /// <remarks>
    /// 不用整组状态：档位切换会连带尝试 GPU 功耗上限，而本机 vBIOS 未开放该旋钮，
    /// 逐项报 <see cref="OperationStatus.Unsupported"/> 使整组恒为
    /// <see cref="OperationStatus.PartiallySucceeded"/>。那是已知限制，不是本次操作失败。
    /// </remarks>
    private static bool StageApplied(ControlResult result)
        => result.IsSettingSucceeded(PerformanceShiftMap.StageSettingKey);

    private static string DescribeSettings(ControlResult result)
        => result.Settings.Count == 0
            ? "无逐项结果"
            : string.Join("，", result.Settings.Select(s => $"{s.Setting}={s.ReadBack ?? "已下发"}"));

    /// <summary>
    /// 按内屏当前亮度步进，步长取面板自己上报的档位间隔。
    /// </summary>
    /// <remarks>
    /// 步长不自造固定值：不同面板的档位粒度不同，用面板上报的 Level 间隔才与 Fn+F9/F10 手感一致。
    /// 读到上限/下限时不写入，直接如实报告「已到边界」。
    /// </remarks>
    private Task<ControlResult> StepBrightnessAsync(int direction, CancellationToken cancellationToken)
    {
        const string setting = "display.brightness";
        var brightness = _context.BrightnessControl;
        if (brightness is null)
        {
            return Task.FromResult(NotWired("亮度调整"));
        }

        var current = brightness.Read();
        if (current is null)
        {
            return Task.FromResult(Failed("亮度调整", "未读到内屏当前亮度，未发起写入（外接屏不支持该接口）。"));
        }

        var range = brightness.ReadRange();
        var step = range?.StepPercent ?? 5;
        var min = range?.MinPercent ?? 0;
        var max = range?.MaxPercent ?? 100;
        var target = Math.Clamp(current.Value + (direction * step), min, max);

        if (target == current.Value)
        {
            return Task.FromResult(ControlResult.Single(SettingResult.PartiallySucceeded(
                setting,
                direction > 0 ? "提高一档" : "降低一档",
                $"{current.Value}%",
                $"已到达亮度{(direction > 0 ? "上限" : "下限")}（当前 {current.Value}%），未写入硬件。")));
        }

        return brightness.SetAsync(target, cancellationToken);
    }

    private static ControlResult NotWired(string displayName)
        => ControlResult.Single(SettingResult.Unsupported(
            "shortcut",
            $"{displayName}未接入：本次启动未装配对应的系统接口实现。"));

    private static ControlResult Failed(string displayName, string message)
        => ControlResult.Single(SettingResult.Failed("shortcut", displayName, message));

    private void ToggleWindowVisibility()
    {
        if (_mainForm.Visible || _miniForm.Visible)
        {
            SavePlacements();
            _mainForm.Hide();
            _miniForm.Hide();
            _context.WriteLog(LogLevel.Debug, "Shell", "快捷键隐藏窗口，采样与控制继续运行。");
            return;
        }

        ShowWindow(_lastWindowMode);
    }

    private async Task ToggleFullSpeedAsync()
    {
        // 能力未达 Verified 时不得发起写入（任务书 §16.1），此处给出明确提示而不是静默失败。
        var entry = _context.DescribeCapability(CapabilityKeys.FanFullSpeed, requiresWrite: true);
        if (!entry.Enabled)
        {
            _tray.ShowTip("强冷不可用", entry.Reason, warning: true);
            _context.WriteLog(LogLevel.Warning, "Hotkey", "强冷快捷键被按下，但该能力尚未验证，未发起请求。");
            return;
        }

        var target = !_fullSpeedActive;
        try
        {
            var result = await _context.Control.SetFullSpeedAsync(target, CancellationToken.None).ConfigureAwait(true);
            _context.LogControlResult(result, target ? "快捷键开启强冷" : "快捷键关闭强冷");

            if (result.Settings.Count > 0 && result.Settings.All(s => s.Status == OperationStatus.Succeeded))
            {
                _fullSpeedActive = target;
                _tray.UpdateFullSpeedState(target);
            }

            _tray.ShowTip("MSI-Control", $"强冷 → {result.Status}", warning: !result.IsSuccess);
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Error, "Hotkey", $"强冷快捷键执行失败：{ex.Message}");
            _tray.ShowTip("MSI-Control", "强冷快捷键执行失败：" + ex.Message, warning: true);
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is { IsDisposed: false })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_context);
        _settingsForm.ApplyPalette(ThemePalette.For(_themeMode));
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
    }

    private void ApplyHotkeys()
    {
        var definitions = new[]
        {
            new HotkeyDefinition(ActionToggleWindow, "显示/隐藏窗口", _context.Settings.ShowHideWindowHotkey),
            new HotkeyDefinition(ActionToggleFullSpeed, "强冷切换", _context.Settings.FullSpeedHotkey),

            // 等效命令固定为 Ctrl+Alt+1…9：不与 F1–F12 抢键，也不占用本程序已有的 Ctrl+Alt+M。
            // 这些绑定暂不落盘（可配置化需要设置架构升级），因此注册失败时只提示、不写入设置。
            new HotkeyDefinition(ActionSpeakerMute, "扬声器静音（≈Fn+F1）", Equivalent(Keys.D1)),
            new HotkeyDefinition(ActionVolumeDown, "音量降低（≈Fn+F2）", Equivalent(Keys.D2)),
            new HotkeyDefinition(ActionVolumeUp, "音量提高（≈Fn+F3）", Equivalent(Keys.D3)),
            new HotkeyDefinition(ActionToggleTouchpad, "触控板开关（≈Fn+F4）", Equivalent(Keys.D4)),
            new HotkeyDefinition(ActionMicrophoneMute, "麦克风静音（≈Fn+F5）", Equivalent(Keys.D5)),
            new HotkeyDefinition(ActionToggleBluetooth, "蓝牙开关（≈Fn+F6）", Equivalent(Keys.D6)),
            new HotkeyDefinition(ActionBrightnessDown, "亮度降低（≈Fn+F9）", Equivalent(Keys.D7)),
            new HotkeyDefinition(ActionBrightnessUp, "亮度提高（≈Fn+F10）", Equivalent(Keys.D8)),
            new HotkeyDefinition(ActionCycleStage, "性能模式切换（≈Fn+F7）", Equivalent(Keys.D9))
        };

        _lastShowHideHotkey = _context.Settings.ShowHideWindowHotkey;
        _lastFullSpeedHotkey = _context.Settings.FullSpeedHotkey;

        var reports = _hotkeys.Apply(definitions);
        foreach (var report in reports)
        {
            if (report.Registered)
            {
                _context.WriteLog(LogLevel.Info, "Hotkey", $"快捷键已注册：{report.DisplayName} = {report.Binding.Describe()}");
                continue;
            }

            // 「用户主动关闭」与「注册失败」是两件事，日志级别必须区分，
            // 否则一个默认关闭的快捷键会被误报为异常。
            if (!report.Binding.Enabled)
            {
                _context.WriteLog(LogLevel.Info, "Hotkey", $"快捷键未启用（按设置关闭）：{report.DisplayName}");
                continue;
            }

            _context.WriteLog(
                LogLevel.Warning,
                "Hotkey",
                $"快捷键注册失败：{report.DisplayName} = {report.Binding.Describe()}；{report.Error}");
        }

        var failures = reports
            .Where(r => !r.Registered && r.Binding.Enabled && !string.IsNullOrWhiteSpace(r.Error))
            .Select(r => $"{r.DisplayName}（{r.Binding.Describe()}）：{r.Error}")
            .ToArray();

        if (failures.Length > 0)
        {
            _tray.ShowTip("部分全局快捷键未生效", string.Join(Environment.NewLine, failures) + Environment.NewLine + "可在「设置与诊断」中修改或关闭。", warning: true);
        }
    }

    /// <summary>
    /// 采样节拍驱动的硬件状态读回：风扇控制模式（EC 0xD4）、实际性能档位（EC 0xD2）
    /// 与实际显卡模式（EC 0x2E/0xD1 + UEFI 目标变量，HWE §21）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是「详细编辑区当前生效控制模式」「实际性能模式」「实际显卡模式」三个徽标的数据来源——
    /// 修复前 <see cref="MainForm.UpdateFanState"/>/<see cref="MainForm.UpdateStageState"/>/
    /// <see cref="MainForm.UpdateGraphicsState"/> 从未被任何宿主调用，界面永远停留在
    /// 「未读取/未适配」初始文案。
    /// </para>
    /// <para>
    /// 单飞（同一时刻最多一次读回）；主窗口隐藏时不读（隐藏页不做无用访问，
    /// 与 ApplySnapshot 的隐藏不绘制规则一致）。读回失败由宿主侧限流记录并返回
    /// <see langword="null"/>，界面保持上一次状态。
    /// </para>
    /// </remarks>
    private void OnSnapshotReadback(object? sender, MonitoringSnapshot snapshot)
    {
        try
        {
            var reader = _context.StateReader;
            if (reader is null || _disposed || !_mainForm.Visible)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _readbackInFlight, 1, 0) != 0)
            {
                return;
            }

            _ = PollHardwareStateAsync(reader);
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Debug, "Shell", $"硬件状态读回调度异常：{ex.Message}");
        }
    }

    private async Task PollHardwareStateAsync(IHardwareStateReader reader)
    {
        try
        {
            var fan = await reader.ReadFanStateAsync(CancellationToken.None).ConfigureAwait(false);
            var stage = await reader.ReadPerformanceStateAsync(CancellationToken.None).ConfigureAwait(false);
            var graphics = await reader.ReadGraphicsStateAsync(CancellationToken.None).ConfigureAwait(false);

            if (_disposed || (fan is null && stage is null && graphics is null))
            {
                return;
            }

            _sync.Post(_ =>
            {
                if (_disposed)
                {
                    return;
                }

                if (fan is not null && !_mainForm.IsDisposed)
                {
                    _mainForm.UpdateFanState(fan);
                }

                if (stage is not null && !_mainForm.IsDisposed)
                {
                    // 安静/节能共用 0xC2：显示前先按「上次确认档位」消歧，
                    // 否则切到安静后界面仍显示节能，用户会判定「切不过去」。
                    _mainForm.UpdateStageState(DisambiguateStage(stage)!);
                }

                if (graphics is not null && !_mainForm.IsDisposed)
                {
                    _mainForm.UpdateGraphicsState(graphics);
                }
            }, null);
        }
        catch (ObjectDisposedException)
        {
            // 退出竞态：读回源先于本轮读回被释放，属正常关闭路径，不记日志。
        }
        catch (Exception ex)
        {
            // 读回是尽力而为的只读操作，不影响桌面壳运行；宿主侧已对同类失败限流记录。
            _context.WriteLog(LogLevel.Debug, "Shell", $"硬件状态读回轮询异常：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _readbackInFlight, 0);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        _context.Snapshots.SetInterval(TimeSpan.FromSeconds(settings.RefreshIntervalSeconds));

        // 只在快捷键绑定真正变化时重新注册。
        // 无谓的重注册会短暂释放组合键，若期间被其他程序占用就会永久丢失——
        // 例如仅仅切换「风扇显示：百分比/RPM」也不应该动全局快捷键。
        if (settings.ShowHideWindowHotkey != _lastShowHideHotkey
            || settings.FullSpeedHotkey != _lastFullSpeedHotkey)
        {
            ApplyHotkeys();
        }

        _mainForm.RefreshFromLatest();
        _miniForm.RefreshFromLatest();

        _context.WriteLog(
            LogLevel.Info,
            "Shell",
            $"设置已应用：刷新间隔 {settings.RefreshIntervalSeconds} 秒，启动窗口 {ModeFormatter.WindowModeText(settings.StartupWindow)}。");
    }

    /// <summary>
    /// 让开机启动项的注册状态与设置保持一致。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 任务书 §11 规定「开机启动默认开启，可关闭」。若只把开关画在界面上而不落实注册，
    /// 设置与真实行为就会不一致——这属于「显示与实际不符」。
    /// </para>
    /// <para>
    /// 因此启动时做一次对账：设置与注册表不一致就按设置修正。
    /// <b>仅在以自身可执行文件运行时才碰注册表</b>；开发主机或测试宿主下只记录日志，
    /// 避免留下指向 <c>dotnet.exe</c> 的无效启动项。
    /// </para>
    /// </remarks>
    private void ReconcileStartupRegistration()
    {
        if (!StartupRegistration.CanRegister)
        {
            _context.WriteLog(
                LogLevel.Info,
                "Startup",
                "当前以开发主机运行，未对开机启动项做任何读写（避免留下无效启动项）。");
            return;
        }

        var intended = _context.Settings.LaunchOnStartup;
        var registered = StartupRegistration.IsEnabled();

        if (registered == intended)
        {
            _context.WriteLog(
                LogLevel.Info,
                "Startup",
                $"开机启动项与设置一致（{(registered ? "已注册" : "未注册")}）。");
            return;
        }

        var error = StartupRegistration.Apply(intended);
        if (error is not null)
        {
            _context.WriteLog(LogLevel.Warning, "Startup", $"开机启动项对账失败：{error}");
            return;
        }

        _context.WriteLog(
            LogLevel.Info,
            "Startup",
            intended ? "已按设置注册开机启动项。" : "已按设置移除开机启动项。");
    }

    /// <summary>
    /// 退出前把固件性能场景恢复为默认档位。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>只在本次会话确实写过性能场景时执行</b>：若用户本次从未切换过档位，
    /// 再写一次 0xC1 就是对固件状态的无谓改动，同样属于「自动变更」。
    /// </para>
    /// <para>
    /// 写入有上限的等待时间：退出路径不能被一次慢事务拖住。
    /// 超时或失败一律如实记日志，不谎称已恢复。
    /// </para>
    /// </remarks>
    private void RestoreDefaultStageOnExit()
    {
        if (!_context.SessionStageApplied)
        {
            _context.WriteLog(
                LogLevel.Info,
                "Shell",
                "本次会话未改动性能场景：退出时不写场景寄存器（避免产生无谓的自动变更）。");
            return;
        }

        try
        {
            var task = _context.Control.RestoreDefaultStageAsync(CancellationToken.None);
            if (!task.Wait(ExitStageRestoreTimeout))
            {
                _context.WriteLog(
                    LogLevel.Warning,
                    "Control",
                    $"退出前恢复默认性能场景超时（{(int)ExitStageRestoreTimeout.TotalSeconds} 秒）："
                    + "实际场景可能仍是退出前的档位，本次不宣称已恢复。");
                return;
            }

            _context.LogControlResult(task.Result, "退出前恢复固件默认性能场景");
        }
        catch (Exception ex)
        {
            // 退出路径不允许抛出自此中断：如实记录，继续正常收尾。
            _context.WriteLog(LogLevel.Warning, "Control", "退出前恢复默认性能场景失败：" + ex.Message);
        }
    }

    private bool ConfirmExit()
    {
        var answer = MessageBox.Show(
            "确定要完全退出 MSI-Control 吗？" + Environment.NewLine + Environment.NewLine +
            "· 退出后将停止采样与任何由本软件维持的调速。" + Environment.NewLine +
            "· 固件可持久执行的设置会继续生效；依赖持续进程的软件调速不会继续生效。" + Environment.NewLine +
            "· 若只想隐藏界面并保持运行，请选择「否」，然后关闭窗口或按 Esc 隐藏到托盘。",
            "MSI-Control · 完全退出",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        return answer == DialogResult.Yes;
    }

    private void RestorePlacements()
    {
        var screen = Screen.PrimaryScreen;
        if (screen is null)
        {
            return;
        }

        var area = screen.WorkingArea;

        if (_context.Settings.NormalWindowPlacement is { } normal)
        {
            _mainForm.ApplyPlacement(normal, area);
        }

        if (_context.Settings.MiniWindowPlacement is { } mini)
        {
            _miniForm.ApplyPlacement(mini, area);
        }
        else
        {
            // 迷你窗口无记录时停靠到右下角，不覆盖主窗口内容。
            _miniForm.StartPosition = FormStartPosition.Manual;
            _miniForm.Location = new Point(
                Math.Max(area.Left, area.Right - _miniForm.Width - 16),
                Math.Max(area.Top, area.Bottom - _miniForm.Height - 16));
        }
    }

    private void SavePlacements()
    {
        try
        {
            _context.UpdateWindowPlacements(_mainForm.CurrentPlacement(), _miniForm.CurrentPlacement());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.WriteLog(LogLevel.Warning, "Shell", $"保存窗口位置失败：{ex.Message}");
        }
    }

    private void OnUserPreferenceChanged(object? sender, EventArgs e)
        => _sync.Post(_ => RefreshTheme(), null);

    private void RefreshTheme()
    {
        if (_disposed)
        {
            return;
        }

        var mode = WindowsTheme.Read(out var note);
        if (note is not null)
        {
            _context.WriteLog(LogLevel.Warning, "Theme", note);
        }

        if (mode == _themeMode)
        {
            // UserPreferenceChanged 会因多种系统设置触发，只有主题真正变化才重绘。
            return;
        }

        _themeMode = mode;
        ApplyTheme();
        _context.WriteLog(LogLevel.Info, "Theme", $"已跟随系统主题切换到 {(_themeMode == ThemeMode.Dark ? "深色" : "浅色")}。");
    }

    private void ApplyTheme()
    {
        var palette = ThemePalette.For(_themeMode);
        _mainForm.ApplyPalette(palette);
        _miniForm.ApplyPalette(palette);
        _settingsForm?.ApplyPalette(palette);
    }
}
