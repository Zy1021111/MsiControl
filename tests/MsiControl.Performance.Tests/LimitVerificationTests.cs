using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>
/// 限制生效性判定。
/// </summary>
/// <remarks>
/// 本组用例对应任务书 §8.5 最容易被违反的一条：
/// <b>「实际低负载功耗低于目标，不是限制成功的证据。」</b>
/// 因此这里既验证正向判定，也验证「不许在证据不足时判成功」。
/// </remarks>
public sealed class LimitVerificationTests
{
    [Fact]
    public void NativeTarget_IsNotAttempted()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: null,
            ReadBackValue: 60m,
            Load: LoadCondition.EffectiveLoad));

        Assert.Equal(LimitVerificationVerdict.NotAttempted, result.Verdict);
        Assert.False(result.IsConfirmed);
    }

    [Fact]
    public void ReadBackEqualsRequest_IsConfirmed()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: 60m,
            Load: LoadCondition.EffectiveLoad));

        Assert.Equal(LimitVerificationVerdict.ConfirmedByReadBack, result.Verdict);
        Assert.True(result.IsConfirmed);
        Assert.Contains("确认限制值已按请求生效", result.Basis);
    }

    [Fact]
    public void ReadBackWithinTolerance_IsConfirmed()
    {
        var result = LimitVerification.Verify(
            new LimitVerificationInput(
                SettingKey: PerformanceSettingKeys.GpuLimit,
                RequestedValue: 90m,
                ReadBackValue: 90.4m,
                Load: LoadCondition.EffectiveLoad),
            tolerance: 0.5m);

        Assert.True(result.IsConfirmed);
    }

    [Fact]
    public void ReadBackDiffers_IsViolatedAndReported()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.GpuLimit,
            RequestedValue: 90m,
            ReadBackValue: 80m,
            Load: LoadCondition.EffectiveLoad));

        Assert.Equal(LimitVerificationVerdict.ReadBackMismatch, result.Verdict);
        Assert.True(result.IsViolated);
        Assert.Contains("80", result.Basis);
        Assert.Contains("90", result.Basis);
    }

    [Fact]
    public void IdleWithoutReadBack_IsInsufficientEvidence_NotSuccess()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: null,
            Load: LoadCondition.Idle,
            ObservedPackageWatts: 12m));

        Assert.Equal(LimitVerificationVerdict.InsufficientEvidence, result.Verdict);
        Assert.False(result.IsConfirmed);
        Assert.True(result.IsInconclusive);
        Assert.Contains(result.Notes, note => note.Contains("不构成限制生效的证据"));
    }

    [Fact]
    public void EffectiveLoadWithoutReadBack_IsStillUnconfirmed()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: null,
            Load: LoadCondition.EffectiveLoad,
            ObservedPackageWatts: 58m,
            ObservationWindow: TimeSpan.FromSeconds(120)));

        Assert.Equal(LimitVerificationVerdict.UnverifiableByReadBack, result.Verdict);
        Assert.False(result.IsConfirmed);
        Assert.Contains("不得宣称限制已生效", result.Basis);
    }

    [Fact]
    public void EffectiveLoadWithOvershoot_IsViolated()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: null,
            Load: LoadCondition.EffectiveLoad,
            ObservedPackageWatts: 75m,
            ObservationWindow: TimeSpan.FromSeconds(60)));

        Assert.Equal(LimitVerificationVerdict.ObservedExceedsRequested, result.Verdict);
        Assert.True(result.IsViolated);
        Assert.Contains(result.Notes, note => note.Contains("瞬时偏差"));
    }

    [Fact]
    public void UnknownLoad_WithoutReadBack_IsInconclusive()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: null,
            Load: LoadCondition.Unknown));

        Assert.Equal(LimitVerificationVerdict.UnverifiableByReadBack, result.Verdict);
        Assert.Contains("不能排除未生效", result.Basis);
    }

    [Fact]
    public void ReadBackConfirmedButObservedOvershoot_ExplainsTimeWindow()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: 60m,
            Load: LoadCondition.EffectiveLoad,
            ObservedPackageWatts: 88m,
            ObservationWindow: TimeSpan.FromSeconds(30)));

        Assert.True(result.IsConfirmed);
        Assert.Contains(result.Notes, note => note.Contains("时间窗口内的瞬时偏差"));
        Assert.Contains(result.Notes, note => note.Contains("不能承诺每次采样都低于设定值"));
    }

    [Fact]
    public void EffectiveLoadWithoutWindow_NotesWeakerEvidence()
    {
        var result = LimitVerification.Verify(new LimitVerificationInput(
            SettingKey: PerformanceSettingKeys.CpuLongTerm,
            RequestedValue: 60m,
            ReadBackValue: null,
            Load: LoadCondition.EffectiveLoad,
            ObservedPackageWatts: 50m));

        Assert.Contains(result.Notes, note => note.Contains("未声明观测时长"));
    }

    [Fact]
    public void NegativeTolerance_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LimitVerification.Verify(
            new LimitVerificationInput(
                SettingKey: PerformanceSettingKeys.CpuLongTerm,
                RequestedValue: 60m,
                ReadBackValue: 60m,
                Load: LoadCondition.EffectiveLoad),
            tolerance: -0.1m));

    [Fact]
    public void NegativeObservationWindow_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LimitVerification.Verify(
            new LimitVerificationInput(
                SettingKey: PerformanceSettingKeys.CpuLongTerm,
                RequestedValue: 60m,
                ReadBackValue: null,
                Load: LoadCondition.EffectiveLoad,
                ObservedPackageWatts: 50m,
                ObservationWindow: TimeSpan.FromSeconds(-1))));

    [Fact]
    public void EmptySettingKey_IsRejected()
        => Assert.Throws<ArgumentException>(() => LimitVerification.Verify(
            new LimitVerificationInput(
                SettingKey: " ",
                RequestedValue: 60m,
                ReadBackValue: null,
                Load: LoadCondition.Idle)));
}
