using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Windows.Controls;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 主题应用测试。
/// </summary>
/// <remarks>
/// 这里存在一个真实缺陷的回归测试：窗口本身实现 <see cref="IThemeAware"/>，
/// 而窗口的 <c>ApplyPalette</c> 会回调 <see cref="ThemeApplier.Apply"/> 以刷新整棵树。
/// 若缺少重入保护，两者互相调用会栈溢出。实机启动冒烟测试曾复现该问题。
/// </remarks>
public sealed class ThemeApplierTests
{
    [Fact]
    public void Apply_DoesNotRecurseWhenFormItselfIsThemeAware()
    {
        using var form = new RecursiveForm();

        ThemeApplier.Apply(form, ThemePalette.Dark);

        Assert.Equal(1, form.ApplyCount);
        Assert.Equal(ThemePalette.Dark.Back, form.BackColor);
        Assert.Equal(ThemePalette.Dark.Fore, form.ForeColor);
    }

    [Fact]
    public void Apply_IsIdempotentAcrossRepeatedCalls()
    {
        using var form = new RecursiveForm();

        ThemeApplier.Apply(form, ThemePalette.Light);
        ThemeApplier.Apply(form, ThemePalette.Dark);

        // 每次对外调用都应完整刷新一次，而不是被重入保护吞掉。
        Assert.Equal(2, form.ApplyCount);
        Assert.Equal(ThemePalette.Dark.Back, form.BackColor);
    }

    [Fact]
    public void ApplyToChildControl_UsesSurfaceColours()
    {
        var tile = new MetricTile("CPU 温度");

        tile.ApplyPalette(ThemePalette.Dark);

        Assert.Equal(ThemePalette.Dark.SurfaceAlt, tile.BackColor);
        tile.SetValue("62.0 °C", "来源：MSI_ACPI");
        Assert.Equal("62.0 °C", tile.ValueText);
        Assert.Equal("来源：MSI_ACPI", tile.NoteText);
    }

    [Fact]
    public void Apply_NullArgumentsAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            ThemeApplier.Apply(null!, ThemePalette.Dark);
        });

        using var form = new RecursiveForm();
        Assert.Throws<ArgumentNullException>(() =>
        {
            ThemeApplier.Apply(form, null!);
        });
    }

    private sealed class RecursiveForm : Form, IThemeAware
    {
        internal int ApplyCount { get; private set; }

        public void ApplyPalette(ThemePalette palette)
        {
            ApplyCount++;
            // 与 MainForm / MiniForm / SettingsForm 的真实写法一致。
            ThemeApplier.Apply(this, palette);
        }
    }
}
