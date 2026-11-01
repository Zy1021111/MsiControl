using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Resources;
using MsiControl.Desktop.Shell;
using MsiControl.Desktop.Theme;
using MsiControl.Desktop.Windows.Controls;
using MsiControl.Persistence.Configuration;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Windows;

/// <summary>
/// 设置与诊断窗口。
/// </summary>
/// <remarks>
/// 按任务书 §12 只保留三组：启动与窗口、数据与配置、设备与诊断。
/// 风扇与性能的详细配置不在此处，而在对应编辑区域管理（design.md §12.2）。
/// </remarks>
public sealed class SettingsForm : Form, IThemeAware
{
    private readonly ShellContext _context;
    private readonly TableLayoutPanel _stack = UiFactory.CreateStack();

    private readonly CheckBox _chkLaunchOnStartup = new() { Text = "开机启动", AutoSize = true };
    private readonly CheckBox _chkCloseToTray = new() { Text = "关闭窗口时进入托盘", AutoSize = true };
    private readonly ComboBox _cmbStartupWindow = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly NumericUpDown _numRefresh = new()
    {
        Minimum = AppSettings.MinRefreshIntervalSeconds,
        Maximum = AppSettings.MaxRefreshIntervalSeconds,
        Width = 70
    };

    // 选项文案「百分比（需有效校准）」比下拉框默认宽度长，固定 120px 时会被截断成
    // 「百分比（需有…」。这里按最长项留足宽度，并让下拉列表宽度与框体一致。
    private readonly ComboBox _cmbFanDisplay = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 180,
        DropDownWidth = 200
    };
    private readonly HotkeyEditor _showHideEditor = new("显示/隐藏窗口");
    private readonly HotkeyEditor _fullSpeedEditor = new("强冷切换");

    private readonly TextBox _txtDevice = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 130,
        Dock = DockStyle.Top
    };

    private readonly ListBox _lstCapabilities = new() { Height = 210, Dock = DockStyle.Top };

    private readonly ListBox _lstProfiles = new() { Height = 110, Dock = DockStyle.Top };

    // ── 设备控制与显示电源（2026-10-31，HWE §31）─────────────────────────
    // 这些开关**即时生效**（不随「保存设置」提交）：它们直接读写固件/显示模式，
    // 与性能表、风扇曲线同类；放进「保存」队列会制造「界面已勾选但硬件未变」的窗口期。
    private readonly CheckBox _chkStartMinimized = new() { Text = "开机后静默驻留后台", AutoSize = true };
    private readonly CheckBox _chkWinFnSwap = new() { Text = "交换 Win / Fn 键", AutoSize = true };
    private readonly CheckBox _chkWindowsKey = new() { Text = "屏蔽 Win 键（仅本程序运行期间）", AutoSize = true };
    private readonly CheckBox _chkTouchpad = new() { Text = "启用触摸板", AutoSize = true };
    private readonly CheckBox _chkBacklight = new() { Text = "键盘背光总开关", AutoSize = true };
    private readonly CheckBox _chkDisplaySaver = new() { Text = "显示省电（使用最低刷新率）", AutoSize = true };
    private readonly CheckBox _chkUsbShare = new() { Text = "USB 关机共享供电", AutoSize = true };
    private readonly Label _lblDeviceState = UiFactory.CreateMutedLabel();
    private readonly Label _lblDisplaySaverState = UiFactory.CreateMutedLabel();

    /// <summary>回填开关状态时置位，避免 <c>CheckedChanged</c> 把回填当成用户操作写回硬件。</summary>
    private bool _syncingDeviceState;

    private readonly Label _lblStartupState = UiFactory.CreateMutedLabel();
    private readonly Label _lblStatus = UiFactory.CreateMutedLabel();

    // 保存是本窗口唯一的「下一步」：用实底主按钮；关闭降为次按钮，两者因此不再同权。
    private readonly Button _btnSave = UiFactory.CreatePrimaryButton("保存设置(&S)");
    private readonly Button _btnClose = UiFactory.CreateButton("关闭(&C)");

    private ThemePalette _palette = ThemePalette.Light;

    /// <summary>构造设置窗口。</summary>
    public SettingsForm(ShellContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));

        Text = "设置与诊断";
        Icon = AppIcon.CreateWindowIcon();
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(680, 600);
        Size = new Size(780, 860);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = UiFactory.BaseFont;
        AutoScroll = true;

        BuildLayout();
        LoadFromSettings();
        LoadDiagnostics();
        RefreshProfiles();

        // 键盘操作：Enter 保存、Esc 关闭。
        AcceptButton = _btnSave;
        CancelButton = _btnClose;

        // 硬件状态必须实机读回：构造函数里读会让窗口打开变慢（WMI 调用约 100–300 ms），
        // 且窗口还没显示时用户看不到「正在读取」。放到 Shown 后异步读取。
        // 用 Click 而不是 CheckedChanged：回填状态时会改 Checked，CheckedChanged 会把回填
        // 当成用户操作，导致「打开设置页就把硬件改一遍」。Click 只在用户点选时触发。
        _chkWinFnSwap.Click += async (_, _) => await ApplyWinFnSwapAsync();
        _chkWindowsKey.Click += (_, _) => ApplyWindowsKey();
        _chkTouchpad.Click += async (_, _) => await ApplyTouchpadAsync();
        _chkBacklight.Click += async (_, _) => await ApplyBacklightAsync();
        _chkDisplaySaver.Click += async (_, _) => await ApplyDisplaySaverAsync();
        _chkUsbShare.Click += async (_, _) => await ApplyUsbPowerShareAsync();

        Shown += (_, _) => RefreshDeviceStates();

        // Fn+F4／系统设置改变触控板状态时同步勾选（桌面壳轮询注册表状态位后发布）。
        // 页面打开期间按 Fn+F4，勾选不再停留在陈旧状态。
        context.TouchpadStateChanged += OnTouchpadStateChangedExternally;
        FormClosed += (_, _) =>
            context.TouchpadStateChanged -= OnTouchpadStateChangedExternally;
    }

    private void OnTouchpadStateChangedExternally(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !_syncingDeviceState)
        {
            BeginInvoke(RefreshDeviceStates);
        }
    }

    /// <inheritdoc />
    public void ApplyPalette(ThemePalette palette)
    {
        _palette = palette;
        ThemeApplier.Apply(this, palette);
        _lblStatus.ForeColor = palette.ForeMuted;
        _stack.BackColor = palette.Back;
    }

    private void BuildLayout()
    {
        // 页面边距与主窗口同一标尺（24 / 16）：三张卡片之间以及卡片与窗口边缘的距离一致。
        _stack.Padding = new Padding(
            UiFactory.DesignTokens.Space.Xl,
            UiFactory.DesignTokens.Space.Lg,
            UiFactory.DesignTokens.Space.Xl,
            UiFactory.DesignTokens.Space.Xl);

        _cmbStartupWindow.Items.AddRange(new object[] { "正常窗口", "迷你窗口" });
        _cmbFanDisplay.Items.AddRange(new object[] { "百分比（需有效校准）", "RPM" });

        // 组 1：启动与窗口
        var startup = UiFactory.CreateStack();
        UiFactory.AddRow(startup, _chkLaunchOnStartup);
        UiFactory.AddRow(startup, _lblStartupState);
        UiFactory.AddRow(startup, UiFactory.CreateMutedLabel(
            StartupRegistration.CanRegister
                ? "开机启动写入当前用户 Run 项（不写系统级、不请求管理员权限）；取消勾选并保存即删除该项。"
                : "当前以开发主机运行，保存时不会写入开机启动项（避免留下无效启动项）。"));

        UiFactory.AddRow(startup, _chkCloseToTray);
        UiFactory.AddRow(startup, UiFactory.CreateMutedLabel(
            "勾选时：关闭窗口 / Esc 只隐藏到托盘，采样与控制继续。取消勾选时：关闭窗口即结束进程。"));
        UiFactory.AddRow(startup, CreateField("启动时显示：", _cmbStartupWindow));
        UiFactory.AddRow(startup, _chkStartMinimized);
        UiFactory.AddRow(startup, UiFactory.CreateMutedLabel(
            "勾选后：开机自启这一次直接进托盘，不弹窗口；手动双击图标启动仍显示窗口。"));
        UiFactory.AddRow(startup, _showHideEditor.Build());
        UiFactory.AddRow(startup, _fullSpeedEditor.Build());
        UiFactory.AddRow(startup, UiFactory.CreateMutedLabel(
            "快捷键可关闭；若组合已被其他程序占用，程序会提示而不会强制占用。"));
        UiFactory.AddRow(_stack, UiFactory.CreateCard("启动与窗口", startup));

        // 组 2：设备控制（HWE §31）
        var devices = UiFactory.CreateStack();
        UiFactory.AddRow(devices, _chkWinFnSwap);
        UiFactory.AddRow(devices, UiFactory.CreateMutedLabel(
            "写入 MSI ACPI Data(0xE8) 的 bit4；该位由固件持久保存，重启后仍然生效。"));
        UiFactory.AddRow(devices, _chkWindowsKey);
        UiFactory.AddRow(devices, UiFactory.CreateMutedLabel(
            "软件层屏蔽（低级键盘钩子），只在本程序运行期间生效，退出即恢复；固件没有此开关。"));
        UiFactory.AddRow(devices, _chkTouchpad);
        UiFactory.AddRow(devices, UiFactory.CreateMutedLabel(
            "走 MSI 固件组合 Ctrl+Win+F24（等效 Fn+F4），状态取 Windows 精密触控板官方状态位。"));
        UiFactory.AddRow(devices, _chkBacklight);
        UiFactory.AddRow(devices, UiFactory.CreateMutedLabel(
            "写入 MSI ACPI Data(0xD3)：只改低 2 位档位，保留固件的其余位；关闭后按档位 3 恢复。"));
        UiFactory.AddRow(devices, _lblDeviceState);
        UiFactory.AddRow(_stack, UiFactory.CreateCard("设备控制", devices));

        // 组 3：显示与电源
        var power = UiFactory.CreateStack();
        UiFactory.AddRow(power, _chkDisplaySaver);
        UiFactory.AddRow(power, _lblDisplaySaverState);
        UiFactory.AddRow(power, _chkUsbShare);
        UiFactory.AddRow(power, UiFactory.CreateMutedLabel(
            "写入 MSI ACPI Data(0xBF) 的 bit5，并同步 UEFI 变量 MsiDCVarData byte[3] bit0；"
            + "UEFI 同步失败时会明确提示「重启后可能回落」，不会假装已保存。"));
        UiFactory.AddRow(_stack, UiFactory.CreateCard("显示与电源", power));

        // 组 4：数据与配置
        var data = UiFactory.CreateStack();
        UiFactory.AddRow(data, CreateField("刷新间隔（秒）：", _numRefresh));
        UiFactory.AddRow(data, CreateField("风扇显示：", _cmbFanDisplay));

        var dataButtons = UiFactory.CreateRow();
        var btnImport = UiFactory.CreateButton("导入配置(&I)");
        btnImport.AccessibleDescription = "从本地选择配置档并校验机型；导入后不会自动写入硬件。";
        btnImport.Click += (_, _) => ImportProfile();

        var btnExport = UiFactory.CreateButton("导出配置(&E)");
        btnExport.AccessibleDescription = "把当前配置导出为 JSON；文件内记录设备标识以便导入时校验机型。";
        btnExport.Click += (_, _) => ExportProfile();

        var btnRestore = UiFactory.CreateButton("恢复应用默认设置(&Z)");
        btnRestore.AccessibleDescription = "把应用设置恢复为默认值，保留窗口位置与开机启动项选择。";
        btnRestore.Click += (_, _) => RestoreDefaults();

        dataButtons.Controls.Add(btnImport);
        dataButtons.Controls.Add(btnExport);
        dataButtons.Controls.Add(btnRestore);
        UiFactory.AddRow(data, dataButtons);
        UiFactory.AddRow(data, UiFactory.CreateMutedLabel(
            "导入仅校验机型并保存到本地配置目录，不会自动写入硬件；需在对应编辑区域显式应用。"));

        UiFactory.AddRow(data, UiFactory.CreateSectionLabel("本地配置档"));
        UiFactory.AddRow(data, _lstProfiles);

        var profileButtons = UiFactory.CreateRow();
        var btnRefreshProfiles = UiFactory.CreateButton("刷新列表(&R)");
        btnRefreshProfiles.AccessibleDescription = "重新扫描本地配置目录。";
        btnRefreshProfiles.Click += (_, _) => RefreshProfiles();

        var btnDeleteProfile = UiFactory.CreateButton("删除选中配置(&D)");
        btnDeleteProfile.AccessibleDescription = "删除选中的本地配置档文件。";
        btnDeleteProfile.Click += (_, _) => DeleteSelectedProfile();

        profileButtons.Controls.Add(btnRefreshProfiles);
        profileButtons.Controls.Add(btnDeleteProfile);
        UiFactory.AddRow(data, profileButtons);
        UiFactory.AddRow(data, UiFactory.CreateMutedLabel(
            "配置档的保存/重命名/标签/排序在风扇与性能编辑区域管理；此处只提供查看与删除。"));
        UiFactory.AddRow(_stack, UiFactory.CreateCard("数据与配置", data));

        // 组 3：设备与诊断
        var diagnostics = UiFactory.CreateStack();
        _txtDevice.Height = 150;
        UiFactory.AddRow(diagnostics, UiFactory.CreateSectionLabel("硬件与固件信息"));
        UiFactory.AddRow(diagnostics, _txtDevice);
        UiFactory.AddRow(diagnostics, UiFactory.CreateSectionLabel("功能支持状态（取自能力基线）"));
        UiFactory.AddRow(diagnostics, _lstCapabilities);

        var diagButtons = UiFactory.CreateRow();
        var btnExportLog = UiFactory.CreateButton("导出本地日志(&L)");
        btnExportLog.AccessibleDescription = "导出已脱敏的本地日志；不含设备序列号与用户目录路径。";
        btnExportLog.Click += (_, _) => ExportLog();
        var btnOpenLogFolder = UiFactory.CreateButton("打开数据目录(&O)");
        btnOpenLogFolder.AccessibleDescription = "在文件资源管理器中打开本程序的数据目录。";
        btnOpenLogFolder.Click += (_, _) => OpenDataFolder();
        diagButtons.Controls.Add(btnExportLog);
        diagButtons.Controls.Add(btnOpenLogFolder);
        UiFactory.AddRow(diagnostics, diagButtons);
        UiFactory.AddRow(diagnostics, UiFactory.CreateMutedLabel(
            "日志在写入时即完成脱敏，不含设备序列号与用户目录路径；总量有上限并自动轮转。"));
        UiFactory.AddRow(_stack, UiFactory.CreateCard("设备与诊断", diagnostics));

        // 底部动作
        var actions = UiFactory.CreateRow();
        _btnSave.Margin = new Padding(0, 0, UiFactory.DesignTokens.Space.Sm, 0);
        _btnSave.AccessibleDescription = "保存并立即应用设置；Enter 等效。";
        _btnSave.Click += (_, _) => SaveSettings();
        _btnClose.AccessibleDescription = "关闭设置窗口；Esc 等效。未保存的修改不会生效。";
        _btnClose.Click += (_, _) => Close();
        actions.Controls.Add(_btnSave);
        actions.Controls.Add(_btnClose);
        UiFactory.AddRow(_stack, actions);
        UiFactory.AddRow(_stack, UiFactory.CreateMutedLabel(
            "键盘操作：Tab 遍历控件，Enter 保存，Esc 关闭，Alt+字母为按钮助记键。"));
        UiFactory.AddRow(_stack, _lblStatus);

        Controls.Add(_stack);
    }

    private static FlowLayoutPanel CreateField(string label, Control field)
    {
        var row = UiFactory.CreateRow();
        row.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(4, 8, 4, 4) });
        row.Controls.Add(field);
        return row;
    }

    private void LoadFromSettings()
    {
        var settings = _context.Settings;

        _chkLaunchOnStartup.Checked = settings.LaunchOnStartup;
        _chkCloseToTray.Checked = settings.CloseToTray;
        _cmbStartupWindow.SelectedIndex = settings.StartupWindow == WindowMode.Mini ? 1 : 0;
        _chkStartMinimized.Checked = settings.StartMinimizedOnStartup;
        _numRefresh.Value = settings.RefreshIntervalSeconds;
        _cmbFanDisplay.SelectedIndex = settings.FanDisplay == FanDisplayMode.Rpm ? 1 : 0;
        _showHideEditor.Set(settings.ShowHideWindowHotkey);
        _fullSpeedEditor.Set(settings.FullSpeedHotkey);

        if (!StartupRegistration.CanRegister && settings.LaunchOnStartup)
        {
            _chkLaunchOnStartup.Checked = false;
            _chkLaunchOnStartup.Enabled = false;
        }

        _lblStartupState.Text = BuildStartupStateText(settings.LaunchOnStartup);
    }

    /// <summary>
    /// 生成开机启动的**实际状态**说明。
    /// </summary>
    /// <remarks>
    /// 刻意把「设置意图」与「注册表实际状态」分开显示：
    /// 二者可能在注册失败或手工改注册表后不一致，只显示意图会误导用户。
    /// </remarks>
    private static string BuildStartupStateText(bool intendedEnabled)
    {
        if (!StartupRegistration.CanRegister)
        {
            return "实际状态：当前环境不适用（以开发主机运行，不修改注册表）。";
        }

        var registered = StartupRegistration.IsEnabled();
        return registered == intendedEnabled
            ? $"实际状态：{(registered ? "已注册为开机启动" : "未注册开机启动")}，与设置一致。"
            : $"实际状态：{(registered ? "已注册为开机启动" : "未注册开机启动")}，与设置不一致；保存后将按设置修正。";
    }

    private void RefreshProfiles()
    {
        _lstProfiles.Items.Clear();

        var directory = _context.Paths.ProfilesDirectory;
        if (!Directory.Exists(directory))
        {
            _lstProfiles.Items.Add("（本地配置目录尚未创建）");
            return;
        }

        var files = Directory.GetFiles(directory, "*.json");
        if (files.Length == 0)
        {
            _lstProfiles.Items.Add("（暂无本地配置档；导入配置后会出现在此处）");
            return;
        }

        foreach (var file in files.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            _lstProfiles.Items.Add(Path.GetFileName(file));
        }
    }

    private void DeleteSelectedProfile()
    {
        if (_lstProfiles.SelectedItem is not string fileName || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            SetStatus("请先在列表中选择一个配置档。", MetricTone.Warning);
            return;
        }

        var path = Path.Combine(_context.Paths.ProfilesDirectory, fileName);

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"删除失败：{ex.Message}", MetricTone.Danger);
            return;
        }

        _context.WriteLog(LogLevel.Info, "Profile", $"已删除本地配置档 {fileName}。");
        RefreshProfiles();
        SetStatus($"已删除配置档 {fileName}。仅删除本软件保存在配置目录内的副本。", MetricTone.Normal);
    }

    private void LoadDiagnostics()
    {
        var device = _context.Device;
        var lines = new List<string>
        {
            $"型号：{device.Model}",
            $"主板码：{device.BoardCode}（已识别：{(device.IsIdentified ? "是" : "否")}）",
            $"CPU：{device.CpuName ?? "未取得"}",
            $"独显：{device.DiscreteGpuName ?? "未取得"}",
            $"核显：{device.IntegratedGpuName ?? "未取得"}",
            $"BIOS：{device.BiosVersion ?? "未取得"}",
            $"EC：{(device.HasValidEcVersion ? device.EcVersion : "未取得有效版本（SMBIOS 的 255/255 不作为版本）")}",
            $"Windows：{device.WindowsBuild ?? "未取得"}",
            $"显卡驱动：{device.GpuDriverVersion ?? "未取得"}",
            $"数据目录：{_context.Paths.RootDirectory}",
            $"设置加载来源：{_context.SettingsLoadResult.Source}" +
                (_context.SettingsLoadResult.QuarantinedFilePath is { } quarantine
                    ? $"（原文件已隔离：{quarantine}）"
                    : string.Empty)
        };

        foreach (var note in _context.SettingsLoadResult.Notes)
        {
            lines.Add("· " + note);
        }

        _txtDevice.Text = string.Join(Environment.NewLine, lines);

        _lstCapabilities.Items.Clear();
        var entries = CapabilityPresenter.DescribeAll(_context.Capabilities);
        if (entries.Count == 0)
        {
            _lstCapabilities.Items.Add("未登记任何能力：未适配机型上全部功能按「待验证」禁用。");
            return;
        }

        foreach (var entry in entries)
        {
            var state = entry.Enabled ? "可用（读写）" : "禁用";
            var detail = string.IsNullOrEmpty(entry.Reason) ? string.Empty : " —— " + entry.Reason;
            _lstCapabilities.Items.Add(
                $"{entry.Title}（{entry.Key}）｜{CapabilityPresenter.LevelText(entry.Level)}｜{state}{detail}");
        }
    }

    private void SaveSettings()
    {
        var current = _context.Settings;
        var problems = new List<string>();

        var showHide = _showHideEditor.Get();
        var fullSpeed = _fullSpeedEditor.Get();
        problems.AddRange(showHide.Validate().Select(p => "显示/隐藏窗口：" + p));
        problems.AddRange(fullSpeed.Validate().Select(p => "强冷切换：" + p));

        // 两个快捷键不得使用同一组组合，否则第二个必然注册失败。此处直接拒绝并说明。
        if (showHide.IsComplete && fullSpeed.IsComplete
            && string.Equals(showHide.Describe(), fullSpeed.Describe(), StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("显示/隐藏窗口与强冷切换使用了相同的快捷键组合，请改为不同组合。");
        }

        if (problems.Count > 0)
        {
            SetStatus("设置未保存：" + string.Join(" ", problems), MetricTone.Danger);
            return;
        }

        var settings = current with
        {
            // 当前环境无法写注册表时，复选框被强制置灰为未勾选；
            // 此时必须保留原本的存储值，否则「环境限制」会被当成「用户要求关闭开机启动」写进配置。
            LaunchOnStartup = StartupRegistration.CanRegister
                ? _chkLaunchOnStartup.Checked
                : current.LaunchOnStartup,
            CloseToTray = _chkCloseToTray.Checked,
            StartupWindow = _cmbStartupWindow.SelectedIndex == 1 ? WindowMode.Mini : WindowMode.Normal,
            StartMinimizedOnStartup = _chkStartMinimized.Checked,
            RefreshIntervalSeconds = (int)_numRefresh.Value,
            FanDisplay = _cmbFanDisplay.SelectedIndex == 1 ? FanDisplayMode.Rpm : FanDisplayMode.Percent,
            ShowHideWindowHotkey = showHide,
            FullSpeedHotkey = fullSpeed
        };

        var messages = new List<string>();

        // 只有「当前环境可写」且「用户确实改了选择」时才碰注册表；
        // 其余情况保留存储值，并由 _lblStartupState 说明实际状态。
        var startupCanBeApplied = StartupRegistration.CanRegister
                                  && settings.LaunchOnStartup != current.LaunchOnStartup;

        if (startupCanBeApplied)
        {
            var error = StartupRegistration.Apply(settings.LaunchOnStartup);
            messages.Add(error is null
                ? settings.LaunchOnStartup ? "已写入开机启动项。" : "已移除开机启动项。"
                : error);
        }

        _context.UpdateSettings(settings);
        messages.Add("设置已保存。");

        _lblStartupState.Text = BuildStartupStateText(settings.LaunchOnStartup);
        SetStatus(string.Join(" ", messages), MetricTone.Normal);
    }

    private void RestoreDefaults()
    {
        var defaults = AppSettings.CreateDefault() with
        {
            NormalWindowPlacement = _context.Settings.NormalWindowPlacement,
            MiniWindowPlacement = _context.Settings.MiniWindowPlacement,
            LaunchOnStartup = _context.Settings.LaunchOnStartup
        };

        _context.UpdateSettings(defaults);
        LoadFromSettings();
        SetStatus("已恢复应用默认设置（保留窗口位置与开机启动项选择）。", MetricTone.Warning);
    }

    private void ImportProfile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "导入配置档",
            Filter = "MSI-Control 配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string json;
        try
        {
            json = File.ReadAllText(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"读取文件失败：{ex.Message}", MetricTone.Danger);
            return;
        }

        var result = ConfigImportValidator.Validate(json, _context.Device);
        if (!result.Accepted || result.Profile is null)
        {
            _context.WriteLog(LogLevel.Warning, "Profile", $"导入被拒绝：{string.Join(" / ", result.Problems)}");
            SetStatus("导入被拒绝：" + string.Join(" ", result.Problems), MetricTone.Danger);
            return;
        }

        var storedPath = Path.Combine(
            _context.Paths.ProfilesDirectory,
            $"imported-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");

        try
        {
            File.WriteAllText(storedPath, ConfigProfileSerializer.Serialize(result.Profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"配置已通过校验，但保存到本地失败：{ex.Message}", MetricTone.Danger);
            return;
        }

        _context.WriteLog(LogLevel.Info, "Profile", $"已导入配置档「{result.Profile.Name}」到 {storedPath}。");
        RefreshProfiles();
        SetStatus(
            "导入成功：" + string.Join(" ", result.Notes) + $" 本地副本：{storedPath}",
            MetricTone.Normal);
    }

    private void ExportProfile()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出配置档",
            Filter = "MSI-Control 配置 (*.json)|*.json",
            FileName = $"msi-control-profile-{DateTimeOffset.Now:yyyyMMdd}.json"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // 当前版本的写入入口全部处于禁用状态，因此有效配置即默认配置表；
        // 导出时在名称中注明来源，避免让人误以为是「用户自定义配置」。
        var profile = new ConfigProfile(
            ConfigProfile.CurrentSchemaVersion,
            _context.Device,
            "默认配置档（导出时本机无自定义修改）",
            new[] { "默认", "未自定义" },
            SortOrder: 0,
            Stages: PerformanceProfileCatalog.Defaults.Select(p => p.Settings).ToArray());

        try
        {
            File.WriteAllText(dialog.FileName, ConfigProfileSerializer.Serialize(profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"导出失败：{ex.Message}", MetricTone.Danger);
            return;
        }

        _context.WriteLog(LogLevel.Info, "Profile", $"已导出配置档到 {dialog.FileName}。");
        SetStatus(
            "导出成功。注意：本文件记录的是默认配置表，导入后必须在界面显式应用才会写入硬件。",
            MetricTone.Normal);
    }

    private void ExportLog()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "导出本地日志",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"msi-control-log-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            _context.Log.Export(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"日志导出失败：{ex.Message}", MetricTone.Danger);
            return;
        }

        SetStatus("日志已导出（已脱敏，不含序列号与用户目录路径）。", MetricTone.Normal);
    }

    private void OpenDataFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _context.Paths.RootDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开数据目录：{ex.Message}", MetricTone.Danger);
        }
    }


    // ── 设备控制：读回与即时应用（HWE §31）───────────────────────────────
    private bool AllowsWrite(string key) => _context.Capabilities.AllowsWrite(key);

    private void RefreshDeviceStates()
    {
        var controls = _context.DeviceControls;

        _syncingDeviceState = true;
        try
        {
            // Win / Fn 交换
            if (controls.WinFnSwap is not { } swap || !AllowsWrite(CapabilityKeys.InputWinFnSwap))
            {
                _chkWinFnSwap.Checked = false;
                _chkWinFnSwap.Enabled = false;
                _chkWinFnSwap.Text = "交换 Win / Fn 键（未接入或未验证）";
            }
            else
            {
                var state = swap.Read();
                _chkWinFnSwap.Enabled = state is not null;
                _chkWinFnSwap.Checked = state?.Swapped ?? false;
            }

            // Win 键屏蔽
            if (controls.WindowsKeyLock is not { } keyLock || !AllowsWrite(CapabilityKeys.InputWindowsKey))
            {
                _chkWindowsKey.Checked = false;
                _chkWindowsKey.Enabled = false;
                _chkWindowsKey.Text = "屏蔽 Win 键（未验证，暂不开放）";
            }
            else
            {
                _chkWindowsKey.Enabled = true;
                _chkWindowsKey.Checked = keyLock.IsActive;
            }

            // 触摸板
            if (_context.TouchpadControl is not { } touchpad || !AllowsWrite(CapabilityKeys.InputTouchpad))
            {
                _chkTouchpad.Checked = false;
                _chkTouchpad.Enabled = false;
                _chkTouchpad.Text = "启用触摸板（未接入或未验证）";
            }
            else
            {
                var state = touchpad.Read();
                _chkTouchpad.Enabled = state is not null;
                _chkTouchpad.Checked = state?.Enabled ?? false;
            }

            // 键盘背光
            if (controls.KeyboardBacklight is not { } backlight || !AllowsWrite(CapabilityKeys.KeyboardBacklight))
            {
                _chkBacklight.Checked = false;
                _chkBacklight.Enabled = false;
                _chkBacklight.Text = "键盘背光总开关（未接入或未验证）";
            }
            else
            {
                var state = backlight.Read();
                _chkBacklight.Enabled = state is not null;
                _chkBacklight.Checked = state?.Enabled ?? false;
            }

            // 显示省电
            if (controls.DisplayPowerSaver is not { } saver || !AllowsWrite(CapabilityKeys.DisplayPowerSaver))
            {
                _chkDisplaySaver.Checked = false;
                _chkDisplaySaver.Enabled = false;
                _lblDisplaySaverState.Text = "显示省电：未接入或能力未验证。";
            }
            else
            {
                var state = saver.Read();
                _chkDisplaySaver.Enabled = state is not null && state.Value.MinimumHz != state.Value.MaximumHz;
                _chkDisplaySaver.Checked = state?.AtMinimum ?? false;
                _lblDisplaySaverState.Text = state is { } value
                    ? value.MinimumHz == value.MaximumHz
                        ? $"显示省电：当前 {value.CurrentHz} Hz，该分辨率只有一种刷新率，无可切换档。"
                        : $"显示省电：当前 {value.CurrentHz} Hz，可用 {value.MinimumHz}–{value.MaximumHz} Hz；"
                          + "开启后切到最低刷新率，关闭后回到最高。"
                    : "显示省电：读不到当前显示模式。";
            }

            // USB 共享供电
            if (controls.UsbPowerShare is not { } usb || !AllowsWrite(CapabilityKeys.PowerUsbShare))
            {
                _chkUsbShare.Checked = false;
                _chkUsbShare.Enabled = false;
                _chkUsbShare.Text = "USB 关机共享供电（未接入或未验证）";
            }
            else
            {
                var state = usb.Read();
                _chkUsbShare.Enabled = state?.Supported ?? false;
                _chkUsbShare.Checked = state?.Enabled ?? false;
                _chkUsbShare.Text = state is { } value && !value.Supported
                    ? "USB 关机共享供电（本机固件未声明支持）"
                    : "USB 关机共享供电";
            }
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Warning, "Device", $"读取设备状态失败：{ex.Message}");
            SetStatus($"读取设备状态失败：{ex.Message}", MetricTone.Danger);
        }
        finally
        {
            _syncingDeviceState = false;
        }
    }

    private async Task ApplyWinFnSwapAsync()
    {
        if (_syncingDeviceState || _context.DeviceControls.WinFnSwap is not { } control)
        {
            return;
        }

        var target = _chkWinFnSwap.Checked;
        _chkWinFnSwap.Enabled = false;
        try
        {
            var result = await Task.Run(() => control.SetSwappedAsync(target, CancellationToken.None));
            ReportDeviceResult("Win/Fn 交换", result);
        }
        finally
        {
            _chkWinFnSwap.Enabled = true;
            RefreshDeviceStates();
        }
    }

    private void ApplyWindowsKey()
    {
        if (_syncingDeviceState || _context.DeviceControls.WindowsKeyLock is not { } control)
        {
            return;
        }

        var target = _chkWindowsKey.Checked;
        var active = control.SetActive(target);
        if (active != target)
        {
            SetStatus(
                target ? "启用 Win 键屏蔽失败：键盘钩子未能安装（可能被安全软件拦截）。" : "停用 Win 键屏蔽失败。",
                MetricTone.Danger);
        }
        else
        {
            _context.WriteLog(LogLevel.Info, "Device", target ? "已启用 Win 键屏蔽（仅本程序运行期间）。" : "已停用 Win 键屏蔽。");
            SetStatus(
                target ? "已屏蔽 Win 键：仅在本程序运行期间生效，退出即恢复。" : "已恢复 Win 键。",
                MetricTone.Normal);
        }

        RefreshDeviceStates();
    }

    private async Task ApplyTouchpadAsync()
    {
        if (_syncingDeviceState || _context.TouchpadControl is not { } control)
        {
            return;
        }

        var target = _chkTouchpad.Checked;
        _chkTouchpad.Enabled = false;
        try
        {
            var result = await Task.Run(() => control.SetEnabledAsync(target, CancellationToken.None));
            ReportDeviceResult("触摸板", result);
        }
        finally
        {
            _chkTouchpad.Enabled = true;
            RefreshDeviceStates();
        }
    }

    private async Task ApplyBacklightAsync()
    {
        if (_syncingDeviceState || _context.DeviceControls.KeyboardBacklight is not { } control)
        {
            return;
        }

        var target = _chkBacklight.Checked;
        _chkBacklight.Enabled = false;
        try
        {
            var result = await Task.Run(() => control.SetEnabledAsync(target, 3, CancellationToken.None));
            ReportDeviceResult("键盘背光", result);
        }
        finally
        {
            _chkBacklight.Enabled = true;
            RefreshDeviceStates();
        }
    }

    private async Task ApplyDisplaySaverAsync()
    {
        if (_syncingDeviceState || _context.DeviceControls.DisplayPowerSaver is not { } control)
        {
            return;
        }

        var target = _chkDisplaySaver.Checked;
        _chkDisplaySaver.Enabled = false;
        try
        {
            var result = await Task.Run(() => control.SetEnabledAsync(target, CancellationToken.None));
            ReportDeviceResult("显示省电", result);
        }
        finally
        {
            _chkDisplaySaver.Enabled = true;
            RefreshDeviceStates();
        }
    }

    private async Task ApplyUsbPowerShareAsync()
    {
        if (_syncingDeviceState || _context.DeviceControls.UsbPowerShare is not { } control)
        {
            return;
        }

        var target = _chkUsbShare.Checked;
        _chkUsbShare.Enabled = false;
        try
        {
            var result = await Task.Run(() => control.SetEnabledAsync(target, CancellationToken.None));
            ReportDeviceResult("USB 共享供电", result);
        }
        finally
        {
            _chkUsbShare.Enabled = true;
            RefreshDeviceStates();
        }
    }

    private void ReportDeviceResult(string title, ControlResult result)
    {
        var item = result.Settings.Count > 0 ? result.Settings[0] : null;
        var detail = item?.Message ?? item?.ReadBack ?? item?.Requested ?? "无结果";
        var text = $"{title}：{detail}";

        _context.WriteLog(
            result.Status == OperationStatus.Succeeded ? LogLevel.Info : LogLevel.Warning,
            "Device",
            text);

        SetStatus(text, result.Status switch
        {
            OperationStatus.Succeeded => MetricTone.Normal,
            OperationStatus.PartiallySucceeded => MetricTone.Warning,
            _ => MetricTone.Danger
        });
    }

    private void SetStatus(string text, MetricTone tone)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = MetricTile.ToneColor(_palette, tone);
    }

    /// <summary>快捷键编辑控件组。</summary>
    private sealed class HotkeyEditor
    {
        private readonly string _title;
        private readonly CheckBox _enabled = new() { Text = "启用", AutoSize = true };
        private readonly CheckBox _ctrl = new() { Text = "Ctrl", AutoSize = true };
        private readonly CheckBox _alt = new() { Text = "Alt", AutoSize = true };
        private readonly CheckBox _shift = new() { Text = "Shift", AutoSize = true };
        private readonly CheckBox _win = new() { Text = "Win", AutoSize = true };
        private readonly ComboBox _key = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 70 };

        internal HotkeyEditor(string title)
        {
            _title = title;

            var keys = new List<string>();
            for (var c = 'A'; c <= 'Z'; c++)
            {
                keys.Add(c.ToString());
            }

            for (var function = 1; function <= 12; function++)
            {
                keys.Add("F" + function);
            }

            _key.Items.AddRange(keys.ToArray());
            _key.SelectedIndex = 0;
        }

        internal Control Build()
        {
            var row = UiFactory.CreateRow();
            row.Controls.Add(new Label
            {
                Text = _title + "：",
                AutoSize = true,
                Margin = new Padding(4, 8, 4, 4)
            });
            row.Controls.Add(_enabled);
            row.Controls.Add(_ctrl);
            row.Controls.Add(_alt);
            row.Controls.Add(_shift);
            row.Controls.Add(_win);
            row.Controls.Add(_key);
            return row;
        }

        internal HotkeyBinding Get()
        {
            var keyName = _key.SelectedItem as string ?? string.Empty;
            return new HotkeyBinding(
                Enabled: _enabled.Checked,
                Control: _ctrl.Checked,
                Alt: _alt.Checked,
                Shift: _shift.Checked,
                Windows: _win.Checked,
                KeyName: keyName);
        }

        internal void Set(HotkeyBinding binding)
        {
            _enabled.Checked = binding.Enabled;
            _ctrl.Checked = binding.Control;
            _alt.Checked = binding.Alt;
            _shift.Checked = binding.Shift;
            _win.Checked = binding.Windows;

            var index = _key.Items.IndexOf(binding.KeyName);
            if (index >= 0)
            {
                _key.SelectedIndex = index;
            }
        }
    }
}
