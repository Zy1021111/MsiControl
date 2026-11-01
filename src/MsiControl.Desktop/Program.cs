using MsiControl.Application.SystemShortcuts;
using MsiControl.Application.Host;
using MsiControl.Application.Logging;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Shell;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop;

/// <summary>
/// 程序入口。
/// </summary>
/// <remarks>
/// <para>
/// 单实例语义（任务书 §10 / design.md §11）：重复启动不会打开第二个窗口，
/// 而是通过命名事件唤醒现有实例并把窗口带到前台。
/// </para>
/// <para>
/// 使用 <c>Local\</c> 前缀：单实例范围为当前登录会话，避免需要跨会话的全局对象权限。
/// </para>
/// <para>
/// <b>整合装配（任务 07）：</b>本方法同时是完整的组合根——先由应用层宿主完成
/// 「设备识别 → 能力基线 → 控制协调器 → 自动策略」装配，再把结果注入桌面壳。
/// 桌面壳自身不探测硬件、不硬编码能力等级（design.md §13.1、docs/接口说明.md §3.7）。
/// </para>
/// </remarks>
internal static class Program
{
    private const string MutexName = @"Local\MSI-Control.Desktop.SingleInstance";
    private const string ActivationEventName = @"Local\MSI-Control.Desktop.Activate";

    [STAThread]
    private static void Main()
    {
        using var singleInstance = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            return;
        }

        // 用完全限定名：本文件的 using 中存在 MsiControl.Application.* 命名空间，
        // 单独写 Application 会被解析到该命名空间而不是 WinForms 的 Application。
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

        // 宿主日志需要转发到桌面壳的轮转日志，但日志实例由 ShellContext 持有，
        // 而 ShellContext 又需要宿主的装配结果。用一个延后解析的转发器打破这个环：
        // 宿主在 Start() 之前不会写日志，此时 context 已赋值。
        ShellContext? context = null;
        var hostLog = new DelegateAppLog((level, category, message) =>
            context?.WriteLog(ToLogLevel(level), category, message));

        // 全局异常钩子：任何未处理异常都要落到轮转日志，杜绝「进程消失却无据可查」。
        // context 就绪前的 UI 异常极少发生；届时 ThreadException 对话框兜底，不静默。
        System.Windows.Forms.Application.ThreadException += (_, e) =>
        {
            context?.WriteLog(
                LogLevel.Error, "Unhandled",
                $"UI 线程未处理异常：{e.Exception.GetType().Name}：{e.Exception.Message}\n{e.Exception.StackTrace}");
        };
        System.Windows.Forms.Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            context?.WriteLog(
                LogLevel.Error, "Unhandled",
                $"非 UI 线程致命异常（IsTerminating={e.IsTerminating}）：{ex?.GetType().Name}：{ex?.Message}\n{ex?.StackTrace}");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            context?.WriteLog(
                LogLevel.Error, "Unhandled",
                $"未观察的任务异常：{e.Exception.GetType().Name}：{e.Exception.Message}\n{e.Exception.StackTrace}");
            e.SetObserved();
        };

        ApplicationHost? host = null;
        string? integrationFailure = null;

        try
        {
            host = ApplicationHost.CreateDefault(log: hostLog);
        }
        catch (Exception ex)
        {
            // 装配失败不能导致「界面都打不开」：降级为未适配状态并如实记录。
            integrationFailure = $"应用层装配失败：{ex.Message}";
        }

        using (host)
        {
            // 系统快捷功能（HWE §29）：四项系统接口对象与一个 Fn 事件源。
            // 它们不受 ApplicationHost 装配成败影响——即便硬件适配层降级，
            // 音量/亮度/触控板这些系统能力仍然可用，是否开放由能力键门控在界面侧决定。
            using var systemShortcuts = SystemShortcutServices.Create();
            var controlSink = host is null ? null : new ApplicationControlSink(host.Coordinator);
            context = ShellContextFactory.CreateDefault(
                device: host?.Device,
                capabilities: host?.Capabilities,
                monitoringProvider: host?.MonitoringProvider,
                controlSink: controlSink,
                stateReader: host,
                fanCurveEngine: host?.FanCurveEngine,
                audioControl: systemShortcuts.Audio,
                brightnessControl: systemShortcuts.Brightness,
                touchpadControl: systemShortcuts.Touchpad,
                systemShortcutLauncher: systemShortcuts.Launcher,
                bluetoothControl: systemShortcuts.Bluetooth,
                fnHotkeys: systemShortcuts.FnHotkeys,
                deviceControls: systemShortcuts.Devices,
                fnKeyChannels: BuildFnKeyChannels(systemShortcuts, host));

            // 用户手动确认档位 → 记进设置文件（下次启动按此恢复）。
            // 只有「手动入口 + 场景项读回成功」才会触发，自动策略不经过这里。
            if (controlSink is not null)
            {
                var sinkContext = context;
                controlSink.StageConfirmed += (_, stage) => RecordConfirmedStage(sinkContext, stage);
            }

            // 协调器只持有默认配置表，必须在这里把设置文件中的档位功耗目标注入回去：
            // 否则「改成 45 W → 重启应用 → 点档位」会把硬件写回默认 60 W（本轮实测证据）。
            ApplyPersistedStageTargets(host, context);

            if (integrationFailure is not null)
            {
                context.WriteLog(LogLevel.Error, "Host", integrationFailure + "；当前以「未适配」状态运行，全部写入入口保持禁用。");
            }

            using var activationSignal = new EventWaitHandle(
                initialState: false,
                mode: EventResetMode.AutoReset,
                name: ActivationEventName);

            using var applicationContext = new DesktopApplicationContext(context);

            var activationListener = new Thread(() => ListenForActivation(activationSignal, applicationContext))
            {
                IsBackground = true,
                Name = "MSI-Control.Activation"
            };

            activationListener.Start();

            // 启动自动策略：供电变化监听与启动恢复评估（任务书 §11）。
            // 自动切档默认关闭（ApplicationHostOptions.AutomaticStageSwitchingEnabled=false）：
            // 供电变化与配置档恢复都只记录判断依据，不下发写入。
            host?.Start();

            // 启动时恢复「上次用户确认过的档位」——这是关闭自动切档后唯一保留的自动写入，
            // 恢复的是用户自己确认过的值，不是策略推断值。
            StartStageRestore(host, context);

            // 必须用完全限定名：本文件顶部 using 了 MsiControl.Application.*，
            // 裸名 Application 会被解析到命名空间而不是 WinForms 的 Application
            // （见顶部 SetHighDpiMode/EnableVisualStyles/SetCompatibleTextRenderingDefault 三处的写法）。
            System.Windows.Forms.Application.Run(applicationContext);

            host?.Stop();
        }
    }

    /// <summary>
    /// 把设置文件里保存的档位 CPU 功耗目标注入控制协调器。
    /// </summary>
    /// <remarks>
    /// 失败不改变启动流程：协调器保持默认配置表，界面照常可用，
    /// 但必须在日志里如实写明「哪些档位没恢复」，不能静默吞掉。
    /// </remarks>
    /// <summary>
    /// 汇总 Fn 键的原始信号通道。
    /// </summary>
    /// <remarks>
    /// 两条通道来源不同：EC 热键码通道由宿主装配（它持有唯一的 EC 访问器），
    /// 键盘虚拟键通道由系统快捷服务装配。都取不到时返回空集合，桌面壳据此说明
    /// 「Fn 键无法被本程序观察」，而不是假装监听成功。
    /// </remarks>
    private static IReadOnlyList<IFnKeyChannel> BuildFnKeyChannels(
        SystemShortcutServices systemShortcuts,
        ApplicationHost? host)
    {
        var channels = new List<IFnKeyChannel>(2);
        if (host?.EcHotkeys is { } ec)
        {
            channels.Add(ec);
        }

        channels.Add(systemShortcuts.KeyChannel);
        return channels;
    }

    private static void ApplyPersistedStageTargets(ApplicationHost? host, ShellContext context)
    {
        if (host is null)
        {
            return;
        }

        var saved = context.Settings.StageTargets;
        if (saved.Count == 0)
        {
            return;
        }

        var problems = new List<string>();
        foreach (var settings in StageTargetResolver.Resolve(context.Settings, PerformanceProfileCatalog.Defaults))
        {
            var update = host.Coordinator.UpdateStageSettings(settings);
            if (!update.Ok)
            {
                problems.Add($"{settings.Stage}：{string.Join(" ", update.Problems)}");
            }
        }

        if (problems.Count == 0)
        {
            context.WriteLog(
                LogLevel.Info,
                "Settings",
                "已按设置文件恢复档位 CPU 功耗目标：" + string.Join("，", saved) + "。");
        }
        else
        {
            context.WriteLog(
                LogLevel.Warning,
                "Settings",
                "设置文件中的档位功耗目标部分未能恢复：" + string.Join("；", problems));
        }
    }

    /// <summary>
    /// 记录用户手动确认的档位到设置文件。
    /// </summary>
    /// <remarks>
    /// 落盘失败必须明说：界面已经显示「已切到 X」，但下次启动不会恢复，
    /// 这个偏差要让用户看得到，而不是静默过去。
    /// </remarks>
    private static void RecordConfirmedStage(ShellContext context, PerformanceStage stage)
    {
        if (context.TryRecordConfirmedStage(stage, out var error))
        {
            return;
        }

        context.WriteLog(
            LogLevel.Warning,
            "Settings",
            $"用户确认的档位 {stage} 未能写入设置文件（{error}）；本次不谎称已记住，下次启动不会自动恢复该档位。");
    }

    /// <summary>
    /// 启动时把机器恢复到上次用户确认的档位。
    /// </summary>
    /// <remarks>
    /// 不阻塞界面：写到后台执行，失败只记日志。成功时标记「本会话已写过性能场景」，
    /// 退出流程据此决定是否需要恢复固件默认场景。
    /// </remarks>
    private static void StartStageRestore(ApplicationHost? host, ShellContext context)
    {
        if (host is null)
        {
            return;
        }

        var stage = context.Settings.LastConfirmedStage;
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await host.RestoreUserConfirmedStageAsync(stage).ConfigureAwait(false);
                if (outcome is null)
                {
                    return;
                }

                var applied = outcome.Result.Settings.Any(s =>
                    s.Setting.EndsWith("stage", StringComparison.Ordinal)
                    && s.Status == OperationStatus.Succeeded);
                if (applied)
                {
                    context.MarkSessionStageApplied();
                }
            }
            catch (Exception ex)
            {
                context.WriteLog(
                    LogLevel.Warning,
                    "StartupRestore",
                    "启动恢复上次确认档位失败：" + ex.Message);
            }
        });
    }

    private static void ListenForActivation(EventWaitHandle signal, DesktopApplicationContext applicationContext)    {
        while (true)
        {
            signal.WaitOne();
            applicationContext.ActivateFromOtherInstance();
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivationEventName, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // 无法唤醒现有实例时静默退出：不打开第二个窗口是更重要的约束。
        }
    }

    private static LogLevel ToLogLevel(AppLogLevel level) => level switch
    {
        AppLogLevel.Debug => LogLevel.Debug,
        AppLogLevel.Warning => LogLevel.Warning,
        AppLogLevel.Error => LogLevel.Error,
        _ => LogLevel.Info
    };
}
