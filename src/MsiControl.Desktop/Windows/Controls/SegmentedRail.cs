using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 分段轨道：把一排等分按钮收进一条圆角凹槽里。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它：</b>此前「性能模式」「显卡模式」只是五个 / 三个彼此相邻的普通按钮，
/// 每颗都有独立边框与 8px 间隙。视觉上它们是「五个开关」，而实际上它们是
/// <b>一组互斥选项</b>——形态与语义相反，用户得先读文字才能判断。
/// 收进凹槽后，外轮廓只有一个，选中项以抬起的亮面出现在槽内，语义与形态一致。
/// </para>
/// <para>
/// <b>分工：</b>轨道只负责「槽」——底色、描边、内边距与裁剪；
/// 按钮三态配色仍由 <see cref="SegmentedGroup"/> 决定，两者互不知道对方实现。
/// 这样主题切换时按钮样式只在 <see cref="SegmentedGroup.ApplyPalette"/> 一处刷新。
/// </para>
/// </remarks>
public sealed class SegmentedRail : Panel, IThemeAware
{
    /// <summary>槽内边距（逻辑像素）：让选中段的描边不贴槽壁。</summary>
    private const int Inset = 4;

    /// <summary>相邻段之间的间隙（逻辑像素）。</summary>
    private const int Gap = 4;

    private readonly TableLayoutPanel _grid;
    private ThemePalette? _palette;

    /// <summary>构造轨道。</summary>
    /// <param name="columns">段数。</param>
    public SegmentedRail(int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        _grid = new TableLayoutPanel
        {
            ColumnCount = columns,
            RowCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        for (var index = 0; index < columns; index++)
        {
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        }

        // 轨道高度 = 按钮高度 + 上下内边距，段按钮 Dock=Fill 后即可撑满槽内高度。
        Height = UiFactory.DesignTokens.Height.Button + (Inset * 2) + 2;
        Dock = DockStyle.Top;
        Margin = Padding.Empty;
        Padding = new Padding(Inset);

        Controls.Add(_grid);
    }

    /// <summary>
    /// 槽底颜色。供 <see cref="SegmentedGroup.TrackColor"/> 使用，使段与段之间不留缝线。
    /// </summary>
    /// <remarks>
    /// 深色主题往画布方向压暗（凹槽比卡片更深）；浅色主题往文字色方向压暗
    /// （浅色画布上「更深」才是凹，往画布方向只会变白，看不出槽）。
    /// </remarks>
    public Color? TrackColor => _palette is null ? null : TrackFillFor(_palette);

    /// <remarks>
    /// 深色主题直接取画布色：卡片与画布只差两个明度台阶，压暗不足时槽底与卡片糊在一起，
    /// 整排按钮又变回「一行带分隔线的文字」。浅色主题则往文字色方向压暗 12%（浅底上更暗才是凹）。
    /// </remarks>
    private static Color TrackFillFor(ThemePalette palette) => palette.Mode == ThemeMode.Dark
        ? palette.Back
        : ThemePalette.Blend(palette.SurfaceAlt, palette.Fore, 0.12);

    /// <summary>把按钮放入指定列。</summary>
    /// <param name="button">段按钮。</param>
    /// <param name="column">列下标。</param>
    public void Place(Button button, int column)
    {
        ArgumentNullException.ThrowIfNull(button);

        if (column < 0 || column >= _grid.ColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        var isLast = column == _grid.ColumnCount - 1;
        button.AutoSize = false;
        button.Dock = DockStyle.Fill;
        button.Height = UiFactory.DesignTokens.Height.Button;
        button.Margin = new Padding(
            column == 0 ? 0 : Gap / 2,
            0,
            isLast ? 0 : Gap / 2,
            0);

        // 圆角由 FocusButton 自己维护（CornerRadius）：这里再裁一次会多分配一次 Region，
        // 而且两处若半径不一致，先到的那次会被后来的覆盖，排查时很难看出是谁画的。
        if (button is FocusButton focus)
        {
            focus.CornerRadius = UiFactory.DesignTokens.Radius.Control;
        }

        _grid.Controls.Add(button, column, 0);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RoundedGeometry.Clip(this, UiFactory.DesignTokens.Radius.Control + 2);
    }

    /// <inheritdoc />
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 槽底比卡片再暗一档，选中段的 Surface 才能「浮起来」。
        e.Graphics.Clear(_palette is null ? BackColor : TrackFillFor(_palette));
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_palette is null || Width <= 2 || Height <= 2)
        {
            return;
        }

        // 槽的轮廓用 BorderStrong：这是唯一能让人看出「这是一条凹槽而不是一排按钮」的线索。
        RoundedGeometry.Stroke(
            e.Graphics,
            new Rectangle(0, 0, Width - 1, Height - 1),
            UiFactory.DesignTokens.Radius.Control + 2,
            _palette.BorderStrong);
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        Invalidate();
    }
}
