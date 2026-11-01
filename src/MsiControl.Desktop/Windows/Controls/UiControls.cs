using MsiControl.Core;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 读数状态到展示语气的映射。
/// </summary>
/// <remarks>
/// 任务书 §6.4 要求四态在界面上可分辨，因此状态→颜色的映射集中在此处，
/// 避免各控件各自决定「过期该用什么颜色」。
/// </remarks>
public static class ToneMap
{
    /// <summary>取得读数状态对应的语气。</summary>
    public static MetricTone Of(ReadingStatus status) => status switch
    {
        ReadingStatus.Valid => MetricTone.Normal,
        ReadingStatus.Stale => MetricTone.Warning,
        ReadingStatus.Sleeping => MetricTone.Muted,
        ReadingStatus.Unavailable => MetricTone.Danger,
        _ => MetricTone.Muted
    };
}

/// <summary>读数展示的语气，用于选择颜色。</summary>
public enum MetricTone
{
    /// <summary>正常有效值。</summary>
    Normal = 0,

    /// <summary>次要信息。</summary>
    Muted = 1,

    /// <summary>降级：过期或回落。</summary>
    Warning = 2,

    /// <summary>故障或不可用。</summary>
    Danger = 3,

    /// <summary>强调：当前生效的模式。</summary>
    Accent = 4
}

/// <summary>
/// 单个读数的展示卡片：标题 + 数值 + 备注。
/// </summary>
/// <remarks>
/// <para>
/// 自绘而非使用现成图表控件：任务书提示词第 10 条要求优先用 WinForms 自绘控件减少 UI 依赖。
/// 备注行用于承载「过期 / 休眠 / 回落说明 / 统计口径」，保证状态语义不被数值掩盖。
/// </para>
/// <para>
/// <b>2026-10-31 视觉修订：</b>改为 8px 圆角 + 发丝描边的「仪表窗」：卡片内并排八块时，
/// 只有描边能让每块读数的边界一眼可数；纯色块在深色主题下会糊成一片。
/// 字号阶梯同时拉开：标题 &lt; 备注 &lt; 数值，数值用 <see cref="MetricTone"/> 着色，
/// 因此扫读时先看到数字、再看到单位、最后才是口径说明。
/// </para>
/// </remarks>
public sealed class MetricTile : UserControl, IThemeAware
{
    private readonly Label _title = new() { AutoSize = true };
    private readonly Label _value = new() { AutoSize = true };
    private readonly Label _note = new() { AutoSize = true };
    private readonly TableLayoutPanel _layout = new()
    {
        ColumnCount = 1,
        RowCount = 3,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        // Dock 必须是 Top 而不是 Fill：Dock=Fill 的子控件不参与父级 AutoSize 的高度计算，
        // 瓷砖在 125% DPI 下会被压成只剩最小高度（历史坑，勿改）。
        Dock = DockStyle.Top,
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    private readonly float _displayFontScale;
    private ThemePalette? _palette;
    private MetricTone _tone = MetricTone.Normal;

    /// <summary>构造卡片。</summary>
    /// <param name="title">标题。</param>
    public MetricTile(string title)
        : this(title, UiFactory.DesignTokens.Type.Display)
    {
    }

    /// <summary>构造卡片，并指定数值字号倍率。</summary>
    /// <param name="title">标题。</param>
    /// <param name="displayFontScale">
    /// 数值字号相对基准字体的倍率。四列并排的窄容器（迷你窗口）需要略小的倍率，
    /// 否则「3.32 GHz」末位会被列宽裁掉。
    /// </param>
    public MetricTile(string title, float displayFontScale)
    {
        Title = title;
        _displayFontScale = displayFontScale;
        _title.Text = title;
        _title.ForeColor = SystemColors.GrayText;
        _title.Font = UiFactory.Scale(Font, UiFactory.DesignTokens.Type.Caption);
        _value.Text = ValueFormatter.Placeholder;
        _value.Font = UiFactory.Scale(Font, _displayFontScale, FontStyle.Bold);
        _note.Text = string.Empty;
        _note.ForeColor = SystemColors.GrayText;
        _note.Font = UiFactory.Scale(Font, UiFactory.DesignTokens.Type.Micro);

        _layout.Controls.Add(_title, 0, 0);
        _layout.Controls.Add(_value, 0, 1);
        _layout.Controls.Add(_note, 0, 2);

        Controls.Add(_layout);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(UiFactory.DesignTokens.Space.Xs);
        Padding = new Padding(
            UiFactory.DesignTokens.Space.Md,
            UiFactory.DesignTokens.Space.Sm,
            UiFactory.DesignTokens.Space.Md,
            UiFactory.DesignTokens.Space.Sm);
        MinimumSize = new Size(120, UiFactory.DesignTokens.Height.Tile);
    }

    /// <summary>标题。</summary>
    public string Title { get; }

    /// <summary>当前数值文本。</summary>
    public string ValueText => _value.Text;

    /// <summary>当前备注文本。</summary>
    public string NoteText => _note.Text;

    /// <summary>设置展示内容。</summary>
    /// <param name="value">数值文本。</param>
    /// <param name="note">备注文本，可为空。</param>
    /// <param name="tone">语气。</param>
    public void SetValue(string value, string? note = null, MetricTone tone = MetricTone.Normal)
    {
        // 备注行始终占位：四块读数瓷砖在有 / 无备注时高度一致，
        // 否则同一行内的瓷砖会参差不齐（实机截图曾出现）。
        var noteText = string.IsNullOrEmpty(note) ? " " : note;

        // 取值、备注、语气都没变时直接返回：本方法每秒被调用一次，
        // 而 ForeColor / Text 的写入会引出一次重绘与一次布局级联。
        if (_value.Text == value && _note.Text == noteText && _tone == tone)
        {
            return;
        }

        _tone = tone;
        _value.Text = value;
        _note.Text = noteText;
        _note.Visible = true;

        // 屏幕阅读器无法读取自绘卡片的分段文本，因此显式提供可访问名与描述。
        AccessibleName = $"{Title}：{value}";
        AccessibleDescription = string.IsNullOrEmpty(note) ? Title : $"{Title}。{note}";

        if (_palette is not null)
        {
            _value.ForeColor = ToneColor(_palette, tone);
            _note.ForeColor = tone == MetricTone.Danger ? _palette.Danger : _palette.ForeMuted;
        }
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.SurfaceAlt;
        ForeColor = palette.Fore;
        _layout.BackColor = palette.SurfaceAlt;
        _title.ForeColor = palette.ForeMuted;
        _note.ForeColor = palette.ForeMuted;

        // 此时控件已挂到窗体上，Font 取自继承链（随系统 DPI 缩放），
        // 因此在换肤时重算字号，避免构造期固定的字号在高 DPI 下失真。
        _title.Font = UiFactory.Scale(Font, UiFactory.DesignTokens.Type.Caption);
        _note.Font = UiFactory.Scale(Font, UiFactory.DesignTokens.Type.Micro);
        _value.Font = UiFactory.Scale(Font, _displayFontScale, FontStyle.Bold);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        RoundedGeometry.Clip(this, UiFactory.DesignTokens.Radius.Tile);
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

        RoundedGeometry.Stroke(
            e.Graphics,
            new Rectangle(0, 0, Width - 1, Height - 1),
            UiFactory.DesignTokens.Radius.Tile,
            _palette.Border);
    }

    internal static Color ToneColor(ThemePalette palette, MetricTone tone) => tone switch
    {
        MetricTone.Muted => palette.ForeMuted,
        MetricTone.Warning => palette.Warn,
        MetricTone.Danger => palette.Danger,
        MetricTone.Accent => palette.Accent,
        _ => palette.Fore
    };
}

/// <summary>
/// 状态徽标：左侧说明 + 右侧取值，整体是一枚圆角胶囊。
/// </summary>
/// <remarks>
/// <para>
/// 用于顶部状态带展示「当前实际性能模式 / 实际显卡模式 / 待生效」。
/// 直接继承 <see cref="FlowLayoutPanel"/>：AutoSize 由流式布局自生。
/// 曾用「UserControl + 内部 Dock=Top 的 FlowLayoutPanel」结构，在流式父容器里
/// 宽度计算成环（父宽等子宽、子宽又等父宽），实机截图中徽标坍缩成无字小块。
/// </para>
/// <para>
/// <b>2026-10-31 视觉修订：</b>原文案直接贴在状态带的底面上，三组「说明：取值」之间只靠间距分开，
/// 读起来像一行没对齐的正文。现改为圆角胶囊（SurfaceAlt 底 + 发丝描边），
/// 状态带因此有了明确的「读数单位」，与下方的卡片体系连成一套。
/// </para>
/// </remarks>
public sealed class StatusPill : FlowLayoutPanel, IThemeAware
{
    private readonly Label _caption = new() { AutoSize = true };
    private readonly Label _value = new() { AutoSize = true };
    private readonly ToolTip _tip = new();

    private ThemePalette? _palette;
    private MetricTone _tone = MetricTone.Normal;

    /// <summary>构造徽标。</summary>
    /// <param name="caption">左侧说明文本。</param>
    public StatusPill(string caption)
    {
        Caption = caption;
        _caption.Text = caption + "：";
        _caption.ForeColor = SystemColors.GrayText;
        _value.Text = ValueFormatter.Placeholder;
        _value.Font = new Font(Font, FontStyle.Bold);

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlowDirection = FlowDirection.LeftToRight;
        WrapContents = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, UiFactory.DesignTokens.Space.Sm, 0);
        Padding = new Padding(
            UiFactory.DesignTokens.Space.Md,
            UiFactory.DesignTokens.Space.Hair,
            UiFactory.DesignTokens.Space.Md,
            UiFactory.DesignTokens.Space.Hair);

        Controls.Add(_caption);
        Controls.Add(_value);
    }

    /// <summary>左侧说明文本。</summary>
    public string Caption { get; }

    /// <summary>当前取值文本。</summary>
    public string ValueText => _value.Text;

    /// <summary>补充说明（悬浮提示）。为空时只提示「说明：取值」本身。</summary>
    public string? Note { get; private set; }

    /// <summary>是否显示为「有变更待生效」状态。</summary>
    public bool HasPending { get; private set; }

    /// <summary>设置取值。</summary>
    /// <param name="note">
    /// 补充说明，例如不可用原因或口径。徽标行宽有限不放正文，以悬浮提示呈现——
    /// 状态条上的「不可用」必须可查明原因，否则用户无从判断是故障还是按设计禁用。
    /// </param>
    public void SetValue(string value, MetricTone tone = MetricTone.Normal, bool hasPending = false, string? note = null)
    {
        // 每秒随采样刷新调用一次：取值 / 语气 / 待生效标志都没变时不碰任何属性，
        // 避免重复写 Text、ForeColor 与 ToolTip 引起的重绘与布局。
        if (_value.Text == value && _tone == tone && HasPending == hasPending && Note == note)
        {
            return;
        }

        _tone = tone;
        _value.Text = value;
        HasPending = hasPending;
        Note = note;

        AccessibleName = $"{Caption}：{value}";

        if (_palette is not null)
        {
            _value.ForeColor = hasPending
                ? _palette.Warn
                : MetricTile.ToneColor(_palette, tone);
        }

        ApplyTip();
    }

    /// <summary>把「说明：取值 + 备注」挂到徽标与两个标签上（容器子控件不继承父提示）。</summary>
    private void ApplyTip()
    {
        var text = string.IsNullOrWhiteSpace(Note)
            ? $"{Caption}：{ValueText}"
            : $"{Caption}：{ValueText}{Environment.NewLine}{Note}";
        _tip.SetToolTip(this, text);
        _tip.SetToolTip(_caption, text);
        _tip.SetToolTip(_value, text);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tip.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <inheritdoc />
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // 胶囊半径取高度一半：AutoSize 下高度随字体变化，固定半径会在高 DPI 上变成圆角矩形。
        RoundedGeometry.Clip(this, Height / 2);
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

        RoundedGeometry.Stroke(
            e.Graphics,
            new Rectangle(0, 0, Width - 1, Height - 1),
            Height / 2,
            _palette.Border);
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.SurfaceAlt;
        ForeColor = palette.Fore;
        _caption.ForeColor = palette.ForeMuted;

        // 同上：换肤时按继承到的字体重算，保证 DPI 与用户字体设置生效。
        _caption.Font = Font;
        _value.Font = new Font(Font, FontStyle.Bold);
        Invalidate();
    }
}
