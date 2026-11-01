using MsiControl.Core;
using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>逐项应用计划的生成与下发前自检。</summary>
public sealed class PerformancePlannerTests
{
    [Fact]
    public void BuildPlan_CoversAllItemsInSuggestedOrder()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings;

        var plan = PerformancePlanner.BuildPlan(settings);

        Assert.Equal(
            new[]
            {
                PerformanceSettingKeys.Stage,
                PerformanceSettingKeys.CpuLongTerm,
                PerformanceSettingKeys.CpuShortTerm,
                PerformanceSettingKeys.CpuTurbo,
                PerformanceSettingKeys.GpuLimit
            },
            plan.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void BuildPlan_EveryItemCarriesItsCapabilityKey()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Eco).Settings;

        var plan = PerformancePlanner.BuildPlan(settings);

        Assert.All(plan, item => Assert.Equal(
            PerformanceSettingKeys.CapabilityKeyFor(item.Key),
            item.CapabilityKey));
    }

    [Fact]
    public void BuildPlan_NativeLimitsBecomeRestoreNative_NotZeroOrInfinite()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.HighPerformance).Settings;

        var plan = PerformancePlanner.BuildPlan(settings);

        var nativeItems = plan.Where(p => p.IsNativeRestore).ToArray();
        Assert.Equal(4, nativeItems.Length);
        Assert.All(nativeItems, item =>
        {
            Assert.Equal("原生上限", item.Requested);
            Assert.DoesNotContain("9999", item.Requested);
            Assert.DoesNotContain("-1", item.Requested);
        });
    }

    [Fact]
    public void BuildPlan_ExtremeStage_DoesNotImplyFullSpeed()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Extreme).Settings;

        var plan = PerformancePlanner.BuildPlan(settings);

        Assert.DoesNotContain(plan, item => item.Key == PerformanceSettingKeys.GpuEco);
        Assert.All(plan, item => Assert.DoesNotContain("强冷", item.Note ?? string.Empty));
    }

    [Fact]
    public void BuildPlan_AddsExplicitEcoItem_WhenFallbackIsPresent()
    {
        var fallback = GpuEcoFallback.Create(
            "GPU 目标低于下界，改用下界。",
            PowerLimit.Custom(35m),
            "该策略不保证精确达到目标瓦数。");

        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Eco).Settings
            with { GpuEcoFallback = fallback };

        var plan = PerformancePlanner.BuildPlan(settings);

        var eco = Assert.Single(plan, p => p.Key == PerformanceSettingKeys.GpuEco);
        Assert.Equal("35 W", eco.Requested);
        Assert.Contains("不保证精确达到目标瓦数", eco.Note);

        var gpu = Assert.Single(plan, p => p.Key == PerformanceSettingKeys.GpuLimit);
        Assert.Contains("替代策略", gpu.Note);
    }

    [Fact]
    public void ValidateBeforeApply_AcceptsAllCatalogDefaults()
    {
        foreach (var profile in PerformanceProfileCatalog.Defaults)
        {
            Assert.Empty(PerformancePlanner.ValidateBeforeApply(profile.Settings));
        }
    }

    [Fact]
    public void ValidateBeforeApply_RejectsShortTermBelowLongTerm()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings
            with
            {
                CpuLongTerm = PowerLimit.Custom(60m),
                CpuShortTerm = PowerLimit.Custom(45m)
            };

        var problems = PerformancePlanner.ValidateBeforeApply(settings);

        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains("短时上限"));
    }

    [Fact]
    public void ValidateBeforeApply_RejectsFallbackAlongsideNativeGpuLimit()
    {
        var fallback = GpuEcoFallback.Create("替代", PowerLimit.Custom(35m), "不保证精确达成。");
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.HighPerformance).Settings
            with { GpuEcoFallback = fallback };

        var problems = PerformancePlanner.ValidateBeforeApply(settings);

        Assert.Contains(problems, p => p.Contains("自相矛盾"));
    }

    [Fact]
    public void ValidateBeforeApply_RejectsCurveBoundToWrongChannel()
    {
        var cpuCurveOnGpu = FanCurve.Create(
            FanChannel.Gpu,
            new[] { new FanPoint(40, 20), new FanPoint(80, 80) });

        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings
            with { CpuCurve = cpuCurveOnGpu };

        var problems = PerformancePlanner.ValidateBeforeApply(settings);

        Assert.Contains(problems, p => p.Contains("CPU 风扇曲线绑定在"));
    }

    [Fact]
    public void DisplayNameOf_UsesCatalogInsteadOfSecondTable()
    {
        Assert.Equal("节能", PerformancePlanner.DisplayNameOf(PerformanceStage.Eco));
        Assert.Equal("平衡", PerformancePlanner.DisplayNameOf(PerformanceStage.Balanced));
        Assert.Equal("狂暴", PerformancePlanner.DisplayNameOf(PerformanceStage.Extreme));
    }

    [Fact]
    public void BuildPlan_NotesDoNotHardcodeDeviceConclusion()
    {
        // 领域模块不得把「本机能不能写」写进文案：能力状态由能力基线决定，
        // 一旦升级为 Verified，硬编码的结论就会变成假消息。
        foreach (var profile in PerformanceProfileCatalog.Defaults)
        {
            var plan = PerformancePlanner.BuildPlan(profile.Settings);

            Assert.All(plan, item => Assert.DoesNotContain("本机", item.Note ?? string.Empty));
        }
    }

    [Fact]
    public void BuildPlan_NotesExplainsTimeWindowForCpuLimits()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings;

        var plan = PerformancePlanner.BuildPlan(settings);

        var longTerm = Assert.Single(plan, p => p.Key == PerformanceSettingKeys.CpuLongTerm);
        Assert.Contains("时间窗口", longTerm.Note);
    }
}
