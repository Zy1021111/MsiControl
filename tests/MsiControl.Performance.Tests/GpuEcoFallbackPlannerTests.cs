using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>
/// GPU 替代节能策略的规划。
/// </summary>
/// <remarks>
/// 核心回归点：范围未知时**不猜下界**；给出替代策略时必须携带能力边界说明；
/// 替代策略不得被表达为「精确达到用户目标」。
/// </remarks>
public sealed class GpuEcoFallbackPlannerTests
{
    [Fact]
    public void Plan_WithoutRange_DoesNotGuessFloor()
    {
        var plan = GpuEcoFallbackPlanner.Plan(20m, LimitRange.Unknown("Get_Range 尚未成功返回。"));

        Assert.False(plan.HasFallback);
        Assert.Contains("不猜测下界", plan.Explanation);
    }

    [Fact]
    public void Plan_TargetAboveMinimum_NeedsNoFallback()
    {
        var plan = GpuEcoFallbackPlanner.Plan(60m, LimitRange.Known(35m, 90m, null, "测试"));

        Assert.False(plan.HasFallback);
        Assert.Contains("无需替代策略", plan.Explanation);
    }

    [Fact]
    public void Plan_TargetBelowMinimum_YieldsExplicitFallback()
    {
        var plan = GpuEcoFallbackPlanner.Plan(20m, LimitRange.Known(35m, 90m, 5m, "Get_Range 返回值"));

        Assert.True(plan.HasFallback);
        var fallback = plan.Fallback!;
        Assert.Equal(35m, fallback.Applied.Watts);
        Assert.Contains("35 W", fallback.Description);
        Assert.Contains("20 W", fallback.Description);
        Assert.Contains("不保证精确达到", fallback.Limitation);
        Assert.Contains("超频", fallback.Limitation);
    }

    [Fact]
    public void Plan_FallbackNeverClaimsToReachOriginalTarget()
    {
        var plan = GpuEcoFallbackPlanner.Plan(20m, LimitRange.Known(35m, 90m, null, "测试"));

        Assert.NotEqual(20m, plan.Fallback!.Applied.Watts);
        Assert.Contains("原目标值不予下发", plan.Fallback.Description);
    }

    [Fact]
    public void Plan_NonPositiveTarget_IsRejectedWithNativeGuidance()
    {
        var plan = GpuEcoFallbackPlanner.Plan(0m, LimitRange.Known(35m, 90m, null, "测试"));

        Assert.False(plan.HasFallback);
        Assert.Contains("原生上限", plan.Explanation);
    }

    [Fact]
    public void Plan_FloorOutsideLegalLimitRange_SoNoFallbackIsOffered()
    {
        // 下界 1500 W 超过 PowerLimit 的合理性上限（1000 W），无法构成合法的限制值。
        var plan = GpuEcoFallbackPlanner.Plan(900m, LimitRange.Known(1500m, 2000m, null, "测试"));

        Assert.False(plan.HasFallback);
        Assert.Contains("无法构成合法的功耗限制值", plan.Explanation);
    }

    [Fact]
    public void Plan_KeepsSourceInLimitationText()
    {
        var plan = GpuEcoFallbackPlanner.Plan(15m, LimitRange.Known(35m, 90m, null, "实测报告：下界 35 W"));

        Assert.Contains("实测报告：下界 35 W", plan.Fallback!.Limitation);
    }
}
