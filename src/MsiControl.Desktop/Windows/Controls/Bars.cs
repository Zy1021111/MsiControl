using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 表面条：App bar 与底部状态栏的容器。
/// </summary>
/// <remarks>
/// <para>
/// 与普通 Panel 的区别：背景取 <see cref="ThemePalette.Surface"/> 而非画布色，
/// 并可按位置画一条发丝分隔线（<see cref="ThemePalette.Border"/>）。
/// WinForms 的 Panel 会被 <see cref="ThemeApplier"/> 统一刷成画布色，
/// 因此这里实现 <see cref="IThemeAware"/> 自己管理背景。
/// </para>
/// <para>
/// 子标签保持透明即可正确透出条面背景（Label 的透明背景受支持）。
/// </para>
/// </remarks>
public sealed class SurfaceBar : Panel, IThemeAware
{
    private readonly bool _lineAtBottom;
    private ThemePalette? _palette;

    /// <summary>构造表面条。</summary>
    /// <param name="lineAtBottom">true 表示分隔线画在底边（App bar）；false 画在顶边（状态栏）。</param>
    public SurfaceBar(bool lineAtBottom)
    {
        _lineAtBottom = lineAtBottom;
        Dock = DockStyle.Top;
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_palette is null || Width <= 0)
        {
            return;
        }

        using var pen = new Pen(_palette.Border);
        var y = _lineAtBottom ? Height - 1 : 0;
        e.Graphics.DrawLine(pen, 0, y, Width - 1, y);
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }
}
