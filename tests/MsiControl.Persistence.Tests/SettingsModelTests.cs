using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 设置值对象的行为测试。
/// </summary>
public sealed class SettingsModelTests
{
    [Fact]
    public void HotkeyBinding_DisabledBindingIsAlwaysValid()
    {
        var binding = HotkeyBinding.Disabled();

        Assert.Empty(binding.Validate());
        Assert.False(binding.IsComplete);
        Assert.Equal("已关闭", binding.Describe());
    }

    [Fact]
    public void HotkeyBinding_RequiresModifierToAvoidHijackingGlobalInput()
    {
        var binding = new HotkeyBinding(true, Control: false, Alt: false, Shift: false, Windows: false, KeyName: "F9");

        var problems = binding.Validate();

        Assert.Single(problems);
        Assert.Contains("修饰键", problems[0], StringComparison.Ordinal);
        Assert.False(binding.IsComplete);
    }

    [Fact]
    public void HotkeyBinding_RequiresKeyNameWhenEnabled()
    {
        var binding = new HotkeyBinding(true, Control: true, Alt: false, Shift: false, Windows: false, KeyName: "   ");

        var problems = binding.Validate();

        Assert.Contains(problems, problem => problem.Contains("未指定主键", StringComparison.Ordinal));
    }

    [Fact]
    public void HotkeyBinding_DescribesAllModifiersInStableOrder()
    {
        var binding = new HotkeyBinding(true, Control: true, Alt: true, Shift: true, Windows: true, KeyName: "M");

        Assert.Equal("Ctrl+Alt+Shift+Win+M", binding.Describe());
    }

    [Fact]
    public void HotkeyBinding_NormalizedUppercasesKeyName()
    {
        var binding = new HotkeyBinding(true, true, false, false, false, " f9 ");

        Assert.Equal("F9", binding.Normalized().KeyName);
    }

    [Fact]
    public void WindowPlacement_RejectsTooSmallSizes()
    {
        var placement = new WindowPlacement(0, 0, 100, 100);

        Assert.False(placement.IsUsable);
    }

    [Fact]
    public void WindowPlacement_ClampKeepsWindowReachableOnSmallerScreen()
    {
        // 曾经在 4K 屏上记录的窗口，切到 1280×720 后必须仍可被抓取。
        var placement = new WindowPlacement(3000, 1800, 1600, 900);

        var clamped = placement.ClampTo(0, 0, 1280, 720);

        Assert.True(clamped.X < 1280);
        Assert.True(clamped.Y < 720);
        Assert.True(clamped.Width <= 1280);
        Assert.True(clamped.Height <= 720);
        Assert.False(clamped.Maximized);
    }

    [Fact]
    public void WindowPlacement_ClampDoesNotChangeMaximizedIntent()
    {
        var placement = new WindowPlacement(100, 100, 800, 600, Maximized: true);

        Assert.True(placement.ClampTo(0, 0, 1920, 1080).Maximized);
    }

    [Fact]
    public void AppSettings_DefaultFollowsDocumentedDecisions()
    {
        var settings = AppSettings.CreateDefault();

        Assert.True(settings.LaunchOnStartup);
        Assert.True(settings.CloseToTray);
        Assert.Equal(WindowMode.Normal, settings.StartupWindow);
        Assert.Equal(1, settings.RefreshIntervalSeconds);
        Assert.True(settings.ShowHideWindowHotkey.Enabled);

        // 强冷能力当前为 Unknown，默认不应给它绑定全局快捷键。
        Assert.False(settings.FullSpeedHotkey.Enabled);
    }

    [Fact]
    public void AppSettings_NormalizeClampsRefreshIntervalToBothBounds()
    {
        var tooSmall = (AppSettings.CreateDefault() with { RefreshIntervalSeconds = 0 }).Normalize();
        var tooLarge = (AppSettings.CreateDefault() with { RefreshIntervalSeconds = 600 }).Normalize();

        Assert.Equal(AppSettings.DefaultRefreshIntervalSeconds, tooSmall.Settings.RefreshIntervalSeconds);
        Assert.Equal(AppSettings.DefaultRefreshIntervalSeconds, tooLarge.Settings.RefreshIntervalSeconds);
        Assert.NotEmpty(tooSmall.Notes);
        Assert.NotEmpty(tooLarge.Notes);
    }

    [Fact]
    public void AppSettings_NormalizeDropsUnusableWindowPlacements()
    {
        var settings = (AppSettings.CreateDefault() with
        {
            NormalWindowPlacement = new WindowPlacement(0, 0, 10, 10)
        }).Normalize();

        Assert.Null(settings.Settings.NormalWindowPlacement);
        Assert.Contains(settings.Notes, note => note.Contains("尺寸过小", StringComparison.Ordinal));
    }

    [Fact]
    public void AppSettings_NormalizeIsIdempotent()
    {
        var once = (AppSettings.CreateDefault() with { RefreshIntervalSeconds = 999 }).Normalize().Settings;
        var twice = once.Normalize();

        Assert.Empty(twice.Notes);
        Assert.Equal(once, twice.Settings);
    }

    [Fact]
    public void AppPaths_DefaultRootIsUnderLocalApplicationData()
    {
        var paths = new AppPaths();

        Assert.EndsWith(AppPaths.ApplicationFolderName, paths.RootDirectory, StringComparison.Ordinal);
        Assert.EndsWith("settings.json", paths.SettingsFilePath, StringComparison.Ordinal);
        Assert.EndsWith("msi-control.log", paths.CurrentLogFilePath, StringComparison.Ordinal);
    }

    [Fact]
    public void AppPaths_EnsureCreatedCreatesAllDirectories()
    {
        using var temp = new TempDirectory();
        var paths = new AppPaths(Path.Combine(temp.Path, "data"));

        paths.EnsureCreated();

        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.True(Directory.Exists(paths.ProfilesDirectory));
        Assert.True(Directory.Exists(paths.LogDirectory));
    }

    [Fact]
    public void AppPaths_TimestampedPathIncludesPrefixAndTimestamp()
    {
        var path = AppPaths.TimestampedPath(@"C:\data", "settings.json", "corrupt", DateTimeOffset.Parse("2026-10-16T19:00:00+08:00"));

        Assert.Contains("corrupt-20261016-190000-settings.json", path, StringComparison.Ordinal);
    }

    // ── 上次用户确认的档位（v3）────────────────────────────────────

    [Fact]
    public void AppSettings_LastConfirmedStage_DefaultsToNull()
    {
        Assert.Null(AppSettings.CreateDefault().LastConfirmedStage);
    }

    [Fact]
    public void AppSettings_Normalize_KeepsValidConfirmedStage()
    {
        var normalized = (AppSettings.CreateDefault() with
        {
            LastConfirmedStage = MsiControl.Core.PerformanceStage.HighPerformance
        }).Normalize();

        Assert.Equal(MsiControl.Core.PerformanceStage.HighPerformance, normalized.Settings.LastConfirmedStage);
        Assert.Empty(normalized.Notes);
    }

    [Fact]
    public void AppSettings_Normalize_DropsUndefinedConfirmedStage()
    {
        // 手工编辑成非法枚举值时不得猜一个档位写进硬件：丢弃并在说明里写明。
        var normalized = (AppSettings.CreateDefault() with
        {
            LastConfirmedStage = (MsiControl.Core.PerformanceStage)99
        }).Normalize();

        Assert.Null(normalized.Settings.LastConfirmedStage);
        Assert.Contains(normalized.Notes, note => note.Contains("上次确认档位", StringComparison.Ordinal));
    }
}
