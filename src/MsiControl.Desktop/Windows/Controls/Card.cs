using System.Drawing.Drawing2D;
using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 内容卡片：带标题行的圆角容器，替代 <see cref="GroupBox"/>。
/// </summary>
/// <remarks>
/// <para>
/// <b>2026-10-31 视觉修订（「精密仪表」方向）：</b>原卡片是「Surface 底 + 1px 描边 + 8px 圆角 +
/// 强调色方块标题点」，在深色主题下几乎与画布同色，只能靠描边辨认轮廓，读起来像一层层表格线。
/// 现改为三处明确改动：
/// </para>
/// <list type="number">
/// <item>圆角 8 → 12：更大的半径让卡片与画布之间形成柔和过渡，不再像表格单元格。</item>
/// <item>顶边加一道 1px 内高光（<see cref="ThemePalette.Highlight"/>）：GDI 画不出投影，
/// 这道高光是「离画布更近」的唯一可靠暗示，也不引入任何渐变。</item>
/// <item>标题点由 6px 方块改为 5px 圆点，标题字号与正文同级加粗——层级交给字重，而不是字号。</item>
/// </list>
/// <para>
/// 布局语义与原来的 <c>UiFactory.CreateSection</c> 一致：
/// 内容控件 <see cref="DockStyle.Top"/> 挂入，卡片 <see cref="Control.AutoSize"/> 自适应高度。
/// 子标签保持透明，透出卡片 Surface 背景。
/// </para>
/// </remarks>
public sealed class Card : UserControl, IThemeAware
{
    /// <summary>圆角半径（px）。</summary>
    public const int CornerRadius = UiFactory.DesignTokens.Radius.Card;

    private readonly Label _title;
    private readonly TitleDot _dot;
    private readonly Control _content;
    private readonly TableLayoutPanel _header;
    private ThemePalette? _palette;

    /// <summary>构造卡片。</summary>
    /// <param name="title">卡片标题；同时作为屏幕阅读器的可访问名。</param>
    /// <param name="content">内容控件，将以 Top 停靠放入卡片。</param>
    public Card(string title, Control content)
        : this(title, content, headerAction: null)
    {
    }

    /// <summary>
    /// 构造带头部动作的卡片。
    /// </summary>
    /// <param name="title">卡片标题。</param>
    /// <param name="content">内容控件。</param>
    /// <param name="headerAction">
    /// 标题行右侧的动作控件（例如「展开 / 收起」切换按钮）。
    /// 放在标题行而不是内容首行：动作与它控制的区块处于同一视觉层级，
    /// 内容收起时标题行仍完整可见。
    /// </param>
    public Card(string title, Control content, Control? headerAction)
    {
        ArgumentNullException.ThrowIfNull(content);

        _title = new Label
        {
            AutoSize = true,
            Text = title,
            ForeColor = SystemColors.GrayText,
            // 标题与正文同字号、加粗：靠字重而不是字号建立层级，
            // 卡片多的时候字号每加一级都会让整体变大一号，反而不精致。
            Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Body, FontStyle.Bold),
            Margin = Padding.Empty
        };

        _dot = new TitleDot
        {
            Size = new Size(5, 5),
            Margin = new Padding(0, 6, UiFactory.DesignTokens.Space.Sm, 0)
        };

        var titleRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        titleRow.Controls.Add(_dot);
        titleRow.Controls.Add(_title);

        // 两列头部：标题占满剩余宽度，动作靠右。
        // 用 TableLayoutPanel 而非右对齐的流式布局：流式布局的 AutoSize 宽度
        // 只等于内容宽度，动作不会贴到卡片右边界。
        _header = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _header.Controls.Add(titleRow, 0, 0);

        if (headerAction is not null)
        {
            headerAction.Margin = Padding.Empty;
            headerAction.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            _header.Controls.Add(headerAction, 1, 0);
        }

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        // 内边距：左右 18 让内容与圆角保持安全距离（半径 12 时 16 会显得贴边），
        // 上 14 / 下 16 让标题行与内容各自有呼吸区，且上下不对称更符合阅读重心。
        Padding = new Padding(18, 14, 18, 16);
        // 卡片之间的沟堑：上下合计 14，配合 4px 基网与 12px 卡片间距令牌。
        Margin = new Padding(0, 6, 0, 8);

        AccessibleName = title;
        AccessibleRole = AccessibleRole.Grouping;

        // 停靠按 z-order 从后往前处理：先加 content、后加 header，
        // header（最后添加）先停靠贴顶，content 停靠在其下。顺序反了标题会跑到卡片底部。
        _content = content;
        Controls.Add(_content);
        _content.Dock = DockStyle.Top;
        _content.Margin = new Padding(0, UiFactory.DesignTokens.Space.Md, 0, 0);
        Controls.Add(_header);
    }

    /// <summary>卡片标题。</summary>
    public string Title => _title.Text;

    /// <summary>卡片内容控件。</summary>
    public Control Content => _content;

    /// <inheritdoc />
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RoundedGeometry.Clip(this, CornerRadius);
    }

    /// <inheritdoc />
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 整块铺满 Surface：圆角裁剪后若只靠 BackColor 填充，
        // 尺寸变化的一瞬间会露出画布色，形成一道闪白。
        var fill = _palette?.Surface ?? BackColor;
        e.Graphics.Clear(fill);
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_palette is null || Width <= 2 || Height <= 2)
        {
            return;
        }

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        RoundedGeometry.Stroke(e.Graphics, bounds, CornerRadius, _palette.Border);

        // 顶边内高光：只画在两侧圆角之间的一条 1px 线上。
        // 若沿整圈描边，高光会被随后的边框完全盖住，等于没画。
        var inset = CornerRadius;
        if (Width > inset * 2 + 2)
        {
            e.Graphics.SmoothingMode = SmoothingMode.None;
            using var highlightPen = new Pen(_palette.Highlight);
            e.Graphics.DrawLine(highlightPen, inset, 1, Width - inset - 1, 1);
        }
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        BackColor = palette.Surface;
        _title.ForeColor = palette.Fore;
        _dot.Fill = palette.Accent;
        Invalidate();
    }

    /// <summary>标题前的强调色圆点。</summary>
    /// <remarks>
    /// 用自绘控件而不是 <see cref="Label"/>：Label 只能画方块，5px 的方块在 125% DPI 下
    /// 会被系统再放大成一颗「像素块」，圆点在同样尺寸下才是「一个标记」。
    /// </remarks>
    private sealed class TitleDot : Control
    {
        private Color _fill = SystemColors.GrayText;

        internal TitleDot()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor,
                true);
            BackColor = Color.Transparent;
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        internal Color Fill
        {
            get => _fill;
            set
            {
                _fill = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_fill);
            e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
        }
    }
}
