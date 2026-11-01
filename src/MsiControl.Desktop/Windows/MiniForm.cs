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
/// 迷你窗口。
/// </summary>
/// <remarks>
/// <para>
/// 2026-10-31 精简（叶哥要求）：只保留<b>性能模式切换</b>、<b>显卡模式切换</b>与
/// CPU / GPU 两组读数。整机功耗卡片（本机无可用口径，恒为不可用）、窗口导航按钮、
/// 强冷与键盘说明文字等冗余展示项已移除。
/// </para>
/// <para>
/// 返回正常窗口与退出程序改由托盘菜单承载（<c>Ctrl+Alt+M</c> 显隐、托盘「打开正常窗口 /
/// 退出程序」），迷你窗口自身不再提供导航按钮，以免与「只看读数与两个切换」的定位冲突。
/// </para>
/// <para>
/// <b>不置顶</b>（<see cref="Form.TopMost"/> 恒为 false），<b>不提供强冷按钮</b>。
/// 这两项是明确约束，不因便于操作而放宽。
/// </para>
/// <para>与正常窗口共用同一份采样与控制核心，切换窗口不重复初始化硬件。</para>
/// <para>
/// 键盘操作：Tab 遍历、助记键、方向键在按钮组内循环、<c>Esc</c> 隐藏到托盘、<c>F5</c> 立即重绘。
/// </para>
/// </remarks>
public sealed class MiniForm : Form, IThemeAware
{
    private readonly ShellContext _context;
    private readonly TableLayoutPanel _stack = UiFactory.CreateStack();
    private readonly Dictionary<string, MetricTile> _tiles = new(StringComparer.Ordinal);
    private readonly List<Button> _stageButtonOrder = new();
    private readonly Dictionary<PerformanceStage, Button> _stageButtons = new();
    private readonly List<Button> _graphicsButtonOrder = new();
    private readonly Dictionary<GraphicsMode, Button> _graphicsButtons = new();
    private SegmentedRail? _stackStageRail;
    private SegmentedRail? _stackGraphicsRail;
    private readonly Label _lblStageReason = UiFactory.CreateMutedLabel();
    private readonly Label _lblGraphicsReason = UiFactory.CreateMutedLabel();
    private readonly Label _lblResult = UiFactory.CreateMutedLabel();
    private readonly ToolTip _reasonTip = new() { ShowAlways = true };

    private ThemePalette _palette = ThemePalette.Light;
    private bool _allowClose;
    private bool _placementApplied;

    /// <summary>构造迷你窗口。</summary>
    public MiniForm(ShellContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        Text = "MSI-Control · 迷你";
        Icon = AppIcon.CreateWindowIcon();
        AccessibleName = "MSI-Control 迷你窗口";
        AccessibleDescription =
            "紧凑视图：性能模式与显卡模式切换、CPU 与 GPU 基础读数。不提供强冷按钮。";
        StartPosition = FormStartPosition.Manual;
        // 五档分段轨道 + 四列读数瓷砖在 560 以下开始互相挤压（GHI 等窄字仍可读，
        // 「3.32 GHz」的末位先被裁掉），因此下限定在 560。
        MinimumSize = new Size(580, 340);
        Size = new Size(640, 560);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFactory.BaseFont;
        AutoScroll = true;
        TopMost = false;
        ShowInTaskbar = true;
        MaximizeBox = false;
        KeyPreview = true;

        BuildLayout();
        ApplyCapabilityGating();
        _context.Snapshots.Updated += OnSnapshotUpdated;
        ApplySnapshot(_context.Snapshots.Latest);

        FormClosing += OnFormClosing;
    }

    /// <summary>
    /// 用户关闭窗口且设置为「关闭窗口即退出」时，请求完全退出。
    /// </summary>
    /// <remarks>
    /// 迷你窗口不再提供「退出程序」按钮（2026-10-31 精简）；该事件只剩关闭语义一条触发路径，
    /// 其余退出入口在托盘菜单与正常窗口。
    /// </remarks>
    public event EventHandler? ExitRequested;

    /// <summary>允许本次关闭。由宿主在退出流程中调用。</summary>
    public void AllowClose() => _allowClose = true;

    /// <summary>取得当前窗口位置与尺寸。</summary>
    public WindowPlacement CurrentPlacement() => new(Left, Top, Width, Height);

    /// <summary>应用记录的位置与尺寸。</summary>
    public void ApplyPlacement(WindowPlacement placement, Rectangle workingArea)
    {
        ArgumentNullException.ThrowIfNull(placement);

        var clamped = placement.ClampTo(workingArea.Left, workingArea.Top, workingArea.Width, workingArea.Height);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(clamped.X, clamped.Y);
        Size = new Size(clamped.Width, clamped.Height);
        _placementApplied = true;
    }

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // 无论有没有保存过尺寸都要跑一次「至少装得下内容」：改版后卡片内边距与轨道变高，
        // 旧配置里记下的 420 高已经装不下三张卡，沿用会让迷你窗口一开就带纵向滚动条。
        // 只增高、不缩小——用户手动调大的尺寸必须保留。
        EnsureContentHeight();

        // 没有保存过尺寸（首次运行）时再按内容收缩一次：默认 560 高会留白。
        if (!_placementApplied)
        {
            FitHeightToContent();
        }
    }

    /// <summary>
    /// 保证窗口至少装得下全部内容（只增高，不缩小）。
    /// </summary>
    /// <remarks>
    /// 改版会改变内容高度，而配置里存的是旧版窗口尺寸；不做这一步的话，
    /// 老用户升级后迷你窗口一打开就是带滚动条的半截窗口，看起来像布局坏了。
    /// </remarks>
    private void EnsureContentHeight()
    {
        var contentHeight = _stack.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        if (contentHeight <= 0)
        {
            return;
        }

        var chrome = Height - ClientSize.Height;
        var workingHeight = Screen.FromControl(this).WorkingArea.Height;
        var wanted = Math.Min(contentHeight + chrome, (int)(workingHeight * 0.8));
        if (Height < wanted)
        {
            Height = Math.Max(MinimumSize.Height, wanted);
        }
    }

    /// <summary>按内容实际高度调整窗口高度（受最小高度与工作区上限约束）。</summary>
    private void FitHeightToContent()
    {
        var contentHeight = _stack.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
        var chrome = Height - ClientSize.Height;
        var workingHeight = Screen.FromControl(this).WorkingArea.Height;
        var wanted = Math.Min(contentHeight + chrome, (int)(workingHeight * 0.8));
        Height = Math.Max(MinimumSize.Height, wanted);
    }

    /// <summary>用最近一次采样重绘界面（设置变化或按下 F5 时调用）。</summary>
    public void RefreshFromLatest() => ApplySnapshot(_context.Snapshots.Latest);

    /// <inheritdoc />
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        // 结果文案的换行宽度跟随内容区，避免长句把迷你窗口撑出横向滚动条。
        var available = ClientSize.Width
            - (UiFactory.DesignTokens.Space.Lg * 2)
            - (UiFactory.DesignTokens.Space.Lg * 2);

        if (available > 160)
        {
            _lblResult.MaximumSize = new Size(available, 0);
        }
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        ThemeApplier.Apply(this, palette);
        _lblStageReason.ForeColor = palette.ForeMuted;
        _lblGraphicsReason.ForeColor = palette.ForeMuted;
        _stack.BackColor = palette.Back;
    }

    private void BuildLayout()
    {
        var tabIndex = 0;

        // 页面边距与主窗口一致（16 / 12）：两个窗口切换时，卡片的左边界不会横向跳动。
        _stack.Padding = new Padding(
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Md,
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Md);

        // ── 模式卡：两条分段轨道 + 操作结果行 ─────────────────────────
        // 原先是「说明文字 + 一排独立按钮」的松散行；改成轨道后，五个档位在视觉上
        // 是一个互斥控件，而不是五个可以同时按下的按钮——这才是它们的语义。
        var modes = UiFactory.CreateStack();

        UiFactory.AddRow(modes, UiFactory.CreateGroupCaption("性能模式"));
        _stackStageRail = new SegmentedRail(Enum.GetValues<PerformanceStage>().Length);
        var stageIndex = 1;
        foreach (var stage in Enum.GetValues<PerformanceStage>())
        {
            var button = UiFactory.CreateSegmentButton($"{ModeFormatter.StageText(stage)}(&{stageIndex})");
            button.TabIndex = tabIndex++;
            button.AccessibleDescription = $"请求切换到{ModeFormatter.StageText(stage)}模式；未取得写入证据时不可用。";
            button.Click += (_, _) => _ = RequestPerformanceAsync(stage);
            _stageButtons[stage] = button;
            _stageButtonOrder.Add(button);
            _stackStageRail.Place(button, stageIndex - 1);
            stageIndex++;
        }

        UiFactory.EnableArrowKeyNavigation(_stageButtonOrder);
        UiFactory.AddRow(modes, _stackStageRail);
        UiFactory.AddRow(modes, _lblStageReason);

        UiFactory.AddRow(modes, UiFactory.CreateGroupCaption("显卡模式"));
        _stackGraphicsRail = new SegmentedRail(Enum.GetValues<GraphicsMode>().Length);
        var graphicsIndex = 0;
        foreach (var mode in Enum.GetValues<GraphicsMode>())
        {
            var mnemonic = mode switch
            {
                GraphicsMode.Integrated => 'I',
                GraphicsMode.Hybrid => 'H',
                _ => 'D'
            };
            var button = UiFactory.CreateSegmentButton($"{ModeFormatter.GraphicsText(mode)}(&{mnemonic})");
            button.TabIndex = tabIndex++;
            button.AccessibleDescription = $"请求切换到{ModeFormatter.GraphicsText(mode)}；切换需重启，程序不会自动重启。";
            button.Click += (_, _) => _ = RequestGraphicsAsync(mode);
            _graphicsButtons[mode] = button;
            _graphicsButtonOrder.Add(button);
            _stackGraphicsRail.Place(button, graphicsIndex++);
        }

        UiFactory.EnableArrowKeyNavigation(_graphicsButtonOrder);
        UiFactory.AddRow(modes, _stackGraphicsRail);
        UiFactory.AddRow(modes, _lblGraphicsReason);

        // 操作结果紧贴切换区：出现 / 消失都在同一条固定高度的行内，不推挤下方卡片。
        // 换行宽度在 OnResize 里按实际内容区重算，不写死像素。
        UiFactory.AddRow(modes, _lblResult);
        UiFactory.AddRow(_stack, UiFactory.CreateCard("模式", modes));

        // ── 读数控：每卡四列 ─────────────────────────────────────────
        var cpuGrid = UiFactory.CreateGrid(4);
        cpuGrid.Controls.Add(CreateTile("cpu.temp", "CPU 温度"), 0, 0);
        cpuGrid.Controls.Add(CreateTile("cpu.freq", "CPU 频率"), 1, 0);
        cpuGrid.Controls.Add(CreateTile("cpu.fan", "CPU 风扇"), 2, 0);
        cpuGrid.Controls.Add(CreateTile("cpu.power", "CPU 功耗"), 3, 0);
        UiFactory.AddRow(_stack, UiFactory.CreateCard("CPU", cpuGrid));

        var gpuGrid = UiFactory.CreateGrid(4);
        gpuGrid.Controls.Add(CreateTile("gpu.temp", "GPU 温度"), 0, 0);
        gpuGrid.Controls.Add(CreateTile("gpu.freq", "GPU 频率"), 1, 0);
        gpuGrid.Controls.Add(CreateTile("gpu.fan", "GPU 风扇"), 2, 0);
        gpuGrid.Controls.Add(CreateTile("gpu.power", "GPU 功耗"), 3, 0);
        UiFactory.AddRow(_stack, UiFactory.CreateCard("GPU", gpuGrid));

        Controls.Add(_stack);
    }

    private MetricTile CreateTile(string key, string title)
    {
        // 迷你窗口四列较窄：内边距收到 6、数值字号收到 0.78 倍。
        // 卡片内边距从 16 提到 18、瓷砖内边距从 8 提到 12 之后，可用宽度比旧版少了 16px，
        // 沿用旧值会让「3.62 GHz」「45 %」的末位被列宽裁掉（实机截图确认）。
        var tile = new MetricTile(title, UiFactory.DesignTokens.Type.Display * 0.78f)
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(
                6,
                UiFactory.DesignTokens.Space.Xs,
                6,
                UiFactory.DesignTokens.Space.Xs)
        };
        _tiles[key] = tile;
        return tile;
    }

    private void ApplyCapabilityGating()
    {
        // 门控键必须是 performance.stage（EC 0xD2 档位通道，Verified）。
        // firmware.scenario 是 MSI_ACPI WMI 侧的只读线索键，用它会令档位按钮永久灰化。
        var stageEntry = _context.DescribeCapability(CapabilityKeys.PerformanceStage, requiresWrite: true);
        foreach (var button in _stageButtons.Values)
        {
            button.Enabled = stageEntry.Enabled;
        }

        SetReason(_lblStageReason, "性能模式切换已禁用", stageEntry);

        var graphicsEntry = _context.DescribeCapability(CapabilityKeys.GraphicsMode, requiresWrite: true);
        foreach (var button in _graphicsButtons.Values)
        {
            button.Enabled = graphicsEntry.Enabled;
        }

        SetReason(_lblGraphicsReason, "显卡模式切换已禁用", graphicsEntry);
    }

    /// <summary>
    /// 写入禁用原因：界面只留一句短提示，完整依据放在悬浮提示里。
    /// </summary>
    /// <remarks>
    /// 能力原因动辄数十至数百字，完整铺在迷你窗口会占掉三四行、把读数挤出视野；
    /// 但又不能只把按钮变灰不解释（任务书提示词第 5 条），故保留短句 + 全文提示。
    /// </remarks>
    private void SetReason(Label label, string headline, CapabilityEntry entry)
    {
        if (entry.Enabled)
        {
            label.Text = string.Empty;
            label.Visible = false;
            _reasonTip.SetToolTip(label, string.Empty);
            return;
        }

        label.Text = headline + "（悬停查看原因）";
        label.Visible = true;
        _reasonTip.SetToolTip(label, headline + "：" + entry.Reason);
        label.AccessibleDescription = headline + "：" + entry.Reason;
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
        if (snapshot is null || !Visible)
        {
            return;
        }

        var fanMode = _context.Settings.FanDisplay;

        // 备注一律去掉「口径：」前缀：四列布局下每格只有约 110px，
        // 前缀占掉 3 个字宽，正文就会被卡片边界裁断（实机截图里表现为「口径：活跃核心均」）。
        SetTile("cpu.temp", snapshot.Cpu.Temperature, ValueFormatter.Temperature(snapshot.Cpu.Temperature), "摄氏度");
        SetTile("cpu.freq", snapshot.Cpu.FrequencyMhz, ValueFormatter.FrequencyGigahertz(snapshot.Cpu.FrequencyMhz), ModeFormatter.FrequencyBasisText(snapshot.Cpu.FrequencyBasis));
        SetTile("cpu.fan", snapshot.Cpu.Fan.Rpm, ValueFormatter.Fan(snapshot.Cpu.Fan, fanMode), ValueFormatter.FanFallbackNote(snapshot.Cpu.Fan, fanMode) ?? "含目标输出");
        SetTile("cpu.power", snapshot.Cpu.PackagePowerWatt, ValueFormatter.Power(snapshot.Cpu.PackagePowerWatt), "封装功耗");

        SetTile("gpu.temp", snapshot.Gpu.Temperature, ValueFormatter.Temperature(snapshot.Gpu.Temperature), snapshot.Gpu.IsSleeping ? "独显休眠" : "摄氏度");
        SetTile("gpu.freq", snapshot.Gpu.CoreFrequencyMhz, ValueFormatter.FrequencyGigahertz(snapshot.Gpu.CoreFrequencyMhz), "核心频率");
        SetTile("gpu.fan", snapshot.Gpu.Fan.Rpm, ValueFormatter.Fan(snapshot.Gpu.Fan, fanMode), ValueFormatter.FanFallbackNote(snapshot.Gpu.Fan, fanMode) ?? "含目标输出");
        SetTile("gpu.power", snapshot.Gpu.PowerWatt, ValueFormatter.Power(snapshot.Gpu.PowerWatt), "整卡功耗");
    }

    private void SetTile(string key, SensorReading reading, string value, string note)
    {
        if (_tiles.TryGetValue(key, out var tile))
        {
            tile.SetValue(value, note, ToneMap.Of(reading.Status));
        }
    }

    private async Task RequestPerformanceAsync(PerformanceStage stage)
    {
        // 与主窗口同一条判据：成功只留一句「已切换到「平衡」」，逐项明细进日志。
        // 两个窗口的提示语必须一致，否则同一个操作在两处给出两种说法。
        await ExecuteRequestAsync(
            () => _context.Control.RequestPerformanceStageAsync(stage, CancellationToken.None),
            $"切换性能模式到 {ModeFormatter.StageText(stage)}",
            result => StageSwitchFeedback.SuccessText(result, stage)).ConfigureAwait(true);
    }

    private async Task RequestGraphicsAsync(GraphicsMode mode)
    {
        await ExecuteRequestAsync(
            () => _context.Control.RequestGraphicsModeAsync(mode, CancellationToken.None),
            $"切换显卡模式到 {ModeFormatter.GraphicsText(mode)}").ConfigureAwait(true);
    }

    private async Task ExecuteRequestAsync(
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
                ShowResult(summary, MetricTone.Normal);
                return;
            }

            ShowResult(
                operation + "：" + ResultStatusText(result),
                result.Settings.All(s => s.Status == OperationStatus.Succeeded) ? MetricTone.Normal : MetricTone.Warning);
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Error, "Control", $"{operation} 抛出异常：{ex.Message}");
            ShowResult(operation + " 失败：" + ex.Message, MetricTone.Danger);
        }
    }

    /// <summary>
    /// 展示一行操作结果。
    /// </summary>
    /// <remarks>
    /// 该行常驻且高度固定：精简后的窗口高度是按内容算出来的，
    /// 结果行若「出现即增高」会把窗口撑出滚动条。
    /// </remarks>
    /// <summary>整组状态的中文说法（直接 ToString 会显示 PartiallySucceeded 这类枚举名）。</summary>
    private static string ResultStatusText(ControlResult result) => result.Status switch
    {
        OperationStatus.Succeeded => "成功",
        OperationStatus.PartiallySucceeded => "部分成功",
        OperationStatus.PendingRestart => "待重启生效",
        _ => "未完成"
    };

    private void ShowResult(string text, MetricTone tone)
    {
        _lblResult.Text = text;
        _lblResult.ForeColor = MetricTile.ToneColor(_palette, tone);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e is not null)
        {
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    HideToTray();
                    e.Handled = true;
                    break;

                case Keys.F5:
                    RefreshFromLatest();
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
            _context.WriteLog(LogLevel.Info, "Shell", "迷你窗口已按 Esc 隐藏到托盘，采样与控制继续运行。");
            Hide();
            return;
        }

        ShowResult("当前设置为「关闭窗口即退出」，请从托盘菜单退出程序。", MetricTone.Warning);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason != CloseReason.UserClosing)
        {
            return;
        }

        if (_context.Settings.CloseToTray)
        {
            e.Cancel = true;
            _context.WriteLog(LogLevel.Info, "Shell", "迷你窗口已关闭到托盘，采样与控制继续运行。");
            Hide();
            return;
        }

        // 同正常窗口：先取消关闭，由宿主决定是否真的退出。
        e.Cancel = true;
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _context.Snapshots.Updated -= OnSnapshotUpdated;
            _reasonTip.Dispose();
        }

        base.Dispose(disposing);
    }
}
