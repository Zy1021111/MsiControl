using MsiControl.Core;
using MsiControl.Persistence.Migration;
using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 设置迁移链的机制测试。
/// </summary>
/// <remarks>
/// 生产迁移表从 v1→v2 起（v2 新增档位 CPU 功耗目标），
/// 这里同时验证「表覆盖到当前版本」与「链式推进、缺步时拒绝」两种性质。
/// </remarks>
public sealed class SettingsMigrationTests
{
    [Fact]
    public void DefaultSteps_CoverEveryVersionUpToCurrent()
    {
        var version = 1;
        while (version < AppSettings.CurrentSchemaVersion)
        {
            var step = Assert.Single(SettingsMigrator.DefaultSteps, s => s.FromVersion == version);
            Assert.Equal(version + 1, step.ToVersion);
            version = step.ToVersion;
        }

        Assert.Equal(AppSettings.CurrentSchemaVersion, version);
    }

    [Fact]
    public void V1ToV2_AddsEmptyStageTargetsAndKeepsUnrelatedChoices()
    {
        var v1 = AppSettings.CreateDefault() with
        {
            SchemaVersion = 1,
            RefreshIntervalSeconds = 5,
            CloseToTray = false,
            NormalWindowPlacement = new WindowPlacement(10, 20, 900, 700)
        };
        var notes = new List<string>();

        var migrated = SettingsMigrator.Migrate(
            v1,
            AppSettings.CurrentSchemaVersion,
            SettingsMigrator.DefaultSteps,
            notes);

        Assert.NotNull(migrated);
        Assert.Equal(AppSettings.CurrentSchemaVersion, migrated!.SchemaVersion);
        Assert.Empty(migrated.StageTargets);
        Assert.Null(migrated.LastConfirmedStage);
        Assert.Equal(5, migrated.RefreshIntervalSeconds);
        Assert.False(migrated.CloseToTray);
        Assert.Equal(v1.NormalWindowPlacement, migrated.NormalWindowPlacement);
        Assert.Contains(notes, note => note.Contains("1 迁移到 2", StringComparison.Ordinal));
        Assert.Contains(notes, note => note.Contains("2 迁移到 3", StringComparison.Ordinal));
    }

    [Fact]
    public void V3ToV4_AddsStartMinimizedFlagAndKeepsUnrelatedChoices()
    {
        var v3 = AppSettings.CreateDefault() with
        {
            SchemaVersion = 3,
            CloseToTray = false,
            StartupWindow = WindowMode.Mini,
            LastConfirmedStage = PerformanceStage.Balanced
        };
        var notes = new List<string>();

        var migrated = SettingsMigrator.Migrate(
            v3,
            AppSettings.CurrentSchemaVersion,
            SettingsMigrator.DefaultSteps,
            notes);

        Assert.NotNull(migrated);
        Assert.Equal(4, migrated!.SchemaVersion);
        Assert.False(migrated.StartMinimizedOnStartup);
        Assert.False(migrated.CloseToTray);
        Assert.Equal(WindowMode.Mini, migrated.StartupWindow);
        Assert.Equal(v3.LastConfirmedStage, migrated.LastConfirmedStage);
    }

    [Fact]
    public void Migrate_WhenAlreadyAtTargetVersion_ReturnsInputUnchanged()
    {
        var source = AppSettings.CreateDefault();
        var notes = new List<string>();

        var result = SettingsMigrator.Migrate(
            source,
            AppSettings.CurrentSchemaVersion,
            SettingsMigrator.DefaultSteps,
            notes);

        Assert.NotNull(result);
        Assert.Same(source, result);
        Assert.Empty(notes);
    }

    [Fact]
    public void Migrate_AppliesRegisteredStepsInOrderAndBumpsVersion()
    {
        var source = AppSettings.CreateDefault() with { SchemaVersion = 1 };
        var notes = new List<string>();
        var steps = new ISettingsMigration[] { new BumpIntervalStep(), new EnableRpmStep() };

        var result = SettingsMigrator.Migrate(source, 3, steps, notes);

        Assert.NotNull(result);
        Assert.Equal(3, result!.SchemaVersion);
        Assert.Equal(30, result.RefreshIntervalSeconds);
        Assert.Equal(FanDisplayMode.Rpm, result.FanDisplay);
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void Migrate_WhenStepMissing_RefusesInsteadOfPartiallyMigrating()
    {
        var source = AppSettings.CreateDefault() with { SchemaVersion = 1 };
        var notes = new List<string>();

        var result = SettingsMigrator.Migrate(source, 3, new ISettingsMigration[] { new BumpIntervalStep() }, notes);

        Assert.Null(result);
        Assert.Contains(notes, note => note.Contains("缺少从版本 2", StringComparison.Ordinal));
    }

    private sealed class BumpIntervalStep : ISettingsMigration
    {
        public int FromVersion => 1;

        public int ToVersion => 2;

        public AppSettings Apply(AppSettings source) => source with { RefreshIntervalSeconds = 30 };
    }

    private sealed class EnableRpmStep : ISettingsMigration
    {
        public int FromVersion => 2;

        public int ToVersion => 3;

        public AppSettings Apply(AppSettings source) => source with { FanDisplay = FanDisplayMode.Rpm };
    }
}
