using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Shell;
using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Windows.Controls;
using MsiControl.Fans;
using MsiControl.Persistence.Configuration;
using MsiControl.Persistence.Logging;

namespace MsiControl.Desktop.Windows;

/// <summary>
/// 主窗口内嵌的风扇曲线面板：只做手动曲线，按「五个性能档位 + 自定义」六个槽位分别保存
/// CPU / GPU 的七节点曲线参数，并把选定槽位下发到软件曲线引擎。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么取代独立窗口（叶哥 2026-10-29 要求）：</b>风扇控制只需手动一条路——
/// 曲线参数与功耗配置本就是同一件事的两面，放在同一张卡片的左右两列即可读完，
/// 不必再开窗口。原窗口的「自动 / 手动」切换、配置档命名 / 导入 / 导出 / 排序 / 标签
/// 全部移除：模式只剩下手动，配置档概念由六个固定槽位取代。
/// </para>
/// <para>
/// <b>能力边界（任务书 §16 规则 11）：</b>面板不接触任何硬件接口。占空比下发由应用层装配的
/// <see cref="FanCurveEngine"/> 完成（引擎自带能力门控与写白名单检查）；
/// 切到自定义曲线走控制协调器（EC 0xD4=0x8D）。
/// </para>
/// <para>
/// <b>槽位语义：</b>「节能 / 安静 / 平衡 / 高性能 / 狂暴」与 <see cref="PerformanceStage"/>
/// 一一对应，切换性能档位时槽位跟随；引擎运行中切档位会立即换用该档曲线（每档独立曲线的
/// 意义所在）。「自定义」不绑定档位，供临时调参使用。
/// </para>
/// <para>
/// <b>回退路径（2026-10-31 调整）：</b>按叶哥要求移除「释放控制」按钮，操作行只保留
/// 「应用曲线 / 还原」。停引擎交还固件的动作仍在应用内保留（退出程序时引擎随宿主停止，
/// 切档位也会按档位改 EC 0xD4），只是不再由本面板提供单独入口。
/// </para>
/// </remarks>
internal sealed class FanCurvePanel : UserControl, IThemeAware
{
    /// <summary>每个通道的曲线节点数（与固件占空比通道的七点结构一致）。</summary>
    private const int NodeCount = 7;

    /// <summary>
    /// 新槽位的默认温度节点（°C）。
    /// </summary>
    /// <remarks>
    /// 沿用原曲线窗口的默认值（叶哥 2026-10-17 认可用作起点），并非本机固件曲线数值——
    /// 任务书 §7.2 明确不得把截图或其他机型的曲线当作本机出厂曲线。
    /// </remarks>
    private static readonly double[] DefaultTemperaturesC = { 0, 50, 55, 60, 65, 70, 75 };

    /// <summary>新槽位的默认占空比节点（%）。</summary>
    private static readonly double[] DefaultOutputsPercent = { 0, 9, 17, 35, 40, 45, 100 };

    private readonly ShellContext _context;

    /// <summary>操作结果回执（投递到主窗口的「操作结果」行）。</summary>
    private readonly Action<string, MetricTone>? _report;

    private readonly FanCurveEngine? _engine;
    private readonly FanCurveProfileStore? _store;

    // 槽位选择用分段轨道而不是 ComboBox：WinForms 的下拉按钮由系统绘制，
    // 深色主题下会露出一块纯白方块（实机截图），是整块面板里最跳的一处。
    // 六个槽位名字都很短（节能 / 安静 / 平衡 / 高性能 / 狂暴 / 自定义），轨道放得下。
    private readonly SegmentedGroup _slotGroup = new();
    private readonly Dictionary<FanCurveSlot, Button> _slotButtons = new();
    private SegmentedRail? _slotRail;

    // 文本不带助记键：125% DPI 下「CPU(&1)」会被按钮宽度截断，助记改由可访问说明承担。
    private readonly Button _btnCpu = UiFactory.CreateSegmentButton("CPU", width: 54);
    private readonly Button _btnGpu = UiFactory.CreateSegmentButton("GPU", width: 54);
    private readonly SegmentedGroup _channelGroup = new();
    private SegmentedRail? _channelRail;
    private readonly NumericField[] _temperatureInputs = new NumericField[NodeCount];
    private readonly FanSlider[] _dutySliders = new FanSlider[NodeCount];
    private readonly Label[] _dutyReadouts = new Label[NodeCount];
    private readonly Button _btnApply = UiFactory.CreatePrimaryButton("应用曲线(&P)");
    private readonly Button _btnReset = UiFactory.CreateButton("还原(&R)");
    private readonly System.Windows.Forms.Timer _statusTimer;

    /// <summary>上次写入日志的状态消息：内容不变时不重复记录。</summary>
    private string _lastStateMessage = string.Empty;

    /// <summary>编辑器缓冲：下标 0 = CPU，1 = GPU。</summary>
    private readonly double[][] _temperatures =
    {
        (double[])DefaultTemperaturesC.Clone(),
        (double[])DefaultTemperaturesC.Clone()
    };

    private readonly double[][] _duties =
    {
        (double[])DefaultOutputsPercent.Clone(),
        (double[])DefaultOutputsPercent.Clone()
    };

    private FanCurveProfileFile _file = new();
    private FanChannel _channel = FanChannel.Cpu;
    private FanCurveSlot _slot = FanCurveSlot.Balanced;
    private PerformanceStage? _lastFollowedStage;

    /// <summary>程序回填编辑器期间置位：屏蔽滑块/数值框的变更事件，避免回填被当成用户编辑。</summary>
    private bool _syncingEditors;
    private bool _gated;
    private DateTimeOffset _stateHoldUntil = DateTimeOffset.MinValue;
    private ThemePalette _palette = ThemePalette.Light;

    /// <summary>构造风扇曲线面板。</summary>
    /// <param name="context">共享上下文。</param>
    /// <param name="report">操作结果回执；可为空（仅写日志与面板状态行）。</param>
    public FanCurvePanel(ShellContext context, Action<string, MetricTone>? report = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _report = report;
        _engine = context.FanCurve?.Engine;
        _store = context.FanCurve?.Profiles;

        // Dock=Top + AutoSize：本面板放在两列网格的单元格里，若用 Dock=Fill 则
        // 不参与父网格的 AutoSize 高度计算，卡片会把下方几行节点裁掉（首版实测只剩 4 行）。
        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = Padding.Empty;
        AccessibleName = "风扇曲线（手动控制）";

        // 自绘滑块一旦抛异常，界面只会表现为「某一块画错了」，日志里什么都没有；
        // 这里接上宿主的日志，出问题时能直接从 msi-control.log 看到异常类型。
        FanSlider.Diagnostics ??= message => _context.WriteLog(LogLevel.Warning, "FanCurve", message);

        _channelGroup.Add(_btnCpu);
        _channelGroup.Add(_btnGpu);
        UiFactory.EnableArrowKeyNavigation(new[] { _btnCpu, _btnGpu });

        BuildLayout();
        BindEvents();
        ApplyEntryGating();

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => RefreshEngineStatus();
        _statusTimer.Start();
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        _palette = palette;

        // 「应用曲线」是 PrimaryButton：强调色实底由 ThemeApplier 的那条分支负责，
        // 这里不再手工刷色。此前的写法是「换肤后再刷一遍」，一旦某个入口忘记重刷
        // 就会得到一个长得像次按钮的主按钮，正确性完全依赖调用顺序。
        _btnApply.Height = UiFactory.DesignTokens.Height.ButtonPrimary;
        _channelGroup.TrackColor = _channelRail?.TrackColor;
        _channelGroup.ApplyPalette(palette);

        _slotGroup.TrackColor = _slotRail?.TrackColor;
        _slotGroup.ApplyPalette(palette);

        // 占空比读数是本面板最重要的数字，用强调色 + 加粗把它从七列里拎出来。
        foreach (var readout in _dutyReadouts)
        {
            if (readout is null)
            {
                continue;
            }

            readout.ForeColor = palette.Accent;
            readout.Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Body, FontStyle.Bold);
        }

        // 滑块是 UserPaint 控件，主题遍历不认识它，必须显式推送调色板。
        foreach (var slider in _dutySliders)
        {
            slider.ApplyPalette(palette);
        }
    }

    // ---------- 状态与日志 ----------

    /// <summary>
    /// 性能档位读回变化时调用：槽位跟随该档位；引擎运行中则立即换用该档曲线。
    /// </summary>
    public void FollowStage(PerformanceStage stage)
    {
        if (_lastFollowedStage == stage)
        {
            return;
        }

        _lastFollowedStage = stage;
        var slot = FanCurveSlots.ForStage(stage);
        if (_slot != slot)
        {
            SelectSlot(slot, loadEditors: true);
        }

        if (_engine?.IsRunning == true)
        {
            ApplySlot(slot, $"性能档位切到{ModeFormatter.StageText(stage)}，风扇曲线随之切换");
        }
    }

    // ---------- 布局 ----------

    private void BuildLayout()
    {
        var stack = UiFactory.CreateStack();
        stack.Dock = DockStyle.Top;
        // 底部留白：AutoSize 链（面板 → 两列网格 → 卡片）在 125% DPI 下会把最后一行
        // 状态文字压掉一半，实测需要这 8px 才能完整露出来。
        stack.Padding = new Padding(0, 0, 0, UiFactory.DesignTokens.Space.Sm);

        // 组标题已按叶哥 2026-10-31 要求完全移除（三步：去 0% 说明 → 去括号 → 去整行）。
        // 0% 的实际语义（本机固件不接受 0%，引擎解释为「交还固件调速」）只由引擎日志说明。

        var slotRail = new SegmentedRail(FanCurveSlots.All.Count);
        _slotRail = slotRail;
        var slotIndex = 0;
        foreach (var slot in FanCurveSlots.All)
        {
            var button = UiFactory.CreateSegmentButton(SlotLabel(slot));
            button.AccessibleName = "风扇曲线槽位：" + SlotLabel(slot);
            button.AccessibleDescription = "选择要编辑的曲线槽位：五个性能档位各一条，另有一条自定义。";
            var captured = slot;
            button.Click += (_, _) => SelectSlot(captured, loadEditors: true);
            _slotButtons[slot] = button;
            _slotGroup.Add(button);
            slotRail.Place(button, slotIndex++);
        }

        // 槽位轨道占满整行：六个槽位名字最长的「高性能」「自定义」是三个字，
        // 与通道轨道挤在同一行时每段只剩约 50px，文字会被截成「高」「自定」（实机截图确认）。
        slotRail.Margin = Padding.Empty;
        UiFactory.AddRow(stack, slotRail);

        // 通道跟槽位一样是「二选一」，用同一种轨道表达；只有两颗按钮，定宽左对齐。
        var channelRail = new SegmentedRail(2)
        {
            Dock = DockStyle.None,
            Anchor = AnchorStyles.Left,
            Size = new Size(140, slotRail.Height),
            Margin = new Padding(0, UiFactory.DesignTokens.Space.Xs, 0, 0)
        };
        _channelRail = channelRail;
        channelRail.Place(_btnCpu, 0);
        channelRail.Place(_btnGpu, 1);
        UiFactory.AddRow(stack, channelRail);

        // selectorRow 已不再使用（保留变量会触发未使用告警，故直接不创建）。

        UiFactory.AddRow(stack, BuildCurveNodeColumns());
        UiFactory.AddRow(stack, BuildActionRow());

        // 状态文字（控制模式 / 引擎运行状态）已按叶哥 2026-10-31 要求从界面移除：
        // 同样的信息仍由 SetState 写入 msi-control.log，不再在面板上占两行。
        Controls.Add(stack);
    }

    /// <summary>
    /// 曲线调节区：七个节点各占一列——顶部占空比读数、中间竖向滑块、底部温度数值输入。
    /// </summary>
    /// <remarks>
    /// 叶哥 2026-10-30 要求「温度手动输入 + 转速拖动滑块，同区协同展示」：
    /// 温度是曲线的自变量，用数值框精确填写；占空比是结果，用滑块直观拖动。
    /// 两者共用同一份节点模型（<see cref="_temperatures"/> / <see cref="_duties"/>），
    /// 切换通道或槽位时一起重填，不出现「滑块动了但保存值没动」的脱节。
    /// </remarks>
    private Control BuildCurveNodeColumns()
    {
        var table = UiFactory.CreateGrid(NodeCount);
        table.ColumnStyles.Clear();
        for (var column = 0; column < NodeCount; column++)
        {
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / NodeCount));
        }

        table.RowCount = 3;
        table.RowStyles.Clear();
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 22f));   // 占空比读数
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 104f));  // 竖向滑块
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));   // 温度输入

        for (var index = 0; index < NodeCount; index++)
        {
            var readout = new Label
            {
                Text = DefaultOutputsPercent[index].ToString("0", System.Globalization.CultureInfo.CurrentCulture) + "%",
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = UiFactory.Scale(UiFactory.BaseFont, UiFactory.DesignTokens.Type.Body, FontStyle.Bold),
                Margin = Padding.Empty
            };
            _dutyReadouts[index] = readout;
            table.Controls.Add(readout, index, 0);

            var slider = new FanSlider
            {
                Minimum = 0,
                Maximum = 100,
                SmallChange = 1,
                LargeChange = 5,
                Value = (int)DefaultOutputsPercent[index],
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 2),
                AccessibleName = $"第 {index + 1} 节点风扇转速百分比",
                AccessibleDescription = "拖动或用方向键调整该节点的风扇转速百分比；对应温度由下方数值框填写。"
            };
            var node = index;
            slider.ValueChanged += (_, _) => OnDutySliderChanged(node);
            _dutySliders[index] = slider;
            table.Controls.Add(slider, index, 1);

            // 自绘数值框：NumericUpDown 的内部箭头是系统绘制的浅色方块，隐藏后又会留下
            // 一块不绘制的空腔透出卡片底色——两种状态都修不干净，故改用圆角外框 + 单层编辑框。
            // 左右各留 3px：七列等分后每格 108px，框与框原本零间距，两个圆角贴在一起
            // 会互相吃掉对方的弧线，看起来只有左边是圆的。
            var temperature = new NumericField
            {
                Minimum = -20m,
                Maximum = 130m,
                DecimalPlaces = 0,
                TextAlign = HorizontalAlignment.Center,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 2, 3, 3),
                AccessibleName = $"第 {index + 1} 节点温度（°C）",
                AccessibleDescription = "该节点的温度阈值，单位摄氏度；必须严格高于前一节点。"
            };
            temperature.Value = ClampTemperature(DefaultTemperaturesC[index]);
            temperature.ValueChanged += (_, _) => OnTemperatureChanged(node);
            _temperatureInputs[index] = temperature;
            table.Controls.Add(temperature, index, 2);
        }

        table.Margin = new Padding(0, UiFactory.DesignTokens.Space.Xs, 0, UiFactory.DesignTokens.Space.Sm);
        return table;
    }

    /// <summary>滑块拖动：写入当前通道缓冲，并刷新顶部读数。</summary>
    private void OnDutySliderChanged(int node)
    {
        if (_syncingEditors)
        {
            return;
        }

        var channelIndex = _channel == FanChannel.Cpu ? 0 : 1;
        _duties[channelIndex][node] = _dutySliders[node].Value;
        _dutyReadouts[node].Text =
            _dutySliders[node].Value.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>温度数值框：写入当前通道缓冲（温度不改占空比，映射关系由应用时的曲线插值决定）。</summary>
    private void OnTemperatureChanged(int node)
    {
        if (_syncingEditors)
        {
            return;
        }

        var channelIndex = _channel == FanChannel.Cpu ? 0 : 1;
        _temperatures[channelIndex][node] = (double)_temperatureInputs[node].Value;
    }

    private Control BuildActionRow()
    {
        var row = UiFactory.CreateRow(wrap: false);
        _btnApply.Margin = new Padding(0, 0, UiFactory.DesignTokens.Space.Sm, 0);
        // 「释放控制」已按叶哥 2026-10-31 要求移除：操作行只留「应用曲线」与「还原」，
        // 前者是主操作，后者的作用域就是编辑器本身，两者语义不重叠。
        _btnReset.Margin = Padding.Empty;
        row.Controls.Add(_btnApply);
        row.Controls.Add(_btnReset);
        return row;
    }

    private void BindEvents()
    {
        _btnCpu.Click += (_, _) => SwitchChannel(FanChannel.Cpu);
        _btnGpu.Click += (_, _) => SwitchChannel(FanChannel.Gpu);

        _btnApply.Click += (_, _) => _ = ApplyFromEditorAsync();
        _btnReset.Click += (_, _) => ResetSlotFromStore();
    }

    // ---------- 状态 ----------

    private void ApplyEntryGating()
    {
        if (_engine is null || _store is null)
        {
            _gated = true;
            SetState("风扇曲线功能未装配：应用宿主未提供软件曲线引擎，全部编辑入口保持禁用。");
            DisableAll();
            return;
        }

        var entry = _context.DescribeCapability(CapabilityKeys.FanCurve, requiresWrite: true);
        if (!entry.Enabled)
        {
            _gated = true;
            SetState("曲线编辑已禁用：" + entry.Reason);
            DisableAll();
            return;
        }

        _gated = false;
        LoadStore();
        SelectSlot(FanCurveSlot.Balanced, loadEditors: true);
        _channelGroup.SetActive(_btnCpu);
        RefreshEngineStatus();
    }

    private void DisableAll()
    {
        foreach (var control in new Control[]
                 {
                     _btnCpu, _btnGpu, _btnApply, _btnReset
                 }.Concat(_slotButtons.Values))
        {
            control.Enabled = false;
        }

        foreach (var input in _temperatureInputs.Cast<Control>().Concat(_dutySliders))
        {
            input.Enabled = false;
        }
    }

    private void SetState(string message)
    {
        if (_lastStateMessage == message)
        {
            return;
        }

        _lastStateMessage = message;
        _context.WriteLog(LogLevel.Debug, "FanCurve", message);
    }

    private void Report(string message, MetricTone tone)
    {
        // 操作结果在状态行保留数秒，避免被 1 秒周期的引擎状态刷新立刻覆盖。
        _stateHoldUntil = DateTimeOffset.Now.AddSeconds(4);
        SetState(message);
        _report?.Invoke(message, tone);
    }

    private void RefreshEngineStatus()
    {
        if (_engine is null || _gated)
        {
            return;
        }

        // 刚发生显式操作时让操作结果停留一会儿，不做周期覆盖。
        if (DateTimeOffset.Now < _stateHoldUntil)
        {
            return;
        }

        if (!_engine.IsRunning)
        {
            var reason = _engine.AutoStopReason;
            SetState(reason is null
                ? "引擎未运行：编辑后按「应用曲线」下发。"
                : reason + " 固件将在数秒内用自身曲线接管。");
            return;
        }

        var parts = new List<string>();
        foreach (var (channel, snapshot) in _engine.LastSnapshots.OrderBy(kv => kv.Key))
        {
            var name = channel == FanChannel.Cpu ? "CPU" : "GPU";
            if (snapshot.ReleasedToFirmware)
            {
                // 不写 0%（固件会把 0 回写成更高值，导致风扇一停一转）：该通道交还固件。
                parts.Add($"{name} 已交还固件（曲线目标 0%）");
                continue;
            }

            parts.Add(
                snapshot.TemperatureC is { } temperature
                    ? $"{name} {snapshot.Percent:0}% @ {temperature:0}°C"
                    : $"{name} {snapshot.Percent:0}%（温度暂不可用，保持上次输出）");
        }

        SetState(parts.Count == 0
            ? "引擎运行中，等待首个下发周期。"
            : "引擎运行中：" + string.Join("；", parts) + "。");
    }

    // ---------- 槽位与编辑器 ----------

    private void LoadStore()
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            _file = _store.Load();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetState($"曲线配置读取失败，本次以默认节点开始：{ex.Message}");
            _file = new FanCurveProfileFile();
        }
    }

    private void SelectSlot(FanCurveSlot slot, bool loadEditors)
    {
        _slot = slot;

        // 活动段由分组统一维护：高亮只是「当前编辑的是哪一档」，
        // 不代表固件已接受该档曲线——后者以引擎状态与读回为准。
        _slotGroup.SetActive(_slotButtons.TryGetValue(slot, out var button) ? button : null);

        if (loadEditors)
        {
            LoadSlotIntoBuffer(slot);
            SyncBufferToEditors(_channel);
        }
    }

    private void LoadSlotIntoBuffer(FanCurveSlot slot)
    {
        var hasStored = _file.SlotCurves.TryGetValue(FanCurveSlots.KeyOf(slot), out var stored);
        for (var channelIndex = 0; channelIndex < 2; channelIndex++)
        {
            var points = channelIndex == 0 ? stored?.CpuPoints : stored?.GpuPoints;
            for (var node = 0; node < NodeCount; node++)
            {
                var fallbackT = DefaultTemperaturesC[node];
                var fallbackO = DefaultOutputsPercent[node];
                _temperatures[channelIndex][node] = hasStored && points is { Count: NodeCount }
                    ? points[node].T
                    : fallbackT;
                _duties[channelIndex][node] = hasStored && points is { Count: NodeCount }
                    ? points[node].O
                    : fallbackO;
            }
        }

        if (!hasStored)
        {
            SetState($"槽位「{SlotLabel(slot)}」尚无已保存曲线，当前显示默认节点（应用后即保存到该槽位）。");
        }
        else
        {
            SetState($"已载入槽位「{SlotLabel(slot)}」的已保存曲线。");
        }
    }

    private void SwitchChannel(FanChannel channel)
    {
        if (_channel == channel)
        {
            return;
        }

        SyncEditorsToBuffer(_channel);
        _channel = channel;
        SyncBufferToEditors(channel);
        _channelGroup.SetActive(channel == FanChannel.Cpu ? _btnCpu : _btnGpu);
    }

    private void SyncEditorsToBuffer(FanChannel channel)
    {
        var index = channel == FanChannel.Cpu ? 0 : 1;
        for (var node = 0; node < NodeCount; node++)
        {
            _temperatures[index][node] = (double)_temperatureInputs[node].Value;
            _duties[index][node] = _dutySliders[node].Value;
        }
    }

    private void SyncBufferToEditors(FanChannel channel)
    {
        var index = channel == FanChannel.Cpu ? 0 : 1;

        // 回填期间屏蔽变更事件：滑块与数值框的 Value 赋值都会触发 ValueChanged，
        // 若不拦住，回填过程会即时把「上一个通道的旧值」写回当前通道缓冲。
        _syncingEditors = true;
        try
        {
            for (var node = 0; node < NodeCount; node++)
            {
                _temperatureInputs[node].Value = ClampTemperature(_temperatures[index][node]);

                var duty = (int)Math.Clamp(Math.Round(_duties[index][node]), 0, 100);
                _dutySliders[node].Value = duty;
                _dutyReadouts[node].Text =
                    duty.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
            }
        }
        finally
        {
            _syncingEditors = false;
        }

        _context.WriteLog(
            LogLevel.Debug,
            "FanCurve",
            $"编辑器回填完成（{channel}）：温度 [{string.Join(",", _temperatures[index].Select(v => v.ToString("0")))}]；" +
            $"占空比 [{string.Join(",", _duties[index].Select(v => v.ToString("0")))}]；" +
            $"读数 [{string.Join(",", _dutyReadouts.Select(r => r.Text))}]。");
    }

    private void ResetSlotFromStore()
    {
        LoadSlotIntoBuffer(_slot);
        SyncBufferToEditors(_channel);
        Report($"已还原槽位「{SlotLabel(_slot)}」为已保存值。", MetricTone.Muted);
    }

    // ---------- 应用 ----------

    private async Task ApplyFromEditorAsync()
    {
        if (_engine is null || _store is null)
        {
            return;
        }

        SyncEditorsToBuffer(_channel);

        var (set, errors) = BuildCurveSet();
        if (set is null)
        {
            Report("曲线校验未通过，未应用：" + string.Join(" ", errors), MetricTone.Warning);
            return;
        }

        var combination = set.Validate();
        if (combination.Count > 0)
        {
            Report("曲线校验未通过，未应用：" + string.Join(" ", combination), MetricTone.Warning);
            return;
        }

        // 手动 = EC 先切到自定义曲线（0xD4=0x8D），引擎随后按周期下发占空比。
        var modeResult = await RequestFanModeAsync(FanControlMode.CustomCurve, "切换风扇模式到自定义曲线（手动）")
            .ConfigureAwait(true);
        if (modeResult is null)
        {
            Report("模式切换请求失败，曲线未应用。详见日志。", MetricTone.Warning);
            return;
        }

        if (!_engine.Start(set))
        {
            Report(_engine.AutoStopReason ?? "曲线引擎未能启动。", MetricTone.Warning);
            return;
        }

        SaveSlotToStore(_slot, set);
        RefreshEngineStatus();
        Report(
            $"槽位「{SlotLabel(_slot)}」曲线已应用：软件引擎每 {_engine.Interval.TotalSeconds:0} 秒下发一次占空比" +
            "（固件会回写自身曲线，由引擎持续纠正）。",
            MetricTone.Normal);
    }

    /// <summary>换用某个已保存槽位的曲线（档位联动时调用，不再重复切 EC 模式）。</summary>
    private void ApplySlot(FanCurveSlot slot, string reason)
    {
        if (_engine is null || !_engine.IsRunning)
        {
            return;
        }

        var hasStored = _file.SlotCurves.TryGetValue(FanCurveSlots.KeyOf(slot), out var stored);
        if (!hasStored)
        {
            SetState($"档位对应的槽位「{SlotLabel(slot)}」尚无已保存曲线，引擎继续使用当前曲线。");
            return;
        }

        var (set, errors) = BuildCurveSetFromPoints(stored!.CpuPoints, stored.GpuPoints);
        if (set is null)
        {
            SetState($"槽位「{SlotLabel(slot)}」的曲线校验未通过，引擎继续使用当前曲线：" + string.Join(" ", errors));
            return;
        }

        if (!_engine.Start(set))
        {
            SetState(_engine.AutoStopReason ?? "曲线引擎未能启动。");
            return;
        }

        SetState($"{reason}：引擎已换用槽位「{SlotLabel(slot)}」。");
        _context.WriteLog(LogLevel.Info, "FanCurve", $"{reason}；已换用槽位「{SlotLabel(slot)}」的曲线。");
    }

    private async Task<ControlResult?> RequestFanModeAsync(FanControlMode mode, string operation)
    {
        try
        {
            var result = await _context.Control
                .RequestFanModeAsync(mode, CancellationToken.None)
                .ConfigureAwait(true);
            _context.LogControlResult(result, operation);
            return result;
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Error, "FanCurve", $"{operation} 失败：{ex.Message}");
            return null;
        }
    }

    private (FanCurveSet? Set, IReadOnlyList<string> Errors) BuildCurveSet()
    {
        // 两个通道都要校验：一次应用会同时下发 CPU 与 GPU，任一通道非法都不能部分生效。
        var cpu = Enumerable.Range(0, NodeCount)
            .Select(node => new FanPoint(_temperatures[0][node], _duties[0][node]));
        var gpu = Enumerable.Range(0, NodeCount)
            .Select(node => new FanPoint(_temperatures[1][node], _duties[1][node]));

        var ok = FanCurveSet.TryCreate(cpu, gpu, out var set, out var errors);
        return ok ? (set, errors) : (null, errors);
    }

    private static (FanCurveSet? Set, IReadOnlyList<string> Errors) BuildCurveSetFromPoints(
        IReadOnlyList<FanProfilePoint> cpuPoints,
        IReadOnlyList<FanProfilePoint> gpuPoints)
    {
        if (cpuPoints.Count != NodeCount || gpuPoints.Count != NodeCount)
        {
            return (null, new[]
            {
                $"槽位曲线节点数为 CPU {cpuPoints.Count} / GPU {gpuPoints.Count}，本面板固定为 {NodeCount} 个。"
            });
        }

        var ok = FanCurveSet.TryCreate(
            cpuPoints.Select(p => new FanPoint(p.T, p.O)),
            gpuPoints.Select(p => new FanPoint(p.T, p.O)),
            out var set,
            out var errors);
        return ok ? (set, errors) : (null, errors);
    }

    private void SaveSlotToStore(FanCurveSlot slot, FanCurveSet set)
    {
        if (_store is null)
        {
            return;
        }

        var profile = new FanCurveProfile(
            Guid.NewGuid(),
            SlotLabel(slot),
            "槽位",
            DateTimeOffset.Now,
            set.Cpu.Points.Select(p => new FanProfilePoint(p.TemperatureC, p.OutputPercent)).ToList(),
            set.Gpu.Points.Select(p => new FanProfilePoint(p.TemperatureC, p.OutputPercent)).ToList());

        var curves = new Dictionary<string, FanCurveProfile>(_file.SlotCurves, StringComparer.Ordinal)
        {
            [FanCurveSlots.KeyOf(slot)] = profile
        };

        _file = _file with { SlotCurves = curves };

        try
        {
            _store.Save(_file);
            _context.WriteLog(LogLevel.Info, "FanCurve", $"槽位「{SlotLabel(slot)}」曲线已保存到 {_store.FilePath}。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _context.WriteLog(LogLevel.Warning, "FanCurve", $"槽位曲线保存失败：{ex.Message}");
        }
    }

    private static string SlotLabel(FanCurveSlot slot)
        => FanCurveSlots.StageOf(slot) is { } stage ? ModeFormatter.StageText(stage) : "自定义";

    private static decimal ClampTemperature(double temperature) => Math.Clamp((decimal)temperature, -20, 130);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Stop();
            _statusTimer.Dispose();
        }

        base.Dispose(disposing);
    }

}
