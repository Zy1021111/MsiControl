using MsiControl.Core;
using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>
/// 目标值与可写范围的对照判定。
/// </summary>
/// <remarks>核心回归点：<b>目标不受支持时不得被静默替换，也不得被静默截断</b>。</remarks>
public sealed class LimitSupportEvaluatorTests
{
    private static readonly LimitRange KnownRange = LimitRange.Known(35m, 90m, 5m, "Get_Range 返回值");

    [Fact]
    public void Native_SaysRestoreNativePolicy()
    {
        var result = LimitSupportEvaluator.Evaluate(PowerLimit.Native, KnownRange);

        Assert.Equal(LimitSupportVerdict.RestoreNativePolicy, result.Verdict);
        Assert.True(result.IsNativePolicy);
        Assert.True(result.CanApplyRequestedValue);
        Assert.False(result.RequiresFallback);
    }

    [Fact]
    public void WithinRange_IsApplicableAsRequested()
    {
        var result = LimitSupportEvaluator.Evaluate(PowerLimit.Custom(60m), KnownRange);

        Assert.Equal(LimitSupportVerdict.WithinReportedRange, result.Verdict);
        Assert.True(result.CanApplyRequestedValue);
        Assert.Contains("60 W", result.Explanation);
    }

    [Fact]
    public void BelowMinimum_DoesNotSilentlyReplaceButOffersCandidate()
    {
        var result = LimitSupportEvaluator.Evaluate(PowerLimit.Custom(20m), KnownRange);

        Assert.Equal(LimitSupportVerdict.BelowReportedMinimum, result.Verdict);
        Assert.True(result.RequiresFallback);
        Assert.False(result.CanApplyRequestedValue);
        Assert.Equal(35m, result.NearestSupportedValue);
        Assert.Contains("不会静默替换", result.Explanation);
    }

    [Fact]
    public void AboveMaximum_DoesNotSilentlyTruncate()
    {
        var result = LimitSupportEvaluator.Evaluate(PowerLimit.Custom(120m), KnownRange);

        Assert.Equal(LimitSupportVerdict.AboveReportedMaximum, result.Verdict);
        Assert.Equal(90m, result.NearestSupportedValue);
        Assert.Contains("不会静默截断", result.Explanation);
    }

    [Fact]
    public void BoundaryValues_AreWithinRange()
    {
        Assert.Equal(
            LimitSupportVerdict.WithinReportedRange,
            LimitSupportEvaluator.Evaluate(PowerLimit.Custom(35m), KnownRange).Verdict);
        Assert.Equal(
            LimitSupportVerdict.WithinReportedRange,
            LimitSupportEvaluator.Evaluate(PowerLimit.Custom(90m), KnownRange).Verdict);
    }

    [Fact]
    public void UnknownRange_IsIndeterminate_NotUnsupported()
    {
        var result = LimitSupportEvaluator.Evaluate(
            PowerLimit.Custom(60m),
            LimitRange.Unknown("Get_Range 尚未调用验证。"));

        Assert.Equal(LimitSupportVerdict.RangeUnknown, result.Verdict);
        Assert.True(result.IsIndeterminate);
        Assert.False(result.RequiresFallback);
        Assert.False(result.CanApplyRequestedValue);
        Assert.Contains("不假定其受支持", result.Explanation);
    }

    [Fact]
    public void TurboOverload_UsesFrequencyUnit()
    {
        var result = LimitSupportEvaluator.Evaluate(
            CpuTurboLimit.Custom(4500),
            LimitRange.Known(800m, 6000m, 100m, "测试"));

        Assert.Equal(LimitSupportVerdict.WithinReportedRange, result.Verdict);
        Assert.Contains("MHz", result.Explanation);
        Assert.Contains("4500", result.Explanation);
    }

    [Fact]
    public void TurboNative_UsesNativePolicy()
    {
        var result = LimitSupportEvaluator.Evaluate(CpuTurboLimit.Native, KnownRange);

        Assert.True(result.IsNativePolicy);
        Assert.Contains("睿频上限", result.Explanation);
    }

    [Fact]
    public void TurboBelowMinimum_RequiresFallback()
    {
        var result = LimitSupportEvaluator.Evaluate(
            CpuTurboLimit.Custom(400),
            LimitRange.Known(800m, 6000m, null, "测试"));

        Assert.Equal(LimitSupportVerdict.BelowReportedMinimum, result.Verdict);
        Assert.Equal(800m, result.NearestSupportedValue);
    }

    [Fact]
    public void Evaluate_DoesNotMutateTarget()
    {
        var target = PowerLimit.Custom(20m);

        LimitSupportEvaluator.Evaluate(target, KnownRange);

        Assert.Equal(20m, target.Watts);
        Assert.Equal(LimitMode.Custom, target.Mode);
    }
}
