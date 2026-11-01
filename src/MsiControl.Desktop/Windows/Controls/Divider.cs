using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 发丝分隔线：用于同一张卡片内分组之间的视觉切分。
/// </summary>
/// <remarks>
/// 不用 <see cref="Panel"/>：<see cref="ThemeApplier"/> 会把面板背景刷成透明，
/// 线色无法保留；这里自绘一条 <see cref="ThemePalette.Border"/> 颜色的线，
/// 明暗主题切换时随之更新。
/// </remarks>
public sealed class Divider : Control, IThemeAware
{
    private ThemePalette? _palette;

    /// <summary>构造分隔线。</summary>
    public Divider()
    {
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Dock = DockStyle.Top;
        Height = 1;
        Margin = new Padding(0, UiFactory.DesignTokens.Space.Divider, 0, UiFactory.DesignTokens.Space.Divider);
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
        e.Graphics.DrawLine(pen, 0, 0, Width, 0);
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }
}
