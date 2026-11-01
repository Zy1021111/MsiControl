using MsiControl.Application.Control;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using Xunit;

namespace MsiControl.Application.Tests;

/// <summary>
/// 控制协调器测试。
/// </summary>
/// <remarks>
/// 覆盖任务书 §19「自动测试重点」中的请求优先级、部分失败、超时与恢复流程，
/// 以及 §16 的串行写入、能力门控与「不宣称回滚」要求。
/// </remarks>
public sealed class ControlCoordinatorTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    private static ControlCoordinator Create(
        DeviceCapabilities capabilities,
        FakePerformanceHardware? performance = null,
        FakeFanHardware? fan = null,
        FakeGraphicsHardware? graphics = null,
        IReadOnlyDictionary<PerformanceStage, PerformanceSettings>? stages = null,
        ControlCoordinatorOptions? options = null,
        RecordingLog? log = null)
        => new(
            capabilities,
            performance,
            fan,
            graphics,
            stages,
            options,
            log);

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ── 能力门控 ─────────────────────────────────────────────────────

    [Fact]
    public async Task RequestStage_WhenAdapterNotIntegrated_ReportsNotIntegrated()
    {
        var coordinator = Create(TestCapabilities.StageWritable());

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        Assert.False(outcome.WasExecuted);
        var setting = Assert.Single(outcome.Result.Settings);
        Assert.Equal(OperationStatus.Unsupported, setting.Status);
        Assert.Contains("任务 05", setting.Message!, StringComparison.Ordinal);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RequestStage_WhenCapabilityIsOnlyDetected_IsBlockedWithoutTouchingAdapter()
    {
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.TargetMachine(), performance);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        Assert.Equal(0, performance.ApplyCalls);
        Assert.All(outcome.Result.Settings, s => Assert.Equal(OperationStatus.Unsupported, s.Status));
        // cpu.power 自 HWE §26 起达可写；仍未达可写且被本档请求的是 gpu.power
        // （Eco 默认表含 GPU 20 W），因此用它作为「未达能力被拦截」的例子。
        Assert.Contains(outcome.Result.Settings, s => s.Setting == CapabilityKeys.GpuPowerLimit);
        Assert.Contains("HWE", string.Join(" ", outcome.Result.Settings.Select(s => s.Message)), StringComparison.Ordinal);
        Assert.Contains(outcome.Notes, n => n.Contains("不做部分写入", StringComparison.Ordinal));
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RequestStage_WhenAllRequiredCapabilitiesVerified_ExecutesAndConfirms()
    {
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.StageWritable(), performance);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.True(outcome.IsFullyApplied);
        Assert.Equal(1, performance.ApplyCalls);
        Assert.Equal(PerformanceStage.Eco, performance.LastAppliedSettings!.Stage);
        Assert.Equal(PerformanceStage.Eco, coordinator.LastConfirmedStage);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RequestStage_NativeStage_NeedsOnlyFirmwareScenario()
    {
        // 高性能与狂暴两档全部为「原生上限」，因此只需要固件场景可写。
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.AllVerified(CapabilityKeys.FirmwareScenario), performance);

        var extreme = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Extreme, None);
        var eco = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.Executed, extreme.Kind);
        Assert.Equal(ControlOutcomeKind.BlockedByCapability, eco.Kind);
        Assert.Equal(1, performance.ApplyCalls);
        AssertHelpers.Compliant(extreme);
        AssertHelpers.Compliant(eco);
    }

    [Fact]
    public async Task RequestStage_WithSelfContradictoryConfiguration_IsRejectedBeforeHardware()
    {
        var performance = new FakePerformanceHardware();
        var broken = new PerformanceSettings(
            PerformanceStage.Eco,
            CpuLongTerm: PowerLimit.Custom(60m),
            CpuShortTerm: PowerLimit.Custom(10m),
            CpuTurbo: CpuTurboLimit.Native,
            GpuLimit: PowerLimit.Custom(20m));

        var stages = new Dictionary<PerformanceStage, PerformanceSettings> { [PerformanceStage.Eco] = broken };
        var coordinator = Create(TestCapabilities.StageWritable(), performance, stages: stages);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.Faulted, outcome.Kind);
        Assert.Equal(0, performance.ApplyCalls);
        Assert.Contains("自相矛盾", outcome.Result.Settings[0].Message!, StringComparison.Ordinal);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public void SettingsFor_UnknownStage_Throws()
    {
        var stages = new Dictionary<PerformanceStage, PerformanceSettings>();
        var restricted = Create(TestCapabilities.StageWritable(), stages: stages);

        Assert.Throws<ArgumentOutOfRangeException>(() => restricted.SettingsFor(PerformanceStage.Eco));
    }

    [Fact]
    public void SettingsFor_DefaultCatalog_CoversAllStages()
    {
        var coordinator = Create(TestCapabilities.StageWritable());

        foreach (var stage in Enum.GetValues<PerformanceStage>())
        {
            Assert.Equal(stage, coordinator.SettingsFor(stage).Stage);
        }
    }

    [Fact]
    public void UpdateStageSettings_ValidCustomLimits_AreAcceptedAndUsed()
    {
        var coordinator = Create(TestCapabilities.StageWritable());

        var current = coordinator.SettingsFor(PerformanceStage.Eco);
        var updated = current with
        {
            CpuLongTerm = PowerLimit.Custom(45m),
            CpuShortTerm = PowerLimit.Custom(45m),
            GpuLimit = PowerLimit.Custom(30m)
        };

        var result = coordinator.UpdateStageSettings(updated);

        Assert.True(result.Ok);
        Assert.Empty(result.Problems);
        Assert.Equal(updated, coordinator.SettingsFor(PerformanceStage.Eco));
    }

    [Fact]
    public void UpdateStageSettings_SelfContradictoryLimits_AreRejected()
    {
        var coordinator = Create(TestCapabilities.StageWritable());

        var current = coordinator.SettingsFor(PerformanceStage.Eco);
        var broken = current with
        {
            CpuLongTerm = PowerLimit.Custom(60m),
            CpuShortTerm = PowerLimit.Custom(10m)
        };

        var result = coordinator.UpdateStageSettings(broken);

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Problems);
        Assert.Null(result.Settings);
        // 整体拒绝：非法配置不得部分生效，原配置保持不变。
        Assert.Equal(current, coordinator.SettingsFor(PerformanceStage.Eco));
    }

    // ── 串行写入 ─────────────────────────────────────────────────────

    [Fact]
    public async Task Writes_AreSerialized()
    {
        var gate = Gate();
        var entered = Gate();
        var probe = new ConcurrencyProbe();

        var performance = new FakePerformanceHardware
        {
            ApplyHandler = async (_, ct) =>
            {
                probe.Enter();
                entered.TrySetResult();
                try
                {
                    await gate.Task.WaitAsync(ct);
                }
                finally
                {
                    probe.Exit();
                }

                return FakePerformanceHardware.Succeed();
            }
        };

        var fan = new FakeFanHardware
        {
            SetFullSpeedHandler = async (enabled, ct) =>
            {
                probe.Enter();
                try
                {
                    await gate.Task.WaitAsync(ct);
                }
                finally
                {
                    probe.Exit();
                }

                return ControlResult.Single(SettingResult.Succeeded(
                    "fan.fullspeed",
                    enabled ? "开启" : "关闭",
                    enabled ? "开启" : "关闭",
                    "写入后读回一致"));
            }
        };

        var capabilities = TestCapabilities.AllVerified(
            CapabilityKeys.FirmwareScenario,
            CapabilityKeys.CpuPowerLimit,
            CapabilityKeys.GpuPowerLimit,
            CapabilityKeys.FanFullSpeed);

        var coordinator = Create(capabilities, performance, fan);

        var first = coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);
        await entered.Task;

        // 第二个请求种类不同（不会互相取代），应排在同一个串行闸门之后。
        var second = coordinator.SetFullSpeedAsync(true, None);

        Assert.Equal(1, probe.Max);

        gate.SetResult();
        await first;
        await second;

        Assert.Equal(1, probe.Max);
        Assert.Equal(1, performance.ApplyCalls);
        Assert.Equal(1, fan.FullSpeedCalls);
    }

    // ── 请求合并与优先级 ─────────────────────────────────────────────

    [Fact]
    public async Task OlderPendingRequest_IsSupersededByNewerOne_WithoutWriting()
    {
        var release = Gate();
        var entered = Gate();
        var performance = new FakePerformanceHardware();

        var fan = new FakeFanHardware
        {
            SetFullSpeedHandler = async (enabled, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return ControlResult.Single(SettingResult.Succeeded(
                    "fan.fullspeed",
                    enabled ? "开启" : "关闭",
                    enabled ? "开启" : "关闭",
                    "写入后读回一致"));
            }
        };

        var capabilities = TestCapabilities.AllVerified(
            CapabilityKeys.FirmwareScenario,
            CapabilityKeys.CpuPowerLimit,
            CapabilityKeys.GpuPowerLimit,
            CapabilityKeys.FanFullSpeed);

        var coordinator = Create(capabilities, performance, fan);

        var blocker = coordinator.SetFullSpeedAsync(true, None);
        await entered.Task;

        var older = coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, None);
        var newer = coordinator.RequestPerformanceStageAsync(PerformanceStage.Balanced, None);

        release.SetResult();
        await blocker;

        var olderOutcome = await older;
        var newerOutcome = await newer;

        Assert.Equal(ControlOutcomeKind.Superseded, olderOutcome.Kind);
        Assert.False(olderOutcome.WasExecuted);
        Assert.Contains("取代", olderOutcome.Result.Settings[0].Message!, StringComparison.Ordinal);
        Assert.Equal(ControlOutcomeKind.Executed, newerOutcome.Kind);
        Assert.Equal(PerformanceStage.Balanced, performance.LastAppliedSettings!.Stage);
        Assert.Equal(1, performance.ApplyCalls);
        AssertHelpers.Compliant(olderOutcome);
        AssertHelpers.Compliant(newerOutcome);
    }

    [Fact]
    public async Task AutomaticRequest_DoesNotSupersedeQueuedManualRequest()
    {
        var release = Gate();
        var entered = Gate();
        var performance = new FakePerformanceHardware();

        var fan = new FakeFanHardware
        {
            SetFullSpeedHandler = async (enabled, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return ControlResult.Single(SettingResult.Succeeded(
                    "fan.fullspeed",
                    enabled ? "开启" : "关闭",
                    enabled ? "开启" : "关闭",
                    "写入后读回一致"));
            }
        };

        var capabilities = TestCapabilities.AllVerified(
            CapabilityKeys.FirmwareScenario,
            CapabilityKeys.CpuPowerLimit,
            CapabilityKeys.GpuPowerLimit,
            CapabilityKeys.FanFullSpeed);

        var coordinator = Create(capabilities, performance, fan);

        var blocker = coordinator.SetFullSpeedAsync(true, None);
        await entered.Task;

        var manual = coordinator.RequestPerformanceStageAsync(PerformanceStage.Balanced, ControlRequestOrigin.Manual, None);
        var automatic = coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, ControlRequestOrigin.Automatic, None);

        var automaticOutcome = await automatic;
        Assert.Equal(ControlOutcomeKind.Superseded, automaticOutcome.Kind);
        Assert.Contains(automaticOutcome.Notes, n => n.Contains("不会取代", StringComparison.Ordinal));

        release.SetResult();
        await blocker;

        var manualOutcome = await manual;
        Assert.Equal(ControlOutcomeKind.Executed, manualOutcome.Kind);
        Assert.Equal(PerformanceStage.Balanced, performance.LastAppliedSettings!.Stage);
        AssertHelpers.Compliant(automaticOutcome);
        AssertHelpers.Compliant(manualOutcome);
    }

    [Fact]
    public async Task ManualRequest_SupersedesQueuedAutomaticRequest()
    {
        var release = Gate();
        var entered = Gate();
        var performance = new FakePerformanceHardware();

        var fan = new FakeFanHardware
        {
            SetFullSpeedHandler = async (enabled, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return ControlResult.Single(SettingResult.Succeeded(
                    "fan.fullspeed",
                    enabled ? "开启" : "关闭",
                    enabled ? "开启" : "关闭",
                    "写入后读回一致"));
            }
        };

        var capabilities = TestCapabilities.AllVerified(
            CapabilityKeys.FirmwareScenario,
            CapabilityKeys.CpuPowerLimit,
            CapabilityKeys.GpuPowerLimit,
            CapabilityKeys.FanFullSpeed);

        var coordinator = Create(capabilities, performance, fan);

        var blocker = coordinator.SetFullSpeedAsync(true, None);
        await entered.Task;

        var automatic = coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, ControlRequestOrigin.Automatic, None);
        var manual = coordinator.RequestPerformanceStageAsync(PerformanceStage.Extreme, ControlRequestOrigin.Manual, None);

        release.SetResult();
        await blocker;

        var automaticOutcome = await automatic;
        var manualOutcome = await manual;

        Assert.Equal(ControlOutcomeKind.Superseded, automaticOutcome.Kind);
        Assert.Equal(ControlOutcomeKind.Executed, manualOutcome.Kind);
        Assert.Equal(PerformanceStage.Extreme, performance.LastAppliedSettings!.Stage);
        Assert.Equal(1, performance.ApplyCalls);
    }

    [Fact]
    public async Task WhenSupersedingDisabled_QueuedRequestsAreExecutedInTurn()
    {
        var release = Gate();
        var entered = Gate();
        var performance = new FakePerformanceHardware();

        var fan = new FakeFanHardware
        {
            SetFullSpeedHandler = async (enabled, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return ControlResult.Single(SettingResult.Succeeded(
                    "fan.fullspeed",
                    enabled ? "开启" : "关闭",
                    enabled ? "开启" : "关闭",
                    "写入后读回一致"));
            }
        };

        var capabilities = TestCapabilities.AllVerified(
            CapabilityKeys.FirmwareScenario,
            CapabilityKeys.CpuPowerLimit,
            CapabilityKeys.GpuPowerLimit,
            CapabilityKeys.FanFullSpeed);

        var options = ControlCoordinatorOptions.Default with { SupersedePendingRequests = false };
        var coordinator = Create(capabilities, performance, fan, options: options);

        var blocker = coordinator.SetFullSpeedAsync(true, None);
        await entered.Task;

        var first = coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, None);
        var second = coordinator.RequestPerformanceStageAsync(PerformanceStage.Balanced, None);

        release.SetResult();
        await blocker;

        Assert.Equal(ControlOutcomeKind.Executed, (await first).Kind);
        Assert.Equal(ControlOutcomeKind.Executed, (await second).Kind);
        Assert.Equal(2, performance.ApplyCalls);
    }

    // ── 超时与取消 ───────────────────────────────────────────────────

    [Fact]
    public async Task Timeout_IsReportedAsUnconfirmed_NotAsSuccess()
    {
        var performance = new FakePerformanceHardware
        {
            ApplyHandler = async (_, ct) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakePerformanceHardware.Succeed();
            }
        };

        var options = ControlCoordinatorOptions.Default with { OperationTimeout = TimeSpan.FromMilliseconds(60) };
        var coordinator = Create(TestCapabilities.StageWritable(), performance, options: options);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.TimedOut, outcome.Kind);
        Assert.False(outcome.WasExecuted);
        Assert.Equal(OperationStatus.Failed, outcome.Result.Settings[0].Status);
        Assert.Contains("超时", outcome.Result.Settings[0].Message!, StringComparison.Ordinal);
        Assert.Contains("不视为成功", outcome.Result.Settings[0].Message!, StringComparison.Ordinal);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task CancellationBeforeStart_PropagatesAndDoesNotWrite()
    {
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.StageWritable(), performance);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.RequestPerformanceStageAsync(
                PerformanceStage.Eco,
                ControlRequestOrigin.Manual,
                cts.Token));

        Assert.Equal(0, performance.ApplyCalls);
    }

    [Fact]
    public async Task CancellationDuringExecution_PropagatesInsteadOfBeingSwallowed()
    {
        var entered = Gate();
        var performance = new FakePerformanceHardware
        {
            ApplyHandler = async (_, ct) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return FakePerformanceHardware.Succeed();
            }
        };

        var options = ControlCoordinatorOptions.Default with { OperationTimeout = TimeSpan.FromSeconds(30) };
        var coordinator = Create(TestCapabilities.StageWritable(), performance, options: options);

        using var cts = new CancellationTokenSource();
        var task = coordinator.RequestPerformanceStageAsync(
            PerformanceStage.Eco,
            ControlRequestOrigin.Manual,
            cts.Token);

        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, performance.ApplyCalls);
    }

    [Fact]
    public async Task AdapterException_BecomesExplicitFailureResult()
    {
        var performance = new FakePerformanceHardware
        {
            ApplyHandler = (_, _) => throw new InvalidOperationException("模拟适配层故障")
        };

        var coordinator = Create(TestCapabilities.StageWritable(), performance);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None);

        Assert.Equal(ControlOutcomeKind.Faulted, outcome.Kind);
        Assert.Contains("模拟适配层故障", outcome.Result.Settings[0].Message!, StringComparison.Ordinal);
        AssertHelpers.Compliant(outcome);
    }

    // ── 部分失败与重读 ───────────────────────────────────────────────

    [Fact]
    public async Task PartialFailure_ReReadsState_AndDoesNotClaimRollback()
    {
        var performance = new FakePerformanceHardware
        {
            ApplyHandler = (_, _) => Task.FromResult(FakePerformanceHardware.Partial()),
            ReadHandler = _ => Task.FromResult(new PerformanceHardwareState(
                PerformanceStage.Balanced,
                CpuLongTermReadBack: PowerLimit.Custom(60m)))
        };

        var coordinator = Create(TestCapabilities.StageWritable(), performance);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Balanced, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.Equal(1, performance.ReadCalls);
        Assert.Contains(outcome.Notes, n => n.Contains("重读", StringComparison.Ordinal) || n.Contains("重新读取", StringComparison.Ordinal));
        Assert.Contains(outcome.Notes, n => n.Contains("未执行整组回滚", StringComparison.Ordinal));
        Assert.NotEqual(PerformanceStage.Balanced, coordinator.LastConfirmedStage);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task FullSuccess_DoesNotReRead()
    {
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.StageWritable(), performance);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, None);

        Assert.Equal(0, performance.ReadCalls);
        Assert.Equal(PerformanceStage.Quiet, coordinator.LastConfirmedStage);
        AssertHelpers.Compliant(outcome);
    }

    // ── 显卡模式 ─────────────────────────────────────────────────────

    [Fact]
    public async Task GraphicsRequest_WhenCapabilityMissing_IsBlocked()
    {
        // 能力缺失（等价于回退路径）：任何显卡请求都在门控处被拦，适配层不被触达。
        var graphics = new FakeGraphicsHardware();
        var coordinator = Create(DeviceCapabilities.Empty, graphics: graphics);

        var outcome = await coordinator.RequestGraphicsModeAsync(GraphicsMode.Hybrid, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        Assert.Equal(0, graphics.RequestCalls);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task GraphicsRequest_WhenCapabilityVerified_ReachesAdapter()
    {
        // HWE §21（2026-10-17）：graphics.mode 升 Verified（叶哥授权）→ 请求到达适配层。
        var graphics = new FakeGraphicsHardware();
        var coordinator = Create(TestCapabilities.TargetMachine(), graphics: graphics);

        var outcome = await coordinator.RequestGraphicsModeAsync(GraphicsMode.Hybrid, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.Equal(1, graphics.RequestCalls);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task GraphicsRequest_WithUnconfirmedExternalDisplayTopology_IsBlocked()
    {
        var graphics = new FakeGraphicsHardware
        {
            State = new GraphicsHardwareState(
                Current: GraphicsMode.Hybrid,
                RequiresRestart: false,
                ExternalDisplayAttached: true,
                ExternalDisplayDependsOnDiscreteGpu: null,
                SupportsHybrid: true,
                SupportsDiscrete: true)
        };

        var coordinator = Create(TestCapabilities.AllVerified(CapabilityKeys.GraphicsMode), graphics: graphics);

        var outcome = await coordinator.RequestGraphicsModeAsync(GraphicsMode.Discrete, None);

        Assert.Equal(ControlOutcomeKind.BlockedByPrecondition, outcome.Kind);
        Assert.Equal(0, graphics.RequestCalls);
        Assert.Contains("实际连接路径", outcome.Result.Settings[0].Message!, StringComparison.Ordinal);
        Assert.Contains(outcome.Notes, n => n.Contains("不得推断为无风险", StringComparison.Ordinal));
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task GraphicsRequest_WithoutExternalDisplay_IsForwardedAndMayBePendingRestart()
    {
        var graphics = new FakeGraphicsHardware
        {
            State = new GraphicsHardwareState(Current: GraphicsMode.Hybrid, SupportsHybrid: true, SupportsDiscrete: true)
        };

        var coordinator = Create(TestCapabilities.AllVerified(CapabilityKeys.GraphicsMode), graphics: graphics);

        var outcome = await coordinator.RequestGraphicsModeAsync(GraphicsMode.Discrete, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.Equal(1, graphics.RequestCalls);
        Assert.Equal(GraphicsMode.Discrete, graphics.LastRequested);
        Assert.Single(outcome.Result.PendingRestartSettings);
        Assert.Contains(outcome.Notes, n => n.Contains("不自动重启", StringComparison.Ordinal));
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task GraphicsRequest_WithConfirmedRisk_IsForwarded()
    {
        var graphics = new FakeGraphicsHardware
        {
            State = new GraphicsHardwareState(
                Current: GraphicsMode.Discrete,
                ExternalDisplayAttached: true,
                ExternalDisplayDependsOnDiscreteGpu: true,
                SupportsHybrid: true,
                SupportsDiscrete: true)
        };

        var coordinator = Create(TestCapabilities.AllVerified(CapabilityKeys.GraphicsMode), graphics: graphics);

        var outcome = await coordinator.RequestGraphicsModeAsync(GraphicsMode.Hybrid, None);

        // 拓扑已确认「会中断」，不是「未知」：因此允许下发，风险由调用方/用户承担。
        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.Equal(1, graphics.RequestCalls);
    }

    // ── 强冷 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FullSpeed_WhenCapabilityUnknown_IsBlocked()
    {
        var fan = new FakeFanHardware();
        // 真实基线中 fan.fullspeed 已升为 Verified（HWE §17.2）；本测试验证「未确证即阻断」这一
        // 通用规则，因此显式把该键压回 Unknown。
        var capabilities = TestCapabilities
            .TargetMachine()
            .With(CapabilityStatus.Unknown(CapabilityKeys.FanFullSpeed, "测试替身：保持未确证。"));
        var coordinator = Create(capabilities, fan: fan);

        var outcome = await coordinator.SetFullSpeedAsync(true, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        Assert.Equal(0, fan.FullSpeedCalls);
        Assert.Contains(outcome.Notes, n => n.Contains("不做部分写入", StringComparison.Ordinal));
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task FullSpeed_WhenVerified_IsForwarded()
    {
        var fan = new FakeFanHardware();
        var coordinator = Create(TestCapabilities.AllVerified(CapabilityKeys.FanFullSpeed), fan: fan);

        var outcome = await coordinator.SetFullSpeedAsync(true, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.Equal(1, fan.FullSpeedCalls);
        Assert.True(fan.LastFullSpeed);
        AssertHelpers.Compliant(outcome);
    }

    // ── 事件与日志 ───────────────────────────────────────────────────

    [Fact]
    public async Task Outcome_IsPublishedAndLogged()
    {
        var log = new RecordingLog();
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.StageWritable(), performance, log: log);

        ControlOutcome? published = null;
        coordinator.OutcomePublished += (_, outcome) => published = outcome;

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Balanced, None);

        Assert.NotNull(published);
        Assert.Same(outcome, published);
        Assert.True(log.Contains("性能档位切换"));
    }

    [Fact]
    public async Task SubscriberException_DoesNotBreakControlFlow()
    {
        var log = new RecordingLog();
        var performance = new FakePerformanceHardware();
        var coordinator = Create(TestCapabilities.StageWritable(), performance, log: log);
        coordinator.OutcomePublished += (_, _) => throw new InvalidOperationException("订阅方故障");

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Balanced, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.True(log.Contains("订阅方抛出异常"));
    }

    [Fact]
    public void AdapterAvailability_DescribesMissingModules()
    {
        var coordinator = Create(TestCapabilities.StageWritable(), new FakePerformanceHardware());
        var availability = coordinator.AdapterAvailability;

        Assert.True(availability.Performance);
        Assert.False(availability.Fans);
        Assert.False(availability.Graphics);
        Assert.False(availability.AllIntegrated);
        Assert.Equal(2, availability.Missing.Count);
        Assert.Contains("未接入", availability.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_ThenRequest_Throws()
    {
        var coordinator = Create(TestCapabilities.StageWritable(), new FakePerformanceHardware());
        coordinator.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => coordinator.RequestPerformanceStageAsync(PerformanceStage.Eco, None));
    }

    // ── 组合档（安静，HWE §21.3） ────────────────────────────────────

    [Fact]
    public async Task RequestQuietStage_WithScenarioChannel_ComposesSilentFan()
    {
        var performance = new FakeScenarioPerformanceHardware();
        var fan = new FakeFanModeSwitchHardware();
        var coordinator = new ControlCoordinator(
            TestCapabilities.AllVerified(CapabilityKeys.PerformanceStage, CapabilityKeys.FanMode),
            performance,
            fan);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.Equal(1, performance.ApplyCalls);
        Assert.Equal(1, fan.SilentCalls);
        Assert.Contains(outcome.Result.Settings, s => s.Setting == "stage" && s.Status == OperationStatus.Succeeded);
        Assert.Contains(outcome.Result.Settings, s => s.Setting == "fan.mode" && s.Status == OperationStatus.Succeeded);
        Assert.Equal(PerformanceStage.Quiet, coordinator.LastConfirmedStage);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RequestQuietStage_WithoutFanModeCapability_IsBlockedWithoutTouchingAdapters()
    {
        var performance = new FakeScenarioPerformanceHardware();
        var fan = new FakeFanModeSwitchHardware();
        var coordinator = new ControlCoordinator(
            TestCapabilities.AllVerified(CapabilityKeys.PerformanceStage),
            performance,
            fan);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        Assert.Contains(outcome.Result.Settings, s => s.Setting == CapabilityKeys.FanMode);
        Assert.Equal(0, performance.ApplyCalls);
        Assert.Equal(0, fan.SilentCalls);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RequestQuietStage_WhenFanAdapterLacksModeSwitch_ReportsUnsupportedFanPart()
    {
        var performance = new FakeScenarioPerformanceHardware();
        var fan = new FakeFanHardware();
        var coordinator = new ControlCoordinator(
            TestCapabilities.AllVerified(CapabilityKeys.PerformanceStage, CapabilityKeys.FanMode),
            performance,
            fan);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.Quiet, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.True(outcome.Result.IsPartial);
        Assert.Contains(
            outcome.Result.Settings,
            s => s.Setting == "fan.mode" && s.Status == OperationStatus.Unsupported);
        Assert.Equal(0, fan.FullSpeedCalls);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RequestHighPerformanceStage_WithScenarioChannel_ExecutesNormally()
    {
        var performance = new FakeScenarioPerformanceHardware();
        var coordinator = new ControlCoordinator(
            TestCapabilities.AllVerified(CapabilityKeys.PerformanceStage),
            performance);

        var outcome = await coordinator.RequestPerformanceStageAsync(PerformanceStage.HighPerformance, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.Equal(1, performance.ApplyCalls);
        Assert.Equal(PerformanceStage.HighPerformance, performance.LastAppliedSettings!.Stage);
        AssertHelpers.Compliant(outcome);
    }

    // ── 退出恢复：只写场景，不应用配置表 ─────────────────────────────

    [Fact]
    public async Task RestoreDefaultStage_WritesScenarioOnly_WithoutApplyingStageSettings()
    {
        var performance = new FakeScenarioRestoreHardware();
        var coordinator = new ControlCoordinator(
            TestCapabilities.AllVerified(CapabilityKeys.PerformanceStage),
            performance);

        var outcome = await coordinator.RestoreDefaultStageAsync(ControlRequestOrigin.Manual, None);

        Assert.Equal(ControlOutcomeKind.Executed, outcome.Kind);
        Assert.True(outcome.Result.IsSuccess);
        Assert.Equal(1, performance.RestoreCalls);
        Assert.Equal(0, performance.ApplyCalls);
        Assert.Equal(PerformanceShiftMap.FirmwareDefaultStage, performance.LastRestoredStage);
        Assert.Null(coordinator.LastConfirmedStage);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RestoreDefaultStage_WhenAdapterLacksRestoreChannel_ReportsUnsupportedAndWritesNothing()
    {
        // 场景型但未实现恢复通道：不得降级为整档应用（那会连带改 MSR 功耗墙）。
        var performance = new FakeScenarioPerformanceHardware();
        var coordinator = new ControlCoordinator(
            TestCapabilities.AllVerified(CapabilityKeys.PerformanceStage),
            performance);

        var outcome = await coordinator.RestoreDefaultStageAsync(ControlRequestOrigin.Manual, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        var setting = Assert.Single(outcome.Result.Settings);
        Assert.Equal(OperationStatus.Unsupported, setting.Status);
        Assert.Contains("IScenarioRestoreChannel", setting.Message!, StringComparison.Ordinal);
        Assert.Equal(0, performance.ApplyCalls);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public async Task RestoreDefaultStage_WhenCapabilityNotVerified_IsBlockedWithoutTouchingAdapter()
    {
        var performance = new FakeScenarioRestoreHardware();
        // 不含 performance.stage：本机基线里该键已是 Verified，必须用空能力集合验证门控。
        var coordinator = new ControlCoordinator(TestCapabilities.AllVerified(), performance);

        var outcome = await coordinator.RestoreDefaultStageAsync(ControlRequestOrigin.Manual, None);

        Assert.Equal(ControlOutcomeKind.BlockedByCapability, outcome.Kind);
        Assert.Equal(0, performance.RestoreCalls);
        AssertHelpers.Compliant(outcome);
    }

    [Fact]
    public void DefaultStageRestoreIntent_TargetsFirmwareDefaultStage()
    {
        var intent = ControlIntent.ForDefaultStageRestore(ControlRequestOrigin.Manual);

        Assert.Equal(ControlIntentKind.DefaultStageRestore, intent.Kind);
        Assert.Equal(PerformanceShiftMap.FirmwareDefaultStage, intent.Stage);
        Assert.Equal(PerformanceStage.Balanced, PerformanceShiftMap.FirmwareDefaultStage);
    }
}
