using MsiControl.Core;
using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Fans.Tests;

public sealed class SoftwareFanPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 20, 0, 0, TimeSpan.FromHours(8));

    private static FanCurve Curve => TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40), (80, 80));

    private static FanPolicyInput Input(double? temperature, TimeSpan offset, bool resumed = false)
        => new(
            temperature,
            temperature is null ? null : T0 + offset,
            T0 + offset,
            resumed);

    [Fact]
    public void FirstEvaluation_WritesCurveTarget()
    {
        var policy = new SoftwareFanPolicy();

        var decision = policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        Assert.Equal(FanPolicyAction.SetOutput, decision.Action);
        Assert.True(decision.RequiresWrite);
        Assert.Equal(20.0, decision.TargetPercent!.Value, 6);
        Assert.Equal(20.0, decision.CurveTargetPercent!.Value, 6);
        Assert.Equal(20.0, policy.LastAppliedPercent!.Value, 6);
    }

    [Fact]
    public void Hysteresis_HoldsWithinBand()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(41.5, TimeSpan.FromSeconds(10)));

        Assert.Equal(FanPolicyAction.Hold, decision.Action);
        Assert.Null(decision.TargetPercent);
        Assert.Contains("滞回带", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IncreaseIsThrottledToMaxUpStep()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(70, TimeSpan.FromSeconds(10)));

        Assert.Equal(FanPolicyAction.SetOutput, decision.Action);
        Assert.Equal(25.0, decision.TargetPercent!.Value, 6);
        Assert.Equal(60.0, decision.CurveTargetPercent!.Value, 6);
        Assert.Contains("升降速节流", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void DecreaseIsThrottledMoreConservatively()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));
        policy.Evaluate(Curve, Input(70, TimeSpan.FromSeconds(10)));

        var decision = policy.Evaluate(Curve, Input(40, TimeSpan.FromSeconds(20)));

        Assert.Equal(FanPolicyAction.SetOutput, decision.Action);
        Assert.Equal(23.0, decision.TargetPercent!.Value, 6);
    }

    [Fact]
    public void MinimumChangeInterval_Holds()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(70, TimeSpan.FromSeconds(1)));

        Assert.Equal(FanPolicyAction.Hold, decision.Action);
        Assert.Contains("距上次调速不足", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SensorTimeout_RelinquishesToFirmware_InsteadOfInventingASafeValue()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(null, TimeSpan.FromSeconds(20)));

        Assert.Equal(FanPolicyAction.RelinquishToFirmware, decision.Action);
        Assert.Null(decision.TargetPercent);
        Assert.Contains("交还固件自动策略", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("不以「拉满转速」代替失去反馈时的决策", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TemporarySensorLoss_Holds()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(null, TimeSpan.FromSeconds(2)));

        Assert.Equal(FanPolicyAction.Hold, decision.Action);
        Assert.Contains("暂时不可用", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumeFromSleep_WithoutFreshSample_Holds()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(null, TimeSpan.FromSeconds(30), resumed: true));

        Assert.Equal(FanPolicyAction.Hold, decision.Action);
        Assert.Contains("睡眠唤醒", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ResumeFromSleep_WithFreshSample_ResetsThrottleBaseline()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));

        var decision = policy.Evaluate(Curve, Input(70, TimeSpan.FromSeconds(1), resumed: true));

        Assert.Equal(FanPolicyAction.SetOutput, decision.Action);
        Assert.Equal(25.0, decision.TargetPercent!.Value, 6);
    }

    [Fact]
    public void Reset_ClearsHistory()
    {
        var policy = new SoftwareFanPolicy();
        policy.Evaluate(Curve, Input(40, TimeSpan.Zero));
        Assert.NotNull(policy.LastAppliedPercent);

        policy.Reset();

        Assert.Null(policy.LastAppliedPercent);
    }

    [Fact]
    public void Settings_ValidateRanges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanPolicySettings(HysteresisC: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanPolicySettings(MaxIncreaseStepPercent: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanPolicySettings(MaxDecreaseStepPercent: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanPolicySettings(MinimumOutputDelta: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanPolicySettings(MinimumChangeInterval: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanPolicySettings(SensorTimeout: TimeSpan.Zero));

        var settings = new FanPolicySettings();
        Assert.Equal(TimeSpan.FromSeconds(5), settings.MinimumChangeInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), settings.SensorTimeout);
    }

    [Fact]
    public void Evaluate_NullArguments_Throw()
    {
        var policy = new SoftwareFanPolicy();

        Assert.Throws<ArgumentNullException>(() => policy.Evaluate(null!, Input(40, TimeSpan.Zero)));
        Assert.Throws<ArgumentNullException>(() => policy.Evaluate(Curve, null!));
    }
}

public sealed class SoftwareFanControlLoopTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 20, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Constructor_RejectsChannelMismatch()
    {
        var gpuCurve = TestData.Curve(FanChannel.Gpu, (40, 20), (60, 40));

        Assert.Throws<ArgumentException>(() => new SoftwareFanControlLoop(FanChannel.Cpu, gpuCurve));
    }

    [Fact]
    public void Constructor_RejectsNullCurve()
    {
        Assert.Throws<ArgumentNullException>(() => new SoftwareFanControlLoop(FanChannel.Cpu, null!));
    }

    [Fact]
    public void CanApplyDecision_IsFalse_AndLimitationIsExplained()
    {
        var loop = new SoftwareFanControlLoop(
            FanChannel.Cpu,
            TestData.Curve(FanChannel.Cpu, (40, 20), (80, 80)));

        Assert.False(loop.CanApplyDecision);

        var description = loop.Describe();
        Assert.Contains("未闭环", description, StringComparison.Ordinal);
        Assert.Contains("IFanHardware", description, StringComparison.Ordinal);
        Assert.Contains("控制进程存活", description, StringComparison.Ordinal);
    }

    [Fact]
    public void Tick_RecordsBoundedHistory()
    {
        var loop = new SoftwareFanControlLoop(
            FanChannel.Cpu,
            TestData.Curve(FanChannel.Cpu, (40, 20), (80, 80)));

        var input = new FanPolicyInput(40, T0, T0);
        for (var i = 0; i < SoftwareFanControlLoop.HistoryLimit + 8; i++)
        {
            loop.Tick(input);
        }

        Assert.Equal(SoftwareFanControlLoop.HistoryLimit, loop.History.Count);
        Assert.NotNull(loop.History[^1]);
    }

    [Fact]
    public void DependencyNotice_MentionsProcessLifetime()
    {
        Assert.Contains("控制进程存活", SoftwareFanControlLoop.DependencyNotice, StringComparison.Ordinal);
        Assert.Contains("自动回滚", SoftwareFanControlLoop.DependencyNotice, StringComparison.Ordinal);
    }
}
