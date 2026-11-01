using MsiControl.Desktop.Interop;

namespace MsiControl.Desktop.Windows.Controls;

/// <summary>
/// 主页面滚动宿主：自管滚动（不使用 <see cref="ScrollableControl.AutoScroll"/>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 AutoScroll：</b>该机制把「直接子控件的边界并集」当滚动范围，
/// 并在每次布局里按瞬时值重算、顺手钳制当前位置。内容区每秒刷新读数时，
/// 内容栈（AutoSize 的表格）会出现一次偏小的瞬时高度（实测 935 → 852，少 83 px），
/// 一旦撞上范围重算，位置就被写成 <c>瞬时高度 − 客户区高度</c>（本机恒为 57 px）。
/// 该值与内容高度、视口高度都无关，且范围恢复后不会自愈；
/// 任何「事后写回」都会变成「每秒纠正一次」，视觉上就是持续抽动。
/// </para>
/// <para>
/// 因此这里把滚动彻底收归自己：<see cref="ScrollableControl.AutoScroll"/> 恒为
/// <see langword="false"/>，偏移只由 <see cref="SetOffset"/> 修改，范围只由
/// <see cref="_extent"/> 决定，滚动条是独立的 <see cref="VScrollBar"/>，
/// WinForms 不再有机会改写位置。
/// </para>
/// </remarks>
public sealed class ScrollHostPanel : Panel
{
    /// <summary>承载内容的中间层。</summary>
    private readonly Panel _viewport = new()
    {
        Margin = Padding.Empty,
        Padding = Padding.Empty
    };

    /// <summary>纵向滚动条。</summary>
    private readonly VScrollBar _bar = new()
    {
        Margin = Padding.Empty,
        SmallChange = 48,
        Visible = false
    };

    /// <summary>滚动内容。</summary>
    private Control? _content;

    /// <summary>内容高度（滚动范围）。</summary>
    private int _extent;

    /// <summary>当前纵向偏移（像素，向下为正）。</summary>
    private int _offset;

    /// <summary>正在同步布局，避免递归。</summary>
    private bool _syncing;

    /// <summary>构造滚动宿主。</summary>
    public ScrollHostPanel()
    {
        DoubleBuffered = true;
        AutoScroll = false;
        _viewport.BackColor = BackColor;
        // 程序写 Value 时也可能触发 Scroll：必须抑制，否则「范围瞬时变化 → Value 被钳
        // → Scroll 回来改写用户意图」会形成反馈环，把位置永久清零。
        _bar.Scroll += (_, e) =>
        {
            if (!_syncing)
            {
                SetOffset(e.NewValue);
            }
        };
        Controls.Add(_viewport);
        Controls.Add(_bar);
    }

    /// <summary>滚动条是否使用深色变体。</summary>
    public bool DarkScrollbar { get; set; }

    /// <summary>内容可用宽度（已扣除滚动条占位）。</summary>
    public int ContentWidth => Math.Max(0, ClientSize.Width - (_bar.Visible ? _bar.Width : 0));

    /// <summary>当前纵向偏移（像素，向下为正）。</summary>
    public int Offset => _offset;

    /// <summary>挂入滚动内容。</summary>
    /// <param name="content">内容树的根控件。</param>
    /// <exception cref="ArgumentNullException"><paramref name="content"/> 为 <see langword="null"/>。</exception>
    public void SetContent(Control content)
    {
        ArgumentNullException.ThrowIfNull(content);

        _content = content;
        content.Dock = DockStyle.Top;
        content.Margin = Padding.Empty;
        _viewport.Controls.Add(content);

        // 内容栈是 AutoSize 表格：它变高/变矮（折叠、换行）不会触发本宿主的布局，
        // 必须自己订阅，否则滚动范围与内容宽度都停在首次布局时的值上。
        content.SizeChanged += (_, _) => SyncLayout();
        content.Layout += (_, _) => SyncLayout();

        SyncLayout();
    }

    /// <summary>内容结构变化（折叠 / 展开）后重算滚动范围，允许变小。</summary>
    /// <remarks>
    /// 平时范围取高水位（防内容栈刷新的瞬时偏小），只有内容真的少了一块时
    /// 才需要下调；由折叠按钮在布局结束后显式调用。
    /// </remarks>
    public void RecomputeExtent()
    {
        if (_content is null || !IsHandleCreated)
        {
            return;
        }

        _content.PerformLayout();
        var height = _content.Height;
        if (height <= 0)
        {
            return;
        }

        _extent = height;
        _offset = Math.Min(_offset, Math.Max(0, height - ClientSize.Height));
        SyncLayout();
    }

    /// <summary>滚回顶部。</summary>
    public void ScrollToTop() => SetOffset(0);

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyScrollbarTheme();

        // 首帧可能还没有客户区尺寸（构造期 ClientSize 为 0），此时量不到内容高度。
        // 句柄就绪后补一次，避免滚动条与范围一直停在初始的空值上。
        BeginInvoke(new Action(SyncLayout));
    }

    /// <inheritdoc />
    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        SyncLayout();
    }

    /// <inheritdoc />
    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        SyncLayout();
    }

    /// <inheritdoc />
    protected override void OnBackColorChanged(EventArgs e)
    {
        base.OnBackColorChanged(e);
        _viewport.BackColor = BackColor;
    }

    /// <inheritdoc />
    protected override void WndProc(ref Message m)
    {
        // 滚轮：内容里的控件不处理时冒泡到本宿主，这里统一换算成位移。
        if (m.Msg == 0x020A)
        {
            var delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            var lines = SystemInformation.MouseWheelScrollLines;
            var step = lines > 0 ? lines * 24 : 48;
            SetOffset(_offset - (Math.Sign(delta) * step));
            return;
        }

        base.WndProc(ref m);
    }

    /// <inheritdoc />
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.PageDown:
                SetOffset(_offset + Math.Max(48, ClientSize.Height - 48));
                return true;
            case Keys.PageUp:
                SetOffset(_offset - Math.Max(48, ClientSize.Height - 48));
                return true;
            case Keys.Home:
                SetOffset(0);
                return true;
            case Keys.End:
                SetOffset(int.MaxValue);
                return true;
            default:
                return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>设置偏移（自动收敛到有效范围，并同步滚动条与内容位置）。</summary>
    private void SetOffset(int value)
    {
        var max = Math.Max(0, _extent - ClientSize.Height);
        var clamped = Math.Clamp(value, 0, max);
        if (clamped == _offset)
        {
            return;
        }

        _offset = clamped;
        ApplyOffset();
    }

    /// <summary>把当前偏移作用到内容层与滚动条上。</summary>
    private void ApplyOffset()
    {
        _syncing = true;
        try
        {
            _viewport.Top = -_offset;
            if (_bar.Visible)
            {
                var barMax = Math.Max(_bar.Minimum, _bar.Maximum - _bar.LargeChange + 1);
                _bar.Value = Math.Clamp(_offset, _bar.Minimum, barMax);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>同步滚动条可见性、范围与内容层尺寸。</summary>
    private void SyncLayout()
    {
        if (_syncing || _content is null)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            var barWidth = SystemInformation.VerticalScrollBarWidth;

            // 两轮收敛：
            // ① 先按「当前是否显示滚动条」定宽（内容栈是 AutoSize 表格，宽度不定算不出高度）；
            // ② 量出高度、判出是否要滚动条后，再把宽度修正到「让出滚动条」后的值，
            //    否则滚动条会压在内容右侧 17 px 上。
            for (var pass = 0; pass < 2; pass++)
            {
                var passNeedsBar = _extent > ClientSize.Height;
                var passWidth = Math.Max(0, ClientSize.Width - (passNeedsBar ? barWidth : 0));
                if (_viewport.Width != passWidth)
                {
                    _viewport.Width = passWidth;
                }

                var measured = _content.Height;
                if (measured > _extent)
                {
                    // 内容高度取高水位：内容栈在刷新瞬间会短暂偏小，不能让它拉低范围。
                    _extent = measured;
                }
            }

            var needsBar = _extent > ClientSize.Height;
            var viewportWidth = Math.Max(0, ClientSize.Width - (needsBar ? barWidth : 0));

            if (_bar.Visible != needsBar)
            {
                _bar.Visible = needsBar;
            }

            _bar.Bounds = new Rectangle(ClientSize.Width - barWidth, 0, barWidth, ClientSize.Height);
            if (_viewport.Width != viewportWidth || _viewport.Height != _extent || _viewport.Top != -_offset)
            {
                _viewport.SetBounds(0, -_offset, viewportWidth, _extent);
            }

            var barMaximum = Math.Max(0, _extent - 1);
            if (_bar.Maximum != barMaximum)
            {
                _bar.Maximum = barMaximum;
            }

            if (_bar.LargeChange != ClientSize.Height)
            {
                _bar.LargeChange = ClientSize.Height;
            }

            // 范围变化后旧偏移可能越界：收敛一次。
            var max = Math.Max(0, _extent - ClientSize.Height);
            if (_offset > max)
            {
                _offset = max;
            }

            ApplyOffset();
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>按 <see cref="DarkScrollbar"/> 切换滚动条主题变体。</summary>
    public void ApplyScrollbarTheme()
    {
        if (_content is not null && !_content.IsHandleCreated)
        {
            _ = _content.Handle;
        }

        if (!_bar.IsHandleCreated)
        {
            _ = _bar.Handle;
        }

        try
        {
            _ = NativeMethods.SetWindowTheme(
                _bar.Handle,
                DarkScrollbar ? "DarkMode_Explorer" : "Explorer",
                null);
        }
        catch (DllNotFoundException)
        {
            // 无 uxtheme：保持系统默认外观。
        }
    }
}
