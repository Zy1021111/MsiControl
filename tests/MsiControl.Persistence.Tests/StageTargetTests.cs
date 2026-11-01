using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 档位 CPU 功耗目标的持久化、规范化与领域映射测试。
/// </summary>
/// <remarks>
/// 背景：本轮实测发现「改 45 W → 重启 → 点档位」会把硬件写回默认 60 W，
/// 根因是档位目标只存在协调器内存。本组用例锁住修复后的三条不变量：
/// 存得下、加载时只认合法值、叠加时只覆盖 CPU 两项。
/// </remarks>
public sealed class StageTargetTests
{
    private static readonly IReadOnlyList<PerformanceProfile> Defaults = PerformanceProfileCatalog.Defaults;

    [Fact]
    public void NewSettings_HaveNoStageTargets()
    {
        var settings = AppSettings.CreateDefault();

        Assert.Empty(settings.StageTargets);
        // 版本号随新增字段递增（v3 增加上次用户确认的档位），不写死具体数字。
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Null(settings.LastConfirmedStage);
    }

    [Fact]
    public void Store_RoundTripsStageTargetsIncludingNative()
    {
        using var temp = new TempDirectory();
        var store = new JsonSettingsStore(temp.File("settings.json"), temp.File("settings.bak.json"));

        var original = AppSettings.CreateDefault() with
        {
            StageTargets = new[]
            {
                new StagePowerTarget(PerformanceStage.Eco, 12.5m),
                new StagePowerTarget(PerformanceStage.Balanced, 45m),
                new StagePowerTarget(PerformanceStage.Extreme, null)
            }
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(SettingsLoadSource.File, loaded.Source);
        Assert.Equal(3, loaded.Settings.StageTargets.Count);
        Assert.Equal(12.5m, loaded.Settings.StageTargets[0].CpuWatts);
        Assert.Equal(45m, loaded.Settings.StageTargets[1].CpuWatts);
        Assert.Null(loaded.Settings.StageTargets[2].CpuWatts);
        Assert.True(loaded.Settings.StageTargets[2].IsNative);
    }

    [Fact]
    public void Normalize_DropsUnknownStageAndInvalidWatts()
    {
        var settings = AppSettings.CreateDefault() with
        {
            StageTargets = new[]
            {
                new StagePowerTarget((PerformanceStage)99, 45m),
                new StagePowerTarget(PerformanceStage.Quiet, 0m),
                new StagePowerTarget(PerformanceStage.Eco, PowerLimit.MaxCustomWatts + 1m),
                new StagePowerTarget(PerformanceStage.Balanced, 45m)
            }
        };

        var result = settings.Normalize();

        var kept = Assert.Single(result.Settings.StageTargets);
        Assert.Equal(PerformanceStage.Balanced, kept.Stage);
        Assert.Equal(45m, kept.CpuWatts);
        Assert.Equal(3, result.Notes.Count);
    }

    [Fact]
    public void Normalize_KeepsLastDuplicateAndOrdersByCatalog()
    {
        var settings = AppSettings.CreateDefault() with
        {
            StageTargets = new[]
            {
                new StagePowerTarget(PerformanceStage.Extreme, null),
                new StagePowerTarget(PerformanceStage.Eco, 9m),
                new StagePowerTarget(PerformanceStage.Eco, 11m)
            }
        };

        var result = settings.Normalize();

        Assert.Equal(2, result.Settings.StageTargets.Count);
        Assert.Equal(PerformanceStage.Eco, result.Settings.StageTargets[0].Stage);
        Assert.Equal(11m, result.Settings.StageTargets[0].CpuWatts);
        Assert.Equal(PerformanceStage.Extreme, result.Settings.StageTargets[1].Stage);
        Assert.Contains(result.Notes, note => note.Contains("重复", StringComparison.Ordinal));
    }

    [Fact]
    public void Normalize_IsIdempotentForCanonicalTargets()
    {
        var settings = AppSettings.CreateDefault() with
        {
            StageTargets = new[]
            {
                new StagePowerTarget(PerformanceStage.Eco, 12m),
                new StagePowerTarget(PerformanceStage.Balanced, 45m)
            }
        };

        var once = settings.Normalize();
        var twice = once.Settings.Normalize();

        Assert.Equal(once.Settings.StageTargets, twice.Settings.StageTargets);
        Assert.Empty(once.Notes);
        Assert.Empty(twice.Notes);
    }

    [Fact]
    public void Resolve_OverridesOnlyCpuLimitsAndKeepsOtherDefaults()
    {
        var settings = AppSettings.CreateDefault() with
        {
            StageTargets = new[]
            {
                new StagePowerTarget(PerformanceStage.Balanced, 45m),
                new StagePowerTarget(PerformanceStage.Extreme, null)
            }
        };

        var resolved = StageTargetResolver.Resolve(settings, Defaults);

        var balanced = resolved.Single(s => s.Stage == PerformanceStage.Balanced);
        Assert.Equal(45m, balanced.CpuLongTerm.Watts);
        Assert.Equal(45m, balanced.CpuShortTerm.Watts);
        // GPU 与睿频目标不在本表编辑范围，必须保持默认表原值（平衡档默认 GPU 90 W）。
        Assert.Equal(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings.GpuLimit,
            balanced.GpuLimit);
        Assert.True(balanced.CpuTurbo.IsNative);
        Assert.Empty(balanced.Validate());

        // 未保存目标的档位保持默认表原值（节能 10 W）。
        var eco = resolved.Single(s => s.Stage == PerformanceStage.Eco);
        Assert.Equal(10m, eco.CpuLongTerm.Watts);

        // 显式 null = 原生上限，必须落成 PowerLimit.Native，不得写成 0。
        var extreme = resolved.Single(s => s.Stage == PerformanceStage.Extreme);
        Assert.True(extreme.CpuLongTerm.IsNative);
        Assert.True(extreme.CpuShortTerm.IsNative);
    }

    [Fact]
    public void Capture_ThenResolve_RestoresSameNumbers()
    {
        var edited = new[]
        {
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Eco).Settings
                with { CpuLongTerm = PowerLimit.Custom(12m), CpuShortTerm = PowerLimit.Custom(12m) },
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Quiet).Settings,
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings
                with { CpuLongTerm = PowerLimit.Custom(45m), CpuShortTerm = PowerLimit.Custom(45m) },
            PerformanceProfileCatalog.GetDefault(PerformanceStage.HighPerformance).Settings,
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Extreme).Settings
        };

        var targets = StageTargetResolver.Capture(edited);
        var resolved = StageTargetResolver.Resolve(
            AppSettings.CreateDefault() with { StageTargets = targets },
            Defaults);

        for (var i = 0; i < edited.Length; i++)
        {
            Assert.Equal(edited[i].Stage, resolved[i].Stage);
            Assert.Equal(edited[i].CpuLongTerm.Watts, resolved[i].CpuLongTerm.Watts);
            Assert.Equal(edited[i].CpuShortTerm.Watts, resolved[i].CpuShortTerm.Watts);
        }
    }

    [Fact]
    public void Store_RoundTripsLastConfirmedStage()
    {
        using var temp = new TempDirectory();
        var store = new JsonSettingsStore(temp.File("settings.json"), temp.File("settings.bak.json"));
        var original = AppSettings.CreateDefault() with
        {
            LastConfirmedStage = PerformanceStage.HighPerformance
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(PerformanceStage.HighPerformance, loaded.Settings.LastConfirmedStage);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.Settings.SchemaVersion);
    }
}
