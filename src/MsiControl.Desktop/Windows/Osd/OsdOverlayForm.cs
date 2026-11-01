using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MsiControl.Desktop.Windows.Osd;

/// <summary>
/// 桌面左上角的状态提示浮层。
/// </summary>
/// <remarks>
/// <para>
/// 用途：按 Fn 键或等效热键后，在屏幕上给出**与硬件真实状态一致**的即时反馈。
/// 没有它，功能虽然执行成功，用户却看不到任何变化，会判定「快捷键不工作」——
/// 这正是本轮要修的问题。
/// </para>
/// <para>
/// <b>不抢焦点</b>：<c>WS_EX_NOACTIVATE</c> + <see cref="ShowWithoutActivation"/>，
/// 否则提示窗会出现并抢走当前窗口的输入焦点，比没有提示更糟。
/// <c>WS_EX_TOOLWINDOW</c> 让它不进入 Alt+Tab 与任务栏。
/// </para>
/// <para>
/// 单实例复用：连续触发只更新内容并重置计时，不叠加多个窗口。
/// </para>
/// </remarks>
internal sealed class OsdOverlayForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int CornerRadius = 12;
    private const int ScreenMargin = 20;

    /// <summary>
    /// 浮层配色。
    /// </summary>
    /// <remarks>
    /// 浮层不跟随系统明暗主题（它出现在桌面与任意应用之上，必须自身有足够对比），
    /// 但**色值必须与深色主题一致**：提示窗用的是 <c>#14171C</c> 卡片底、
    /// <c>#EDF1F5</c> 主文本与 <c>#2FD3C0</c> 强调色，与主界面是同一套语言。
    /// 早期这里是一组独立的灰蓝，导致同一个「平衡模式」提示与主窗口里的青色读数不像一个产品。
    /// </remarks>
    private static readonly Color SurfaceColor = Color.FromArgb(0x14, 0x17, 0x1C);
    private static readonly Color TitleColor = Color.FromArgb(0xED, 0xF1, 0xF5);

    private readonly Label _title;
    private readonly RoundedBadge _badge;
    private readonly System.Windows.Forms.Timer _hideTimer;
    private readonly System.Windows.Forms.Timer _fadeTimer;

    private bool _fading;

    internal OsdOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SurfaceColor;
        DoubleBuffered = true;
        Padding = new Padding(18, 12, 14, 12);
        Margin = Padding.Empty;

        _title = new Label
        {
            AutoSize = true,
            ForeColor = TitleColor,
            BackColor = Color.Transparent,
            Font = new Font(UiFactory.BaseFont.FontFamily, UiFactory.BaseFont.SizeInPoints * 1.06f, FontStyle.Regular),
            Margin = new Padding(0, 0, 12, 0),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _badge = new RoundedBadge
        {
            Font = new Font(UiFactory.BaseFont.FontFamily, UiFactory.BaseFont.SizeInPoints * 0.86f, FontStyle.Bold),
            Margin = new Padding(0, 1, 0, 1)
        };

        var layout = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.Controls.Add(_title);
        layout.Controls.Add(_badge);
        Controls.Add(layout);

        _hideTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _hideTimer.Tick += (_, _) => BeginFade();

        _fadeTimer = new System.Windows.Forms.Timer { Interval = 28 };
        _fadeTimer.Tick += (_, _) => StepFade();
    }

    /// <summary>提示窗不参与激活。</summary>
    protected override bool ShowWithoutActivation => true;

    /// <inheritdoc />
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
            return parameters;
        }
    }

    /// <summary>
    /// 显示一条提示；已在显示时更新内容并重新计时。
    /// </summary>
    internal void Show(OsdMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        _title.Text = message.Title;
        _badge.SetContent(message.Badge, ToneColor(message.Tone));

        // 重新布局后再定位：内容宽度变化会影响 AutoSize 结果。
        PerformLayout();
        PlaceAtTopLeft();

        _fadeTimer.Stop();
        _fading = false;
        Opacity = 1d;

        if (!Visible)
        {
            Show();
        }

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>立即隐藏并停止计时（退出时调用）。</summary>
    internal void HideImmediately()
    {
        _hideTimer.Stop();
        _fadeTimer.Stop();
        _fading = false;
        Opacity = 1d;
        Hide();
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // 圆角区域必须随尺寸重建，否则 AutoSize 变化后会出现直角。
        using var path = CreateRoundedPath(ClientRectangle, CornerRadius);
        Region = new Region(path);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _fadeTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void PlaceAtTopLeft()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + ScreenMargin, area.Top + ScreenMargin);
    }

    private void BeginFade()
    {
        _hideTimer.Stop();
        _fading = true;
        _fadeTimer.Start();
    }

    private void StepFade()
    {
        if (!_fading)
        {
            _fadeTimer.Stop();
            return;
        }

        Opacity -= 0.12d;
        if (Opacity > 0.05d)
        {
            return;
        }

        _fadeTimer.Stop();
        _fading = false;
        Opacity = 1d;
        Hide();
    }

    private static Color ToneColor(OsdTone tone) => tone switch
    {
        OsdTone.Success => Color.FromArgb(0x4A, 0xDE, 0x80),
        OsdTone.Warning => Color.FromArgb(0xF5, 0xB8, 0x3D),
        OsdTone.Danger => Color.FromArgb(0xFF, 0x7A, 0x70),
        _ => Color.FromArgb(0x2F, 0xD3, 0xC0)
    };

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return path;
        }

        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var arc = new Rectangle(bounds.X, bounds.Y, diameter, diameter);

        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>右侧圆角状态徽标。</summary>
    private sealed class RoundedBadge : Control
    {
        private string _text = string.Empty;
        private Color _fill = Color.FromArgb(0x2F, 0xD3, 0xC0);

        internal RoundedBadge()
        {
            // SupportsTransparentBackColor 必须与 UserPaint 一起打开，
            // 否则下方设置 Color.Transparent 会抛「Control does not support transparent
            // background colors」——该异常发生在构造期，会直接打断启动流程。
            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint
                | ControlStyles.SupportsTransparentBackColor,
                true);
            AutoSize = false;
            BackColor = Color.Transparent;
            // 徽标底色是亮青 / 亮绿：白色文字的对比度不足 2:1，必须改用近黑。
            ForeColor = Color.FromArgb(0x06, 0x14, 0x12);
        }

        internal void SetContent(string text, Color fill)
        {
            _text = text ?? string.Empty;
            _fill = fill;

            // 宽度随文字自适应：徽标文案是数据，不预设尺寸。
            var measured = TextRenderer.MeasureText(_text, Font);
            Size = new Size(measured.Width + 18, measured.Height + 6);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var path = CreateRoundedPath(bounds, bounds.Height / 2);
            using var brush = new SolidBrush(_fill);
            e.Graphics.FillPath(brush, path);

            TextRenderer.DrawText(
                e.Graphics,
                _text,
                Font,
                bounds,
                ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
