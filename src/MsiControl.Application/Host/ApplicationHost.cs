using MsiControl.Application.Control;
using MsiControl.Application.Integration;
using MsiControl.Application.Logging;
using MsiControl.Application.Policy;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Fans;
using MsiControl.Graphics;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using MsiControl.Hardware.Msi.Hotkeys;
using MsiControl.Hardware.Msi.Power;
using MsiControl.Hardware.Msi.Transport;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using MsiControl.Monitoring;

namespace MsiControl.Application.Host;

/// <summary>宿主装配参数。</summary>
public sealed record ApplicationHostOptions
{
    /// <summary>默认参数。</summary>
    public static ApplicationHostOptions Default { get; } = new();

    /// <summary>供电轮询间隔。</summary>
    public TimeSpan PowerPollInterval { get; init; } = PollingPowerSupplyWatcher.DefaultInterval;

    /// <summary>是否在启动时自动启用供电策略。默认启用（任务书 §11）。</summary>
    public bool AutoStartPowerPolicy { get; init; } = true;

    /// <summary>是否在启动时检测第三方控制工具。默认启用（任务书 §11）。</summary>
    public bool ScanThirdPartyConflicts { get; init; } = true;

    /// <summary>是否在启动时评估并尝试执行启动恢复。默认启用（任务书 §11）。</summary>
    public bool AutoRestoreOnStartup { get; init; } = true;

    /// <summary>
    /// 是否允许**自动**切换性能档位（供电变化自动节能、启动恢复自动应用配置档）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 默认 <see langword="false"/>（2026-10-31 按用户要求收紧）：性能档位只在用户于界面/
    /// 托盘显式确认后切换，运行期间不发生任何自动变更。策略对象仍会计算并记录判断依据，
    /// 但**不下发任何写入**。
    /// </para>
    /// <para>
    /// 关闭后仍保留的唯一自动写入是「启动时恢复上次用户确认过的档位」
    /// （<see cref="RestoreUserConfirmedStageAsync"/>）——它恢复的是用户自己确认过的值，
    /// 不是策略推断出来的值。
    /// </para>
    /// </remarks>
    public bool AutomaticStageSwitchingEnabled { get; init; }

    /// <summary>协调器行为参数。</summary>
    public ControlCoordinatorOptions Coordinator { get; init; } = ControlCoordinatorOptions.Default;
}

/// <summary>
/// 应用层宿主：把设备识别、能力基线、控制协调器与自动策略装配成一个可运行的整体。
/// </summary>
/// <remarks>
/// <para>
/// 这是任务 07 作为<b>整合负责人</b>交付的组合根。它解决的问题是：
/// 桌面壳不应自己去问硬件「你是谁、你能做什么」，而应由宿主把
/// 「真实设备标识 + 真实能力基线 + 协调器 + 自动策略」一次性装配好再注入
/// （docs/桌面框架说明.md、docs/接口说明.md §5 任务 07）。
/// </para>
/// <para><b>依赖方向</b>（design.md §13.1）：</para>
/// <code>
/// Desktop  →  Application（本类型）  →  Core 接口
///                                          ↓
///                                   Hardware.Msi（能力基线，只读）
/// </code>
/// <para>
/// 本类型不引用 <c>MsiControl.Desktop</c>，桌面侧的接缝适配由桌面壳自己完成。
/// </para>
/// <para><b>诚实性保证：</b>适配层未注入时协调器一律返回「未接入」，
/// 采样使用调用方提供的实现或「未适配」实现，绝不用模拟数据填充界面。
/// </para>
/// </remarks>
public sealed class ApplicationHost : IDisposable, IHardwareStateReader
{
    private readonly ApplicationHostOptions _options;
    private readonly IAppLog _log;
    private readonly IPerformanceHardware? _performanceHardware;
    private readonly IFanHardware? _fanHardware;
    private readonly IGraphicsHardware? _graphicsHardware;
    private readonly IPowerSupplyWatcher _powerWatcher;
    private readonly bool _ownsPowerWatcher;
    private readonly bool _ownsMonitoringProvider;

    // 读回失败日志的限流时间戳：读回随采样节拍进行（默认 1 秒一次），
    // EC 瞬时故障若每次都记 Warning 会刷爆轮转日志，因此同类失败 60 秒最多记一次。
    private DateTimeOffset _lastFanReadFailureLogUtc;
    private DateTimeOffset _lastStageReadFailureLogUtc;
    private DateTimeOffset _lastGraphicsReadFailureLogUtc;

    private Task? _startupTask;
    private bool _started;
    private bool _disposed;
    private PawnIoEcRegisterAccess? _ecAccess;
    private EcHotkeyChannel? _ecHotkeyChannel;
    private bool _ownsEcAccess;
    private readonly FanCurveEngine? _fanCurveEngine;

    /// <summary>
    /// EC 写白名单：风扇模式 0xD4、强冷（Cooler Boost）0x98、性能场景（Shift）0xD2、
    /// 显卡 MUX 0x2E、显卡切换触发 0xD1。
    /// </summary>
    /// <remarks>
    /// <para>风扇与性能三个地址有本机受控实验证据（HWE §17、§20）；显卡两地址的协议依据
    /// 与本机只读对照见 HWE §21（ElXreno/msi-gpu-switcher，MIT），
    /// 实际放行还须能力键 <c>graphics.mode</c> 达 Verified（双重条件，缺一不可）。</para>
    /// <para><b>按用户决定（2026-10-17 下午）启用强冷</b>：此前「暂不启用」的决定由叶哥解除，
    /// 0x98 加入白名单；能力键 fan.fullspeed 同步升 Verified，双重条件齐备后才可写入。</para>
    /// </remarks>
    private static readonly IReadOnlySet<byte> EcWriteWhitelist =
        new HashSet<byte>
        {
            Ms15M3EcRegisterMap.FanMode.Address,      // 0xD4
            Ms15M3EcRegisterMap.CoolerBoost.Address,  // 0x98
            Ms15M3EcRegisterMap.ShiftMode.Address,    // 0xD2
            Ms15M3EcRegisterMap.CpuFanPercent.Address, // 0x71 占空比（软件曲线引擎，HWE §23）
            Ms15M3EcRegisterMap.GpuFanPercent.Address, // 0x89 占空比（软件曲线引擎，HWE §23）
            Ms15M3EcRegisterMap.GpuMux.Address,       // 0x2E
            Ms15M3EcRegisterMap.GpuSwitchTrigger.Address // 0xD1
        };

    private ApplicationHost(
        ApplicationHostOptions options,
        IAppLog log,
        DeviceInfoResult deviceInfo,
        DeviceCapabilities capabilities,
        IMonitoringProvider? monitoringProvider,
        IPerformanceHardware? performanceHardware,
        ControlCoordinator coordinator,
        PowerSupplyPolicy powerPolicy,
        ILastAppliedConfigSource lastAppliedConfigSource,
        IPowerSupplyWatcher powerWatcher,
        bool ownsPowerWatcher,
        ThirdPartyConflictReport conflicts,
        bool ownsMonitoringProvider = false,
        PawnIoEcRegisterAccess? ecRegisterAccess = null,
        bool ownsEcRegisterAccess = false,
        IFanHardware? fanHardware = null,
        FanCurveEngine? fanCurveEngine = null,
        IGraphicsHardware? graphicsHardware = null,
        IFnKeyChannel? fnKeyChannel = null)
    {
        _options = options;
        _log = log;
        _performanceHardware = performanceHardware;
        _fanHardware = fanHardware;
        _graphicsHardware = graphicsHardware;
        _fanCurveEngine = fanCurveEngine;
        _powerWatcher = powerWatcher;
        _ownsPowerWatcher = ownsPowerWatcher;
        _ownsMonitoringProvider = ownsMonitoringProvider;
        _ecAccess = ecRegisterAccess;
        _ownsEcAccess = ownsEcRegisterAccess;
        _ecHotkeyChannel = fnKeyChannel as EcHotkeyChannel;

        DeviceInfo = deviceInfo;
        Capabilities = capabilities;
        MonitoringProvider = monitoringProvider;
        Coordinator = coordinator;
        PowerPolicy = powerPolicy;
        LastAppliedConfigSource = lastAppliedConfigSource;

        Modules = ModuleRegistry.CreateDefault(
            monitoringIntegrated: monitoringProvider is not null,
            adapters: coordinator.AdapterAvailability);

        Conflicts = conflicts;

        ExitBehavior = Policy.ExitBehavior.Describe(capabilities);

        StartupRestore = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(deviceInfo.Device, powerWatcher.Current),
            lastAppliedConfigSource);

        coordinator.OutcomePublished += OnCoordinatorOutcome;
    }

    /// <summary>设备标识读取结果（含缺失项说明）。</summary>
    public DeviceInfoResult DeviceInfo { get; }

    /// <summary>本机设备标识。由注册表的 SMBIOS 快照规范化得到。</summary>
    public DeviceIdentity Device => DeviceInfo.Device;

    /// <summary>本机能力集合。取自 <see cref="CapabilityBaseline.ForDevice"/>，未适配机型为全禁用。</summary>
    public DeviceCapabilities Capabilities { get; }

    /// <summary>监控数据源。调用方未显式提供时由 <see cref="CreateDefault"/> 装配真实实现并由本宿主负责释放。</summary>
    public IMonitoringProvider? MonitoringProvider { get; }

    /// <summary>控制协调器。桌面壳把它适配为界面侧的控制出口。</summary>
    public ControlCoordinator Coordinator { get; }

    /// <summary>风扇适配层。用于只读状态读回（EC 0xD4）；未装配时为 <see langword="null"/>。</summary>
    public IFanHardware? FanHardware => _fanHardware;

    /// <summary>显卡模式模块。用于只读状态读回（EC 0x2E/0xD1 + UEFI，HWE §21）；未装配时为 <see langword="null"/>。</summary>
    public IGraphicsHardware? GraphicsHardware => _graphicsHardware;

    /// <summary>软件曲线引擎。EC 通道就绪时装配；未装配（通道不可用）时为 <see langword="null"/>。</summary>
    public FanCurveEngine? FanCurveEngine => _fanCurveEngine;

    /// <summary>
    /// Fn 键的 EC 热键码通道（只读轮询 EC <c>0xC0</c>）。
    /// EC 通道不可用时为 <see langword="null"/>；启动是否成功由调用方查询并如实记录。
    /// </summary>
    public IFnKeyChannel? EcHotkeys => _ecHotkeyChannel;

    /// <summary>供电变化策略。</summary>
    public PowerSupplyPolicy PowerPolicy { get; }

    /// <summary>「最近成功应用配置」来源。</summary>
    public ILastAppliedConfigSource LastAppliedConfigSource { get; }

    /// <summary>模块接入状态表。</summary>
    public IReadOnlyList<ModuleStatus> Modules { get; }

    /// <summary>第三方控制工具检测报告（启动时一次）。</summary>
    public ThirdPartyConflictReport Conflicts { get; }

    /// <summary>退出行为说明。</summary>
    public ExitBehaviorSummary ExitBehavior { get; }

    /// <summary>启动恢复决定。在 <see cref="Start"/> 之前为初步评估结果，启动后为最终结论。</summary>
    public StartupRestoreDecision StartupRestore { get; private set; }

    /// <summary>当前供电状态。</summary>
    public PowerSupplyKind PowerSupply => _powerWatcher.Current;

    /// <summary>自动策略（供电变化 / 启动恢复）实际下发后发布。可能在任意线程触发。</summary>
    public event EventHandler<ControlOutcome>? AutomaticPolicyApplied;

    /// <summary>
    /// 按当前可用实现装配宿主。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>默认装配（任务 07 提示词（六）「先接可用的模块」）：</b>任务 02–06 的模块工程已全部交付，
    /// 因此本方法对四个模块参数采用「为空时装配真实实现」的语义，并让它们**共享同一个只读传输通道**
    /// （<see cref="WmiMsiAcpiTransport"/>，白名单只放行 <c>Get_*</c>）：
    /// </para>
    /// <list type="bullet">
    /// <item>监控：<see cref="MonitoringProviderFactory.CreateDefault"/>；</item>
    /// <item>风扇：<see cref="FanHardwareMs15M3"/>；</item>
    /// <item>性能：<see cref="Ms15M3PerformanceHardware"/>；</item>
    /// <item>显卡：<see cref="GraphicsHardwareService"/> + <see cref="GraphicsTopologyProbe"/>。</item>
    /// </list>
    /// <para>
    /// <b>装配不放宽任何权限：</b>所有适配层都以 <c>capabilities</c> 为门控依据，
    /// 未达 <see cref="CapabilityLevel.Verified"/> 的能力键一律拒绝写入；
    /// 监控侧同样按能力键决定是否读取。调用方显式传入实现时（测试替身、自定义装配），
    /// 以传入实现为准，且释放责任留给调用方。
    /// </para>
    /// </remarks>
    /// <param name="deviceInfoSource">设备标识来源。为空时使用注册表 SMBIOS 实现。</param>
    /// <param name="monitoringProvider">监控数据源（任务 03）。为空时装配真实实现（见备注）。</param>
    /// <param name="fanHardware">风扇适配层（任务 04）。为空时装配真实实现（见备注）。</param>
    /// <param name="performanceHardware">性能适配层（任务 05）。为空时装配真实实现（见备注）。</param>
    /// <param name="graphicsHardware">显卡适配层（任务 06）。为空时装配真实实现（见备注）。</param>
    /// <param name="powerStatusProbe">供电读取器。为空时使用 Win32 实现。</param>
    /// <param name="lastAppliedConfigSource">最近成功应用配置来源。为空时视为「无记录」。</param>
    /// <param name="options">装配参数。为空时使用默认值。</param>
    /// <param name="log">日志出口。为空时不记录。</param>
    /// <param name="processProbe">进程枚举来源。为空时使用系统实现。</param>
    /// <returns>已装配但**尚未启动**的宿主。</returns>
    public static ApplicationHost CreateDefault(
        IDeviceInfoSource? deviceInfoSource = null,
        IMonitoringProvider? monitoringProvider = null,
        IFanHardware? fanHardware = null,
        IPerformanceHardware? performanceHardware = null,
        IGraphicsHardware? graphicsHardware = null,
        IPowerStatusProbe? powerStatusProbe = null,
        ILastAppliedConfigSource? lastAppliedConfigSource = null,
        ApplicationHostOptions? options = null,
        IAppLog? log = null,
        IProcessProbe? processProbe = null)
    {
        var settings = options ?? ApplicationHostOptions.Default;
        var appLog = log ?? NullAppLog.Instance;

        var deviceInfo = (deviceInfoSource ?? new RegistryDeviceInfoSource()).Read();
        var capabilities = CapabilityBaseline.ForDevice(deviceInfo.Device);

        // 四个业务模块共享同一个只读通道：Transport 无状态（每次调用新建 ManagementScope），
        // 共享可避免重复初始化；白名单决定它不可能写入硬件。
        var transport = new WmiMsiAcpiTransport();

        // 为空时装配真实实现。显式传入（测试替身 / 自定义装配）时以传入为准，责任不转移。
        var ownsMonitoring = monitoringProvider is null;

        // ── EC 访问器（进程内通道归一）────────────────────────────────
        // 创建一次，同时供风扇硬件、性能硬件、显卡写入方与监控工厂使用：
        // 所有 EC 事务经同一把访问锁串行化，避免多通道并发端口 IO 事务穿插
        // （实测会导致瞬时超时率暴涨）。
        // 写白名单：0xD4（风扇模式）+ 0x98（强冷）+ 0xD2（性能场景）
        //         + 0x2E/0xD1（显卡 MUX 与切换触发，HWE §21）。
        PawnIoEcRegisterAccess? ecAccess = null;
        var ownsEcAccess = false;
        Ms15M3GraphicsModeWriter? graphicsWriter = null;
        Ms15M3GraphicsModeStateReader? graphicsStateReader = null;
        EcHotkeyChannel? ecHotkeyChannel = null;
        FanCurveEngine? fanCurveEngine = null;
        IFanHardware resolvedFanHardware;
        if (fanHardware is not null)
        {
            resolvedFanHardware = fanHardware;
        }
        else
        {
            var ecCreation = PawnIoEcAccess.TryCreate(EcWriteWhitelist);
            if (ecCreation.Succeeded && ecCreation.Channel is not null)
            {
                ecAccess = new PawnIoEcRegisterAccess(ecCreation.Channel);
                resolvedFanHardware = new FanHardwareMs15M3Ec(ecAccess, capabilities);
                ownsEcAccess = true;
                // 性能：EC 通道可用时优先场景型适配器（0xD2 三编码有实验证据，HWE §20）；
                // 通道不可用时由下方兜底装配回落 WMI 实现（其写入仍被能力与协议双闸门拦截）。
                // 性能：EC 场景 + CPU 功耗墙（MSR 0x610，HWE §26）双通道。
                // 功耗墙通道由适配器持有并释放，与监控侧的 RAPL 读取通道各自独立句柄。
                performanceHardware ??= new PerformanceHardwareMs15M3Ec(
                    ecAccess,
                    capabilities,
                    new RaplCpuPowerLimitChannel());
                // 显卡写入方：协议依据 HWE §21；是否可写仍由能力键 graphics.mode 把守。
                graphicsWriter = Ms15M3GraphicsModeWriter.TryCreateFrom(ecAccess);
                // 显卡状态读回方：EC 0x2E/0xD1 + UEFI 目标变量（HWE §21），只读。
                graphicsStateReader = Ms15M3GraphicsModeStateReader.TryCreateFrom(ecAccess);
                // Fn 键的 EC 通道：只读轮询 0xC0（热键码），不写 0xC1（HWE §33）。
                ecHotkeyChannel = new EcHotkeyChannel(ecAccess);
                // 软件曲线引擎：占空比写通道（0x71/0x89，HWE §23）；是否可写由 fan.curve 能力键把守。
                if (resolvedFanHardware is FanHardwareMs15M3Ec ecFanHardware)
                {
                    fanCurveEngine = new FanCurveEngine(
                        ecFanHardware,
                        channel => channel == FanChannel.Cpu
                            ? ecAccess.ReadRegister(Ms15M3EcRegisterMap.CpuTemperature.Address)
                            : ecAccess.ReadRegister(Ms15M3EcRegisterMap.GpuTemperature.Address),
                        message => appLog.Write(AppLogLevel.Warning, "FanCurveEngine", message));
                }

                appLog.Write(
                    AppLogLevel.Info,
                    "Host",
                    "EC 通道就绪（单通道共享；写白名单 0xD4/0x98/0xD2/0x71/0x89/0x2E/0xD1："
                    + "风扇模式、强冷、性能场景、占空比、显卡 MUX 与切换触发）。");
            }
            else
            {
                resolvedFanHardware = new FanHardwareMs15M3(transport, capabilities);
                appLog.Write(
                    AppLogLevel.Warning,
                    "Host",
                    "风扇硬件：EC 通道不可用（" + ecCreation.Error + "），回落为 WMI 只读适配器。");
            }
        }

        monitoringProvider ??= MonitoringProviderFactory.CreateDefault(
            capabilities,
            transport: transport,
            ecRegisterAccess: ecAccess);

        performanceHardware ??= new Ms15M3PerformanceHardware(transport, capabilities);
        // EC 写入方就绪时接入真实切换通道；未就绪时回退「不支持」写入方（附原因，不产生假成功）。
        // 读回方同理：EC 通道就绪时「当前模式」直读 MUX 三载体，未就绪时回落拓扑推断。
        graphicsHardware ??= new GraphicsHardwareService(
            new GraphicsTopologyProbe(),
            capabilities,
            writer: graphicsWriter,
            stateReader: graphicsStateReader);

        var coordinator = new ControlCoordinator(
            capabilities,
            performanceHardware,
            resolvedFanHardware,
            graphicsHardware,
            stageSettings: null,
            settings.Coordinator,
            appLog);

        var watcher = new PollingPowerSupplyWatcher(powerStatusProbe, settings.PowerPollInterval);

        return new ApplicationHost(
            settings,
            appLog,
            deviceInfo,
            capabilities,
            monitoringProvider,
            performanceHardware,
            coordinator,
            new PowerSupplyPolicy(),
            lastAppliedConfigSource ?? new NoLastAppliedConfigSource(),
            watcher,
            ownsPowerWatcher: true,
            conflicts: BuildConflictReport(settings, processProbe),
            ownsMonitoringProvider: ownsMonitoring,
            ecRegisterAccess: ecAccess,
            ownsEcRegisterAccess: ownsEcAccess,
            fanHardware: resolvedFanHardware,
            fanCurveEngine: fanCurveEngine,
            graphicsHardware: graphicsHardware,
            fnKeyChannel: ecHotkeyChannel);
    }

    /// <summary>
    /// 以显式部件构造宿主。供测试与自定义装配使用。
    /// </summary>
    /// <param name="deviceInfo">设备标识读取结果。</param>
    /// <param name="capabilities">能力集合。</param>
    /// <param name="coordinator">已构造的协调器。</param>
    /// <param name="powerWatcher">供电监听器。</param>
    /// <param name="performanceHardware">性能适配层，用于读取实际档位。可为 <see langword="null"/>。</param>
    /// <param name="monitoringProvider">监控数据源，可为 <see langword="null"/>。</param>
    /// <param name="lastAppliedConfigSource">最近成功应用配置来源，可为 <see langword="null"/>。</param>
    /// <param name="options">装配参数。</param>
    /// <param name="log">日志出口。</param>
    /// <param name="conflicts">第三方冲突报告。为空时不做检测。</param>
    /// <param name="ownsPowerWatcher">是否由宿主负责释放供电监听器。</param>
    /// <param name="fanHardware">风扇适配层，用于只读状态读回。可为 <see langword="null"/>。</param>
    public static ApplicationHost CreateExplicit(
        DeviceInfoResult deviceInfo,
        DeviceCapabilities capabilities,
        ControlCoordinator coordinator,
        IPowerSupplyWatcher powerWatcher,
        IPerformanceHardware? performanceHardware = null,
        IMonitoringProvider? monitoringProvider = null,
        ILastAppliedConfigSource? lastAppliedConfigSource = null,
        ApplicationHostOptions? options = null,
        IAppLog? log = null,
        ThirdPartyConflictReport? conflicts = null,
        bool ownsPowerWatcher = false,
        IFanHardware? fanHardware = null,
        IGraphicsHardware? graphicsHardware = null)
    {
        ArgumentNullException.ThrowIfNull(deviceInfo);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(powerWatcher);

        return new ApplicationHost(
            options ?? ApplicationHostOptions.Default,
            log ?? NullAppLog.Instance,
            deviceInfo,
            capabilities,
            monitoringProvider,
            performanceHardware,
            coordinator,
            new PowerSupplyPolicy(),
            lastAppliedConfigSource ?? new NoLastAppliedConfigSource(),
            powerWatcher,
            ownsPowerWatcher,
            conflicts ?? EmptyConflictReport(),
            fanHardware: fanHardware,
            graphicsHardware: graphicsHardware);
    }

    /// <summary>
    /// 启动宿主：记录装配摘要、启用供电监听、异步评估启动恢复。
    /// </summary>
    /// <remarks>
    /// 刻意不阻塞调用方：启动恢复涉及硬件写入，不应拖慢界面呈现（任务书 §16「长操作不阻塞 UI」）。
    /// 需要确定性等待时可调用 <see cref="WaitForStartupAsync"/>。
    /// </remarks>
    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
        {
            return;
        }

        _started = true;
        LogAssemblySummary();

        if (_options.AutoStartPowerPolicy)
        {
            _powerWatcher.Changed += OnSupplyChanged;
            _powerWatcher.Start();
            _log.Write(
                AppLogLevel.Info,
                "Policy",
                $"供电策略已启用；当前供电：{SupplyText(_powerWatcher.Current)}；" +
                $"探测方式：{DescribeWatcher()}（仅只读探测，不启动外部进程）。");
        }

        if (_options.AutoRestoreOnStartup)
        {
            _startupTask = Task.Run(() => RunStartupRestoreAsync());
        }
    }

    /// <summary>等待启动恢复流程结束。仅供测试与诊断使用。</summary>
    public Task WaitForStartupAsync() => _startupTask ?? Task.CompletedTask;

    /// <summary>停止供电监听。</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _powerWatcher.Changed -= OnSupplyChanged;
        _powerWatcher.Stop();
    }

    /// <summary>立即重新读取供电状态并按策略处理。</summary>
    /// <returns>本次处理产生的控制结果；无需动作时为 <see langword="null"/>。</returns>
    /// <remarks>供唤醒后或手动触发使用；正常路径由监听器驱动。</remarks>
    public async Task<ControlOutcome?> RefreshPowerSupplyAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_powerWatcher is PollingPowerSupplyWatcher polling)
        {
            // Refresh 在状态变化时触发 Changed，从而走同一条处理路径。
            polling.Refresh();
            return null;
        }

        return await ApplyPowerDecisionAsync(_powerWatcher.Current, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>处理一次系统唤醒。</summary>
    /// <returns>策略结论（当前实现为「不自动重放写入」，并给出依据）。</returns>
    public static PowerSupplyDecision HandleResume() => PowerSupplyPolicy.OnResumed();

    /// <summary>
    /// 启动时恢复「上次用户确认过的性能档位」。
    /// </summary>
    /// <param name="stage">设置文件里记录的档位；<see langword="null"/> 表示从未确认过。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>本次写入的结果；不发生写入时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <para>
    /// 这是关闭自动切档后**唯一保留的自动写入**，依据是「恢复的是用户自己确认过的值」，
    /// 而不是策略推断出的值。请求以 <see cref="ControlRequestOrigin.Automatic"/> 提交，
    /// 因此不会覆盖仍在排队的手动请求（任务书 §11）。
    /// </para>
    /// <para>
    /// 三道拒绝条件（均不写入，并写明原因）：无记录、本机设备未识别、档位无已证实编码。
    /// </para>
    /// </remarks>
    public async Task<ControlOutcome?> RestoreUserConfirmedStageAsync(
        PerformanceStage? stage,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (stage is not { } target)
        {
            _log.Write(
                AppLogLevel.Info,
                "StartupRestore",
                "设置中没有「上次用户确认的档位」：本次启动不改动性能模式。");
            return null;
        }

        if (!Device.IsIdentified)
        {
            _log.Write(
                AppLogLevel.Warning,
                "StartupRestore",
                "本机设备标识未识别：无法判断记录是否适用于本机，不恢复性能档位。");
            return null;
        }

        if (!PerformanceShiftMap.IsStageSupported(target))
        {
            _log.Write(
                AppLogLevel.Warning,
                "StartupRestore",
                $"记录中的档位 {target} 在本机没有已证实的场景编码，不恢复性能档位。");
            return null;
        }

        _log.Write(
            AppLogLevel.Info,
            "StartupRestore",
            $"按设置文件恢复上次用户确认的档位：{ControlIntent.StageText(target)}"
            + "（该值为用户手动确认过的档位；以自动策略来源提交，不会覆盖仍在排队的手动请求）。");

        var outcome = await Coordinator
            .RequestPerformanceStageAsync(target, ControlRequestOrigin.Automatic, cancellationToken)
            .ConfigureAwait(false);

        PublishAutomatic(outcome);
        return outcome;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        Coordinator.OutcomePublished -= OnCoordinatorOutcome;
        Coordinator.Dispose();

        if (_ownsMonitoringProvider && MonitoringProvider is IDisposable ownedProvider)
        {
            // 仅释放由本宿主创建的实现（内部持有 NVML 会话等资源）；
            // 调用方显式传入的提供者由调用方负责释放。
            ownedProvider.Dispose();
        }

        // EC 热键轮询先停：它同样周期性地占用 EC 事务。
        _ecHotkeyChannel?.Dispose();
        _ecHotkeyChannel = null;

        if (_ownsEcAccess && _ecAccess is not null)
        {
            // 软件曲线引擎先于 EC 通道停机：保证释放后不再有周期任务触碰已关闭的通道。
            _fanCurveEngine?.Dispose();

            // EC 访问器由本宿主创建（内含 EC 通道句柄），随宿主释放；
            // 监控工厂与风扇硬件共享同一实例，由宿主统一释放。
            _ecAccess.Dispose();
            _ecAccess = null;
        }

        if (_ownsPowerWatcher)
        {
            _powerWatcher.Dispose();
        }
    }

    private async Task RunStartupRestoreAsync()
    {
        try
        {
            StartupRestore = StartupRestorePolicy.Evaluate(
                new StartupRestoreInput(Device, _powerWatcher.Current),
                LastAppliedConfigSource);

            foreach (var check in StartupRestore.Checks)
            {
                _log.Write(
                    check.Passed ? AppLogLevel.Info : AppLogLevel.Warning,
                    "StartupRestore",
                    $"[{(check.Passed ? "通过" : "未通过")}] {check.Name}：{check.Detail}");
            }

            _log.Write(AppLogLevel.Info, "StartupRestore", StartupRestore.Reason);

            if (!StartupRestore.ShouldRestore || StartupRestore.Stage is not { } stage)
            {
                return;
            }

            if (!_options.AutomaticStageSwitchingEnabled)
            {
                // 自动切档已关闭：只保留判断依据，不下发写入。
                // 「恢复上次用户确认档位」不走本路径，由组合根在启动后显式调用
                // RestoreUserConfirmedStageAsync，二者语义不同（策略推断 vs 用户确认）。
                _log.Write(
                    AppLogLevel.Warning,
                    "StartupRestore",
                    "自动切档已关闭：即使配置档校验全部通过，本版本也不会自动应用档位；"
                    + "性能模式只由用户在界面确认后切换（ApplicationHostOptions.AutomaticStageSwitchingEnabled）。");
                return;
            }

            var outcome = await Coordinator
                .RequestPerformanceStageAsync(stage, ControlRequestOrigin.Automatic, CancellationToken.None)
                .ConfigureAwait(false);

            PublishAutomatic(outcome);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, "StartupRestore", "启动恢复流程异常：" + ex.Message);
        }
    }

    private void OnSupplyChanged(object? sender, PowerSupplyKind supply)
        => _ = Task.Run(async () =>
        {
            try
            {
                await ApplyPowerDecisionAsync(supply, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Error, "Policy", $"供电策略处理异常：{ex.Message}");
            }
        });

    private async Task<ControlOutcome?> ApplyPowerDecisionAsync(
        PowerSupplyKind supply,
        CancellationToken cancellationToken)
    {
        var currentStage = await ReadCurrentStageAsync(cancellationToken).ConfigureAwait(false);
        var decision = PowerPolicy.OnSupplyChanged(supply, currentStage);

        _log.Write(AppLogLevel.Info, "Policy", decision.Reason);

        if (decision.Kind == PowerSupplyActionKind.None || decision.Stage is not { } stage)
        {
            return null;
        }

        if (!_options.AutomaticStageSwitchingEnabled)
        {
            _log.Write(
                AppLogLevel.Warning,
                "Policy",
                "自动切档已关闭：供电变化不改变性能模式（策略建议的档位未下发）；"
                + "如需按供电状态调整，请在界面手动确认。");
            return null;
        }

        var outcome = await Coordinator
            .RequestPerformanceStageAsync(stage, ControlRequestOrigin.Automatic, cancellationToken)
            .ConfigureAwait(false);

        PublishAutomatic(outcome);
        return outcome;
    }

    /// <summary>
    /// 读取当前实际档位。
    /// </summary>
    /// <remarks>
    /// 优先向硬件适配层读回实际档位；未接入（任务 05 未交付）时退回
    /// 「本软件最近确认成功应用的档位」，<b>且该值明确标记为非硬件读回</b>。
    /// </remarks>
    private async Task<PerformanceStage?> ReadCurrentStageAsync(CancellationToken cancellationToken)
    {
        if (_performanceHardware is null)
        {
            return Coordinator.LastConfirmedStage;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Coordinator.OperationTimeout);
            var state = await _performanceHardware.ReadAsync(timeout.Token).ConfigureAwait(false);
            return state.ActiveStage ?? Coordinator.LastConfirmedStage;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Write(AppLogLevel.Warning, "Policy", "读取实际档位超时，改用本软件最近确认成功的档位。");
            return Coordinator.LastConfirmedStage;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, "Policy", $"读取实际档位失败（{ex.Message}），改用本软件最近确认成功的档位。");
            return Coordinator.LastConfirmedStage;
        }
    }

    /// <summary>
    /// 读回当前风扇控制状态（EC 0xD4）。供桌面壳随采样节拍推送界面。
    /// </summary>
    /// <returns>读回的状态；适配层未装配、超时或读取失败时为 <see langword="null"/>
    /// （界面保持上一次状态，不编造值）。</returns>
    public async Task<FanHardwareState?> ReadFanStateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_fanHardware is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Coordinator.OperationTimeout);
            return await _fanHardware.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogReadFailureThrottled(ref _lastFanReadFailureLogUtc, "风扇状态读回超时，界面保持上一次状态。");
            return null;
        }
        catch (Exception ex)
        {
            LogReadFailureThrottled(ref _lastFanReadFailureLogUtc, $"风扇状态读回失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读回当前实际性能档位（EC 0xD2）。供桌面壳随采样节拍推送界面。
    /// </summary>
    /// <returns>读回的状态；适配层未装配、超时或读取失败时为 <see langword="null"/>。</returns>
    public async Task<PerformanceHardwareState?> ReadPerformanceStateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_performanceHardware is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Coordinator.OperationTimeout);
            return await _performanceHardware.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogReadFailureThrottled(ref _lastStageReadFailureLogUtc, "实际性能档位读回超时，界面保持上一次状态。");
            return null;
        }
        catch (Exception ex)
        {
            LogReadFailureThrottled(ref _lastStageReadFailureLogUtc, $"实际性能档位读回失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 读回当前实际显卡模式（EC 0x2E bit6 登记态 + EC 0xD1 触发位 + UEFI 目标变量，HWE §21）。
    /// 供桌面壳随采样节拍推送界面。
    /// </summary>
    /// <returns>读回的状态；适配层未装配、超时或读取失败时为 <see langword="null"/>。</returns>
    public async Task<GraphicsHardwareState?> ReadGraphicsStateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_graphicsHardware is null)
        {
            return null;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Coordinator.OperationTimeout);
            return await _graphicsHardware.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogReadFailureThrottled(ref _lastGraphicsReadFailureLogUtc, "显卡模式读回超时，界面保持上一次状态。");
            return null;
        }
        catch (Exception ex)
        {
            LogReadFailureThrottled(ref _lastGraphicsReadFailureLogUtc, $"显卡模式读回失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>同类读回失败 60 秒最多记一次 Warning，避免随采样刷爆轮转日志。</summary>
    private void LogReadFailureThrottled(ref DateTimeOffset lastLogUtc, string message)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - lastLogUtc < TimeSpan.FromSeconds(60))
        {
            return;
        }

        lastLogUtc = now;
        _log.Write(AppLogLevel.Warning, "Readback", message);
    }

    private void PublishAutomatic(ControlOutcome outcome)
    {
        try
        {
            AutomaticPolicyApplied?.Invoke(this, outcome);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, "Policy", "自动策略订阅方抛出异常：" + ex.Message);
        }
    }

    private void OnCoordinatorOutcome(object? sender, ControlOutcome outcome)
    {
        // 手动性能选择会影响「拔电后是否发生过手动选择」的判定（任务书 §11）。
        // 被取代的请求不代表用户最终选择，因此不计入。
        if (outcome.Intent.Kind == ControlIntentKind.PerformanceStage
            && outcome.Intent.Origin == ControlRequestOrigin.Manual
            && outcome.Kind != ControlOutcomeKind.Superseded)
        {
            PowerPolicy.NotifyManualPerformanceSelection();
        }
    }

    private void LogAssemblySummary()
    {
        _log.Write(
            AppLogLevel.Info,
            "Host",
            $"设备标识：{Device.Model} / {Device.BoardCode}" +
            $"（已识别={Device.IsIdentified}，首要适配机型={Device.IsTargetDevice}，" +
            $"BIOS={Device.BiosVersion ?? "未知"}，Windows 构建={Device.WindowsBuild ?? "未知"}）。");

        foreach (var note in DeviceInfo.Notes)
        {
            _log.Write(AppLogLevel.Info, "Host", "设备标识说明：" + note);
        }

        var writableCount = Capabilities.All.Count(c => c.AllowsWrite);
        _log.Write(
            writableCount > 0 ? AppLogLevel.Info : AppLogLevel.Warning,
            "Host",
            Capabilities.Count == 0
                ? "能力集合为空：未适配机型，全部写入与只读入口按「待验证」禁用并显示原因。"
                : $"能力集合含 {Capabilities.Count} 项，其中可写入 {writableCount} 项" +
                  "（仅「已确证」允许写入，任务书 §16.1）。");

        var missing = Capabilities.MissingKeys();
        if (Capabilities.Count > 0 && missing.Count > 0)
        {
            _log.Write(AppLogLevel.Warning, "Host", $"能力基线与能力键清单脱节，缺少：{string.Join('、', missing)}");
        }

        _log.Write(AppLogLevel.Info, "Host", Coordinator.AdapterAvailability.Describe());
        _log.Write(AppLogLevel.Info, "Host", ModuleRegistry.Summarize(Modules));

        var notIntegrated = ModuleRegistry.NotIntegratedNames(Modules);
        if (notIntegrated.Count > 0)
        {
            _log.Write(
                AppLogLevel.Warning,
                "Host",
                $"尚未接入的模块：{string.Join('、', notIntegrated)}；" +
                "相关入口一律禁用并说明原因，不使用模拟数据。");
        }

        _log.Write(
            Conflicts.AnyDetected ? AppLogLevel.Warning : AppLogLevel.Info,
            "ThirdParty",
            Conflicts.Summary);

        if (Conflicts.AnyDetected)
        {
            var domains = ThirdPartyConflictDetector.OverlappingDomains(Conflicts);
            _log.Write(
                AppLogLevel.Warning,
                "ThirdParty",
                $"重叠控制领域：{string.Join('、', domains)}；本软件不会为争夺控制权而重复写入。");
        }

        _log.Write(AppLogLevel.Info, "Exit", ExitBehavior.Summary);

        if (!Device.IsIdentified)
        {
            _log.Write(
                AppLogLevel.Warning,
                "Host",
                "设备未识别：启动恢复、供电自动切换与所有写入入口均保持禁用" +
                "（未识别 ≠ 同机型，任务书 §2.2）。");
        }
    }

    private string DescribeWatcher()
        => _powerWatcher is PollingPowerSupplyWatcher polling
            ? $"轮询只读探测，间隔 {polling.Interval.TotalSeconds:0.#} 秒"
            : _powerWatcher.GetType().Name;

    private static string SupplyText(PowerSupplyKind kind) => kind switch
    {
        PowerSupplyKind.Ac => "交流电",
        PowerSupplyKind.Battery => "电池",
        _ => "未知"
    };

    private static ThirdPartyConflictReport BuildConflictReport(
        ApplicationHostOptions options,
        IProcessProbe? processProbe)
        => options.ScanThirdPartyConflicts
            ? new ThirdPartyConflictDetector(processProbe).Scan()
            : EmptyConflictReport();

    private static ThirdPartyConflictReport EmptyConflictReport()
        => new(
            Array.Empty<ThirdPartyConflict>(),
            ThirdPartyConflictDetector.KnownProducts,
            ThirdPartyConflictDetector.PolicyText);

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ApplicationHost), "宿主已释放。");
        }
    }
}
