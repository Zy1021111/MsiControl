using MsiControl.Application.Control;
using MsiControl.Application.Host;
using MsiControl.Application.Integration;
using MsiControl.Application.Policy;
using MsiControl.Core;
using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Application.Tests;

/// <summary>整合设施（设备识别、模块状态表、第三方共存、宿主装配）测试。</summary>
public sealed class IntegrationTests
{
    // ── 设备标识 ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("MS-15M3 REV:1.0", "MS-15M3")]
    [InlineData("MS-15M3", "MS-15M3")]
    [InlineData("  ms-15m3 rev 2.0 ", "ms-15m3")]
    [InlineData("MS-17Q2_REV:1.0", "MS-17Q2")]
    public void NormalizeBoardCode_StripsRevisionSuffix(string raw, string expected)
        => Assert.Equal(expected, RegistryDeviceInfoSource.NormalizeBoardCode(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizeBoardCode_EmptyInput_ReturnsNull(string? raw)
        => Assert.Null(RegistryDeviceInfoSource.NormalizeBoardCode(raw));

    [Fact]
    public void DeviceInfo_FromSmbios_FillsIdentityAndLeavesUnverifiedFieldsNull()
    {
        var source = new RegistryDeviceInfoSource(FakeBiosInfoReader.TargetMachine());

        var result = source.Read();

        Assert.True(result.Device.IsIdentified);
        Assert.True(result.Device.IsTargetDevice);
        Assert.Equal("MS-15M3", result.Device.BoardCode);
        Assert.Equal("Vector 16 HX AI A2XWHG", result.Device.Model);
        Assert.Equal("E15M3IMS.117", result.Device.BiosVersion);

        // 未取得有效值的字段必须保持 null，不得用占位字符串冒充已知。
        Assert.Null(result.Device.EcVersion);
        Assert.Null(result.Device.DiscreteGpuName);
        Assert.Null(result.Device.IntegratedGpuName);
        Assert.Null(result.Device.GpuDriverVersion);
        Assert.False(result.Device.HasValidEcVersion);
        Assert.False(string.IsNullOrEmpty(result.Device.WindowsBuild));
        Assert.Contains(result.Notes, n => n.Contains("EC 版本保持未知", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceInfo_WithoutSmbios_ReturnsUnidentifiedWithReason()
    {
        var source = new RegistryDeviceInfoSource(new FakeBiosInfoReader());

        var result = source.Read();

        Assert.False(result.Device.IsIdentified);
        Assert.Equal(DeviceIdentity.UnknownBoardCode, result.Device.BoardCode);
        Assert.Contains(result.Notes, n => n.Contains("未识别", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceInfo_WhenReaderThrows_ReturnsUnidentifiedInsteadOfCrashing()
    {
        var source = new RegistryDeviceInfoSource(new ThrowingBiosInfoReader());

        var result = source.Read();

        Assert.False(result.Device.IsIdentified);
        Assert.Contains(result.Notes, n => n.Contains("读取 SMBIOS 信息失败", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceInfo_WithoutBoardCodeButWithModel_FallsBackToModel()
    {
        var reader = new FakeBiosInfoReader
        {
            Values = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [BiosInfoKeys.SystemProductName] = "Vector 16 HX AI A2XWHG"
            }
        };

        var result = new RegistryDeviceInfoSource(reader).Read();

        Assert.Equal("Vector 16 HX AI A2XWHG", result.Device.Model);
        Assert.False(result.Device.IsIdentified);
        Assert.Contains(result.Notes, n => n.Contains("未能读取主板型号", StringComparison.Ordinal));
    }

    // ── 模块接入状态表 ───────────────────────────────────────────────

    [Fact]
    public void ModuleRegistry_WithNothingInjected_MarksAllUnbuiltModules()
    {
        var adapters = new PerformanceAdapterAvailability(Performance: false, Fans: false, Graphics: false);

        var statuses = ModuleRegistry.CreateDefault(monitoringIntegrated: false, adapters);

        Assert.Equal(9, statuses.Count);
        Assert.Equal(
            ModuleIntegrationState.NotIntegrated,
            statuses.Single(s => s.Name.Contains("实时监控", StringComparison.Ordinal)).State);
        Assert.Equal(
            ModuleIntegrationState.NotIntegrated,
            statuses.Single(s => s.Name.Contains("风扇", StringComparison.Ordinal)).State);
        Assert.Equal(ModuleIntegrationState.ReadOnly, statuses.Single(s => s.Project.EndsWith("MsiControl.Hardware.Msi", StringComparison.Ordinal)).State);
        Assert.Equal(4, ModuleRegistry.NotIntegratedNames(statuses).Count);
    }

    [Fact]
    public void ModuleRegistry_WithAllInjected_MarksIntegrated()
    {
        var adapters = new PerformanceAdapterAvailability(Performance: true, Fans: true, Graphics: true);

        var statuses = ModuleRegistry.CreateDefault(monitoringIntegrated: true, adapters);

        Assert.Empty(ModuleRegistry.NotIntegratedNames(statuses));
        Assert.All(statuses, s => Assert.DoesNotContain("尚未交付", s.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void ModuleRegistry_MarkdownTable_ListsEveryProjectPath()
    {
        var adapters = new PerformanceAdapterAvailability(Performance: false, Fans: false, Graphics: false);
        var statuses = ModuleRegistry.CreateDefault(false, adapters);

        var table = ModuleRegistry.ToMarkdownTable(statuses);

        foreach (var status in statuses)
        {
            Assert.Contains(status.Project, table, StringComparison.Ordinal);
        }

        Assert.Contains("| 模块 | 负责任务 |", table, StringComparison.Ordinal);
    }

    [Fact]
    public void ModuleRegistry_Summary_CountsStates()
    {
        var adapters = new PerformanceAdapterAvailability(Performance: false, Fans: false, Graphics: false);
        var summary = ModuleRegistry.Summarize(ModuleRegistry.CreateDefault(false, adapters));

        Assert.Contains("未接入 4", summary, StringComparison.Ordinal);
        Assert.Contains("已接入", summary, StringComparison.Ordinal);
    }

    // ── 第三方共存检测 ───────────────────────────────────────────────

    [Fact]
    public void ConflictDetector_DetectsXtuFromProcessName()
    {
        var probe = new FakeProcessProbe { Names = new[] { "explorer", "XtuService", "dotnet" } };
        var report = new ThirdPartyConflictDetector(probe).Scan();

        Assert.True(report.AnyDetected);
        var conflict = Assert.Single(report.Detected);
        Assert.Contains("XTU", conflict.Product.DisplayName, StringComparison.Ordinal);
        Assert.Contains("CPU 功耗限制", conflict.Product.OverlappingDomains);
        Assert.Contains("XtuService", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictDetector_DetectsMultipleProducts()
    {
        var probe = new FakeProcessProbe { Names = new[] { "XtuService", "MSIAfterburner" } };
        var report = new ThirdPartyConflictDetector(probe).Scan();

        Assert.Equal(2, report.Detected.Count);
        var domains = ThirdPartyConflictDetector.OverlappingDomains(report);
        Assert.Contains("风扇转速控制", domains);
        Assert.Contains("CPU 睿频上限", domains);
    }

    [Fact]
    public void ConflictDetector_WithNoKnownProcesses_ReportsNothing()
    {
        var report = new ThirdPartyConflictDetector(new FakeProcessProbe()).Scan();

        Assert.False(report.AnyDetected);
        Assert.Empty(ThirdPartyConflictDetector.OverlappingDomains(report));
        Assert.Contains("未检测到", report.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictDetector_NeverModifiesThirdPartyState()
    {
        var report = new ThirdPartyConflictDetector(new FakeProcessProbe { Names = new[] { "XtuService" } }).Scan();

        // 只报告：策略说明必须写明「不修改、不停止、不争夺控制权」。
        Assert.Contains("不修改或删除第三方配置", report.PolicyNote, StringComparison.Ordinal);
        Assert.Contains("不自动停止或卸载", report.PolicyNote, StringComparison.Ordinal);
        Assert.Contains("不会通过重复写入争夺控制权", report.PolicyNote, StringComparison.Ordinal);
    }

    [Fact]
    public void ConflictDetector_ProbeFailure_IsTreatedAsNoDetection()
    {
        var report = new ThirdPartyConflictDetector(new ThrowingProcessProbe()).Scan();

        Assert.False(report.AnyDetected);
    }

    // ── 宿主装配 ─────────────────────────────────────────────────────

    [Fact]
    public void Host_CreateDefault_UsesCapabilityBaselineOfIdentifiedDevice()
    {
        using var host = ApplicationHost.CreateDefault(
            deviceInfoSource: new FakeDeviceInfoSource { Result = TargetDeviceInfo() },
            options: QuietOptions());

        Assert.True(host.Device.IsTargetDevice);
        Assert.Equal(CapabilityKeys.All.Count, host.Capabilities.Count);

        // 2026-10-28（HWE §17/§20/§21/§22/§26）：可写能力为 7 项——热区（读）、风扇模式（EC 0xD4）、
        // 强冷（EC 0x98，叶哥下午决定启用）、性能档位（EC 0xD2，HWE §20 三编码实证）、
        // 显卡模式（HWE §21：协议依据 + 本机只读对照，叶哥授权放行）、
        // 风扇曲线（EC 0x71/0x89 占空比，HWE §23，软件曲线引擎保持）
        // 与 CPU 功耗限制（MSR 0x610，HWE §26：可写 + 读回 + 切档不被覆盖）。
        var writable = host.Capabilities.All.Where(c => c.AllowsWrite).Select(c => c.Key).ToArray();
        Assert.Equal(
            new[]
            {
                CapabilityKeys.FanCurve,
                CapabilityKeys.FanFullSpeed,
                CapabilityKeys.FanMode,
                CapabilityKeys.ThermalFallback,
                CapabilityKeys.PerformanceStage,
                CapabilityKeys.CpuPowerLimit,
                CapabilityKeys.GraphicsMode,
                // 2026-10-30（HWE §29）：新增五项系统快捷能力，均走 Windows 官方接口
                // （Core Audio / WMI 亮度 / 精密触控板状态位 / WMI 事件订阅），
                // 已在本机完成读写与事件订阅实测。
                CapabilityKeys.AudioVolume,
                CapabilityKeys.AudioMicrophone,
                CapabilityKeys.DisplayBrightness,
                CapabilityKeys.InputTouchpad,
                CapabilityKeys.HotkeyFn,
                // 2026-10-31（HWE §32）：蓝牙射频走 WinRT Radio，Off→On→Off 读回一致。
                CapabilityKeys.SystemBluetooth,
                // 2026-10-31（HWE §31）：设备控制三项走 MSI ACPI Data 命令，
                // 「写入 → 读回 → 恢复」本机全部一致；显示省电走 Win32 显示模式 API，
                // 260 Hz → 60 Hz → 260 Hz 读回一致。
                CapabilityKeys.KeyboardBacklight,
                CapabilityKeys.InputWinFnSwap,
                CapabilityKeys.PowerUsbShare,
                CapabilityKeys.DisplayPowerSaver
            },
            writable);
        Assert.Empty(host.Capabilities.MissingKeys());

        // 组合根 P0 修复（任务 07 收尾）：CreateDefault 对四个模块参数采用
        // 「为空时装配真实实现」的语义，因此监控必须是真实 provider 而非 null。
        Assert.NotNull(host.MonitoringProvider);

        Assert.False(host.Conflicts.AnyDetected);
    }

    [Fact]
    public async Task Host_CreateDefault_AssemblesRealModulesButRefusesUnsupportedGraphicsTarget()
    {
        // P0 修复的回归测试：组合根装配任务 03–06 的真实适配层之后，
        // 「已装配」不等于「无差别放行」（任务书 §16.1）。
        // 2026-10-17 下午更新：性能档位（performance.stage，EC 0xD2）、风扇模式（fan.mode）、
        // 强冷（fan.fullspeed）已凭 EC 写入实验升为 Verified——对真实通道断言它们会触发
        // **真实硬件写入**，因此本测试注入场景型替身验证性能路径已放行。
        // 显卡模式（HWE §21）也已 Verified 且接真实写入通道：本测试请求「核显模式」——
        // 该目标在 MUX 协议中**无编码**，应在支持性检查处被拒，绝不触达真实写入
        // （2026-10-17 教训：对本机真实通道请求 Discrete 会被真实执行并写入固件状态）。
        var scenarioPerformance = new FakeScenarioPerformanceHardware();
        using var host = ApplicationHost.CreateDefault(
            deviceInfoSource: new FakeDeviceInfoSource { Result = TargetDeviceInfo() },
            performanceHardware: scenarioPerformance,
            options: QuietOptions());

        Assert.NotNull(host.MonitoringProvider);
        Assert.True(host.Coordinator.AdapterAvailability.AllIntegrated);
        Assert.Empty(ModuleRegistry.NotIntegratedNames(host.Modules));
        Assert.All(
            host.Modules.Where(s => s.OwnerTask is "03" or "04" or "05" or "06"),
            s => Assert.Equal(ModuleIntegrationState.Integrated, s.State));

        // 性能档位：场景型通道 + performance.stage 已 Verified → 请求到达适配层（替身，不触硬件）。
        var performance = await host.Coordinator.RequestPerformanceStageAsync(
            PerformanceStage.Balanced,
            ControlRequestOrigin.Manual,
            CancellationToken.None);
        Assert.Equal(ControlOutcomeKind.Executed, performance.Kind);
        Assert.Equal(1, scenarioPerformance.ApplyCalls);
        Assert.Equal(PerformanceStage.Balanced, scenarioPerformance.LastAppliedSettings!.Stage);
        Assert.Equal(PerformanceStage.Balanced, host.Coordinator.LastConfirmedStage);

        // 显卡模式：能力已 Verified，但「核显模式」无 MUX 编码 → 逐项结果为 Unsupported
        // （请求到达服务层但在支持性检查处被拒，绝不触达真实写入）。
        var graphics = await host.Coordinator.RequestGraphicsModeAsync(
            GraphicsMode.Integrated,
            ControlRequestOrigin.Manual,
            CancellationToken.None);
        Assert.Equal(OperationStatus.Unsupported, graphics.Result.Settings[0].Status);
        Assert.Contains("核显", graphics.Result.Settings[0].Message!, StringComparison.Ordinal);
        AssertHelpers.Compliant(graphics);
    }

    [Fact]
    public void Host_CreateDefault_WithUnidentifiedDevice_DisablesEverything()
    {
        using var host = ApplicationHost.CreateDefault(
            deviceInfoSource: new FakeDeviceInfoSource(),
            options: QuietOptions());

        Assert.False(host.Device.IsIdentified);
        Assert.Equal(0, host.Capabilities.Count);
        Assert.False(host.Capabilities.AllowsWrite(CapabilityKeys.FanCurve));
        Assert.Contains(host.StartupRestore.Checks, c => !c.Passed);
        Assert.False(host.StartupRestore.ShouldRestore);
    }

    [Fact]
    public async Task Host_Start_StartsPowerWatcherAndLogsAssemblySummary()
    {
        var log = new RecordingLog();
        var watcher = new FakePowerSupplyWatcher();
        var options = new ApplicationHostOptions
        {
            AutoStartPowerPolicy = true,
            ScanThirdPartyConflicts = false,
            AutoRestoreOnStartup = false
        };

        using var host = CreateHost(TargetDeviceInfo(), watcher: watcher, options: options, log: log);

        host.Start();
        await host.WaitForStartupAsync();

        Assert.Equal(1, watcher.StartCalls);
        Assert.True(watcher.IsRunning);
        Assert.True(log.Contains("设备标识："));
        Assert.True(log.Contains("模块接入状态"));
        Assert.True(log.Contains("隐藏窗口 ≠ 退出程序"));
        Assert.True(log.Contains("供电策略已启用"));
    }

    [Fact]
    public async Task Host_Unplug_WithAutomaticSwitchingDisabled_DoesNotWriteAnyStage()
    {
        var watcher = new FakePowerSupplyWatcher(PowerSupplyKind.Ac);
        var performance = new FakePerformanceHardware
        {
            ReadHandler = _ => Task.FromResult(new PerformanceHardwareState(PerformanceStage.Balanced))
        };

        using var host = CreateHost(TargetDeviceInfo(), coordinator: CreateCoordinator(performance), watcher: watcher, performanceHardware: performance);

        host.Start();
        await host.WaitForStartupAsync();

        watcher.SetCurrent(PowerSupplyKind.Battery);
        var outcome = await host.RefreshPowerSupplyAsync();

        // 默认关闭自动切档：策略仍记录判断依据，但不下发任何写入。
        Assert.Null(outcome);
        Assert.Equal(0, performance.ApplyCalls);
        Assert.Equal(PerformanceStage.Balanced, host.PowerPolicy.StageBeforeUnplug);
        Assert.True(host.PowerPolicy.IsOnBattery);
    }

    [Fact]
    public async Task Host_Unplug_WithAutomaticSwitchingEnabled_SubmitsAutomaticRequest()
    {
        // 保留旧路径的可测性：显式开启自动切档时行为与迁移前一致（默认值已改为关闭）。
        var watcher = new FakePowerSupplyWatcher(PowerSupplyKind.Ac);
        var performance = new FakePerformanceHardware
        {
            ReadHandler = _ => Task.FromResult(new PerformanceHardwareState(PerformanceStage.Balanced))
        };

        var options = new ApplicationHostOptions
        {
            AutoStartPowerPolicy = true,
            ScanThirdPartyConflicts = false,
            AutoRestoreOnStartup = false,
            AutomaticStageSwitchingEnabled = true
        };

        using var host = CreateHost(
            TargetDeviceInfo(),
            coordinator: CreateCoordinator(performance),
            watcher: watcher,
            performanceHardware: performance,
            options: options);

        host.Start();
        await host.WaitForStartupAsync();

        watcher.SetCurrent(PowerSupplyKind.Battery);
        var outcome = await host.RefreshPowerSupplyAsync();

        Assert.NotNull(outcome);
        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome!.Kind);
        Assert.Equal(PerformanceStage.Balanced, host.PowerPolicy.StageBeforeUnplug);
        Assert.True(host.PowerPolicy.IsOnBattery);
    }

    [Fact]
    public async Task Host_ManualSelectionDuringBattery_ThenReplug_KeepsManualChoice()
    {
        var watcher = new FakePowerSupplyWatcher(PowerSupplyKind.Ac);
        var performance = new FakePerformanceHardware
        {
            ReadHandler = _ => Task.FromResult(new PerformanceHardwareState(PerformanceStage.Balanced))
        };

        using var host = CreateHost(TargetDeviceInfo(), coordinator: CreateCoordinator(performance), watcher: watcher, performanceHardware: performance);

        host.Start();
        await host.WaitForStartupAsync();

        watcher.SetCurrent(PowerSupplyKind.Battery);
        await host.RefreshPowerSupplyAsync();

        await host.Coordinator.RequestPerformanceStageAsync(
            PerformanceStage.Quiet,
            ControlRequestOrigin.Manual,
            CancellationToken.None);

        Assert.True(host.PowerPolicy.HasManualSelectionSinceUnplug);

        watcher.SetCurrent(PowerSupplyKind.Ac);
        var outcome = await host.RefreshPowerSupplyAsync();

        Assert.Null(outcome);
        Assert.Null(host.PowerPolicy.StageBeforeUnplug);
        Assert.False(host.PowerPolicy.HasManualSelectionSinceUnplug);
    }

    [Fact]
    public async Task Host_AutoSelectionOfSupersededRequest_DoesNotCountAsManualSelection()
    {
        var watcher = new FakePowerSupplyWatcher(PowerSupplyKind.Battery);
        using var host = CreateHost(TargetDeviceInfo(), watcher: watcher);

        host.Start();
        await host.WaitForStartupAsync();

        await host.Coordinator.RequestPerformanceStageAsync(
            PerformanceStage.Balanced,
            ControlRequestOrigin.Automatic,
            CancellationToken.None);

        Assert.False(host.PowerPolicy.HasManualSelectionSinceUnplug);
    }

    [Fact]
    public async Task Host_StartupRestore_WithAutomaticSwitchingEnabled_SubmitsAutomaticRequest()
    {
        var log = new RecordingLog();
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(
                PerformanceProfileCatalog.GetDefault(PerformanceStage.Quiet).Settings,
                TargetDeviceInfo().Device,
                DateTimeOffset.Now)
        };

        var options = new ApplicationHostOptions
        {
            AutoStartPowerPolicy = false,
            ScanThirdPartyConflicts = false,
            AutoRestoreOnStartup = true,
            AutomaticStageSwitchingEnabled = true
        };

        ControlOutcome? automatic = null;
        using var host = CreateHost(TargetDeviceInfo(), options: options, log: log, lastApplied: source);
        host.AutomaticPolicyApplied += (_, outcome) => automatic = outcome;

        Assert.True(host.StartupRestore.ShouldRestore);

        host.Start();
        await host.WaitForStartupAsync();

        Assert.NotNull(automatic);
        Assert.Equal(ControlRequestOrigin.Automatic, automatic!.Intent.Origin);
        Assert.Equal(ControlOutcomeKind.BlockedByCapability, automatic.Kind);
        Assert.True(log.Contains("启动恢复"));
    }

    [Fact]
    public async Task Host_StartupRestore_ByDefault_DoesNotWriteEvenWhenChecksPass()
    {
        // 默认关闭自动切档：即便配置档校验全部通过，也不得自动应用档位。
        var log = new RecordingLog();
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(
                PerformanceProfileCatalog.GetDefault(PerformanceStage.Extreme).Settings,
                TargetDeviceInfo().Device,
                DateTimeOffset.Now)
        };

        var performance = new FakePerformanceHardware();
        var options = new ApplicationHostOptions
        {
            AutoStartPowerPolicy = false,
            ScanThirdPartyConflicts = false,
            AutoRestoreOnStartup = true
        };

        ControlOutcome? automatic = null;
        using var host = CreateHost(
            TargetDeviceInfo(),
            options: options,
            log: log,
            lastApplied: source,
            coordinator: CreateCoordinator(performance),
            performanceHardware: performance);
        host.AutomaticPolicyApplied += (_, outcome) => automatic = outcome;

        host.Start();
        await host.WaitForStartupAsync();

        Assert.Null(automatic);
        Assert.Equal(0, performance.ApplyCalls);
        Assert.True(log.Contains("自动切档已关闭"));
    }

    // ── 启动恢复「上次用户确认的档位」 ────────────────────────────────

    [Fact]
    public async Task Host_RestoreUserConfirmedStage_SubmitsAutomaticRequestForThatStage()
    {
        var log = new RecordingLog();
        var performance = new FakePerformanceHardware();
        using var host = CreateHost(
            TargetDeviceInfo(),
            coordinator: CreateCoordinator(performance),
            performanceHardware: performance,
            log: log);

        ControlOutcome? automatic = null;
        host.AutomaticPolicyApplied += (_, outcome) => automatic = outcome;

        var outcome = await host.RestoreUserConfirmedStageAsync(PerformanceStage.HighPerformance);

        Assert.NotNull(outcome);
        Assert.Same(automatic, outcome);
        Assert.Equal(ControlRequestOrigin.Automatic, outcome!.Intent.Origin);
        Assert.Equal(PerformanceStage.HighPerformance, automatic!.Intent.Stage);
        Assert.True(log.Contains("上次用户确认的档位"));
    }

    [Fact]
    public async Task Host_RestoreUserConfirmedStage_WithoutRecord_DoesNotWrite()
    {
        var performance = new FakePerformanceHardware();
        using var host = CreateHost(
            TargetDeviceInfo(),
            coordinator: CreateCoordinator(performance),
            performanceHardware: performance);

        var outcome = await host.RestoreUserConfirmedStageAsync(null);

        Assert.Null(outcome);
        Assert.Equal(0, performance.ApplyCalls);
    }

    [Fact]
    public async Task Host_RestoreUserConfirmedStage_WhenDeviceUnidentified_Refuses()
    {
        var performance = new FakePerformanceHardware();
        using var host = CreateHost(
            new DeviceInfoResult(DeviceIdentity.Unknown, Array.Empty<string>()),
            coordinator: CreateCoordinator(performance),
            performanceHardware: performance);

        var outcome = await host.RestoreUserConfirmedStageAsync(PerformanceStage.Balanced);

        Assert.Null(outcome);
        Assert.Equal(0, performance.ApplyCalls);
    }

    [Fact]
    public async Task Host_StartupRestore_WithoutRecord_DoesNotSubmitAnything()
    {
        var options = new ApplicationHostOptions
        {
            AutoStartPowerPolicy = false,
            ScanThirdPartyConflicts = false,
            AutoRestoreOnStartup = true
        };

        ControlOutcome? automatic = null;
        using var host = CreateHost(TargetDeviceInfo(), options: options);
        host.AutomaticPolicyApplied += (_, outcome) => automatic = outcome;

        host.Start();
        await host.WaitForStartupAsync();

        Assert.Null(automatic);
    }

    [Fact]
    public void Host_Resume_DoesNotReplayWrites()
    {
        var decision = ApplicationHost.HandleResume();

        Assert.Equal(PowerSupplyActionKind.None, decision.Kind);
        Assert.Contains("不自动重放", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Host_Dispose_StopsAndReleasesWatcher()
    {
        var watcher = new FakePowerSupplyWatcher();
        var host = CreateHost(TargetDeviceInfo(), watcher: watcher);

        host.Start();
        host.Dispose();

        Assert.False(watcher.IsRunning);
        Assert.Throws<ObjectDisposedException>(() => host.Start());
    }

    [Fact]
    public void Host_UnidentifiedDevice_LogsDisabledReason()
    {
        var log = new RecordingLog();
        using var host = CreateHost(new DeviceInfoResult(DeviceIdentity.Unknown, new[] { "测试：无 SMBIOS" }), log: log);

        host.Start();

        Assert.True(log.Contains("设备未识别"));
    }

    [Fact]
    public void Host_ExitBehaviorSummary_IsExposedForUi()
    {
        using var host = CreateHost(TargetDeviceInfo());

        Assert.NotEmpty(host.ExitBehavior.Summary);
        Assert.Contains("隐藏窗口 ≠ 退出程序", host.ExitBehavior.Summary, StringComparison.Ordinal);
    }

    // ── 辅助 ─────────────────────────────────────────────────────────

    private static ApplicationHostOptions QuietOptions() => new()
    {
        AutoStartPowerPolicy = false,
        ScanThirdPartyConflicts = false,
        AutoRestoreOnStartup = false
    };

    private static DeviceInfoResult TargetDeviceInfo() => new(
        new DeviceIdentity(
            "Vector 16 HX AI A2XWHG",
            DeviceIdentity.TargetBoardCode,
            BiosVersion: "E15M3IMS.117",
            WindowsBuild: "26300"),
        new[] { "测试替身提供的标识。" });

    private static ControlCoordinator CreateCoordinator(FakePerformanceHardware? performance = null)
        => new(TestCapabilities.TargetMachine(), performance);

    private static ApplicationHost CreateHost(
        DeviceInfoResult deviceInfo,
        ControlCoordinator? coordinator = null,
        FakePowerSupplyWatcher? watcher = null,
        FakePerformanceHardware? performanceHardware = null,
        ApplicationHostOptions? options = null,
        RecordingLog? log = null,
        ILastAppliedConfigSource? lastApplied = null)
    {
        var capabilities = MsiControl.Hardware.Msi.Capabilities.CapabilityBaseline.ForDevice(deviceInfo.Device);

        return ApplicationHost.CreateExplicit(
            deviceInfo,
            capabilities,
            coordinator ?? new ControlCoordinator(capabilities, performanceHardware),
            watcher ?? new FakePowerSupplyWatcher(),
            performanceHardware,
            monitoringProvider: null,
            lastAppliedConfigSource: lastApplied,
            options: options ?? QuietOptions(),
            log: log);
    }

    private sealed class ThrowingBiosInfoReader : IBiosInfoReader
    {
        public IReadOnlyDictionary<string, string?> Read() => throw new InvalidOperationException("模拟注册表不可读");
    }

    private sealed class ThrowingProcessProbe : IProcessProbe
    {
        public IReadOnlyList<string> GetProcessNames() => throw new InvalidOperationException("模拟进程枚举失败");
    }
}
