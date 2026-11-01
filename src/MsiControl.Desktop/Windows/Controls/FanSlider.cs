using MsiControl.Desktop.Theme;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 垂直滑块（自绘）：主题感知的占空比调节控件。
/// </summary>
/// <remarks>
/// <para>
/// 原生 <see cref="TrackBar"/> 的视觉样式固定跟随系统（浅色），不跟随应用的
/// 明暗主题，深色模式下会呈现突兀的浅色块；本控件改用设计令牌自绘轨道、
/// 填充与滑块，保证两种主题下都与整体风格一致。
/// </para>
/// <para>
/// 交互：鼠标点击/拖拽、滚轮步进；方向键小步、PageUp/PageDown 大步、
/// Home/End 到边界（任务书 §10 键盘操作）。辅助功能暴露 <see cref="AccessibleRole.Slider"/>
/// 与当前值语义。
/// </para>
/// </remarks>
public sealed class FanSlider : Control
{
    private const int TrackWidth = 6;
    private const int ThumbWidth = 30;
    private const int ThumbHeight = 14;
    private const int EdgePadding = 10;

    private int _value;
    private bool _dragging;

    /// <summary>
    /// 当前主题调色板。
    /// </summary>
    /// <remarks>
    /// 刻意<b>不</b>给默认值：<see cref="ThemePalette.Light"/> 的 Surface 是纯白，
    /// 一旦某个滑块没能收到 <see cref="ApplyPalette"/>（主题遍历早于控件创建、
    /// 面板重新展开后重建等），它就会在深色卡片上画出<b>白色块</b>——
    /// 实机截图里表现为「某一列滑块变成白底空块」。
    /// 未推送主题时只画父容器底色，宁可少画，也不臆造浅色。
    /// </remarks>
    private ThemePalette? _palette;

    /// <summary>
    /// 绘制异常上报通道（由宿主注入，通常写入应用日志）。
    /// </summary>
    /// <remarks>
    /// 自绘控件一旦在 <see cref="OnPaint"/> 抛异常，WinForms 不会给出任何可视提示，
    /// 该区域会停在异常发生前的内容上，排查时无从下手；这里兜底记录一条。
    /// </remarks>
    internal static Action<string>? Diagnostics { get; set; }

    /// <summary>构造垂直滑块。</summary>
    public FanSlider()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Opaque
            | ControlStyles.Selectable,
            true);

        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
        // 背景由 OnPaint 全量绘制（Opaque）：尺寸瞬时为 0 或主题未推送时也不会
        // 留下一块由系统默认画刷填出的浅色矩形。
        BackColor = SystemColors.Control;
    }

    /// <summary>最小值。</summary>
    public int Minimum { get; set; }

    /// <summary>最大值。</summary>
    public int Maximum { get; set; } = 100;

    /// <summary>方向键 / 滚轮步长。</summary>
    public int SmallChange { get; set; } = 1;

    /// <summary>PageUp/PageDown 步长。</summary>
    public int LargeChange { get; set; } = 5;

    /// <summary>当前值（构造范围内自动收敛）。</summary>
    public int Value
    {
        get => _value;
        set
        {
            var clamped = Math.Clamp(value, Minimum, Maximum);
            if (clamped == _value)
            {
                return;
            }

            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>值变化（拖拽、滚轮、键盘与程序赋值统一走此事件）。</summary>
    public event EventHandler? ValueChanged;

    /// <summary>应用主题（由宿主窗口在换肤时推送）。</summary>
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;
        BackColor = palette.Surface;
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 背景统一在 OnPaint 里铺满（Opaque）：避免两处刷底色导致闪烁，
        // 也避免主题未推送时用系统默认浅色画刷留下一块白。
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        try
        {
            PaintSlider(e.Graphics);
        }
        catch (Exception ex)
        {
            // 自绘异常不能冒泡：冒泡会让整个窗口的绘制链中断，
            // 该区域会停在异常发生前的内容上（实机表现为一块异常色块）。
            Diagnostics?.Invoke($"FanSlider 绘制失败：{ex.GetType().Name}：{ex.Message}");
            try
            {
                e.Graphics.Clear(BackColor);
            }
            catch
            {
                // 连背景都画不出来时保持静默，避免二次异常。
            }
        }
    }

    private void PaintSlider(System.Drawing.Graphics g)
    {
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var rect = ClientRectangle;

        // 先铺满整块客户区：后面任何分支提前返回都不会留下残色块。
        g.Clear(_palette?.Surface ?? BackColor);

        var palette = _palette;
        if (palette is null || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var trackX = rect.Left + (rect.Width - TrackWidth) / 2;
        var top = rect.Top + EdgePadding;
        var bottom = rect.Bottom - EdgePadding;
        if (bottom <= top)
        {
            return;
        }

        var thumbCenterY = YForValue(_value);
        var fillTop = thumbCenterY;

        // 未填充段：用 BorderStrong 而不是 SurfaceAlt。
        // SurfaceAlt 与卡片 Surface 只差一个明度台阶，未填充段几乎看不见，
        // 滑块看起来像一根悬空的短棒、看不出整条曲线的量程（实机截图确认）。
        using (var restBrush = new SolidBrush(Enabled ? palette.BorderStrong : palette.SurfaceAlt))
        using (var restPath = RoundedPath(trackX, top, TrackWidth, fillTop - top, TrackWidth / 2))
        {
            g.FillPath(restBrush, restPath);
        }

        // 已填充段（强调色，自下而上）。
        using (var fillBrush = new SolidBrush(Enabled ? palette.Accent : palette.ForeFaint))
        using (var fillPath = RoundedPath(trackX, fillTop, TrackWidth, bottom - fillTop, TrackWidth / 2))
        {
            g.FillPath(fillBrush, fillPath);
        }

        // 滑块：居中于轨道；获得焦点时用强调色描边表达键盘焦点（FocusButton 同一语义）。
        var thumbRect = new Rectangle(
            rect.Left + (rect.Width - ThumbWidth) / 2,
            thumbCenterY - ThumbHeight / 2,
            ThumbWidth,
            ThumbHeight);
        var thumbBack = Enabled ? palette.Accent : palette.SurfaceAlt;
        using (var thumbBrush = new SolidBrush(thumbBack))
        using (var thumbPath = RoundedPath(thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, 4))
        {
            g.FillPath(thumbBrush, thumbPath);
        }

        using var borderPen = new Pen(Focused && Enabled ? palette.Fore : palette.Surface, 1f);
        using var borderPath = RoundedPath(thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, 4);
        g.DrawPath(borderPen, borderPath);
    }

    /// <inheritdoc />
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left && Enabled)
        {
            Focus();
            _dragging = true;
            SetValueFromY(e.Y);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        if (_dragging && Enabled)
        {
            SetValueFromY(e.Y);
        }
    }

    /// <inheritdoc />
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (!Enabled)
        {
            return;
        }

        Value += Math.Sign(e.Delta) * SmallChange;
        ((HandledMouseEventArgs)e).Handled = true;
    }

    /// <inheritdoc />
    protected override bool IsInputKey(Keys keyData) => keyData switch
    {
        Keys.Up or Keys.Down or Keys.Left or Keys.Right
            or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End => true,
        _ => base.IsInputKey(keyData)
    };

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var delta = e.KeyCode switch
        {
            Keys.Up or Keys.Right => SmallChange,
            Keys.Down or Keys.Left => -SmallChange,
            Keys.PageUp => LargeChange,
            Keys.PageDown => -LargeChange,
            _ => 0
        };

        if (delta != 0)
        {
            Value += delta;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Home)
        {
            Value = Minimum;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.End)
        {
            Value = Maximum;
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    /// <inheritdoc />
    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private void SetValueFromY(int y)
    {
        var top = ClientRectangle.Top + EdgePadding;
        var bottom = ClientRectangle.Bottom - EdgePadding;
        if (bottom <= top)
        {
            return;
        }

        var clampedY = Math.Clamp(y, top, bottom);
        var fraction = (bottom - clampedY) / (float)(bottom - top);
        Value = (int)Math.Round(Minimum + fraction * (Maximum - Minimum));
    }

    private int YForValue(int value)
    {
        var top = ClientRectangle.Top + EdgePadding;
        var bottom = ClientRectangle.Bottom - EdgePadding;
        var fraction = (value - Minimum) / (float)(Maximum - Minimum);
        return (int)Math.Round(bottom - fraction * (bottom - top));
    }

    /// <summary>构造圆角矩形路径（r 为圆角半径；高度为 0 时返回空路径保护）。</summary>
    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(int x, int y, int w, int h, int r)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        if (h <= 0 || w <= 0)
        {
            return path;
        }

        var radius = Math.Min(r, Math.Min(w, h) / 2);
        path.AddArc(x, y, radius * 2, radius * 2, 180, 90);
        path.AddArc(x + w - radius * 2, y, radius * 2, radius * 2, 270, 90);
        path.AddArc(x + w - radius * 2, y + h - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(x, y + h - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <inheritdoc />
    protected override AccessibleObject CreateAccessibilityInstance() => new FanSliderAccessibleObject(this);

    private sealed class FanSliderAccessibleObject : Control.ControlAccessibleObject
    {
        public FanSliderAccessibleObject(FanSlider owner)
            : base(owner)
        {
        }

        // 基类 Owner 声明为可空，但 ControlAccessibleObject 构造时已强制传入所有者。
        private FanSlider OwnerSlider => (FanSlider)Owner!;

        public override AccessibleRole Role => AccessibleRole.Slider;

        public override string? Value => OwnerSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        public override string? Name => OwnerSlider.AccessibleName;
    }
}
