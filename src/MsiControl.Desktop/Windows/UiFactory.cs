using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Windows.Controls;

namespace MsiControl.Desktop.Windows;

/// <summary>
/// 界面构造辅助。
/// </summary>
/// <remarks>
/// 集中构造控件的目的：保证所有区块的间距、字号与配色来自同一处，
/// 后续调整视觉风格时不必逐窗口修改。
/// <para>
/// 2026-10-16 界面美化 P0：间距、控件高度与字号阶梯收敛为 <see cref="DesignTokens"/>，
/// 布局代码不再散落魔数；容器统一双缓冲，消除采样刷新时的闪烁。
/// </para>
/// </remarks>
internal static class UiFactory
{
    /// <summary>
    /// 界面字体基准。
    /// </summary>
    /// <remarks>
    /// <see cref="SystemFonts.MessageBoxFont"/> 的可空标注为真（旧系统或异常主题下可能取不到），
    /// 因此回退到 <see cref="Control.DefaultFont"/>，保证任何情况下都能得到非空字体。
    /// 不使用固定字号：基准字体按系统 DPI 度量，子控件继承后可随缩放变化。
    /// </remarks>
    internal static Font BaseFont { get; } = SystemFonts.MessageBoxFont ?? Control.DefaultFont;

    /// <summary>
    /// 按倍率缩放字体，用于字号阶梯。
    /// </summary>
    /// <param name="baseFont">基准字体，通常是控件继承到的字体。</param>
    /// <param name="multiplier">倍率，见 <see cref="DesignTokens.Type"/>。</param>
    /// <param name="style">字形。</param>
    /// <returns>新字体实例；调用方负责在换肤时按继承字体重算。</returns>
    internal static Font Scale(Font baseFont, float multiplier, FontStyle style = FontStyle.Regular)
    {
        ArgumentNullException.ThrowIfNull(baseFont);
        return new Font(baseFont.FontFamily, baseFont.Size * multiplier, style);
    }

    /// <summary>次要文本（口径说明、来源、禁用原因）。</summary>
    /// <remarks>
    /// 刻意不设置正文字号：让标签继承宿主窗体的字体，从而自动跟随系统 DPI 与用户字体设置。
    /// <para>
    /// 权威标记是 <see cref="MutedText.Marker"/>（放在 <see cref="Control.Tag"/>）：
    /// <see cref="ThemeApplier"/> 每次应用主题都会把它换成当前主题的次文本色，
    /// 明暗来回切换不会失效。构造期的 <see cref="SystemColors.GrayText"/> 只是
    /// 首次主题应用前的中性默认色。
    /// </para>
    /// </remarks>
    internal static Label CreateMutedLabel(string text = "")
        => new()
        {
            AutoSize = true,
            Text = text,
            ForeColor = SystemColors.GrayText,
            // 字号也降一档：说明文字与控件正文同号时，一整页「控件 + 说明」会读成一片灰色正文，
            // 控件本身反而找不到。缩小后说明退成注释层，控件才是主体。
            Font = Scale(BaseFont, DesignTokens.Type.Caption),
            Tag = MutedText.Marker
        };

    /// <summary>正文标签。</summary>
    internal static Label CreateBodyLabel(string text = "")
        => new() { AutoSize = true, Text = text };

    /// <summary>区块内的小标题标签。</summary>
    /// <remarks>
    /// 加粗字体基于 <see cref="SystemFonts.MessageBoxFont"/>（按系统 DPI 度量），
    /// 而不是固定字号，以免在高 DPI 下标题比正文还小。
    /// <para>
    /// 字号取 <see cref="DesignTokens.Type.Body"/>：这是<b>卡片内部</b>的分段标题，
    /// 若比卡片标题还大，层级就倒过来了（设置窗口里曾出现「硬件与固件信息」
    /// 比它所在的「设备与诊断」卡片标题更醒目）。层级靠字重与间距，不靠字号。
    /// </para>
    /// </remarks>
    internal static Label CreateSectionLabel(string text)
        => new()
        {
            AutoSize = true,
            Text = text,
            Font = Scale(BaseFont, DesignTokens.Type.Body, FontStyle.Bold),
            Margin = new Padding(DesignTokens.Space.Xs, DesignTokens.Space.Sm, 0, DesignTokens.Space.Xs)
        };

    /// <summary>创建按钮。</summary>
    /// <param name="text">按钮文本。可含 <c>&amp;</c> 助记键前缀。</param>
    /// <param name="width">最小宽度。为 0 时使用自动宽度。</param>
    /// <remarks>
    /// 产出为 <see cref="FocusButton"/>：键盘焦点时在边框内侧画强调色描边，
    /// 弥补 Flat 样式下系统焦点框在深色主题里辨识度不足的问题。
    /// </remarks>
    internal static Button CreateButton(string text, int width = 0)
    {
        var button = new FocusButton
        {
            Text = text,
            // 屏幕阅读器读到的应是纯文本，不能把助记键符号读出来。
            AccessibleName = text.Replace("&", string.Empty, StringComparison.Ordinal),
            AccessibleRole = AccessibleRole.PushButton,
            AutoSize = width <= 0,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Height = DesignTokens.Height.Button,
            // 左右各 4，相邻按钮合计 8，即组内间距令牌。
            Margin = new Padding(DesignTokens.Space.Xs),
            UseVisualStyleBackColor = false
        };

        if (width > 0)
        {
            button.Width = width;
            button.AutoSize = false;
        }

        return button;
    }

    /// <summary>
    /// 创建分段按钮（<see cref="SegmentedGroup"/> 的一员）。
    /// </summary>
    /// <remarks>
    /// 外形与 <see cref="CreateButton"/> 一致，但配色由所属分组统一管理：
    /// 活动 / 非活动 / 禁用三态在组内保持一致，不逐按钮决定。
    /// </remarks>
    internal static Button CreateSegmentButton(string text, int width = 0) => CreateButton(text, width);

    /// <summary>
    /// 创建主按钮（强调色实底）。
    /// </summary>
    /// <param name="text">按钮文本。可含 <c>&amp;</c> 助记键前缀。</param>
    /// <param name="width">最小宽度。为 0 时使用自动宽度。</param>
    /// <remarks>
    /// 同一屏内最多一个：它是这一屏唯一的「下一步」。多个主按钮等于没有主按钮。
    /// </remarks>
    internal static Button CreatePrimaryButton(string text, int width = 0)
    {
        var button = new PrimaryButton
        {
            Text = text,
            AccessibleName = text.Replace("&", string.Empty, StringComparison.Ordinal),
            AccessibleRole = AccessibleRole.PushButton,
            AutoSize = width <= 0,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Height = DesignTokens.Height.ButtonPrimary,
            Margin = new Padding(DesignTokens.Space.Xs),
            UseVisualStyleBackColor = false,
            Padding = new Padding(DesignTokens.Space.Sm, 0, DesignTokens.Space.Sm, 0)
        };

        if (width > 0)
        {
            button.Width = width;
            button.AutoSize = false;
        }

        return button;
    }

    /// <summary>
    /// 创建容器标题行右侧的辅助动作按钮（幽灵样式，小一号）。
    /// </summary>
    /// <param name="text">按钮文本。</param>
    /// <remarks>
    /// 展开 / 收起这类动作服务于所在区块本身，不该与区块内容里的操作抢注意力；
    /// 去掉边框后，标题行剩下的视觉重量刚好落在「区块名」上，层级因此变清楚。
    /// </remarks>
    internal static Button CreateHeaderActionButton(string text)
    {
        var button = new GhostButton
        {
            Text = text,
            AccessibleName = text.Replace("&", string.Empty, StringComparison.Ordinal),
            AccessibleRole = AccessibleRole.PushButton,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            Height = DesignTokens.Height.CollapsedCard - DesignTokens.Space.Sm,
            Margin = Padding.Empty,
            UseVisualStyleBackColor = false,
            Padding = new Padding(DesignTokens.Space.Sm, 0, DesignTokens.Space.Xs, 0)
        };

        return button;
    }

    /// <summary>创建水平流式布局行。</summary>
    /// <param name="wrap">空间不足时是否换行；单行状态栏等场景传 false。</param>
    internal static FlowLayoutPanel CreateRow(bool wrap = true)
        => new BufferedRow
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = wrap,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

    /// <summary>
    /// 创建可滚动内容宿主：窗口 chrome（App bar / 状态栏）固定在宿主的滚动区之外。
    /// </summary>
    /// <remarks>
    /// WinForms 的 <see cref="Form.AutoScroll"/> 与底部停靠控件并存时滚动范围会算错，
    /// 因此滚动收在内层面板里，窗体自身不滚动。
    /// 滚动条主题由 <see cref="MsiControl.Desktop.Windows.Controls.ScrollHostPanel.DarkScrollbar"/> 控制。
    /// </remarks>
    internal static ScrollHostPanel CreateScrollHost()
        => new()
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

    /// <summary>创建垂直堆叠的单列网格。</summary>
    internal static TableLayoutPanel CreateStack()
    {
        var stack = new BufferedStack
        {
            ColumnCount = 1,
            // 关键：AutoSizeMode 必须是 GrowOnly。
            // GrowAndShrink 会在每次内容变化时把自身 Size 先写成 PreferredSize，
            // 而 TableLayoutPanel 在 Percent 列下的 PreferredSize.Width 是「列最小宽度之和」
            // （实测 132）；于是宽度每秒塌缩一次再被 Dock 拉回，卡片随之换行，
            // 内容总高度骤变 → 外层 AutoScroll 按瞬时高度重算范围 → 用户偏移被钳掉
            // （140 → 57），表现为每秒一次的抽动。GrowOnly 只增不减，宽度不再回缩。
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return stack;
    }

    /// <summary>创建等宽多列网格。</summary>
    internal static TableLayoutPanel CreateGrid(int columns)
    {
        var grid = new BufferedGrid
        {
            ColumnCount = columns,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        for (var index = 0; index < columns; index++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        }

        return grid;
    }

    /// <summary>
    /// 把内容包进一张带标题的卡片。
    /// </summary>
    /// <param name="title">卡片标题。</param>
    /// <param name="content">内容控件。</param>
    internal static Card CreateCard(string title, Control content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new Card(title, content) { Dock = DockStyle.Fill };
    }

    /// <summary>
    /// 把内容包进一张带标题与头部动作的卡片。
    /// </summary>
    /// <param name="title">卡片标题。</param>
    /// <param name="content">内容控件。</param>
    /// <param name="headerAction">标题行右侧的动作控件。</param>
    internal static Card CreateCard(string title, Control content, Control? headerAction)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new Card(title, content, headerAction) { Dock = DockStyle.Fill };
    }

    /// <summary>创建卡片内的发丝分隔线（分组切分）。</summary>
    internal static Divider CreateDivider() => new();

    /// <summary>
    /// 创建等分的按钮行：按钮填满整行宽度，视觉上是一组分段控件。
    /// </summary>
    /// <param name="columns">列数，即按钮个数。</param>
    /// <remarks>
    /// 相对流式布局的收益：每行按钮宽度一致、右边界与卡片内边距对齐，
    /// 多行分组之间形成稳定的列网格，不再出现「按钮宽度参差、右侧留白」的观感。
    /// </remarks>
    internal static TableLayoutPanel CreateButtonRow(int columns)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

        var grid = new BufferedGrid
        {
            ColumnCount = columns,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        for (var index = 0; index < columns; index++)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
        }

        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        return grid;
    }

    /// <summary>把按钮放入等分行的指定列（最后一列不保留右间距）。</summary>
    internal static void PlaceButton(TableLayoutPanel row, Button button, int column)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(button);

        var isLast = column == row.ColumnCount - 1;
        button.AutoSize = false;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 0, isLast ? 0 : DesignTokens.Space.Sm, 0);
        row.Controls.Add(button, column, 0);
    }

    /// <summary>
    /// 创建分组说明（次要文本，字号比正文小一档）。
    /// </summary>
    /// <remarks>
    /// 字号也一并下调到 <see cref="DesignTokens.Type.Caption"/>：只靠颜色变灰，
    /// 在深色主题里与正文的灰阶差只有 3 级，读起来仍像正文，区块标题与内容的层级就立不住。
    /// </remarks>
    internal static Label CreateGroupCaption(string text)
    {
        var label = CreateMutedLabel(text);
        label.Font = Scale(BaseFont, DesignTokens.Type.Caption);
        label.Margin = new Padding(DesignTokens.Space.Xs, 0, 0, DesignTokens.Space.Xs);
        return label;
    }

    /// <summary>创建顶部应用栏（Surface 底 + 底部分隔线）。</summary>
    internal static SurfaceBar CreateAppBar()
        => new(lineAtBottom: true) { Height = DesignTokens.Height.AppBar };

    /// <summary>创建底部状态栏（Surface 底 + 顶部分隔线）。</summary>
    internal static SurfaceBar CreateStatusBar()
        => new(lineAtBottom: false)
        {
            Height = DesignTokens.Height.StatusBar,
            Dock = DockStyle.Bottom
        };

    /// <summary>把区块加入堆叠网格。</summary>
    internal static void AddRow(TableLayoutPanel stack, Control row)
    {
        var index = stack.RowCount;
        stack.RowCount = index + 1;
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(row, 0, index);
    }

    /// <summary>
    /// 让一组按钮支持方向键在组内移动焦点。
    /// </summary>
    /// <param name="buttons">按视觉顺序排列的按钮。</param>
    /// <remarks>
    /// 任务书 §10 / design.md §4.1 要求「支持键盘操作」。
    /// 默认情况下方向键在容器内不移动焦点（会被当作对话框导航键或直接忽略），
    /// 因此显式接管：左右/上下在组内循环，到边界回绕。
    /// </remarks>
    internal static void EnableArrowKeyNavigation(IReadOnlyList<Button> buttons)
    {
        if (buttons.Count == 0)
        {
            return;
        }

        for (var index = 0; index < buttons.Count; index++)
        {
            var current = index;
            buttons[current].PreviewKeyDown += (_, e) =>
            {
                switch (e.KeyCode)
                {
                    case Keys.Left:
                    case Keys.Up:
                        buttons[current == 0 ? buttons.Count - 1 : current - 1].Focus();
                        e.IsInputKey = true;
                        break;

                    case Keys.Right:
                    case Keys.Down:
                        buttons[current == buttons.Count - 1 ? 0 : current + 1].Focus();
                        e.IsInputKey = true;
                        break;
                }
            };
        }
    }

    /// <summary>
    /// 设计令牌：间距、控件高度与字号阶梯。
    /// </summary>
    /// <remarks>
    /// 唯一数值来源：布局代码只允许引用本类常量，不再写裸数字。
    /// 间距为 4px 基网；字号为相对 <see cref="BaseFont"/> 的倍率，随系统 DPI 换算。
    /// </remarks>
    internal static class DesignTokens
    {
        /// <summary>
        /// 间距标尺（4px 基网）。
        /// </summary>
        /// <remarks>
        /// 只用 4 的倍数：WinForms 的 <see cref="Padding"/> 与 <see cref="Margin"/> 不参与 DPI 换算，
        /// 非 4 倍数的值在 125% / 150% 缩放下会累积出 1–2px 的错位，多列网格尤其明显。
        /// </remarks>
        internal static class Space
        {
            /// <summary>发丝间距：徽标内垂直留白。</summary>
            internal const int Hair = 2;

            /// <summary>图标与文字、控件外边距（左右各 4，相邻合计 8）。</summary>
            internal const int Xs = 4;

            /// <summary>组内间距、区块标题与内容间距。</summary>
            internal const int Sm = 8;

            /// <summary>分组分隔线上下的留白。</summary>
            internal const int Divider = 6;

            /// <summary>卡片间距（沟堑）、双列沟壑的一半。</summary>
            internal const int Md = 12;

            /// <summary>卡片内边距。</summary>
            internal const int Lg = 16;

            /// <summary>页面边距、区块间距。</summary>
            internal const int Xl = 24;

            /// <summary>页面级大留白（首屏上下呼吸区）。</summary>
            internal const int Xxl = 32;
        }

        /// <summary>
        /// 圆角标尺（px，逻辑像素）。
        /// </summary>
        /// <remarks>
        /// 三档就够：容器（卡片）&gt; 内容块（瓷砖）&gt; 控件（按钮、输入框）。
        /// 全界面共用同一组半径，是「同一套设计语言」最容易被感知到的信号之一。
        /// </remarks>
        internal static class Radius
        {
            /// <summary>卡片容器。</summary>
            internal const int Card = 12;

            /// <summary>读数瓷砖、输入容器。</summary>
            internal const int Tile = 8;

            /// <summary>按钮、下拉框、状态徽标。</summary>
            internal const int Control = 6;

            /// <summary>胶囊（状态标签；实际取高度一半）。</summary>
            internal const int Pill = 999;
        }

        /// <summary>
        /// 控件高度规范。
        /// </summary>
        /// <remarks>
        /// 2026-10-31 统一抬高一档：28px 在 125% DPI 下实际只有 35 物理像素，
        /// 中文按钮文字（含助记键）显得局促；30/34 的按钮阶梯与 30 的输入框同高，
        /// 同一行内「输入框 + 按钮」不再高低不齐。
        /// </remarks>
        internal static class Height
        {
            /// <summary>次要按钮。</summary>
            internal const int Button = 30;

            /// <summary>主要按钮。</summary>
            internal const int ButtonPrimary = 34;

            /// <summary>输入框。</summary>
            internal const int Input = 30;

            /// <summary>读数瓷砖最小高度。</summary>
            internal const int Tile = 92;

            /// <summary>应用栏高度（容纳「标题 + 设备」两行文本）。</summary>
            internal const int AppBar = 60;

            /// <summary>状态带高度（应用栏下方，承载状态徽标）。</summary>
            internal const int StatusBand = 48;

            /// <summary>底部状态栏高度。</summary>
            internal const int StatusBar = 30;

            /// <summary>折叠卡行高。</summary>
            internal const int CollapsedCard = 36;
        }

        /// <summary>字号阶梯倍率（相对基准字体）。</summary>
        internal static class Type
        {
            /// <summary>数值（瓷砖大数）。</summary>
            internal const float Display = 1.85f;

            /// <summary>区块标题。</summary>
            internal const float Title = 1.14f;

            /// <summary>正文。</summary>
            internal const float Body = 1f;

            /// <summary>说明文字。</summary>
            internal const float Caption = 0.88f;

            /// <summary>极小号（单位、脚注、徽标）。</summary>
            internal const float Micro = 0.8f;
        }
    }

    /// <summary>双缓冲垂直堆叠面板：消除采样刷新时的闪烁。</summary>
    internal sealed class BufferedStack : TableLayoutPanel
    {
        /// <summary>构造双缓冲堆叠面板。</summary>
        internal BufferedStack() => DoubleBuffered = true;
    }

    /// <summary>双缓冲流式面板：水平行与垂直列共用。</summary>
    internal sealed class BufferedRow : FlowLayoutPanel
    {
        /// <summary>构造双缓冲流式面板。</summary>
        internal BufferedRow() => DoubleBuffered = true;
    }

    /// <summary>双缓冲多列网格：读数瓷砖容器。</summary>
    internal sealed class BufferedGrid : TableLayoutPanel
    {
        /// <summary>构造双缓冲网格。</summary>
        internal BufferedGrid() => DoubleBuffered = true;
    }

    /// <summary>双缓冲面板：滚动内容宿主。</summary>
    internal sealed class BufferedPanel : Panel
    {
        /// <summary>构造双缓冲面板。</summary>
        internal BufferedPanel() => DoubleBuffered = true;
    }
}