using System.Globalization;
using MsiControl.Application.Control;
using System.Diagnostics;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Resources;
using MsiControl.Desktop.Shell;
using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Windows.Controls;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Windows;

/// <summary>
/// 正常窗口。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §10 / design.md §4.2：默认启动窗口，包含**全部功能入口**，
/// 参考 G-Helper 的紧凑布局；风扇曲线与性能详细参数放在**按需展开**的区域，
/// 不使用宽大侧边栏与多层导航。
/// </para>
/// <para>
/// <b>本窗口不接触硬件：</b>只订阅 <see cref="ShellContext.Snapshots"/>，
/// 只通过 <see cref="ShellContext.Control"/> 发出请求（提示词第 11 条）。
/// </para>
/// <para>
/// 所有入口的启用状态一律来自 <see cref="ShellContext.Capabilities"/>。
/// 按用户要求（2026-10-18），主页面只保留核心内容，不铺陈说明性小字：
/// 不可用入口的禁用原因集中在「设置与诊断」的能力清单中完整展示，
/// 入口本身仍带屏幕阅读器可读的 <see cref="Control.AccessibleDescription"/>。
/// </para>
/// <para>
/// 键盘操作：Tab 顺序遍历、按钮助记键（Alt+字母/数字）、方向键在按钮组内循环、
/// <c>Esc</c> 隐藏到托盘、<c>F5</c> 立即重绘（任务书 §10「支持键盘操作」）。
/// </para>
/// </remarks>
public sealed class MainForm : Form, IThemeAware
{
    private const string TileCpuTemp = "cpu.temp";
    private const string TileCpuFreq = "cpu.freq";
    private const string TileCpuFan = "cpu.fan";
    private const string TileCpuPower = "cpu.power";
    private const string TileGpuTemp = "gpu.temp";
    private const string TileGpuFreq = "gpu.freq";
    private const string TileGpuFan = "gpu.fan";
    private const string TileGpuPower = "gpu.power";

    private readonly ShellContext _context;
    private readonly TableLayoutPanel _stack = UiFactory.CreateStack();
    private readonly StatusPill _pillStage = new("实际性能模式");
    private readonly StatusPill _pillGraphics = new("实际显卡模式");

    private readonly Dictionary<string, MetricTile> _tiles = new(StringComparer.Ordinal);
    private readonly List<Button> _stageButtonOrder = new();
    private readonly Dictionary<PerformanceStage, Button> _stageButtons = new();
    private readonly List<Button> _graphicsButtonOrder = new();
    private readonly Dictionary<GraphicsMode, Button> _graphicsButtons = new();
    private readonly SegmentedGroup _stageGroup = new();
    private readonly SegmentedGroup _graphicsGroup = new();
    private readonly Label _brandMark = new() { Width = 20, Height = 20, AutoSize = false };
    private readonly Label _appBarTitle = new() { AutoSize = true };
    private readonly Label _statusDot = new() { Width = 8, Height = 8, AutoSize = false };
    private readonly Label _lblCapability = UiFactory.CreateMutedLabel();

    /// <summary>性能模式分段轨道；同时提供槽底颜色给 <see cref="SegmentedGroup"/>。</summary>
    private SegmentedRail? _stageRail;

    /// <summary>显卡模式分段轨道。</summary>
    private SegmentedRail? _graphicsRail;

    /// <summary>「详细编辑」两列网格：窗口变窄时切换为上下堆叠。</summary>
    private TableLayoutPanel? _editorsGrid;

    /// <summary>「详细编辑」左列（性能详细参数）。</summary>
    private Control? _editorsLeftColumn;

    /// <summary>当前「详细编辑」是否处于堆叠布局，避免每次 Resize 重复搬运控件。</summary>
    private bool _editorsStacked;

    /// <summary>窄于该宽度时「详细编辑」由左右两列改为上下堆叠。</summary>
    /// <remarks>
    /// 判据是**内容区宽度**而非窗口宽度：右侧曲线面板固定 440 逻辑像素，
    /// 两列按比例分（左 38% / 右 62%），窗口最小宽度 880 下左列仍有约 320、右列约 520，
    /// 五档瓦数输入框与七列曲线滑块都放得下。阈值因此下调到 720，保证**在允许的最小窗口
    /// 里也是并排**——此前阈值 860 高于最小窗口的内容宽度，等于一进程序就是上下堆叠，
    /// 风扇曲线被挤到功耗表下面（叶哥 2026-10-31 要求改为并排）。
    /// </remarks>
    private const int EditorsStackThreshold = 720;

    private readonly Button _btnFanDisplay = UiFactory.CreateButton("风扇显示：百分比(&R)");
    // 折叠动作用幽灵按钮：它服务于卡片本身，不该与卡片内容里的操作抢重量。
    private readonly Button _btnToggleEditors = UiFactory.CreateHeaderActionButton("展开详细编辑(&T)");
    private readonly Button _btnToggleExtended = UiFactory.CreateHeaderActionButton("展开扩展传感器(&E)");

    /// <summary>风扇曲线面板（详细编辑卡右列）：仅手动曲线，按六个槽位保存。</summary>
    private readonly FanCurvePanel _fanPanel;

    private readonly Label _lblResult = UiFactory.CreateMutedLabel();

    /// <summary>最近一次写进结果行的文案与色调（同值不重写，避免无谓重排）。</summary>
    private string _resultText = string.Empty;
    private MetricTone _resultTone = MetricTone.Muted;
    private readonly Label _lblSnapshotInfo = UiFactory.CreateMutedLabel();
    private readonly Label _lblDevice = UiFactory.CreateMutedLabel();

    // 双缓冲两列网格（名称 / 数值）：按采样动态增行时不闪烁，且键值左右分离便于扫读。
    private readonly TableLayoutPanel _extendedGrid = CreateExtendedGrid();

    private readonly TableLayoutPanel _editorsPanel = UiFactory.CreateStack();
    private readonly ScrollHostPanel _contentHost = UiFactory.CreateScrollHost();

    private readonly Dictionary<string, (Label NameLabel, Label ValueLabel)> _extendedRows = new(StringComparer.Ordinal);

    private ThemePalette _palette = ThemePalette.Light;
    /// <summary>
    /// 重启命令参数：<c>/r</c> 重启、<c>/t 5</c> 留 5 秒给本进程正常退出（停采样、落日志）。
    /// </summary>
    /// <remarks>只在这一处使用；「立即重启」的唯一执行点，便于审计（任务书 §9.1 的自动化禁止依然成立）。</remarks>
    internal const string RestartArguments = "/r /t 5";

    private bool _allowClose;
    private bool _extendedExpanded;
    private bool _editorsExpanded = true;

    /// <summary>构造正常窗口。</summary>
    public MainForm(ShellContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        Text = "MSI-Control";
        Icon = AppIcon.CreateWindowIcon();
        AccessibleName = "MSI-Control 主窗口";
        AccessibleDescription =
            "显示 CPU 与 GPU 实时读数、性能模式、显卡模式与风扇控制入口。" +
            "未取得实证的能力入口处于禁用状态；禁用原因见「设置与诊断」的能力清单。";
        StartPosition = FormStartPosition.CenterScreen;
        // 最小宽度按「详细编辑」并排所需的内容宽度反推（约 860 + 边距），
        // 再窄下去会触发堆叠布局——仍然可用，只是需要多滚一屏。
        MinimumSize = new Size(880, 680);
        // 默认「详细编辑」展开、扩展传感器收起：一屏内呈现读数 + 控制 + 曲线入口 + 性能表，
        // 更高的内容由内层滚动宿主兜底。
        Size = new Size(980, 1020);
        AutoScaleMode = AutoScaleMode.Dpi;
        // 以系统按 DPI 度量出的字体为基准，子控件继承后即可随系统缩放。
        Font = UiFactory.BaseFont;
        // 滚动收在内层宿主（BuildLayout 的 CreateScrollHost）：窗体自身挂着
        // 顶部应用栏与底部状态栏，Form.AutoScroll 与底部停靠控件并存会算错滚动范围。
        AutoScroll = false;
        KeyPreview = true;

        // 面板先于布局创建：BuildLayout 会把它放进详细编辑卡的右列。
        _fanPanel = new FanCurvePanel(_context, (message, tone) => SetResult(message, tone));

        BuildLayout();
        ApplyCapabilityGating();
        SubscribeSnapshots();

        // 初始状态明确标注「未适配」，等待宿主（任务 07）注入真实状态后更新，
        // 不使用任何推测值填充（提示词第 6、7 条）。
        _pillStage.SetValue("未适配", MetricTone.Muted);
        _pillGraphics.SetValue("未适配", MetricTone.Muted);
        FormClosing += OnFormClosing;

        // 首次显示把滚动位置复位到顶部：内容比窗口高时，WinForms 会把获得焦点的
        // 子控件滚进视野，导致标题行与监控卡上半被顶出可视区（实测首屏即如此）。
        Shown += (_, _) => BeginInvoke(new Action(_contentHost.ScrollToTop));
    }

    /// <summary>用户请求完全退出。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>用户请求切换到迷你窗口。</summary>
    public event EventHandler? MiniRequested;

    /// <summary>用户请求打开设置与诊断。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>允许本次关闭。由宿主在退出流程中调用。</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>取得当前窗口位置与尺寸。</summary>
    public WindowPlacement CurrentPlacement() => new(
        Left,
        Top,
        Width,
        Height,
        WindowState == FormWindowState.Maximized);

    /// <summary>应用记录的位置与尺寸。</summary>
    public void ApplyPlacement(WindowPlacement placement, Rectangle workingArea)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var clamped = placement.ClampTo(workingArea.Left, workingArea.Top, workingArea.Width, workingArea.Height);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(clamped.X, clamped.Y);
        Size = new Size(clamped.Width, clamped.Height);

        if (clamped.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    /// <summary>用最近一次采样重绘界面（设置变化或按下 F5 时调用）。</summary>
    public void RefreshFromLatest()
    {
        _btnFanDisplay.Text = _context.Settings.FanDisplay == FanDisplayMode.Rpm
            ? "风扇显示：RPM(&R)"
            : "风扇显示：百分比(&R)";

        ApplySnapshot(_context.Snapshots.Latest);
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        ThemeApplier.Apply(this, palette);
        _lblDevice.ForeColor = palette.ForeMuted;
        _lblSnapshotInfo.ForeColor = palette.ForeMuted;

        // 结果行的颜色是语义色（成功绿 / 警告黄 / 失败红），主题遍历会把它刷成统一的次要灰，
        // 因此换肤后按最近一次的语调重刷一次；否则切一次主题，提示就永久变成灰色。
        if (!string.IsNullOrEmpty(_resultText))
        {
            _lblResult.ForeColor = MetricTile.ToneColor(palette, _resultTone);
        }

        // 应用栏与状态栏的自绘元素（普通 Label 不带主题标记，在此显式着色）。
        // 图标自带深色圆角瓦片，容器保持透明才能露出圆角（刷成强调色会变成方块）。
        _brandMark.BackColor = Color.Transparent;
        _appBarTitle.ForeColor = palette.Fore;
        _statusDot.BackColor = palette.Ok;

        // 分段组需要在主题遍历之后重刷：活动段样式必须压过通用按钮样式。
        // 轨道底色先取给分组，非活动段才能与槽壁融成一片（否则每段仍带独立描边）。
        _stageGroup.TrackColor = _stageRail?.TrackColor;
        _graphicsGroup.TrackColor = _graphicsRail?.TrackColor;
        _stageGroup.ApplyPalette(palette);
        _graphicsGroup.ApplyPalette(palette);

        // 滚动条跟随明暗主题（浅色用系统默认，深色切 DarkMode_Explorer）。
        _contentHost.DarkScrollbar = palette.Mode == ThemeMode.Dark;
        _contentHost.ApplyScrollbarTheme();

        // 曲线面板的主按钮要在整树着色**之后**重刷强调色，否则会被通用按钮样式覆盖。
        _fanPanel.ApplyPalette(palette);
    }

    private void BuildLayout()
    {
        var tabIndex = 0;

        _lblDevice.Text = BuildDeviceText();

        // ① 应用栏：品牌标 + 标题 + 设备名 + 窗口动作（助记键保留在按钮文本中）。
        var appBar = BuildAppBar(ref tabIndex);

        // ② 状态带：实际性能 / 实际显卡 / 整机功耗 + 风扇显示切换（不随内容滚动）。
        // 独立成一条 Surface 带，与内容画布在视觉上分层，不再是散落在画布上的三颗徽标。
        var statusBand = BuildStatusBand(ref tabIndex);

        // ③ 内容区：双列监控卡 → 控制卡 → 详细编辑卡 → 扩展传感器卡 → 操作结果。
        // 页面边距 24（原 16）：卡片现在有 12px 圆角与描边，贴边的观感会显得「挤在框里」；
        // 24 的边距让每张卡四周都有均匀的呼吸区，也让应用栏、内容、状态栏三者的左边界对齐。
        _stack.Padding = new Padding(
            UiFactory.DesignTokens.Space.Xl,
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Xl,
            UiFactory.DesignTokens.Space.Lg);

        UiFactory.AddRow(_stack, BuildMonitorSection());
        UiFactory.AddRow(_stack, BuildControlCard(ref tabIndex));
        // 操作结果紧贴控制卡：反馈出现在发起动作的地方，而不是页面最底部
        // （那里在默认展开状态下已经滚出可视区）。无结果时整行隐藏，不占空白。
        UiFactory.AddRow(_stack, BuildResultRow());
        // 详细编辑（常用配置）在前，扩展传感器（次要明细、且默认收起）在后
        // （叶哥 2026-10-28 要求：扩展传感器移到详细编辑下方）。
        UiFactory.AddRow(_stack, BuildEditorsSection(ref tabIndex));
        UiFactory.AddRow(_stack, BuildExtendedSection(ref tabIndex));

        // ④ 底部状态栏：能力统计 + 采样信息。
        var statusBar = BuildStatusBar();

        // 停靠顺序：滚动宿主最先加入（最内层 Fill），其后状态带、应用栏，状态栏最后（最外底部）。
        _contentHost.SetContent(_stack);
        Controls.Add(_contentHost);
        Controls.Add(statusBand);
        Controls.Add(appBar);
        Controls.Add(statusBar);

        UpdateResultLabelWidth();
    }

    /// <summary>构建状态带：两颗状态胶囊 + 风扇显示切换。</summary>
    private SurfaceBar BuildStatusBand(ref int tabIndex)
    {
        var band = new SurfaceBar(lineAtBottom: true)
        {
            Height = UiFactory.DesignTokens.Height.StatusBand,
            Padding = new Padding(
                UiFactory.DesignTokens.Space.Xl,
                0,
                UiFactory.DesignTokens.Space.Xl,
                0)
        };

        // 两列：左侧状态胶囊（只读信息），右侧风扇显示切换（唯一可操作项）。
        // 原先两者挤在一条流式行里，「风扇显示」跟着胶囊的宽度漂移，窗口一变宽就悬在中间。
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, 13, 0, 0),
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        var pills = UiFactory.CreateRow(wrap: false);
        pills.Margin = Padding.Empty;
        pills.Controls.Add(_pillStage);
        pills.Controls.Add(_pillGraphics);

        _btnFanDisplay.TabIndex = tabIndex++;
        _btnFanDisplay.AccessibleDescription = "在风扇百分比与 RPM 之间切换显示；无有效校准时始终回落显示 RPM。";
        _btnFanDisplay.Height = UiFactory.DesignTokens.Height.Button;
        _btnFanDisplay.Margin = new Padding(UiFactory.DesignTokens.Space.Md, 4, 0, 0);
        _btnFanDisplay.Click += (_, _) => ToggleFanDisplay();

        layout.Controls.Add(pills, 0, 0);
        layout.Controls.Add(_btnFanDisplay, 1, 0);
        band.Controls.Add(layout);
        return band;
    }

    /// <summary>构建操作结果行：定宽换行，长文案不会把内容区撑出横向滚动条。</summary>
    private Control BuildResultRow()
    {
        var row = UiFactory.CreateStack();
        // AutoSize + 左右锚定 + MaximumSize 宽度 = 自动换行的定宽文本。
        // 不能用 Dock=Fill：AutoSize 行高取自子控件首选高度，而 Dock=Fill 的高度又取自行高，
        // 二者互相等待会把标签压成 1px（实机诊断：文本存在但高度为 1）。
        _lblResult.AutoSize = true;
        _lblResult.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _lblResult.Visible = false;
        _lblResult.Margin = new Padding(
            UiFactory.DesignTokens.Space.Xs,
            UiFactory.DesignTokens.Space.Xs,
            UiFactory.DesignTokens.Space.Xs,
            UiFactory.DesignTokens.Space.Sm);
        UiFactory.AddRow(row, _lblResult);
        return row;
    }

    /// <summary>按内容区可用宽度限制结果文案宽度（Label 定宽后自动换行）。</summary>
    private void UpdateResultLabelWidth()
    {
        var available = _contentHost.ContentWidth
            - UiFactory.DesignTokens.Space.Lg * 2
            - UiFactory.DesignTokens.Space.Sm;

        if (available > 160)
        {
            _lblResult.MaximumSize = new Size(available, 0);
        }
    }

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateResultLabelWidth();

        // 用**内容区**宽度判断，而不是窗口宽度：应用栏、状态带、状态栏不参与内容排版，
        // 按窗口宽度算会让「刚好够宽」的窗口提前堆叠。
        var contentWidth = _contentHost.ContentWidth - (UiFactory.DesignTokens.Space.Xl * 2);
        if (contentWidth > 0)
        {
            ApplyEditorsLayout(stacked: contentWidth < EditorsStackThreshold);
        }
    }

    /// <summary>
    /// 在「左右两列」与「上下堆叠」之间切换「详细编辑」的排布。
    /// </summary>
    /// <param name="stacked">true 为上下堆叠（窄窗口），false 为左右并排。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么重挂控件而不是重建控件：</b>左列里是性能瓦数的 <see cref="TextBox"/>，
    /// 用户可能正在输入未提交的值；重建控件会把草稿直接丢掉。这里只做「摘下来 → 重新配置行列 → 放回去」，
    /// 控件实例、事件订阅与输入内容全部保留。
    /// </para>
    /// <para>
    /// 右列宽度固定 440 逻辑像素：曲线区是七列竖向滑块（每列约 62），再窄下去
    /// 滑块与温度输入框会被卡片边界裁切（首版实测）。因此窄窗口的正确解法是换行，不是压缩。
    /// </para>
    /// </remarks>
    private void ApplyEditorsLayout(bool stacked)
    {
        if (_editorsGrid is null || _editorsLeftColumn is null)
        {
            return;
        }

        if (_editorsGrid.Controls.Count == 2 && _editorsStacked == stacked)
        {
            return;
        }

        _editorsStacked = stacked;
        _editorsGrid.SuspendLayout();
        try
        {
            // 先摘下再改行列：TableLayoutPanel 在「列数缩小但仍有子控件占着该列」时
            // 会抛索引异常，直接改 ColumnCount 是不安全的。
            _editorsGrid.Controls.Clear();
            _editorsGrid.ColumnStyles.Clear();
            _editorsGrid.RowStyles.Clear();

            if (stacked)
            {
                _editorsGrid.ColumnCount = 1;
                _editorsGrid.RowCount = 2;
                _editorsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
                _editorsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _editorsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                _editorsLeftColumn.Margin = new Padding(0, 0, 0, UiFactory.DesignTokens.Space.Md);
                _fanPanel.Margin = Padding.Empty;

                _editorsGrid.Controls.Add(_editorsLeftColumn, 0, 0);
                _editorsGrid.Controls.Add(_fanPanel, 0, 1);
            }
            else
            {
                _editorsGrid.ColumnCount = 2;
                _editorsGrid.RowCount = 1;
                // 定宽 440 在窄窗口会把左列的瓦数输入框压成一条缝；改按比例分，
                // 窗口变宽时曲线区同步变宽，七列滑块不会白白留在大片空白里。
                _editorsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38f));
                _editorsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62f));
                _editorsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                // 双列沟堑：左列右侧留 12，右列不再留白，右边界与卡片内边距对齐。
                _editorsLeftColumn.Margin = new Padding(0, 0, UiFactory.DesignTokens.Space.Md, 0);
                _fanPanel.Margin = Padding.Empty;

                _editorsGrid.Controls.Add(_editorsLeftColumn, 0, 0);
                _editorsGrid.Controls.Add(_fanPanel, 1, 0);
            }
        }
        finally
        {
            _editorsGrid.ResumeLayout(performLayout: true);
        }
    }

    /// <summary>构建顶部应用栏。</summary>
    private SurfaceBar BuildAppBar(ref int tabIndex)
    {
        var bar = UiFactory.CreateAppBar();
        bar.Padding = new Padding(
            UiFactory.DesignTokens.Space.Xl,
            0,
            UiFactory.DesignTokens.Space.Xl,
            0);

        // 品牌标直接用应用图标位图：标题栏、任务栏与界面标一致，不再是一个孤立的字母块。
        _brandMark.Size = new Size(26, 26);
        _brandMark.Image = AppIcon.CreateBitmap(_brandMark.Width);
        _brandMark.ImageAlign = ContentAlignment.MiddleCenter;
        _brandMark.BackColor = Color.Transparent;
        // 只锚左侧 → 在标题两行之间垂直居中。
        _brandMark.Anchor = AnchorStyles.Left;
        _brandMark.Margin = new Padding(0, 0, UiFactory.DesignTokens.Space.Md, 0);

        _appBarTitle.Text = "MSI-Control";
        _appBarTitle.Font = UiFactory.Scale(
            UiFactory.BaseFont,
            UiFactory.DesignTokens.Type.Title,
            FontStyle.Bold);
        _appBarTitle.Margin = Padding.Empty;

        // 设备标识作为标题下的第二行：放在同一列里可获得整行宽度，
        // 窗口变窄时由省略号收尾，而不是被右侧动作按钮压掉半个字。
        _lblDevice.AutoSize = true;
        _lblDevice.AutoEllipsis = true;
        _lblDevice.Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Caption);
        _lblDevice.Margin = new Padding(0, 1, 0, 0);

        var titleStack = UiFactory.CreateStack();
        titleStack.Margin = Padding.Empty;
        UiFactory.AddRow(titleStack, _appBarTitle);
        UiFactory.AddRow(titleStack, _lblDevice);

        // 左组：品牌标 + 「标题 / 设备」两行文本。
        var brandRow = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        brandRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        brandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        brandRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        brandRow.Controls.Add(_brandMark, 0, 0);
        brandRow.Controls.Add(titleStack, 1, 0);

        var actions = UiFactory.CreateRow(wrap: false);

        // 应用栏按钮的高度由 60px 条高决定，垂直居中交给 Anchor：
        // 手动加 Margin.Top 会与「居中」叠加，在 125% DPI 下偏下 7~8px（首版实测）。
        const int actionTop = 0;

        var btnMini = UiFactory.CreateButton("迷你窗口(&M)");
        btnMini.TabIndex = tabIndex++;
        btnMini.Height = UiFactory.DesignTokens.Height.Button;
        btnMini.AccessibleDescription = "切换到迷你窗口；两种窗口共用同一份采样与控制核心。";
        btnMini.Margin = new Padding(0, actionTop, UiFactory.DesignTokens.Space.Sm, 0);
        btnMini.Click += (_, _) => MiniRequested?.Invoke(this, EventArgs.Empty);

        var btnSettings = UiFactory.CreateButton("设置与诊断(&S)");
        btnSettings.TabIndex = tabIndex++;
        btnSettings.Height = UiFactory.DesignTokens.Height.Button;
        btnSettings.AccessibleDescription = "打开设置与诊断，可查看设备信息与全部能力的验证状态。";
        btnSettings.Margin = new Padding(0, actionTop, UiFactory.DesignTokens.Space.Sm, 0);
        btnSettings.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        // 退出是破坏性动作（结束进程、停软件调速）：用幽灵样式降权，
        // 不与「迷你窗口 / 设置」并列成同一种「可以随便点」的按钮。
        var btnExit = UiFactory.CreateHeaderActionButton("退出程序(&X)");
        btnExit.TabIndex = tabIndex++;
        btnExit.Height = UiFactory.DesignTokens.Height.Button;
        btnExit.AccessibleDescription = "完全退出：停止采样与软件调速。仅隐藏界面请改用「关闭到托盘」。";
        btnExit.Margin = new Padding(0, actionTop, 0, 0);
        btnExit.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        actions.Controls.Add(btnMini);
        actions.Controls.Add(btnSettings);
        actions.Controls.Add(btnExit);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        // 左右两组的垂直居中：Anchor 只给水平方向，垂直方向自动居中。
        brandRow.Anchor = AnchorStyles.Left;
        actions.Anchor = AnchorStyles.Right;
        layout.Controls.Add(brandRow, 0, 0);
        layout.Controls.Add(actions, 2, 0);
        bar.Controls.Add(layout);

        return bar;
    }

    /// <summary>构建底部状态栏。</summary>
    private SurfaceBar BuildStatusBar()
    {
        var bar = UiFactory.CreateStatusBar();
        bar.Padding = new Padding(
            UiFactory.DesignTokens.Space.Xl,
            0,
            UiFactory.DesignTokens.Space.Xl,
            0);

        // 30px 高的状态栏里，文本行高约 17 → 上留白 6；状态点 8px → 上留白 11，
        // 两者视觉基线因此对齐。
        _statusDot.BackColor = ThemePalette.Light.Ok;
        _statusDot.Margin = new Padding(0, 11, 6, 0);

        _lblCapability.Text = BuildCapabilityText();
        _lblCapability.Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Caption);
        _lblCapability.Margin = new Padding(0, 6, UiFactory.DesignTokens.Space.Lg, 0);

        _lblSnapshotInfo.Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Caption);
        _lblSnapshotInfo.Margin = new Padding(0, 6, 0, 0);

        var row = UiFactory.CreateRow(wrap: false);
        row.Controls.Add(_statusDot);
        row.Controls.Add(_lblCapability);
        row.Controls.Add(_lblSnapshotInfo);
        bar.Controls.Add(row);

        return bar;
    }

    /// <summary>生成能力统计文本（只读汇总，不涉及任何写入）。</summary>
    private string BuildCapabilityText()
    {
        var verified = _context.Capabilities.All.Count(status => status.Level == CapabilityLevel.Verified);
        return $"能力 {verified}/{_context.Capabilities.Count} 已确证";
    }

    private string BuildDeviceText()
    {
        var device = _context.Device;
        var text = $"设备：{device.Model}（主板 {device.BoardCode}）";
        return device.IsIdentified
            ? text
            : text + "　·　机型未识别，全部写入入口按「待验证」禁用";
    }

    /// <summary>构建双列监控区：CPU 卡与 GPU 卡并排。</summary>
    private Control BuildMonitorSection()
    {
        var grid = UiFactory.CreateGrid(2);

        var cpuCard = BuildCpuCard();
        var gpuCard = BuildGpuCard();

        // 双列沟堑：左右各 6，合计 12（Space.Md）。
        cpuCard.Margin = new Padding(
            0,
            UiFactory.DesignTokens.Space.Xs,
            UiFactory.DesignTokens.Space.Md / 2,
            UiFactory.DesignTokens.Space.Sm);
        gpuCard.Margin = new Padding(
            UiFactory.DesignTokens.Space.Md / 2,
            UiFactory.DesignTokens.Space.Xs,
            0,
            UiFactory.DesignTokens.Space.Sm);

        grid.Controls.Add(cpuCard, 0, 0);
        grid.Controls.Add(gpuCard, 1, 0);
        return grid;
    }

    private Card BuildCpuCard()
    {
        var grid = UiFactory.CreateGrid(2);
        grid.Controls.Add(CreateTile(TileCpuTemp, "温度"), 0, 0);
        grid.Controls.Add(CreateTile(TileCpuFreq, "频率"), 1, 0);
        grid.Controls.Add(CreateTile(TileCpuFan, "风扇"), 0, 1);
        grid.Controls.Add(CreateTile(TileCpuPower, "封装功耗"), 1, 1);
        return UiFactory.CreateCard("CPU", grid);
    }

    private Card BuildGpuCard()
    {
        var grid = UiFactory.CreateGrid(2);
        grid.Controls.Add(CreateTile(TileGpuTemp, "温度"), 0, 0);
        grid.Controls.Add(CreateTile(TileGpuFreq, "核心频率"), 1, 0);
        grid.Controls.Add(CreateTile(TileGpuFan, "风扇"), 0, 1);
        grid.Controls.Add(CreateTile(TileGpuPower, "功耗"), 1, 1);
        return UiFactory.CreateCard("GPU（默认独显）", grid);
    }

    private MetricTile CreateTile(string key, string title)
    {
        var tile = new MetricTile(title) { Dock = DockStyle.Fill };
        _tiles[key] = tile;
        return tile;
    }

    private Card BuildControlCard(ref int tabIndex)
    {
        var stack = UiFactory.CreateStack();

        // ── 性能模式：五档等分一行 ─────────────────────────────────────
        // 数字助记键便于纯键盘操作；分段组样式由 SegmentedGroup 统一管理。
        var stages = Enum.GetValues<PerformanceStage>();
        var stageRail = new SegmentedRail(stages.Length);
        _stageRail = stageRail;
        var stageIndex = 1;
        foreach (var stage in stages)
        {
            var button = UiFactory.CreateSegmentButton($"{ModeFormatter.StageText(stage)}(&{stageIndex})");
            button.TabIndex = tabIndex++;
            button.AccessibleDescription = $"请求切换到{ModeFormatter.StageText(stage)}模式；未取得写入证据时不可用。";
            button.Click += (_, _) => _ = RequestPerformanceAsync(stage);
            _stageButtons[stage] = button;
            _stageButtonOrder.Add(button);
            _stageGroup.Add(button);
            stageRail.Place(button, stageIndex - 1);
            stageIndex++;
        }

        UiFactory.EnableArrowKeyNavigation(_stageButtonOrder);

        UiFactory.AddRow(stack, UiFactory.CreateGroupCaption("性能模式"));
        UiFactory.AddRow(stack, stageRail);
        UiFactory.AddRow(stack, UiFactory.CreateDivider());

        // ── 显卡模式：三档等分一行 ─────────────────────────────────────
        var graphicsModes = Enum.GetValues<GraphicsMode>();
        var graphicsRow = new SegmentedRail(graphicsModes.Length);
        _graphicsRail = graphicsRow;
        var graphicsIndex = 0;
        foreach (var mode in graphicsModes)
        {
            var mnemonic = mode switch
            {
                GraphicsMode.Integrated => 'I',
                GraphicsMode.Hybrid => 'H',
                _ => 'D'
            };
            var button = UiFactory.CreateSegmentButton($"{ModeFormatter.GraphicsText(mode)}(&{mnemonic})");
            button.TabIndex = tabIndex++;
            button.AccessibleDescription = $"请求切换到{ModeFormatter.GraphicsText(mode)}；切换需重启才生效，成功后程序会询问是否立即重启。";
            button.Click += (_, _) => _ = RequestGraphicsAsync(mode);
            _graphicsButtons[mode] = button;
            _graphicsButtonOrder.Add(button);
            _graphicsGroup.Add(button);
            graphicsRow.Place(button, graphicsIndex++);
        }

        UiFactory.EnableArrowKeyNavigation(_graphicsButtonOrder);

        UiFactory.AddRow(stack, UiFactory.CreateGroupCaption("显卡模式（改动需重启生效，切换成功后会询问是否重启）"));
        UiFactory.AddRow(stack, graphicsRow);

        // 风扇不再有独立分档按钮组（叶哥 2026-10-29 要求：只保留手动曲线）。
        // 曲线的档位、参数与应用入口统一在「详细编辑」卡右列，风扇模式切换只剩这一条路。

        return UiFactory.CreateCard("控制", stack);
    }

    /// <summary>
    /// 构建「详细编辑」卡：风扇曲线入口 + 性能详细参数表。
    /// </summary>
    /// <remarks>
    /// 默认<b>展开</b>（叶哥 2026-10-28 要求）：这是常用内容，收起反而多一步点击。
    /// 收起动作放在卡片标题行右侧，内容收起后标题与开关仍在同一行，状态一眼可见。
    /// </remarks>
    private Card BuildEditorsSection(ref int tabIndex)
    {
        _btnToggleEditors.TabIndex = tabIndex++;
        _btnToggleEditors.AccessibleDescription = "展开或收起风扇曲线编辑与性能详细参数。";
        _btnToggleEditors.Margin = Padding.Empty;
        _btnToggleEditors.Click += (_, _) =>
        {
            _editorsExpanded = !_editorsExpanded;
            _editorsPanel.Visible = _editorsExpanded;
            ApplyToggleText(_btnToggleEditors, _editorsExpanded, "详细编辑", 'T');
            ScheduleExtentRecompute();
        };

        // 左右两列（叶哥 2026-10-29 要求）：左边功耗目标、右边风扇曲线，
        // 两件事在同一张卡里并排，比原先「曲线一行在上、表格在下」少一层纵向堆叠。
        // 布局本身交给 ApplyEditorsLayout：窗口变窄时同一份控件改为上下堆叠，
        // 不重建控件、不丢编辑器里未提交的输入。
        _editorsGrid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };

        // ── 左列：性能详细参数（软件配置目标） ─────────────────────────
        var leftColumn = UiFactory.CreateStack();
        leftColumn.Margin = new Padding(0, 0, UiFactory.DesignTokens.Space.Md, 0);
        UiFactory.AddRow(leftColumn, UiFactory.CreateGroupCaption("性能详细参数（软件配置目标）"));
        UiFactory.AddRow(leftColumn, BuildPerformanceTable());
        _editorsLeftColumn = leftColumn;

        // ── 右列：风扇曲线（手动，按档位保存） ─────────────────────────
        _fanPanel.Margin = Padding.Empty;

        ApplyEditorsLayout(stacked: false);

        UiFactory.AddRow(_editorsPanel, _editorsGrid);

        _editorsPanel.Visible = _editorsExpanded;
        ApplyToggleText(_btnToggleEditors, _editorsExpanded, "详细编辑", 'T');

        return UiFactory.CreateCard("详细编辑", _editorsPanel, _btnToggleEditors);
    }

    /// <summary>按展开状态刷新折叠按钮的文本与可访问名。</summary>
    private static void ApplyToggleText(Button button, bool expanded, string target, char mnemonic)
    {
        ArgumentNullException.ThrowIfNull(button);

        // 前缀箭头直接表达「下一动作」：展开态显示 ▾（可收起），收起态显示 ▸。
        button.Text = expanded ? $"▾ 收起(&{mnemonic})" : $"▸ 展开(&{mnemonic})";
        button.AccessibleName = $"{(expanded ? "收起" : "展开")}{target}";
    }

    private Control BuildPerformanceTable()
    {
        var stages = Enum.GetValues<PerformanceStage>();
        var headers = PerformanceTablePresenter.ColumnHeaders;
        var columnCount = headers.Count + 1;

        var table = UiFactory.CreateGrid(columnCount);
        table.RowCount = stages.Length + 1;

        // 第 0 列（档位）按内容自适应；数值列占满剩余宽度。
        // GPU 列已移除（HWE §26.3：本机无可用写入通道），原因写在能力清单里。
        table.ColumnStyles.Clear();
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (var column = 1; column <= headers.Count; column++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        }

        for (var column = 0; column < columnCount; column++)
        {
            table.Controls.Add(CreateTableHeader(column == 0 ? "档位" : headers[column - 1]), column, 0);
        }

        // 行数据唯一来源是控制协调器的当前配置目标（初始等于默认配置表），
        // 不在界面层另抄一份数字（与 PerformanceTablePresenter 同一约定）。
        var line = 1;
        foreach (PerformanceStage stage in stages)
        {
            var row = PerformanceTablePresenter.BuildRow(_context.Control.GetStageSettings(stage));
            table.Controls.Add(CreateTableCell(row.Stage, bold: true), 0, line);
            table.Controls.Add(CreatePerformanceEditor(stage, row.CpuPower), 1, line);
            line++;
        }

        return table;
    }

    private Control CreatePerformanceEditor(PerformanceStage stage, string initialText)
    {
        var editor = new TextBox
        {
            Font = UiFactory.BaseFont,
            Text = initialText,
            AccessibleName = $"{ModeFormatter.StageText(stage)}·CPU 功耗",
            AccessibleDescription =
                "CPU 功耗配置目标，单位瓦。同一数值同时作为持续与短时上限；" +
                "留空表示该项采用原生上限（不限）。回车确认生效，Esc 还原。"
        };

        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CommitPerformanceEditor(editor, stage);
            }
            else if (e.KeyCode == Keys.Escape)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                editor.Text = PerformanceDisplayText(stage);
            }
        };

        // 未按回车确认就离开单元格：还原为当前配置目标，避免输入框停留在未生效的草稿值上。
        editor.Leave += (_, _) =>
        {
            var canonical = PerformanceDisplayText(stage);
            if (editor.Text != canonical)
            {
                editor.Text = canonical;
            }
        };

        // 套圆角外框：TextBox 自身只有直角边框，与全应用的圆角语言不一致。
        // 不用 Dock=Fill：单元格是整行宽，Fill 会把输入框拉成 700 多像素的长条；
        // 定宽 + 左锚定，单元格剩余空间自然留白。
        return new RoundedField(editor)
        {
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Width = 100,
            Margin = new Padding(4, 2, 12, 2)
        };
    }

    private string PerformanceDisplayText(PerformanceStage stage)
        => PerformanceTablePresenter.BuildRow(_context.Control.GetStageSettings(stage)).CpuPower;

    private void CommitPerformanceEditor(TextBox editor, PerformanceStage stage)
    {
        var text = editor.Text.Trim();
        var current = _context.Control.GetStageSettings(stage);
        var stageText = ModeFormatter.StageText(stage);
        const string title = "CPU 功耗";

        if (!PerformanceTablePresenter.TryParsePower(text, out var limit, out var error))
        {
            SetResult($"{stageText}·CPU 功耗：{error}", MetricTone.Warning);
            return;
        }

        // 单个输入框的合并语义：同一数值同写持续与短时上限。
        var next = current with { CpuLongTerm = limit!, CpuShortTerm = limit! };

        var update = _context.Control.UpdateStageSettings(next);
        if (!update.Ok)
        {
            editor.Text = PerformanceDisplayText(stage);
            SetResult($"{stageText}·{title} 未生效：{string.Join(" ", update.Problems)}", MetricTone.Warning);
            return;
        }

        // 回显统一从配置模型重取：输入框内容恒为「数字或空」，与实际保存的值不会脱节。
        editor.Text = PerformanceDisplayText(stage);
        var shown = string.IsNullOrEmpty(editor.Text) ? "原生上限（不限）" : editor.Text + " W";

        // 立即落盘：配置目标只存在协调器内存里的话，重启应用就会回默认值
        // （本轮实测证据：改 45 W 后重启回到 60 W，再点档位会把硬件也写成默认值）。
        var targets = StageTargetResolver.Capture(
            PerformanceProfileCatalog.Defaults.Select(p => _context.Control.GetStageSettings(p.Stage)));
        var persisted = _context.TryUpdateStageTargets(targets, out var saveError);

        var message = persisted
            ? $"{stageText}·{title} 配置目标已更新为 {shown}，已保存到设置文件；切换档位时按本机能力门控逐项执行。"
            : $"{stageText}·{title} 配置目标已更新为 {shown}，但保存到设置文件失败：{saveError}。"
              + "本次运行内有效，重启后会回到默认值。";
        SetResult(message, persisted ? MetricTone.Normal : MetricTone.Warning);
        _context.WriteLog(persisted ? LogLevel.Info : LogLevel.Warning, "Performance", message);
    }

    private static Label CreateTableHeader(string text)
        => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(UiFactory.BaseFont, FontStyle.Bold),
            Margin = new Padding(4, 3, 12, 5)
        };

    private static Label CreateTableCell(string text, bool bold = false)
        => new()
        {
            Text = text,
            AutoSize = true,
            Font = bold
                ? new Font(UiFactory.BaseFont, FontStyle.Bold)
                : UiFactory.BaseFont,
            Margin = new Padding(4, 3, 12, 3)
        };

    /// <summary>
    /// 构建「扩展传感器」卡：默认<b>收起</b>（次要信息，展开才占版面）。
    /// </summary>
    private Card BuildExtendedSection(ref int tabIndex)
    {
        _btnToggleExtended.TabIndex = tabIndex++;
        _btnToggleExtended.AccessibleDescription = "展开或收起扩展传感器列表。";
        _btnToggleExtended.Margin = Padding.Empty;
        _btnToggleExtended.Click += (_, _) =>
        {
            _extendedExpanded = !_extendedExpanded;
            _extendedGrid.Visible = _extendedExpanded;
            ApplyToggleText(_btnToggleExtended, _extendedExpanded, "扩展传感器", 'E');
            ScheduleExtentRecompute();
        };

        // 初始折叠：与 _extendedExpanded 初始值保持一致，避免未展开时占据布局。
        _extendedGrid.Visible = _extendedExpanded;
        ApplyToggleText(_btnToggleExtended, _extendedExpanded, "扩展传感器", 'E');

        return UiFactory.CreateCard("扩展传感器", _extendedGrid, _btnToggleExtended);
    }

    /// <summary>创建扩展传感器的两列（名称 / 数值）网格。</summary>
    private static TableLayoutPanel CreateExtendedGrid()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 0,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        return grid;
    }

    private void ApplyCapabilityGating()
    {
        // 性能档位：能力键为 performance.stage（EC Shift 通道，Verified）。
        // 通道整体开放后按档位区分：五档全开放（HWE §20/§21）；
        // 无编码证据的枚举值逐钮禁用，完整原因集中在「设置与诊断」能力清单展示。
        var stageEntry = _context.DescribeCapability(CapabilityKeys.PerformanceStage, requiresWrite: true);
        foreach (var pair in _stageButtons)
        {
            pair.Value.Enabled = stageEntry.Enabled && PerformanceShiftMap.IsStageSupported(pair.Key);
        }

        var graphicsEntry = _context.DescribeCapability(CapabilityKeys.GraphicsMode, requiresWrite: true);
        foreach (var button in _graphicsButtons.Values)
        {
            button.Enabled = graphicsEntry.Enabled;
        }

        // 风扇入口只剩「详细编辑」卡右列的曲线面板，其禁用与说明由面板自身按
        // fan.curve 能力键门控（ApplyEntryGating），这里不再重复一套按钮级门控。
        // 性能详细参数为「软件配置目标」的行内编辑（不触发硬件写入），
        // 不按硬件写能力门控；瓦数/睿频目标的硬件写入发生在切档时并如实报告结果。
    }

    private void SubscribeSnapshots()
    {
        _context.Snapshots.Updated += OnSnapshotUpdated;
        RefreshFromLatest();
    }

    private void OnSnapshotUpdated(object? sender, MonitoringSnapshot snapshot)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action<MonitoringSnapshot>(ApplySnapshot), snapshot);
            return;
        }

        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(MonitoringSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        // 隐藏时不做任何绘制与文本更新（任务书 §20：隐藏页面停止无用绘制）。
        if (!Visible)
        {
            return;
        }

        var fanMode = _context.Settings.FanDisplay;

        // 整帧更新包在一次挂起布局里做：读数文本变化会沿「标签 → 瓷砖 → 网格 → 滚动宿主」
        // 逐级触发 AutoSize 重排，级联中途宿主会把用户的滚动位置钳制到一个偏小的值
        // （实测：滚到 140 后 0.3~1s 内被改成 57，且不再还原——就是「页面自己跳回去」）。
        // 挂起后整帧只做一次布局，中途不再产生那些瞬态范围，滚动位置也就不会被改写。
        ApplyWithLayoutSuspended(() =>
        {
            // 读数瓷砖只呈现「量」本身：数据来源属诊断信息，集中在「设置与诊断」里查看，
            // 主页面不再逐格标注（叶哥 2026-10-28 要求）。备注行只保留与分析有关的口径或状态语义。
            var cpu = snapshot.Cpu;
            SetTile(TileCpuTemp, cpu.Temperature, ValueFormatter.Temperature(cpu.Temperature), null);
            SetTile(TileCpuFreq, cpu.FrequencyMhz, ValueFormatter.FrequencyGigahertz(cpu.FrequencyMhz), ModeFormatter.FrequencyBasisText(cpu.FrequencyBasis));
            SetTile(TileCpuFan, cpu.Fan.Rpm, ValueFormatter.Fan(cpu.Fan, fanMode), ValueFormatter.FanFallbackNote(cpu.Fan, fanMode) ?? ValueFormatter.TargetPercent(cpu.Fan));
            SetTile(TileCpuPower, cpu.PackagePowerWatt, ValueFormatter.Power(cpu.PackagePowerWatt), null);

            var gpu = snapshot.Gpu;
            SetTile(TileGpuTemp, gpu.Temperature, ValueFormatter.Temperature(gpu.Temperature), null);
            SetTile(TileGpuFreq, gpu.CoreFrequencyMhz, ValueFormatter.FrequencyGigahertz(gpu.CoreFrequencyMhz), gpu.IsSleeping ? "独显休眠" : null);
            SetTile(TileGpuFan, gpu.Fan.Rpm, ValueFormatter.Fan(gpu.Fan, fanMode), ValueFormatter.FanFallbackNote(gpu.Fan, fanMode) ?? ValueFormatter.TargetPercent(gpu.Fan));
            SetTile(TileGpuPower, gpu.PowerWatt, ValueFormatter.Power(gpu.PowerWatt), null);

            UpdateExtendedSensors(snapshot);
            _lblSnapshotInfo.Text =
                $"采样于 {ValueFormatter.SampledAt(snapshot.SampledAt)} · 刷新间隔 " +
                $"{_context.Settings.RefreshIntervalSeconds} 秒";
        });
    }

    /// <summary>
    /// 在一个「布局挂起」区段内执行界面更新，结束时只做一次布局。
    /// </summary>
    /// <param name="apply">界面更新动作。</param>
    /// <remarks>
    /// 自动刷新路径（采样读数、状态徽标）必须走这里：这些更新每次都会改写多个
    /// <see cref="AutoSize"/> 文本，若逐条布局，滚动宿主会在级联中途按瞬态范围改写
    /// 用户的滚动位置（见 <see cref="ApplySnapshot"/> 的说明）。
    /// 用户主动操作（点按钮、改配置）不在此列——那些路径本来就只改一处，且需要即时反馈。
    /// </remarks>
    /// <summary>折叠 / 展开后延迟重算滚动范围（等布局把高度更新完）。</summary>
    private void ScheduleExtentRecompute()
    {
        if (!_contentHost.IsHandleCreated || _contentHost.IsDisposed)
        {
            return;
        }

        try
        {
            _contentHost.BeginInvoke(new Action(_contentHost.RecomputeExtent));
        }
        catch (InvalidOperationException)
        {
            // 窗口在投递瞬间被销毁：无需重算。
        }
    }

    private void ApplyWithLayoutSuspended(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 注意：这里**不要**在窗体层 SuspendLayout/ResumeLayout(performLayout: true)。
        // 窗体 ResumeLayout 会触发整棵停靠树重排（外壳与应用栏/状态带/状态栏都参与），
        // 而这次重排对「只更新了读数文本」的刷新毫无必要，只增加耗时与瞬时布局态。
        // 只需要让内容树重排。

        // 关键：级联要挂在**内容树根**上，而不是只挂窗体。
        // SuspendLayout 只停住被调用控件自身的布局，父级仍会被逐个要求重排；
        // 只挂窗体时，每条文本变更仍会一路「瓷砖 → 网格 → 卡片 → 内容栈」重排到底
        // （实测整帧耗时 140–170 ms、UI 线程空闲占 25%）。
        // 挂在内容栈上，级联到此为止，整帧只做一次合并布局。
        _stack.SuspendLayout();
        try
        {
            action();
        }
        finally
        {
            _stack.ResumeLayout(performLayout: true);
        }
    }

    private void SetTile(string key, SensorReading reading, string value, string? note)
    {
        if (!_tiles.TryGetValue(key, out var tile))
        {
            return;
        }

        tile.SetValue(value, note, ToneMap.Of(reading.Status));
    }

    private void UpdateExtendedSensors(MonitoringSnapshot snapshot)
    {
        var visibleCount = 0;

        foreach (var sensor in snapshot.Extended)
        {
            var text = ValueFormatter.Extended(sensor.Reading);
            var tone = MetricTile.ToneColor(_palette, ToneMap.Of(sensor.Reading.Status));

            // 不可用项直接不进列表（叶哥 2026-10-28 要求）：扩展区只列「有值」的传感器，
            // 一排红色的「不可用」既没有信息量，也把有效的几项淹没了。
            // 行一旦建过就保留并切 Visible，传感器后续恢复可用时无需重建布局。
            var hidden = sensor.Reading.Status == ReadingStatus.Unavailable;

            if (_extendedRows.TryGetValue(sensor.Key, out var row))
            {
                row.NameLabel.Text = sensor.DisplayName;
                row.ValueLabel.Text = text;
                row.ValueLabel.ForeColor = tone;
                row.NameLabel.Visible = !hidden;
                row.ValueLabel.Visible = !hidden;
                if (!hidden)
                {
                    visibleCount++;
                }

                continue;
            }

            if (hidden)
            {
                continue;
            }

            visibleCount++;

            var nameLabel = UiFactory.CreateMutedLabel(sensor.DisplayName);
            nameLabel.Margin = new Padding(
                UiFactory.DesignTokens.Space.Xs,
                UiFactory.DesignTokens.Space.Hair,
                UiFactory.DesignTokens.Space.Md,
                UiFactory.DesignTokens.Space.Hair);

            var valueLabel = new Label
            {
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                TextAlign = ContentAlignment.MiddleRight,
                Text = text,
                ForeColor = tone,
                Margin = new Padding(
                    UiFactory.DesignTokens.Space.Md,
                    UiFactory.DesignTokens.Space.Hair,
                    UiFactory.DesignTokens.Space.Xs,
                    UiFactory.DesignTokens.Space.Hair)
            };

            AddExtendedRow(nameLabel, valueLabel);
            _extendedRows[sensor.Key] = (nameLabel, valueLabel);
        }

        // 占位行只在「没有任何可见扩展项」时出现（含全部被过滤为不可用的情况）：
        // 首帧快照可能还未产生扩展读数，若把它当作稳态就会出现「有数据但仍显示暂无」的矛盾。
        var placeholderVisible = visibleCount == 0;
        if (_extendedRows.TryGetValue(NoExtendedSensorKey, out var placeholder))
        {
            placeholder.NameLabel.Visible = placeholderVisible;
            placeholder.ValueLabel.Visible = placeholderVisible;
        }
        else if (placeholderVisible)
        {
            var nameLabel = UiFactory.CreateMutedLabel("本机暂无可用扩展传感器");
            var valueLabel = new Label { AutoSize = true, Text = ValueFormatter.Placeholder };
            AddExtendedRow(nameLabel, valueLabel);
            _extendedRows[NoExtendedSensorKey] = (nameLabel, valueLabel);
        }
    }

    private const string NoExtendedSensorKey = "__none__";

    /// <summary>向扩展网格追加一行（名称 / 数值）。</summary>
    private void AddExtendedRow(Label nameLabel, Label valueLabel)
    {
        var index = _extendedGrid.RowCount;
        _extendedGrid.RowCount = index + 1;
        _extendedGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _extendedGrid.Controls.Add(nameLabel, 0, index);
        _extendedGrid.Controls.Add(valueLabel, 1, index);
    }

    /// <summary>由宿主在性能能力接入后调用，更新顶部「实际性能模式」显示。</summary>
    /// <param name="state">读回的性能状态。</param>
    /// <param name="pending">已请求但尚未生效的档位。无待生效项时为 <see langword="null"/>。</param>
    /// <param name="requiresRestart">待生效档位是否需要重启。</param>
    /// <remarks>
    /// 待生效信息**只作附加显示，不覆盖实际状态**（任务书 §6.1）。
    /// </remarks>
    public void UpdateStageState(
        PerformanceHardwareState state,
        PerformanceStage? pending = null,
        bool requiresRestart = false)
    {
        ArgumentNullException.ThrowIfNull(state);

        // ActiveStage 为空 ≠ 未适配：可能是读回了未登记的场景编码（0xC0/0xC3/0xC5 等）。
        // 此时如实展示原始场景标识，不得回退成「未适配」误导为链路未接通。
        var text = state.ActiveStage is { } activeStage
            ? ModeFormatter.StageText(activeStage)
            : string.IsNullOrWhiteSpace(state.FirmwareScenario)
                ? "未知（读回未包含档位信息）"
                : $"未登记场景（{state.FirmwareScenario}）";
        var hasPending = pending.HasValue && pending != state.ActiveStage;
        if (hasPending)
        {
            var restart = requiresRestart ? "重启后生效" : "待生效";
            text = $"{text}（待生效：{ModeFormatter.StageText(pending)}，{restart}）";
        }

        // 徽标与高亮同为自动刷新路径：与采样读数一起包在一次布局里，
        // 避免状态带文本逐条布局引起滚动宿主重排（原因见 ApplySnapshot）。
        ApplyWithLayoutSuspended(() =>
        {
            _pillStage.SetValue(text, MetricTone.Normal, hasPending);

            // 分段高亮：有待生效项时高亮待生效档位（请求目标），否则高亮实际档位。
            // 高亮只作导航辅助，实际生效状态以徽标文字与硬件读回为准。
            var stageTarget = pending ?? state.ActiveStage;
            _stageGroup.SetActive(
                stageTarget.HasValue && _stageButtons.TryGetValue(stageTarget.Value, out var stageButton)
                    ? stageButton
                    : null);

            // 曲线槽位跟随实际档位（引擎运行中会立即换用该档曲线）。
            // 只在读回拿到已登记档位时跟随：未登记场景没有对应槽位，不得猜一个顶上。
            if (state.ActiveStage is { } active)
            {
                _fanPanel.FollowStage(active);
            }
        });
    }

    /// <summary>由宿主在显卡能力接入后调用，更新顶部「实际显卡模式」显示。</summary>
    public void UpdateGraphicsState(GraphicsHardwareState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 与性能徽标同一条自动刷新路径，同样合并为一次布局。
        ApplyWithLayoutSuspended(() =>
        {
            _pillGraphics.SetValue(
                ModeFormatter.GraphicsTextWithPending(state.Current, state.Pending, state.RequiresRestart),
                MetricTone.Normal,
                state.HasPendingChange);

            // 分段高亮：同上，待生效优先。
            var graphicsTarget = state.Pending ?? state.Current;
            _graphicsGroup.SetActive(
                graphicsTarget.HasValue && _graphicsButtons.TryGetValue(graphicsTarget.Value, out var graphicsButton)
                    ? graphicsButton
                    : null);
        });
    }

    /// <summary>
    /// 由桌面壳随采样节拍调用：用 EC 0xD4 读回的真值更新曲线区的控制模式行。
    /// </summary>
    /// <remarks>
    /// 按用户要求（2026-10-18），主页面只保留核心内容：本行只展示当前生效控制模式，
    /// 曲线/强冷/校准基准的解释性括注不再铺陈在主页面，完整说明见「设置与诊断」能力清单。
    /// 模式文案仍走 <see cref="ModeFormatter.FanModeReadbackText"/>，不得把 advanced
    /// 表述为「自定义曲线已生效」。
    /// </remarks>
    public void UpdateFanState(FanHardwareState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // 控制模式读回不再上屏（叶哥 2026-10-31 要求移除面板状态文字），
        // 但读回本身继续用于门控与日志。
    }

    private void ToggleFanDisplay()
    {
        var next = _context.Settings.FanDisplay == FanDisplayMode.Rpm
            ? FanDisplayMode.Percent
            : FanDisplayMode.Rpm;

        _context.UpdateSettings(_context.Settings with { FanDisplay = next });
        RefreshFromLatest();
        SetResult(
            next == FanDisplayMode.Rpm
                ? "风扇显示已切换为 RPM。"
                : "风扇显示已切换为百分比；无有效校准基准时仍会回落显示 RPM。",
            MetricTone.Muted);
    }

    private async Task RequestPerformanceAsync(PerformanceStage stage)
    {
        // 成功时只留一句「已切换到「平衡」」：逐项明细（含本机永久不支持的 GPU 功耗上限）
        // 全部写在日志里，不再拼进界面提示。失败时仍回落到逐项明细，不掩盖问题。
        await ExecuteRequestAsync(
            () => _context.Control.RequestPerformanceStageAsync(stage, CancellationToken.None),
            $"切换性能模式到 {ModeFormatter.StageText(stage)}",
            result => StageSwitchFeedback.SuccessText(result, stage)).ConfigureAwait(true);
    }

    private async Task RequestGraphicsAsync(GraphicsMode mode)
    {
        var result = await ExecuteRequestAsync(
            () => _context.Control.RequestGraphicsModeAsync(mode, CancellationToken.None),
            $"切换显卡模式到 {ModeFormatter.GraphicsText(mode)}").ConfigureAwait(true);

        // 只有「真的写入、且在等重启」才值得问一句：重启是让它生效的唯一动作。
        // 「已是目标模式、无需切换」不进这个分支——没有东西要靠重启生效。
        if (result is null
            || result.Status != OperationStatus.PendingRestart
            || result.PendingRestartSettings.Count == 0)
        {
            return;
        }

        AskRestartAfterGraphicsSwitch(mode);
    }

    /// <summary>
    /// 显卡模式已写入待生效时，询问是否立即重启，并按选择执行。
    /// </summary>
    /// <remarks>
    /// <b>重启动作的唯一入口</b>：除用户在这里明确选择「立即重启」之外，
    /// 程序不会自行重启设备（任务书 §9.1 的自动化禁止条款依然成立）。
    /// 重启前先走正常退出路径，让采样循环停止、日志落盘。
    /// </remarks>
    private void AskRestartAfterGraphicsSwitch(GraphicsMode mode)
    {
        var modeText = ModeFormatter.GraphicsText(mode);
        const string detail =
            "目标模式已写入 EC 0x2E（MUX 位）与 UEFI 变量 MsiDCVarData 并读回一致。"
            + "重启后由固件按新配置初始化显示通路；若接了外接屏，重启后的显示拓扑会随之变化。"
            + "协议依据未区分「普通重启」与「关机再开机」，建议先用普通重启。";

        var choice = RestartPromptForm.Ask(this, modeText, detail, _palette);
        if (choice != RestartChoice.RestartNow)
        {
            SetResult(
                $"{modeText} 已提交，待重启生效。需要生效时请重新启动设备。",
                MetricTone.Warning);
            _context.WriteLog(LogLevel.Info, "Graphics", $"{modeText}：用户选择稍后重启。");
            return;
        }

        RestartNow(modeText);
    }

    /// <summary>按用户选择重启设备；发起失败时如实提示并保留「稍后重启」的结果。</summary>
    private void RestartNow(string modeText)
    {
        try
        {
            // 5 秒缓冲：本进程先正常退出（停止采样、写日志），系统随后重启。
            using var process = Process.Start(new ProcessStartInfo("shutdown", RestartArguments)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });

            _context.WriteLog(LogLevel.Warning, "Graphics", $"{modeText}：用户选择立即重启，已提交 shutdown /r /t 5。");
            SetResult($"{modeText} 已提交，正在重启设备……", MetricTone.Normal);

            // 走正常退出路径：不直接杀进程，保证采样线程与日志收尾。
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _context.WriteLog(LogLevel.Error, "Graphics", $"{modeText}：重启未能发起：{ex.Message}");
            SetResult(
                $"{modeText} 已提交，但重启未能发起：{ex.Message}。请手动重启设备。",
                MetricTone.Danger);
        }
    }

    /// <summary>
    /// 执行一次控制请求，并把结果收敛成**一条**提示。
    /// </summary>
    /// <param name="request">实际请求。</param>
    /// <param name="operation">操作名，用于日志与失败文案。</param>
    /// <param name="successSummary">
    /// 主目标已达成时返回单行成功文案；返回 <see langword="null"/> 表示回落到逐项明细。
    /// </param>
    /// <remarks>
    /// <para>
    /// 这里曾经先写一条「请求已发出」，等结果回来再写一条结果文案。请求通常几十毫秒就返回，
    /// 于是每次切换都会**连着闪两条提示**，读起来像「重复提示」
    /// （叶哥 2026-10-31 反馈）。现在只保留最终结果那一条。
    /// </para>
    /// <para>
    /// 结果文案不再无条件拼逐项明细：档位切换会连带尝试 GPU 功耗上限，而本机该通道永久不支持，
    /// 明细里必然挂着一条「本机不支持」的长原因，把一次成功的切换染成警告色。
    /// 逐项明细已由 <see cref="Shell.ShellContext.LogControlResult"/> 全量写入日志，界面不必重复。
    /// </para>
    /// </remarks>
    private async Task<ControlResult?> ExecuteRequestAsync(
        Func<Task<ControlResult>> request,
        string operation,
        Func<ControlResult, string?>? successSummary = null)
    {
        try
        {
            var result = await request().ConfigureAwait(true);
            _context.LogControlResult(result, operation);

            var summary = successSummary?.Invoke(result);
            if (summary is not null)
            {
                SetResult(summary, MetricTone.Normal);
                return result;
            }

            SetResult(FormatResult(result, operation), ResultTone(result));
            return result;
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Error, "Control", $"{operation} 抛出异常：{ex.Message}");
            SetResult($"{operation} 失败：{ex.Message}", MetricTone.Danger);
            return null;
        }
    }

    private static string FormatResult(ControlResult result, string operation)
    {
        var parts = result.Settings.Select(s =>
            $"{s.Setting}＝{StatusText(s.Status)}" +
            (string.IsNullOrWhiteSpace(s.Message) ? string.Empty : $"（{s.Message}）") +
            (string.IsNullOrWhiteSpace(s.ReadBack) ? string.Empty : $" 读回 {s.ReadBack}"));

        return $"{operation}：{result.Status}　" + string.Join("；", parts);
    }

    private static MetricTone ResultTone(ControlResult result)
    {
        if (result.Settings.Count == 0)
        {
            return MetricTone.Danger;
        }

        if (result.Settings.All(s => s.Status == OperationStatus.Succeeded))
        {
            return MetricTone.Normal;
        }

        return result.Settings.Any(s => s.Status == OperationStatus.Failed) ? MetricTone.Danger : MetricTone.Warning;
    }

    private static string StatusText(OperationStatus status) => status switch
    {
        OperationStatus.Succeeded => "成功",
        OperationStatus.PartiallySucceeded => "部分成功",
        OperationStatus.Failed => "失败",
        OperationStatus.PendingRestart => "待重启生效",
        OperationStatus.Unsupported => "本机不支持",
        _ => status.ToString()
    };

    private void SetResult(string text, MetricTone tone)
    {
        // 同文案同色不再重写：Label 每次改 Text 都会触发 AutoSize 重算与父容器重排，
        // 连续两次相同提示（例如重复触发同一操作）会白闪一下。
        // 状态存在独立字段里，不能借用 Tag——Tag 上挂着 MutedText.Marker，
        // 换肤时靠它识别次要文本，覆盖掉会让整个标签在明暗切换后停在旧颜色。
        if (_resultText == text && _resultTone == tone)
        {
            return;
        }

        _resultText = text;
        _resultTone = tone;
        _lblResult.Text = text;
        _lblResult.ForeColor = MetricTile.ToneColor(_palette, tone);
        // 空文案时整行隐藏：结果区只在有反馈时出现，避免常驻一条空行。
        _lblResult.Visible = !string.IsNullOrWhiteSpace(text);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e is not null)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    // 与标题栏关闭按钮一致：按设置进入托盘，不结束进程。
                    HideToTray();
                    e.Handled = true;
                    break;

                case Keys.F5:
                    RefreshFromLatest();
                    SetResult("已按 F5 重绘为最近一次采样结果。", MetricTone.Muted);
                    e.Handled = true;
                    break;
            }
        }

        base.OnKeyDown(e!);
    }

    private void HideToTray()
    {
        if (_context.Settings.CloseToTray)
        {
            _context.WriteLog(LogLevel.Info, "Shell", "正常窗口已按 Esc 隐藏到托盘，采样与控制继续运行。");
            Hide();
            return;
        }

        // 未启用「关闭到托盘」时，Esc 不结束进程，只提示用户正确的退出入口。
        SetResult("当前设置为「关闭窗口即退出」，请使用「退出程序」按钮结束进程。", MetricTone.Warning);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // 「关闭到托盘」与「完全退出」是两个不同操作（提示词第 12 条）。
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        if (_context.Settings.CloseToTray)
        {
            e.Cancel = true;
            _context.WriteLog(LogLevel.Info, "Shell", "正常窗口已关闭到托盘，采样与控制继续运行。");
            Hide();
            return;
        }

        // 关键：必须先取消本次关闭，把「是否真的退出」交给宿主判断。
        // 宿主会弹出确认框；用户若选择「否」，窗体必须保持存活——
        // 否则会出现「窗口已销毁、进程仍在托盘」的残状态，之后从托盘再打开会抛 ObjectDisposedException。
        e.Cancel = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 事件为强引用，窗体关闭后必须解除订阅，否则已释放的窗体会一直被采样回调持有。
            _context.Snapshots.Updated -= OnSnapshotUpdated;
        }

        base.Dispose(disposing);
    }
}
