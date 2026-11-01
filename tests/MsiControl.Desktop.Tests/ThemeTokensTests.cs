using MsiControl.Desktop.Theme;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 主题令牌与主题应用测试（2026-10-16 界面美化 P0）。
/// </summary>
/// <remarks>
/// 覆盖四项新行为：
/// 次要文本标记换肤（含明暗来回切换的回归）、按钮悬停/按下与禁用色、调色板对比度守卫。
/// 只构造控件不显示窗口，不依赖消息循环。
/// </remarks>
public sealed class ThemeTokensTests
{
    [Fact]
    public void MutedLabelMarkerIsConvertedToCurrentTheme()
    {
        using var form = new Form();
        var muted = new Label { Tag = MutedText.Marker };
        form.Controls.Add(muted);

        ThemeApplier.Apply(form, ThemePalette.Dark);
        Assert.Equal(ThemePalette.Dark.ForeMuted, muted.ForeColor);

        ThemeApplier.Apply(form, ThemePalette.Light);
        Assert.Equal(ThemePalette.Light.ForeMuted, muted.ForeColor);
    }

    [Fact]
    public void MutedLabelSurvivesRepeatedThemeSwitches()
    {
        // 回归：曾用「系统 GrayText 前景色」作标记，首次换肤后标记消失，
        // 明暗来回切换时内联创建的次要文本会停留在旧主题色下。
        using var form = new Form();
        var muted = new Label { Tag = MutedText.Marker };
        form.Controls.Add(muted);

        for (var round = 0; round < 3; round++)
        {
            ThemeApplier.Apply(form, ThemePalette.Dark);
            Assert.Equal(ThemePalette.Dark.ForeMuted, muted.ForeColor);

            ThemeApplier.Apply(form, ThemePalette.Light);
            Assert.Equal(ThemePalette.Light.ForeMuted, muted.ForeColor);
        }
    }

    [Fact]
    public void PlainLabelKeepsItsOwnForeColor()
    {
        using var form = new Form();
        var label = new Label { ForeColor = Color.Red };
        var grayLabel = new Label { ForeColor = SystemColors.GrayText };
        form.Controls.Add(label);
        form.Controls.Add(grayLabel);

        ThemeApplier.Apply(form, ThemePalette.Dark);

        // 不带标记的标签颜色由控件自理，主题应用不得覆盖（包括恰好使用 GrayText 的）。
        Assert.Equal(Color.Red, label.ForeColor);
        Assert.Equal(SystemColors.GrayText, grayLabel.ForeColor);
    }

    [Fact]
    public void ButtonHoverAndPressedUseHoverToken()
    {
        using var form = new Form();
        var button = new Button
        {
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        form.Controls.Add(button);

        ThemeApplier.Apply(form, ThemePalette.Dark);

        Assert.Equal(ThemePalette.Dark.Hover, button.FlatAppearance.MouseOverBackColor);
        Assert.Equal(ThemePalette.Dark.Hover, button.FlatAppearance.MouseDownBackColor);
        Assert.Equal(ThemePalette.Dark.SurfaceAlt, button.BackColor);
    }

    [Fact]
    public void DisabledButtonUsesForeFaint()
    {
        using var form = new Form();
        var button = new Button
        {
            Enabled = false,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        form.Controls.Add(button);

        ThemeApplier.Apply(form, ThemePalette.Dark);

        Assert.Equal(ThemePalette.Dark.ForeFaint, button.ForeColor);
    }

    [Fact]
    public void DarkPaletteMeetsContrastRequirements()
        => AssertPaletteContrast(ThemePalette.Dark);

    [Fact]
    public void LightPaletteMeetsContrastRequirements()
        => AssertPaletteContrast(ThemePalette.Light);

    [Fact]
    public void HoverTokenIsDistinguishableFromSurfaces()
    {
        foreach (var palette in new[] { ThemePalette.Light, ThemePalette.Dark })
        {
            Assert.NotEqual(palette.Hover, palette.Surface);
            Assert.NotEqual(palette.Hover, palette.SurfaceAlt);
        }
    }

    private static void AssertPaletteContrast(ThemePalette palette)
    {
        // 主文本：画布与卡片均需 ≥7:1（WCAG AAA 正文）。
        Assert.True(
            ContrastRatio(palette.Fore, palette.Back) >= 7.0,
            $"Fore/Back = {ContrastRatio(palette.Fore, palette.Back):F2}");
        Assert.True(
            ContrastRatio(palette.Fore, palette.Surface) >= 7.0,
            $"Fore/Surface = {ContrastRatio(palette.Fore, palette.Surface):F2}");

        // 次文本：卡片与画布均需 ≥4.5:1（WCAG AA 正文）。
        Assert.True(
            ContrastRatio(palette.ForeMuted, palette.Surface) >= 4.5,
            $"ForeMuted/Surface = {ContrastRatio(palette.ForeMuted, palette.Surface):F2}");
        Assert.True(
            ContrastRatio(palette.ForeMuted, palette.Back) >= 4.5,
            $"ForeMuted/Back = {ContrastRatio(palette.ForeMuted, palette.Back):F2}");

        // 强调色用于焦点、活动态与主按钮文字：≥4.5:1。
        Assert.True(
            ContrastRatio(palette.Accent, palette.Surface) >= 4.5,
            $"Accent/Surface = {ContrastRatio(palette.Accent, palette.Surface):F2}");

        // 状态色按大字/图形标准：≥3:1。
        Assert.True(
            ContrastRatio(palette.Ok, palette.Surface) >= 3.0,
            $"Ok/Surface = {ContrastRatio(palette.Ok, palette.Surface):F2}");
        Assert.True(
            ContrastRatio(palette.Warn, palette.Surface) >= 3.0,
            $"Warn/Surface = {ContrastRatio(palette.Warn, palette.Surface):F2}");
        Assert.True(
            ContrastRatio(palette.Danger, palette.Surface) >= 3.0,
            $"Danger/Surface = {ContrastRatio(palette.Danger, palette.Surface):F2}");
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var srgb = value / 255.0;
            return srgb <= 0.03928
                ? srgb / 12.92
                : Math.Pow((srgb + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R))
            + (0.7152 * Channel(color.G))
            + (0.0722 * Channel(color.B));
    }
}
