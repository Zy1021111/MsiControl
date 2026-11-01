using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Windows.Controls;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// P1 结构组件测试（2026-10-16 界面美化 P1）。
/// </summary>
/// <remarks>
/// 覆盖分段按钮组的活动态 / 禁用态 / 主题重刷，以及卡片的基础结构与主题应用。
/// 只构造控件不显示窗口，不依赖消息循环。
/// </remarks>
public sealed class StructureControlsTests
{
    [Fact]
    public void SegmentedGroup_ActiveUsesAccentStyle()
    {
        var group = new SegmentedGroup();
        var a = new Button { Enabled = true };
        var b = new Button { Enabled = true };
        group.Add(a);
        group.Add(b);
        group.ApplyPalette(ThemePalette.Dark);

        group.SetActive(a);

        Assert.Equal(ThemePalette.Dark.Surface, a.BackColor);
        Assert.Equal(ThemePalette.Dark.Accent, a.ForeColor);
        Assert.Equal(ThemePalette.Dark.Accent, a.FlatAppearance.BorderColor);

        Assert.Equal(ThemePalette.Dark.SurfaceAlt, b.BackColor);
        Assert.Equal(ThemePalette.Dark.Fore, b.ForeColor);
        Assert.Equal(ThemePalette.Dark.Border, b.FlatAppearance.BorderColor);
    }

    [Fact]
    public void SegmentedGroup_DisabledNeverLooksActive()
    {
        var group = new SegmentedGroup();
        var disabled = new Button { Enabled = false };
        group.Add(disabled);
        group.ApplyPalette(ThemePalette.Dark);

        group.SetActive(disabled);

        // 禁用段即使被设为活动，也必须呈禁用样式，不得出现「已选中」的强调态。
        Assert.Equal(ThemePalette.Dark.ForeFaint, disabled.ForeColor);
        Assert.Equal(ThemePalette.Dark.Border, disabled.FlatAppearance.BorderColor);
        Assert.NotEqual(ThemePalette.Dark.Accent, disabled.ForeColor);
    }

    [Fact]
    public void SegmentedGroup_RejectsForeignButton()
    {
        var group = new SegmentedGroup();
        group.Add(new Button());

        Assert.Throws<ArgumentException>(() => group.SetActive(new Button()));
    }

    [Fact]
    public void SegmentedGroup_RepaintKeepsActiveOnThemeSwitch()
    {
        var group = new SegmentedGroup();
        var a = new Button { Enabled = true };
        group.Add(a);
        group.ApplyPalette(ThemePalette.Light);
        group.SetActive(a);

        group.ApplyPalette(ThemePalette.Dark);

        Assert.Equal(ThemePalette.Dark.Accent, a.ForeColor);
        Assert.Equal(ThemePalette.Dark.Surface, a.BackColor);
    }

    [Fact]
    public void Card_AppliesSurfaceAndAccentDot()
    {
        var content = new Panel { Dock = DockStyle.Top };
        using var card = new Card("CPU", content);

        card.ApplyPalette(ThemePalette.Dark);

        Assert.Equal(ThemePalette.Dark.Surface, card.BackColor);
        Assert.Equal("CPU", card.Title);
        Assert.Same(content, card.Content);
    }

    [Fact]
    public void FocusButton_AcceptsThemeAndRejectsNull()
    {
        using var button = new FocusButton();

        button.ApplyPalette(ThemePalette.Dark);
        Assert.Throws<ArgumentNullException>(() => button.ApplyPalette(null!));

        // 焦点描边依赖 OnPaint，无法在无消息循环的单测中断言像素；
        // 这里验证主题应用路径不抛异常、类型契约成立。
        Assert.IsAssignableFrom<Button>(button);
    }
}
