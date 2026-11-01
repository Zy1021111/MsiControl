using MsiControl.Application.Control;
using MsiControl.Application.Policy;
using MsiControl.Core;
using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Application.Tests;

/// <summary>供电变化、启动恢复与退出行为策略测试。</summary>
public sealed class PolicyTests
{
    // ── 供电变化 ─────────────────────────────────────────────────────

    [Fact]
    public void FirstEvent_OnAc_DoesNotSwitchAnything()
    {
        var policy = new PowerSupplyPolicy();

        var decision = policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Balanced);

        Assert.Equal(PowerSupplyActionKind.None, decision.Kind);
        Assert.Equal(PowerSupplyKind.Ac, policy.LastKnownSupply);
    }

    [Fact]
    public void UnknownSupply_DoesNotSwitch()
    {
        var policy = new PowerSupplyPolicy();

        var decision = policy.OnSupplyChanged(PowerSupplyKind.Unknown, PerformanceStage.Balanced);

        Assert.Equal(PowerSupplyActionKind.None, decision.Kind);
        Assert.Contains("未知", decision.Reason, StringComparison.Ordinal);
        Assert.Equal(PowerSupplyKind.Unknown, policy.LastKnownSupply);
    }

    [Fact]
    public void Unplug_RecordsStageAndSwitchesToEco()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Extreme);

        var decision = policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Extreme);

        Assert.Equal(PowerSupplyActionKind.SwitchToStage, decision.Kind);
        Assert.Equal(PerformanceStage.Eco, decision.Stage);
        Assert.Equal(PerformanceStage.Extreme, policy.StageBeforeUnplug);
        Assert.True(policy.IsOnBattery);
    }

    [Fact]
    public void Replug_WithoutManualSelection_RestoresRecordedStage()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Balanced);
        policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Balanced);

        var decision = policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Eco);

        Assert.Equal(PowerSupplyActionKind.RestoreRecordedStage, decision.Kind);
        Assert.Equal(PerformanceStage.Balanced, decision.Stage);
        Assert.Null(policy.StageBeforeUnplug);
    }

    [Fact]
    public void Replug_AfterManualSelection_KeepsManualChoice()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Balanced);
        policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Balanced);

        policy.NotifyManualPerformanceSelection();
        Assert.True(policy.HasManualSelectionSinceUnplug);

        var decision = policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Quiet);

        Assert.Equal(PowerSupplyActionKind.None, decision.Kind);
        Assert.Contains("保留用户的手动选择", decision.Reason, StringComparison.Ordinal);
        Assert.Null(policy.StageBeforeUnplug);
    }

    [Fact]
    public void ManualSelectionWhileOnAc_DoesNotAffectNextCycle()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Balanced);

        policy.NotifyManualPerformanceSelection();
        Assert.False(policy.HasManualSelectionSinceUnplug);

        policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Quiet);
        var decision = policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Eco);

        Assert.Equal(PowerSupplyActionKind.RestoreRecordedStage, decision.Kind);
        Assert.Equal(PerformanceStage.Quiet, decision.Stage);
    }

    [Fact]
    public void RepeatedSupplyEvents_AreNoOps()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Balanced);

        var second = policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Balanced);
        var third = policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Balanced);

        Assert.Equal(PowerSupplyActionKind.None, second.Kind);
        Assert.Equal(PowerSupplyActionKind.None, third.Kind);
        Assert.Contains("未变化", second.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnplugWithoutReadableStage_DoesNotInventRestoreTarget()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Ac, null);

        var unplug = policy.OnSupplyChanged(PowerSupplyKind.Battery, null);
        var replug = policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Eco);

        Assert.Equal(PowerSupplyActionKind.SwitchToStage, unplug.Kind);
        Assert.Null(policy.StageBeforeUnplug);
        Assert.Equal(PowerSupplyActionKind.None, replug.Kind);
        Assert.Contains("未能读取到档位", replug.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resume_DoesNotReplayWrites()
    {
        var decision = PowerSupplyPolicy.OnResumed();

        Assert.Equal(PowerSupplyActionKind.None, decision.Kind);
        Assert.Null(decision.Stage);
        Assert.Contains("尚未实测", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_ClearsRecordedState()
    {
        var policy = new PowerSupplyPolicy();
        policy.OnSupplyChanged(PowerSupplyKind.Ac, PerformanceStage.Balanced);
        policy.OnSupplyChanged(PowerSupplyKind.Battery, PerformanceStage.Balanced);

        policy.Reset();

        Assert.Equal(PowerSupplyKind.Unknown, policy.LastKnownSupply);
        Assert.Null(policy.StageBeforeUnplug);
        Assert.False(policy.HasManualSelectionSinceUnplug);
    }

    [Fact]
    public void SupplyPolicy_HasNoGraphicsAction()
    {
        // 任务书 §11：供电变化不自动切换显卡模式。用类型结构保证，而不是靠注释。
        Assert.DoesNotContain("Graphics", string.Join(",", Enum.GetNames<PowerSupplyActionKind>()), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            typeof(PowerSupplyDecision).GetProperties().Select(p => p.Name),
            name => name.Contains("Graphics", StringComparison.OrdinalIgnoreCase));
    }

    // ── 启动恢复 ─────────────────────────────────────────────────────

    [Fact]
    public void StartupRestore_WithoutRecord_Refuses()
    {
        var source = new FakeLastAppliedConfigSource();
        var input = new StartupRestoreInput(TargetDevice(), PowerSupplyKind.Ac);

        var decision = StartupRestorePolicy.Evaluate(input, source);

        Assert.False(decision.ShouldRestore);
        Assert.Null(decision.Stage);
        Assert.Contains(decision.Checks, c => !c.Passed && c.Name == "存在最近成功应用记录");
        Assert.Contains(decision.Checks, c => c.Name == "显卡模式排除" && c.Passed);
    }

    [Fact]
    public void StartupRestore_WhenDeviceUnidentified_Refuses()
    {
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(
                SettingsFor(PerformanceStage.Balanced),
                TargetDevice(),
                DateTimeOffset.Now)
        };

        var decision = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(DeviceIdentity.Unknown, PowerSupplyKind.Ac),
            source);

        Assert.False(decision.ShouldRestore);
        Assert.Contains(decision.Checks, c => !c.Passed && c.Name == "本机设备已识别");
    }

    [Fact]
    public void StartupRestore_WhenBoardDiffers_Refuses()
    {
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(
                SettingsFor(PerformanceStage.Balanced),
                new DeviceIdentity("Other Laptop", "MS-17XX"),
                DateTimeOffset.Now)
        };

        var decision = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(TargetDevice(), PowerSupplyKind.Ac),
            source);

        Assert.False(decision.ShouldRestore);
        Assert.Contains(decision.Checks, c => !c.Passed && c.Name == "配置与本机同主板");
    }

    [Fact]
    public void StartupRestore_WhenRecordDeviceUnidentified_Refuses()
    {
        // 未识别 ≠ 同机型：双方都必须有有效标识（docs/接口与工程约定.md §3.10）。
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(
                SettingsFor(PerformanceStage.Balanced),
                DeviceIdentity.Unknown,
                DateTimeOffset.Now)
        };

        var decision = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(TargetDevice(), PowerSupplyKind.Ac),
            source);

        Assert.False(decision.ShouldRestore);
    }

    [Fact]
    public void StartupRestore_WhenSupplyUnknown_Refuses()
    {
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(SettingsFor(PerformanceStage.Balanced), TargetDevice(), DateTimeOffset.Now)
        };

        var decision = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(TargetDevice(), PowerSupplyKind.Unknown),
            source);

        Assert.False(decision.ShouldRestore);
        Assert.Contains(decision.Checks, c => !c.Passed && c.Name == "供电状态已知");
    }

    [Fact]
    public void StartupRestore_WhenConfigurationIsContradictory_Refuses()
    {
        var broken = new PerformanceSettings(
            PerformanceStage.Balanced,
            CpuLongTerm: PowerLimit.Custom(60m),
            CpuShortTerm: PowerLimit.Custom(20m),
            CpuTurbo: CpuTurboLimit.Native,
            GpuLimit: PowerLimit.Custom(90m));

        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(broken, TargetDevice(), DateTimeOffset.Now)
        };

        var decision = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(TargetDevice(), PowerSupplyKind.Ac),
            source);

        Assert.False(decision.ShouldRestore);
        Assert.Contains(decision.Checks, c => !c.Passed && c.Name == "配置自洽");
    }

    [Fact]
    public void StartupRestore_WhenAllChecksPass_ApprovesRecordedStage()
    {
        var source = new FakeLastAppliedConfigSource
        {
            Value = new LastAppliedConfig(
                SettingsFor(PerformanceStage.HighPerformance),
                TargetDevice(),
                DateTimeOffset.Now)
        };

        var decision = StartupRestorePolicy.Evaluate(
            new StartupRestoreInput(TargetDevice(), PowerSupplyKind.Ac),
            source);

        Assert.True(decision.ShouldRestore);
        Assert.Equal(PerformanceStage.HighPerformance, decision.Stage);
        Assert.All(decision.Checks, c => Assert.True(c.Passed, c.Name + "：" + c.Detail));
        Assert.Contains("不会覆盖", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupRestoreDecision_HasNoGraphicsTarget()
    {
        Assert.DoesNotContain(
            typeof(StartupRestoreDecision).GetProperties().Select(p => p.Name),
            name => name.Contains("Graphics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoLastAppliedConfigSource_ReturnsNullAndExplainsWhy()
    {
        var source = new NoLastAppliedConfigSource();

        Assert.Null(source.Read());
        Assert.Contains("任务 04/05", NoLastAppliedConfigSource.Reason, StringComparison.Ordinal);
    }

    // ── 退出行为 ─────────────────────────────────────────────────────

    [Fact]
    public void ExitBehavior_OnTargetMachine_ListsOnlyVerifiedWritesAsPersistent()
    {
        var summary = MsiControl.Application.Policy.ExitBehavior.Describe(TestCapabilities.TargetMachine());

        // CPU 功耗限制自 HWE §26 起达可写：它属于「退出程序后仍保留」的一类
        // （写进 MSR 0x610，直到重启才被固件复位），必须在清单里如实出现。
        Assert.Contains(
            summary.FirmwarePersistent,
            text => text.Contains("CPU 功耗限制", StringComparison.Ordinal));

        // 其余写入仍未达可写（睿频 / GPU 功耗 / 显卡模式），不得混进「退出后仍生效」。
        Assert.Contains(summary.Unverified, text => text.Contains("GPU 功耗限制", StringComparison.Ordinal));
        Assert.Contains(summary.Unverified, text => text.Contains("睿频", StringComparison.Ordinal));
        Assert.Contains("隐藏窗口 ≠ 退出程序", summary.HiddenIsNotExit, StringComparison.Ordinal);
        Assert.Contains("不承诺", summary.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ExitBehavior_WithSoftwareDrivenCurves_ListsProcessDependent()
    {
        var summary = MsiControl.Application.Policy.ExitBehavior.Describe(
            TestCapabilities.TargetMachine(),
            curvesExecutedByFirmware: false);

        Assert.Contains(summary.ProcessDependent, text => text.Contains("停止全部进程后不再维持", StringComparison.Ordinal));
    }

    [Fact]
    public void ExitBehavior_WithFirmwareDrivenCurves_ListsFirmwarePersistent()
    {
        var summary = MsiControl.Application.Policy.ExitBehavior.Describe(
            TestCapabilities.TargetMachine(),
            curvesExecutedByFirmware: true);

        Assert.Contains(summary.FirmwarePersistent, text => text.Contains("固件持续执行", StringComparison.Ordinal));
    }

    [Fact]
    public void ExitBehavior_WithVerifiedPowerLimit_ReportsFirmwarePersistence()
    {
        var summary = MsiControl.Application.Policy.ExitBehavior.Describe(
            TestCapabilities.AllVerified(CapabilityKeys.CpuPowerLimit));

        Assert.Contains(summary.FirmwarePersistent, text => text.Contains("功耗限制", StringComparison.Ordinal));
    }

    private static DeviceIdentity TargetDevice()
        => new("Vector 16 HX AI A2XWHG", DeviceIdentity.TargetBoardCode, BiosVersion: "E15M3IMS.117");

    private static PerformanceSettings SettingsFor(PerformanceStage stage)
        => PerformanceProfileCatalog.GetDefault(stage).Settings;
}
