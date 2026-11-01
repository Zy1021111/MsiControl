using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 圆角输入容器：给原生输入控件套一层圆角外框。
/// </summary>
/// <remarks>
/// <para>
/// WinForms 的 <see cref="NumericUpDown"/> / <see cref="TextBox"/> 只能选
/// <see cref="BorderStyle.FixedSingle"/> 或 <see cref="BorderStyle.None"/>，两者都是直角；
/// 直接给控件设 <see cref="Control.Region"/> 会把直角边框切成残缺的弧线
/// （边框本身不存在圆弧），放大后能看到四角断口。
/// </para>
/// <para>
/// 因此做法与外层一致：内部控件 <see cref="BorderStyle.None"/>，由本容器
/// 裁圆角 + 填充底色 + 自绘圆角描边。容器高度取内部控件的首选高度加 2px，
/// 保证描边不会压在文字上。
/// </para>
/// </remarks>
public class RoundedField : Panel, IThemeAware
{
    private readonly int _radius;
    private ThemePalette? _palette;

    /// <summary>构造圆角输入容器。</summary>
    /// <param name="inner">内部输入控件，将以 Fill 停靠在容器内。</param>
    /// <param name="radius">圆角半径；0 表示取控件级令牌。</param>
    public RoundedField(Control inner, int radius = 0)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _radius = radius > 0 ? radius : UiFactory.DesignTokens.Radius.Control;

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // 左右 6px：内部输入控件没有边框后，文字会直接贴到描边上（实机截图里「20」挤在最左边）。
        // 上下各 1px 只留出描边的位置——输入控件的行高由字体锁定，容器再高它也不会跟着长。
        Padding = new Padding(6, 1, 6, 1);
        Margin = Padding.Empty;

        switch (inner)
        {
            case NumericUpDown numeric:
                numeric.BorderStyle = BorderStyle.None;
                // NumericUpDown 默认 AutoSize=true，会按「文本 + 箭头」的首选宽度撑开，
                // 完全不理会 Dock=Fill——结果它比外框宽出十几个像素，
                // 右边那一截盖住外框的圆角，看起来就是「左边圆、右边直角」。
                numeric.AutoSize = false;
                break;
            case TextBox textBox:
                textBox.BorderStyle = BorderStyle.None;
                break;
        }

        inner.Dock = DockStyle.Fill;
        inner.Margin = Padding.Empty;
        Controls.Add(inner);

        // 高度跟随内部控件：NumericUpDown / TextBox 的高度由字体决定，写死像素会在高 DPI 下错位。
        Height = inner.PreferredSize.Height + Padding.Vertical + 2;
    }

    /// <summary>内部的输入控件。</summary>
    public Control Inner => Controls.Count > 0 ? Controls[0] : this;

    /// <inheritdoc />
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyClip();
    }

    /// <inheritdoc />
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        ApplyClip();
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyClip();
    }

    /// <summary>
    /// 把容器与内部控件一起裁成圆角。
    /// </summary>
    /// <remarks>
    /// 只裁容器是不够的：子控件是独立窗口，它的矩形底色会盖住容器的圆角区域，
    /// 结果只有容器露在外面的那一小块是圆的，主体仍是直角（首版放大截图里
    /// 表现为「左边圆、右边直角」）。因此内部控件也要按略小一号的半径裁一次。
    /// </remarks>
    private void ApplyClip()
    {
        RoundedGeometry.Clip(this, _radius);

        // 逐层往下裁：NumericUpDown 内部还有编辑框与（已隐藏的）箭头按钮，
        // 只裁直接子控件时，孙控件的矩形底仍会盖住圆角。
        ClipDescendants(this, Math.Max(1, _radius - 1));
    }

    private static void ClipDescendants(Control parent, int radius)
    {
        foreach (Control child in parent.Controls)
        {
            RoundedGeometry.Clip(child, radius);
            if (child.HasChildren)
            {
                ClipDescendants(child, radius);
            }
        }
    }

    /// <inheritdoc />
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(_palette?.SurfaceAlt ?? BackColor);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_palette is null || Width <= 2 || Height <= 2)
        {
            return;
        }

        // 在 OnPaint 里再铺一次底，而不是只靠 OnPaintBackground：
        // 容器被 ThemeApplier 设成 Transparent 后，内部控件不绘制的区域会直接透出
        // 卡片底色，表现为输入框右半截「凹下去一块更暗的矩形」（实机放大截图确认）。
        // 子控件是独立窗口，会在父绘制之后绘制，这里先铺底不会盖住它们。
        RoundedGeometry.Fill(
            e.Graphics,
            new Rectangle(0, 0, Width, Height),
            _radius,
            _palette.SurfaceAlt);

        RoundedGeometry.Stroke(
            e.Graphics,
            new Rectangle(0, 0, Width - 1, Height - 1),
            _radius,
            _palette.Border);
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        BackColor = palette.SurfaceAlt;
        Invalidate();
    }
}
