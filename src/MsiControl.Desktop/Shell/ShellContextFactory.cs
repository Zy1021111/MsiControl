using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Fans;
using MsiControl.Persistence;
using MsiControl.Persistence.Configuration;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 组装桌面壳上下文。
/// </summary>
/// <remarks>
/// <para>
/// 这是任务 02 与「整合负责人（任务 07）」的接缝：任务 07 在装配完整应用时，
/// 传入真实设备标识、真实能力基线与真实监控实现；而未接入时本工厂给出的默认值
/// 必须让界面呈现**诚实的未适配状态**，而不是模拟数据。
/// </para>
/// <para>
/// 默认值刻意保持「全部禁用」：设备标识为未知 → 能力集合为空 →
/// 一切写入入口关闭并显示原因。这与任务书 §16.1「未登记即禁用」的安全方向一致。
/// </para>
/// </remarks>
public static class ShellContextFactory
{
    /// <summary>
    /// 创建默认上下文。
    /// </summary>
    /// <param name="paths">数据路径。为空时使用 <c>%LOCALAPPDATA%\MSI-Control</c>。</param>
    /// <param name="device">设备标识。为空时为 <see cref="DeviceIdentity.Unknown"/>。</param>
    /// <param name="capabilities">能力集合。为空时为 <see cref="DeviceCapabilities.Empty"/>（全部禁用）。</param>
    /// <param name="monitoringProvider">监控数据源。为空时使用「未适配」实现。</param>
    /// <param name="controlSink">控制请求出口。为空时使用「未适配」实现。</param>
    /// <param name="stateReader">硬件状态读回源（风扇模式/性能档位）。为空时桌面壳不做状态读回。</param>
    /// <param name="fanCurveEngine">软件曲线引擎。为空时桌面壳不提供风扇曲线编辑功能。</param>
    public static ShellContext CreateDefault(
        AppPaths? paths = null,
        DeviceIdentity? device = null,
        DeviceCapabilities? capabilities = null,
        IMonitoringProvider? monitoringProvider = null,
        IControlRequestSink? controlSink = null,
        IHardwareStateReader? stateReader = null,
        FanCurveEngine? fanCurveEngine = null,
        IAudioEndpointControl? audioControl = null,
        IDisplayBrightnessControl? brightnessControl = null,
        IPointingDeviceControl? touchpadControl = null,
        ISystemShortcutLauncher? systemShortcutLauncher = null,
        IBluetoothRadioControl? bluetoothControl = null,
        IFnHotkeySource? fnHotkeys = null,
        SystemDeviceControls? deviceControls = null,
        IReadOnlyList<IFnKeyChannel>? fnKeyChannels = null)
    {
        paths ??= new AppPaths();
        paths.EnsureCreated();

        var store = new JsonSettingsStore(paths);
        var loadResult = store.Load();

        if (loadResult.Source != SettingsLoadSource.File)
        {
            // 首次运行（或原文件损坏/过新已被隔离）时落盘一份默认设置。
            // 目的：让配置文件的路径与格式对用户可见、可手工编辑，
            // 避免出现「设置界面有值、磁盘上却没有文件」这种难以排查的状态。
            try
            {
                store.Save(loadResult.Settings);
                loadResult = loadResult with
                {
                    Notes = loadResult.Notes
                        .Append($"已写入默认设置文件：{paths.SettingsFilePath}")
                        .ToArray()
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 写不了不是致命错误：本次运行仍使用内存中的默认设置。
                loadResult = loadResult with
                {
                    Notes = loadResult.Notes.Append($"写入默认设置文件失败：{ex.Message}").ToArray()
                };
            }
        }

        var redactor = new LogRedactor(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var log = new RotatingLogWriter(paths.CurrentLogFilePath, redactor);

        var identity = device ?? DeviceIdentity.Unknown;
        var caps = capabilities ?? DeviceCapabilities.Empty;
        var provider = monitoringProvider ?? new NotAdaptedMonitoringProvider();
        var control = controlSink ?? new NotAdaptedControlRequestSink();

        var interval = TimeSpan.FromSeconds(loadResult.Settings.RefreshIntervalSeconds);
        var snapshots = new SnapshotStream(provider, interval);

        var context = new ShellContext(
            paths,
            store,
            log,
            loadResult,
            identity,
            caps,
            snapshots,
            control,
            loadResult.Settings,
            stateReader,
            fanCurveEngine is null
                ? null
                : new FanCurveServices(fanCurveEngine, new FanCurveProfileStore(paths.RootDirectory)),
            audioControl,
            brightnessControl,
            touchpadControl,
            systemShortcutLauncher,
            bluetoothControl,
            fnHotkeys,
            deviceControls,
            fnKeyChannels);

        log.Write(LogLevel.Info, "Shell", $"MSI-Control 桌面壳启动。数据目录：{paths.RootDirectory}");
        log.Write(
            LogLevel.Info,
            "Shell",
            $"设备标识：{identity.Model} / {identity.BoardCode}（已识别={identity.IsIdentified}）。");

        foreach (var note in loadResult.Notes)
        {
            log.Write(loadResult.IsDegraded ? LogLevel.Warning : LogLevel.Info, "Settings", note);
        }

        if (loadResult.IsDegraded)
        {
            log.Write(
                LogLevel.Warning,
                "Settings",
                $"设置加载降级：{loadResult.Source}，隔离文件={loadResult.QuarantinedFilePath ?? "无"}。");
        }

        log.Write(
            caps.Count == 0 ? LogLevel.Warning : LogLevel.Info,
            "Shell",
            caps.Count == 0
                ? "能力集合为空：全部功能入口将按「待验证」禁用并显示原因。"
                : $"能力集合含 {caps.Count} 项。");

        var missing = caps.MissingKeys();
        if (caps.Count > 0 && missing.Count > 0)
        {
            log.Write(LogLevel.Warning, "Shell", $"能力基线与能力键清单脱节，缺少：{string.Join('、', missing)}");
        }

        return context;
    }
}
