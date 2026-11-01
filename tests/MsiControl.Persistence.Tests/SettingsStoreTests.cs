using System.Text.Json;
using MsiControl.Core;
using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 设置存储的行为测试。
/// </summary>
/// <remarks>
/// 覆盖重点：往返一致性、原子写与备份、损坏隔离、版本管控、旧格式兼容、取值规范化。
/// 这些是「配置不会悄悄丢或被改坏」的底线。
/// </remarks>
public sealed class SettingsStoreTests
{
    private static JsonSettingsStore CreateStore(TempDirectory temp)
        => new(temp.File("settings.json"), temp.File("settings.bak.json"));

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.False(result.IsDegraded);
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
        Assert.True(result.Settings.LaunchOnStartup);
        Assert.True(result.Settings.CloseToTray);
        Assert.Equal(1, result.Settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        var original = new AppSettings
        {
            LaunchOnStartup = false,
            CloseToTray = false,
            StartupWindow = WindowMode.Mini,
            RefreshIntervalSeconds = 5,
            FanDisplay = FanDisplayMode.Rpm,
            ShowHideWindowHotkey = new HotkeyBinding(true, true, true, false, false, "K"),
            FullSpeedHotkey = new HotkeyBinding(true, true, false, true, false, "F9"),
            NormalWindowPlacement = new WindowPlacement(10, 20, 900, 700),
            MiniWindowPlacement = new WindowPlacement(30, 40, 500, 400, Maximized: true)
        };

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(SettingsLoadSource.File, loaded.Source);
        Assert.False(loaded.IsDegraded);
        Assert.Equal(original.LaunchOnStartup, loaded.Settings.LaunchOnStartup);
        Assert.Equal(original.CloseToTray, loaded.Settings.CloseToTray);
        Assert.Equal(original.StartupWindow, loaded.Settings.StartupWindow);
        Assert.Equal(original.RefreshIntervalSeconds, loaded.Settings.RefreshIntervalSeconds);
        Assert.Equal(original.FanDisplay, loaded.Settings.FanDisplay);
        Assert.Equal(original.ShowHideWindowHotkey, loaded.Settings.ShowHideWindowHotkey);
        Assert.Equal(original.FullSpeedHotkey, loaded.Settings.FullSpeedHotkey);
        Assert.Equal(original.NormalWindowPlacement, loaded.Settings.NormalWindowPlacement);
        Assert.Equal(original.MiniWindowPlacement, loaded.Settings.MiniWindowPlacement);
    }

    [Fact]
    public void Save_WritesVersionedEnvelope()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.Save(AppSettings.CreateDefault());

        using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        Assert.True(document.RootElement.TryGetProperty("schemaVersion", out var version));
        Assert.Equal(AppSettings.CurrentSchemaVersion, version.GetInt32());
        Assert.True(document.RootElement.TryGetProperty("savedAt", out _));
        Assert.True(document.RootElement.TryGetProperty("settings", out _));
    }

    [Fact]
    public void Save_KeepsPreviousVersionAsBackup()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.Save(AppSettings.CreateDefault() with { RefreshIntervalSeconds = 3 });
        store.Save(AppSettings.CreateDefault() with { RefreshIntervalSeconds = 7 });

        var backupPath = temp.File("settings.bak.json");
        Assert.True(File.Exists(backupPath), "覆盖保存前应留下上一个版本的备份。");

        using var document = JsonDocument.Parse(File.ReadAllText(backupPath));
        var backupSettings = document.RootElement.GetProperty("settings");
        Assert.Equal(3, backupSettings.GetProperty("refreshIntervalSeconds").GetInt32());
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.Save(AppSettings.CreateDefault());

        // 原子写使用 settings.json.tmp，成功替换后不得残留。
        Assert.False(File.Exists(store.FilePath + ".tmp"));
    }

    [Fact]
    public void Save_DoesNotPersistDerivedProperties()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        store.Save(AppSettings.CreateDefault() with
        {
            NormalWindowPlacement = new WindowPlacement(10, 20, 900, 700),
            StageTargets = new[] { new StagePowerTarget(PerformanceStage.Balanced, 45m) }
        });

        var text = File.ReadAllText(store.FilePath);

        // 派生属性写进文件会误导用户「改它就能生效」，因此必须排除。
        Assert.DoesNotContain("isComplete", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hasModifier", text, StringComparison.Ordinal);
        Assert.DoesNotContain("isUsable", text, StringComparison.Ordinal);
        Assert.DoesNotContain("isNative", text, StringComparison.Ordinal);

        // 但正常字段必须完整保留。
        Assert.Contains("\"keyName\"", text, StringComparison.Ordinal);
        Assert.Contains("\"normalWindowPlacement\"", text, StringComparison.Ordinal);
        Assert.Contains("\"cpuWatts\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WhenJsonCorrupt_QuarantinesFileAndFallsBackToDefaults()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        File.WriteAllText(store.FilePath, "{ this is not json");

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.RecoveredFromCorrupt, result.Source);
        Assert.True(result.IsDegraded);
        Assert.NotNull(result.QuarantinedFilePath);
        Assert.True(File.Exists(result.QuarantinedFilePath!));
        Assert.False(File.Exists(store.FilePath), "损坏文件应被移走，避免下次再次触发降级。");
        Assert.Equal(AppSettings.CurrentSchemaVersion, result.Settings.SchemaVersion);
    }

    [Fact]
    public void Load_WhenSchemaVersionIsNewer_QuarantinesAndFallsBack()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        var future = AppSettings.CurrentSchemaVersion + 1;
        File.WriteAllText(
            store.FilePath,
            $$"""
              { "schemaVersion": {{future}}, "savedAt": "2026-10-16T00:00:00+08:00", "settings": { "refreshIntervalSeconds": 9 } }
              """);

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.RecoveredFromUnsupportedVersion, result.Source);
        Assert.True(result.IsDegraded);
        Assert.NotNull(result.QuarantinedFilePath);
        Assert.Equal(1, result.Settings.RefreshIntervalSeconds);
    }

    [Fact]
    public void Load_WhenEnvelopeHasNoSettingsNode_QuarantinesAndFallsBack()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        File.WriteAllText(store.FilePath, """{ "schemaVersion": 1 }""");

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.RecoveredFromCorrupt, result.Source);
        Assert.NotNull(result.QuarantinedFilePath);
    }

    [Fact]
    public void Load_WhenFileIsEmpty_ReturnsDefaultsWithoutQuarantine()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        File.WriteAllText(store.FilePath, "   ");

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.Defaults, result.Source);
        Assert.Null(result.QuarantinedFilePath);
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Load_WhenLegacyBareObject_AcceptsAndRecordsNote()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);

        // 无版本信封的旧格式：整份文件就是设置对象本身。
        File.WriteAllText(store.FilePath, """{ "refreshIntervalSeconds": 4, "closeToTray": false }""");

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.File, result.Source);
        Assert.Equal(4, result.Settings.RefreshIntervalSeconds);
        Assert.False(result.Settings.CloseToTray);
        Assert.Contains(result.Notes, note => note.Contains("版本信封", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_NormalizesOutOfRangeValuesAndReportsThem()
    {
        using var temp = new TempDirectory();
        var store = CreateStore(temp);
        File.WriteAllText(
            store.FilePath,
            """
            {
              "schemaVersion": 1,
              "settings": {
                "refreshIntervalSeconds": 9999,
                "startupWindow": 42,
                "fanDisplay": 7,
                "showHideWindowHotkey": { "enabled": true, "control": false, "alt": false, "shift": false, "windows": false, "keyName": "M" }
              }
            }
            """);

        var result = store.Load();

        Assert.Equal(SettingsLoadSource.File, result.Source);
        Assert.Equal(AppSettings.DefaultRefreshIntervalSeconds, result.Settings.RefreshIntervalSeconds);
        Assert.Equal(WindowMode.Normal, result.Settings.StartupWindow);
        Assert.Equal(FanDisplayMode.Percent, result.Settings.FanDisplay);
        Assert.False(result.Settings.ShowHideWindowHotkey.Enabled);
        Assert.True(result.Notes.Count >= 4, "每一项修正都应留下说明。");
    }
}
