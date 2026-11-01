using MsiControl.Core;
using MsiControl.Desktop.Hotkeys;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Theme;
using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 模式文本、主题配色与快捷键解析测试。
/// </summary>
public sealed class ShellPresentationTests
{
    [Fact]
    public void StageTextCoversAllFiveStages()
    {
        Assert.Equal("节能", ModeFormatter.StageText(PerformanceStage.Eco));
        Assert.Equal("安静", ModeFormatter.StageText(PerformanceStage.Quiet));
        Assert.Equal("平衡", ModeFormatter.StageText(PerformanceStage.Balanced));
        Assert.Equal("高性能", ModeFormatter.StageText(PerformanceStage.HighPerformance));
        Assert.Equal("狂暴", ModeFormatter.StageText(PerformanceStage.Extreme));
        Assert.Equal("未适配", ModeFormatter.StageText((PerformanceStage?)null));
    }

    [Fact]
    public void GraphicsTextCoversAllModes()
    {
        Assert.Equal("核显", ModeFormatter.GraphicsText(GraphicsMode.Integrated));
        Assert.Equal("混合", ModeFormatter.GraphicsText(GraphicsMode.Hybrid));
        Assert.Equal("独显直连", ModeFormatter.GraphicsText(GraphicsMode.Discrete));
        Assert.Equal("未适配", ModeFormatter.GraphicsText((GraphicsMode?)null));
    }

    [Fact]
    public void PendingGraphicsModeNeverOverwritesActualState()
    {
        var text = ModeFormatter.GraphicsTextWithPending(
            GraphicsMode.Hybrid,
            GraphicsMode.Discrete,
            requiresRestart: true);

        // 实际状态必须仍然出现在最前面，待生效只是附加信息（任务书 §6.1、§9.1）。
        Assert.StartsWith("混合", text, StringComparison.Ordinal);
        Assert.Contains("待生效：独显直连", text, StringComparison.Ordinal);
        Assert.Contains("重启后生效", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PendingGraphicsModeEqualToCurrentShowsNoPendingSuffix()
    {
        var text = ModeFormatter.GraphicsTextWithPending(GraphicsMode.Hybrid, GraphicsMode.Hybrid, requiresRestart: false);

        Assert.Equal("混合", text);
    }

    [Fact]
    public void WindowAndFanModeTextsAreLocalized()
    {
        Assert.Equal("正常窗口", ModeFormatter.WindowModeText(WindowMode.Normal));
        Assert.Equal("迷你窗口", ModeFormatter.WindowModeText(WindowMode.Mini));
        Assert.Equal("固件自动", ModeFormatter.FanModeText(FanControlMode.Automatic));
        Assert.Equal("全速（强冷）", ModeFormatter.FanModeText(FanControlMode.FullSpeed));
    }

    [Fact]
    public void FanModeReadbackTextNeverClaimsCustomCurveIsActive()
    {
        // EC advanced（0x8D）下固件仍在自动调速（HWE §17.3）；
        // 读回展示不得表述为「自定义曲线已生效」，否则违反任务书 §1 原则 6。
        Assert.Equal(
            "高级（advanced；固件仍在自动调速）",
            ModeFormatter.FanModeReadbackText(FanControlMode.CustomCurve));
        Assert.Equal("固件自动", ModeFormatter.FanModeReadbackText(FanControlMode.Automatic));
        Assert.Equal("静音模式", ModeFormatter.FanModeReadbackText(FanControlMode.Silent));
        Assert.Equal("全速（强冷）", ModeFormatter.FanModeReadbackText(FanControlMode.FullSpeed));
    }

    [Fact]
    public void FrequencyBasisTextIsAlwaysExplicit()
    {
        Assert.Equal("活跃核心均值", ModeFormatter.FrequencyBasisText(FrequencyAggregation.AverageActive));
        Assert.Equal("活跃核心最大值", ModeFormatter.FrequencyBasisText(FrequencyAggregation.MaximumActive));
    }

    [Fact]
    public void PalettesDifferBetweenLightAndDarkAndKeepContrast()
    {
        var light = ThemePalette.Light;
        var dark = ThemePalette.Dark;

        Assert.NotEqual(light.Back, dark.Back);
        Assert.NotEqual(light.Fore, dark.Fore);
        Assert.Equal(ThemeMode.Light, light.Mode);
        Assert.Equal(ThemeMode.Dark, dark.Mode);

        // 前景与背景必须可区分，否则文本不可读。
        Assert.NotEqual(light.Fore, light.Back);
        Assert.NotEqual(dark.Fore, dark.Back);
        Assert.NotEqual(light.ForeMuted, light.Back);
        Assert.NotEqual(dark.ForeMuted, dark.Back);
    }

    [Fact]
    public void PaletteForReturnsMatchingMode()
    {
        Assert.Equal(ThemeMode.Dark, ThemePalette.For(ThemeMode.Dark).Mode);
        Assert.Equal(ThemeMode.Light, ThemePalette.For(ThemeMode.Light).Mode);
    }

    [Fact]
    public void WindowsThemeReadAlwaysReturnsAValueWithOptionalNote()
    {
        var mode = WindowsTheme.Read(out var note);

        Assert.True(Enum.IsDefined(mode));
        if (note is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(note));
        }
    }

    [Theory]
    [InlineData("F9", true)]
    [InlineData("f9", true)]
    [InlineData(" M ", true)]
    [InlineData("F12", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("NotAKey", false)]
    [InlineData("None", false)]
    public void TryParseKeyHandlesConfigKeyNames(string keyName, bool expected)
    {
        var parsed = HotkeyManager.TryParseKey(keyName, out var key);

        Assert.Equal(expected, parsed);
        if (!expected)
        {
            Assert.Equal(Keys.None, key);
        }
    }

    [Fact]
    public void TryParseKeyMapsLettersToVirtualKeys()
    {
        Assert.True(HotkeyManager.TryParseKey("M", out var key));
        Assert.Equal(Keys.M, key);
    }
}
