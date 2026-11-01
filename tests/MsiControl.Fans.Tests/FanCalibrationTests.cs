using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices;
using Xunit;

namespace MsiControl.Fans.Tests;

public sealed class FanCalibrationBaselineTests
{
    private static DateTimeOffset Now => DateTimeOffset.Now;

    [Fact]
    public void RequiresMinimumSamples()
    {
        var ok = FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            new[] { 3000, 3010 },
            Now,
            "测试",
            out var baseline,
            out var errors);

        Assert.False(ok);
        Assert.Null(baseline);
        Assert.Contains(errors, e => e.Contains("最少", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsUnstableSamples()
    {
        var ok = FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            new[] { 3000, 4000, 3000 },
            Now,
            "测试",
            out var baseline,
            out var errors);

        Assert.False(ok);
        Assert.Null(baseline);
        Assert.Contains(errors, e => e.Contains("不稳定", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsNonPositiveSamples()
    {
        var ok = FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            new[] { 0, 3000, 3000 },
            Now,
            "测试",
            out _,
            out var errors);

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("非正的转速", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsEmptySource()
    {
        var ok = FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            new[] { 3000, 3000, 3000 },
            Now,
            "   ",
            out _,
            out var errors);

        Assert.False(ok);
        Assert.Contains(errors, e => e.Contains("来源说明不能为空", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildsAverageAndConvertsRpmToPercent()
    {
        var ok = FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            new[] { 2990, 3000, 3010 },
            Now,
            "测试",
            out var baseline,
            out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.Equal(3000, baseline!.FullSpeedRpm);
        Assert.Equal(50.0, baseline.ToPercent(1500), 6);
        Assert.Equal(100.0, baseline.ToPercent(5000), 6);
        Assert.Equal(0.0, baseline.ToPercent(-10), 6);
        Assert.Contains("不代表设定任意输出都能得到对应转速", baseline.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void BaselineSet_IsUsableOnlyWhenBothChannelsPresent()
    {
        FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            new[] { 3000, 3000, 3000 },
            Now,
            "测试",
            out var cpu,
            out _);

        Assert.False(new FanCalibrationBaselineSet(cpu, null).IsUsable);
        Assert.True(new FanCalibrationBaselineSet(cpu, cpu).IsUsable);
        Assert.Null(FanCalibrationBaselineSet.Empty.For(FanChannel.Gpu));
        Assert.Contains("基准不完整", FanCalibrationBaselineSet.Empty.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void TryCreate_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu, null!, Now, "测试", out _, out _));
        Assert.Throws<ArgumentNullException>(() => FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu, new[] { 1 }, Now, null!, out _, out _));
    }
}

public sealed class FanCalibrationSessionTests
{
    private static FanCalibrationPlan Plan(int sampleCount = 4)
        => new(sampleCount, TimeSpan.FromSeconds(5), UserAcknowledgedFullSpeed: true);

    [Fact]
    public void Plan_Default_IsNotAcknowledged()
    {
        Assert.False(FanCalibrationPlan.Default.UserAcknowledgedFullSpeed);
        Assert.Empty(FanCalibrationPlan.Default.Validate());
        Assert.Equal(TimeSpan.FromSeconds(30), FanCalibrationPlan.Default.EffectiveSettleTime);
    }

    [Fact]
    public void Plan_Validate_RejectsTooFewSamples()
    {
        var plan = new FanCalibrationPlan(1, TimeSpan.FromSeconds(5), true);

        Assert.Contains(plan.Validate(), p => p.Contains("少于", StringComparison.Ordinal));
    }

    [Fact]
    public void Report_IgnoresStaleReadings()
    {
        var session = new FanCalibrationSession(Plan());
        var stale = TestData.Reading(3000, DateTimeOffset.Now).AsStaleIfValid("测试：已过期");
        var fans = new FanSnapshot(stale, TestData.Reading(3000, DateTimeOffset.Now));

        Assert.False(session.Report(fans));
        Assert.Empty(session.CpuSamples);
        Assert.Equal(0, session.ProgressPercent);
    }

    [Fact]
    public void Report_TracksProgress()
    {
        var session = new FanCalibrationSession(Plan(4));
        var now = DateTimeOffset.Now;

        Assert.False(session.Report(TestData.Snapshot(3000, 3000, now)));
        Assert.Equal(25, session.ProgressPercent);

        Assert.False(session.Report(TestData.Snapshot(3000, 3000, now)));
        Assert.Equal(50, session.ProgressPercent);
        Assert.True(session.IsRunning);
    }

    [Fact]
    public void Complete_BuildsBaselinesFromSamples()
    {
        var session = new FanCalibrationSession(Plan(3));
        var now = DateTimeOffset.Now;
        session.Report(TestData.Snapshot(2990, 3990, now));
        session.Report(TestData.Snapshot(3000, 4000, now));
        session.Report(TestData.Snapshot(3010, 4010, now));
        Assert.True(session.ProgressPercent >= 75);

        var baselines = session.Complete();

        Assert.True(baselines.IsUsable);
        Assert.Equal(3000, baselines.Cpu!.FullSpeedRpm);
        Assert.Equal(4000, baselines.Gpu!.FullSpeedRpm);
        Assert.Equal(FanCalibrationState.Completed, session.State);
        Assert.Contains("校准完成", session.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Complete_Fails_WhenSamplesInsufficient()
    {
        var session = new FanCalibrationSession(Plan(3));
        session.Report(TestData.Snapshot(3000, 3000, DateTimeOffset.Now));

        var baselines = session.Complete();

        Assert.False(baselines.IsUsable);
        Assert.Equal(FanCalibrationState.Failed, session.State);
        Assert.Contains("样本数量", session.TerminationReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancel_MarksStateAndKeepsSamples()
    {
        var session = new FanCalibrationSession(Plan(3));
        session.Report(TestData.Snapshot(3000, 3000, DateTimeOffset.Now));

        session.Cancel();

        Assert.Equal(FanCalibrationState.Cancelled, session.State);
        Assert.False(session.IsRunning);
        Assert.Single(session.CpuSamples);
        Assert.Contains("已取消", session.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_RequiresReason()
    {
        var session = new FanCalibrationSession(Plan());

        Assert.Throws<ArgumentException>(() => session.Fail("  "));
        session.Fail("硬件失败。");
        Assert.Equal(FanCalibrationState.Failed, session.State);
    }

    [Fact]
    public void Report_AfterTermination_IsIgnored()
    {
        var session = new FanCalibrationSession(Plan(3));
        session.Cancel();

        Assert.False(session.Report(TestData.Snapshot(3000, 3000, DateTimeOffset.Now)));
        Assert.Empty(session.CpuSamples);
    }
}

public sealed class FanCalibrationServiceTests
{
    private static (FanCalibrationService Calibration, FanApplicationService Application, FakeFanHardware Hardware)
        CreateVerifiedPair(bool captureSnapshot = true)
    {
        var curves = TestData.CurveSet();
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(curves),
            ApplyResult = TestData.Accepted(),
            FullSpeedResult = TestData.Accepted("fan.fullspeed", "启用"),

            // 进入满速后读回 Mode=FullSpeed，退出满速后由各用例改写回自定义曲线状态。
            StateAfterWrite = new FanHardwareState(FanControlMode.FullSpeed, curves.Cpu, curves.Gpu)
        };

        var application = new FanApplicationService(
            hardware,
            TestData.VerifiedLimits(),
            TestData.VerifiedFanCapabilities());

        if (captureSnapshot)
        {
            var read = application.ReadStateAsync(CancellationToken.None).GetAwaiter().GetResult();
            application.BootSnapshot.TryCapture(read);
        }

        return (new FanCalibrationService(application, TestData.VerifiedFanCapabilities()), application, hardware);
    }

    private static FanCalibrationPlan AcknowledgedPlan(int sampleCount = 3)
        => new(sampleCount, TimeSpan.FromSeconds(1), UserAcknowledgedFullSpeed: true);

    [Fact]
    public void CheckPreconditions_RequiresUserAcknowledgement()
    {
        var (calibration, _, _) = CreateVerifiedPair();

        var reasons = calibration.CheckPreconditions(FanCalibrationPlan.Default);

        Assert.Contains(reasons, r => r.Contains("尚未取得用户对满速测试的明确同意", StringComparison.Ordinal));
        Assert.Contains(FanCalibrationService.FullSpeedTestNotice, string.Join(" ", reasons), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckPreconditions_BlocksOnRealBaseline()
    {
        var hardware = new FakeFanHardware { CanDecodeState = false };
        var capabilities = Ms15M3Capabilities.Create();
        var application = new FanApplicationService(hardware, FanCurveLimits.Ms15M3, capabilities);
        var calibration = new FanCalibrationService(application, capabilities);

        var reasons = calibration.CheckPreconditions(AcknowledgedPlan());

        Assert.Contains(reasons, r => r.Contains(CapabilityKeys.FanCalibration, StringComparison.Ordinal));
        // 2026-10-17 下午：fan.fullspeed 已升 Verified（叶哥决定启用），不再是校准前置阻断项。
        Assert.DoesNotContain(reasons, r => r.Contains(CapabilityKeys.FanFullSpeed, StringComparison.Ordinal));
        Assert.Contains(reasons, r => r.Contains("首次写入前的快照", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_IsBlocked_WithoutAcknowledgement_AndTouchesNoHardware()
    {
        var (calibration, _, hardware) = CreateVerifiedPair();

        var result = await calibration.StartAsync(FanCalibrationPlan.Default, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal(0, hardware.WriteCount);
        Assert.Null(calibration.Current);
    }

    [Fact]
    public async Task Start_Succeeds_WithVerifiedCapabilitiesAndAcknowledgement()
    {
        var (calibration, _, hardware) = CreateVerifiedPair();

        var result = await calibration.StartAsync(AcknowledgedPlan(), CancellationToken.None);

        Assert.Equal(FanCalibrationOutcome.Started, result.Outcome);
        Assert.NotNull(calibration.Current);
        Assert.Equal(1, hardware.WriteCount);
        Assert.Contains("SetFullSpeedAsync:True", hardware.Calls);
    }

    [Fact]
    public async Task Complete_ProducesBaselines_AndRestoresFullSpeed()
    {
        var (calibration, _, hardware) = CreateVerifiedPair();
        await calibration.StartAsync(AcknowledgedPlan(3), CancellationToken.None);

        var now = DateTimeOffset.Now;
        calibration.Report(TestData.Snapshot(2990, 3990, now));
        calibration.Report(TestData.Snapshot(3000, 4000, now));
        calibration.Report(TestData.Snapshot(3010, 4010, now));

        hardware.StateAfterWrite = TestData.CustomState();
        var result = await calibration.CompleteAsync(CancellationToken.None);

        Assert.Equal(FanCalibrationOutcome.Completed, result.Outcome);
        Assert.True(calibration.Baselines.IsUsable);
        Assert.Null(calibration.Current);
        Assert.Equal(2, hardware.WriteCount);
        Assert.Contains("SetFullSpeedAsync:False", hardware.Calls);
    }

    [Fact]
    public async Task Cancel_StillRestoresFullSpeed()
    {
        var (calibration, _, hardware) = CreateVerifiedPair();
        await calibration.StartAsync(AcknowledgedPlan(3), CancellationToken.None);

        hardware.StateAfterWrite = TestData.CustomState();
        var result = await calibration.CancelAsync(CancellationToken.None);

        Assert.Equal(FanCalibrationOutcome.Cancelled, result.Outcome);
        Assert.Null(calibration.Current);
        Assert.Contains("SetFullSpeedAsync:False", hardware.Calls);
        Assert.False(calibration.Baselines.IsUsable);
    }

    [Fact]
    public async Task Complete_WithoutSession_Fails()
    {
        var (calibration, _, _) = CreateVerifiedPair();

        var result = await calibration.CompleteAsync(CancellationToken.None);

        Assert.Equal(FanCalibrationOutcome.Failed, result.Outcome);
        Assert.Contains("没有进行中的校准会话", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Complete_ReportsFailure_WhenRestoreNotConfirmed()
    {
        var (calibration, _, hardware) = CreateVerifiedPair();
        await calibration.StartAsync(AcknowledgedPlan(3), CancellationToken.None);

        var now = DateTimeOffset.Now;
        calibration.Report(TestData.Snapshot(3000, 4000, now));
        calibration.Report(TestData.Snapshot(3000, 4000, now));
        calibration.Report(TestData.Snapshot(3000, 4000, now));

        // 退出满速后读回的状态与进入前不一致：不得宣称恢复成功。
        hardware.StateAfterWrite = new FanHardwareState(
            FanControlMode.CustomCurve,
            TestData.Curve(FanChannel.Cpu, (40, 90), (60, 90)),
            TestData.Curve(FanChannel.Gpu, (40, 90), (60, 90)));

        var result = await calibration.CompleteAsync(CancellationToken.None);

        Assert.Equal(FanCalibrationOutcome.Failed, result.Outcome);
        Assert.Contains("未确认", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_WithoutSession_ReturnsFalse()
    {
        var (calibration, _, _) = CreateVerifiedPair();

        Assert.False(calibration.Report(TestData.Snapshot(3000, 3000, DateTimeOffset.Now)));
    }
}
