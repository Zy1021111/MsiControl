using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Presentation;
using MsiControl.Desktop.Resources;
using MsiControl.Desktop.Shell;
using MsiControl.Persistence.Logging;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Tray;

/// <summary>
/// 托盘图标与菜单。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §10 / design.md §11：托盘提供「打开正常窗口 / 打开迷你窗口 / 性能模式切换 /
/// 强冷切换 / 退出」。
/// </para>
/// <para>
/// 能力未达 <see cref="CapabilityLevel.Verified"/> 的项<b>不隐藏、而是禁用并给出原因</b>：
/// 隐藏会让用户以为软件缺少该功能，禁用加说明才能传达「本机尚未验证」这一事实。
/// </para>
/// </remarks>
public sealed class TrayHost : IDisposable
{
    private readonly ShellContext _context;
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Dictionary<PerformanceStage, ToolStripMenuItem> _stageItems = new();
    private readonly ToolStripMenuItem _fullSpeedItem;
    private readonly string? _fullSpeedDisabledReason;
    private bool _disposed;

    /// <summary>构造托盘。</summary>
    /// <param name="context">共享上下文。</param>
    /// <param name="openWindow">请求打开指定窗口。</param>
    /// <param name="exit">请求完全退出。</param>
    public TrayHost(ShellContext context, Action<WindowMode> openWindow, Action exit)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ArgumentNullException.ThrowIfNull(openWindow);
        ArgumentNullException.ThrowIfNull(exit);

        _menu = new ContextMenuStrip
        {
            // 默认值为 false：不打开这个开关，禁用项上的原因提示永远不会显示，
            // 界面就退化成「只把入口变灰而不解释」，违反任务书提示词第 5 条。
            ShowItemToolTips = true,
            ShowImageMargin = false
        };

        var openNormal = new ToolStripMenuItem("打开正常窗口");
        openNormal.Click += (_, _) => openWindow(WindowMode.Normal);
        _menu.Items.Add(openNormal);

        var openMini = new ToolStripMenuItem("打开迷你窗口");
        openMini.Click += (_, _) => openWindow(WindowMode.Mini);
        _menu.Items.Add(openMini);

        _menu.Items.Add(new ToolStripSeparator());

        // 门控键必须是 performance.stage：firmware.scenario 是 MSI_ACPI WMI 侧的只读线索键
        // （维持 Detected），用它会让档位菜单永久灰化。
        var stageEntry = _context.DescribeCapability(CapabilityKeys.PerformanceStage, requiresWrite: true);
        var stageMenu = new ToolStripMenuItem("性能模式") { Enabled = stageEntry.Enabled };

        foreach (var stage in Enum.GetValues<PerformanceStage>())
        {
            var item = new ToolStripMenuItem(ModeFormatter.StageText(stage))
            {
                CheckOnClick = false,
                Enabled = stageEntry.Enabled,
                Tag = stage
            };

            if (!stageEntry.Enabled)
            {
                item.ToolTipText = stageEntry.Reason;
            }

            item.Click += (_, _) => _ = RequestStageAsync(stage);
            _stageItems[stage] = item;
            stageMenu.DropDownItems.Add(item);
        }

        if (!stageEntry.Enabled)
        {
            stageMenu.ToolTipText = stageEntry.Reason;
            // 除悬停提示外再加一条可见说明：并非所有系统设置都会显示菜单工具提示。
            stageMenu.DropDownItems.Add(new ToolStripSeparator());
            stageMenu.DropDownItems.Add(new ToolStripMenuItem("已禁用：" + Shorten(stageEntry.Reason))
            {
                Enabled = false,
                ToolTipText = stageEntry.Reason
            });
        }

        _menu.Items.Add(stageMenu);

        var fullSpeedEntry = _context.DescribeCapability(CapabilityKeys.FanFullSpeed, requiresWrite: true);
        _fullSpeedDisabledReason = fullSpeedEntry.Enabled ? null : fullSpeedEntry.Reason;
        _fullSpeedItem = new ToolStripMenuItem(BuildFullSpeedText(active: false, _fullSpeedDisabledReason))
        {
            CheckOnClick = false,
            Enabled = fullSpeedEntry.Enabled
        };

        if (!fullSpeedEntry.Enabled)
        {
            _fullSpeedItem.ToolTipText = fullSpeedEntry.Reason;
        }

        _fullSpeedItem.Click += (_, _) => _ = RequestFullSpeedAsync(!_fullSpeedActive);
        _menu.Items.Add(_fullSpeedItem);

        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出（停止全部进程）");
        exitItem.Click += (_, _) => exit();
        _menu.Items.Add(exitItem);

        _icon = new NotifyIcon
        {
            // 托盘按系统小图标尺寸取同一份 app.ico，与标题栏 / 任务栏一致
            // （早期用 SystemIcons.Application 占位，任务栏与托盘图标对不上）。
            Icon = AppIcon.CreateTrayIcon(),
            Text = "MSI-Control",
            ContextMenuStrip = _menu,
            Visible = true
        };

        _icon.DoubleClick += (_, _) => openWindow(WindowMode.Normal);
    }

    private bool _fullSpeedActive;

    /// <summary>显示气泡提示。</summary>
    public void ShowTip(string title, string text, bool warning = false)
        => _icon.ShowBalloonTip(
            4000,
            title,
            text,
            warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    /// <summary>同步当前实际档位的勾选状态。</summary>
    public void UpdateStageState(PerformanceStage? stage)
    {
        foreach (var (key, item) in _stageItems)
        {
            item.Checked = stage.HasValue && key == stage.Value;
        }
    }

    /// <summary>同步强冷开关状态。</summary>
    public void UpdateFullSpeedState(bool active)
    {
        _fullSpeedActive = active;
        _fullSpeedItem.Checked = active;
        _fullSpeedItem.Text = BuildFullSpeedText(active, _fullSpeedDisabledReason);
    }

    /// <summary>
    /// 截断过长的原因文案，用于菜单项标题。
    /// </summary>
    /// <remarks>完整文案仍放在 <c>ToolTipText</c> 中，避免菜单宽度被撑开。</remarks>
    private static string Shorten(string reason, int maxLength = 34)
    {
        var normalized = reason.Replace(Environment.NewLine, " ", StringComparison.Ordinal).Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
    }

    private static string BuildFullSpeedText(bool active, string? disabledReason)
    {
        var baseText = active ? "关闭强冷" : "强冷（全速）";
        return string.IsNullOrEmpty(disabledReason)
            ? baseText
            : $"{baseText} —— 已禁用：{Shorten(disabledReason)}";
    }

    private async Task RequestStageAsync(PerformanceStage stage)
    {
        var operation = $"托盘切换性能模式到 {ModeFormatter.StageText(stage)}";
        try
        {
            var result = await _context.Control.RequestPerformanceStageAsync(stage, CancellationToken.None)
                .ConfigureAwait(true);
            _context.LogControlResult(result, operation);

            // 只有逐项确认成功才更新勾选，避免出现虚假的选中状态。
            if (result.Settings.Count > 0 && result.Settings.All(s => s.Status == OperationStatus.Succeeded))
            {
                UpdateStageState(stage);
            }

            // 成功只弹一句：逐项明细里必然挂着「GPU 功耗上限本机不支持」的长原因，
            // 拼进气泡会把一次成功的切档说成一屏警告（与主窗口同一口径）。
            var summary = StageSwitchFeedback.SuccessText(result, stage);
            if (summary is not null)
            {
                ShowTip("MSI-Control", summary);
                return;
            }

            ShowTip("MSI-Control", Describe(result, operation), warning: !result.IsSuccess);
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Error, "Tray", $"{operation} 抛出异常：{ex.Message}");
            ShowTip("MSI-Control", $"{operation} 失败：{ex.Message}", warning: true);
        }
    }

    private async Task RequestFullSpeedAsync(bool enabled)
    {
        var operation = enabled ? "托盘开启强冷" : "托盘关闭强冷";
        try
        {
            var result = await _context.Control.SetFullSpeedAsync(enabled, CancellationToken.None)
                .ConfigureAwait(true);
            _context.LogControlResult(result, operation);

            if (result.Settings.Count > 0 && result.Settings.All(s => s.Status == OperationStatus.Succeeded))
            {
                UpdateFullSpeedState(enabled);
            }

            ShowTip("MSI-Control", Describe(result, operation), warning: !result.IsSuccess);
        }
        catch (Exception ex)
        {
            _context.WriteLog(LogLevel.Error, "Tray", $"{operation} 抛出异常：{ex.Message}");
            ShowTip("MSI-Control", $"{operation} 失败：{ex.Message}", warning: true);
        }
    }

    private static string Describe(ControlResult result, string operation)
    {
        var items = result.Settings
            .Select(s => $"{s.Setting}：{s.Status}" + (string.IsNullOrWhiteSpace(s.Message) ? string.Empty : $"（{s.Message}）"))
            .ToArray();

        return $"{operation} → {result.Status}" + (items.Length > 0 ? Environment.NewLine + string.Join(Environment.NewLine, items) : string.Empty);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
