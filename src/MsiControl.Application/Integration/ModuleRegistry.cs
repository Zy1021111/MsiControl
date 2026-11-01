using MsiControl.Application.Control;

namespace MsiControl.Application.Integration;

/// <summary>一个模块的接入状态。</summary>
public enum ModuleIntegrationState
{
    /// <summary>已接入：工程存在且已被应用装配使用。</summary>
    Integrated = 0,

    /// <summary>只读接入：工程存在并已被应用使用，但按能力等级只开放只读用途。</summary>
    ReadOnly = 1,

    /// <summary>未接入：工程尚不存在（对应任务未交付）。</summary>
    NotIntegrated = 2,

    /// <summary>已存在但被阻塞：有明确阻塞原因，必须记录而不是猜测。</summary>
    Blocked = 3
}

/// <summary>一个模块的接入情况。</summary>
/// <param name="Name">模块显示名。</param>
/// <param name="OwnerTask">负责的任务编号。</param>
/// <param name="Project">工程路径（相对仓库根）。</param>
/// <param name="State">接入状态。</param>
/// <param name="Reason">依据或阻塞原因。</param>
public sealed record ModuleStatus(
    string Name,
    string OwnerTask,
    string Project,
    ModuleIntegrationState State,
    string Reason)
{
    /// <summary>状态显示名。</summary>
    public string StateText => State switch
    {
        ModuleIntegrationState.Integrated => "已接入",
        ModuleIntegrationState.ReadOnly => "只读接入",
        ModuleIntegrationState.NotIntegrated => "未接入",
        ModuleIntegrationState.Blocked => "已阻塞",
        _ => State.ToString()
    };
}

/// <summary>
/// 模块接入状态表。
/// </summary>
/// <remarks>
/// <para>
/// 任务 07 作为整合负责人必须维护「哪些模块已接入、待接入、阻塞原因」（提示词 §六）。
/// 本类型是该表的**唯一代码来源**：界面、日志、交付文档 `docs/integration.md` 共用它，
/// 避免文档说「已接入」而代码里根本没有实现。
/// </para>
/// <para>
/// <b>纪律：只有真正被装配使用的模块才标记为「已接入」。</b>
/// 工程存在但未注入到协调器时，状态必须是「未接入」并写明原因。
/// </para>
/// </remarks>
public static class ModuleRegistry
{
    /// <summary>按当前装配情况生成状态表。</summary>
    /// <param name="monitoringIntegrated">监控模块（任务 03）是否已提供采样实现。</param>
    /// <param name="adapters">各控制适配层的接入情况。</param>
    public static IReadOnlyList<ModuleStatus> CreateDefault(
        bool monitoringIntegrated,
        PerformanceAdapterAvailability adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        return new[]
        {
            new ModuleStatus(
                "基础接口 Core",
                "01",
                "src/MsiControl.Core",
                ModuleIntegrationState.Integrated,
                "共享契约（模型、接口、能力等级、结果类型）已被应用层与桌面壳使用。"),

            new ModuleStatus(
                "MSI 硬件适配层 Hardware.Msi",
                "01（骨架）/ 04 / 05 / 06",
                "src/MsiControl.Hardware.Msi",
                ModuleIntegrationState.ReadOnly,
                "能力基线与只读 Discovery/Transport 已被应用层使用；" +
                "写入通道（Transport 的 Set_*）按约定保持关闭，需对应能力达「已确证」且由 04/05/06 公告后开启。"),

            new ModuleStatus(
                "配置持久化 Persistence",
                "02",
                "src/MsiControl.Persistence",
                ModuleIntegrationState.Integrated,
                "设置文件、配置档校验与轮转日志已被桌面壳使用。"),

            new ModuleStatus(
                "桌面壳 Desktop",
                "02",
                "src/MsiControl.Desktop",
                ModuleIntegrationState.Integrated,
                "正常/迷你窗口、托盘、快捷键已接入；控制出口由本任务的协调器提供。"),

            new ModuleStatus(
                "实时监控 Monitoring",
                "03",
                "src/MsiControl.Monitoring",
                monitoringIntegrated
                    ? ModuleIntegrationState.Integrated
                    : ModuleIntegrationState.NotIntegrated,
                monitoringIntegrated
                    ? "已提供采样实现并注入共享采样流。"
                    : "工程尚未交付：采样使用「未适配」实现，界面显示「不可用」而不是模拟数据。"),

            new ModuleStatus(
                "风扇 Fans",
                "04",
                "src/MsiControl.Fans",
                adapters.Fans ? ModuleIntegrationState.Integrated : ModuleIntegrationState.NotIntegrated,
                adapters.Fans
                    ? "风扇适配层已注入协调器。"
                    : "工程尚未交付：曲线、校准、强冷与恢复写入路径一律返回「未接入」。"),

            new ModuleStatus(
                "性能与功耗 Performance",
                "05",
                "src/MsiControl.Performance",
                adapters.Performance ? ModuleIntegrationState.Integrated : ModuleIntegrationState.NotIntegrated,
                adapters.Performance
                    ? "性能适配层已注入协调器。"
                    : "工程尚未交付：档位、功耗与睿频写入路径一律返回「未接入」。"),

            new ModuleStatus(
                "显卡 Graphics",
                "06",
                "src/MsiControl.Graphics",
                adapters.Graphics ? ModuleIntegrationState.Integrated : ModuleIntegrationState.NotIntegrated,
                adapters.Graphics
                    ? "显卡适配层已注入协调器。"
                    : "工程尚未交付：模式切换路径一律返回「未接入」，且不会自动重启。"),

            new ModuleStatus(
                "应用层 Application（本任务）",
                "07",
                "src/MsiControl.Application",
                ModuleIntegrationState.Integrated,
                "协调器、启动/供电/退出策略、模块状态表、第三方共存检测与宿主装配已交付。")
        };
    }

    /// <summary>生成 Markdown 表格，供交付文档直接引用。</summary>
    public static string ToMarkdownTable(IReadOnlyList<ModuleStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var lines = new List<string>
        {
            "| 模块 | 负责任务 | 工程 | 接入状态 | 依据 / 阻塞原因 |",
            "|---|---|---|---|---|"
        };

        lines.AddRange(statuses.Select(s =>
            $"| {s.Name} | {s.OwnerTask} | `{s.Project}` | {s.StateText} | {s.Reason} |"));

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>一行摘要，供启动日志使用。</summary>
    public static string Summarize(IReadOnlyList<ModuleStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var counts = statuses
            .GroupBy(s => s.State)
            .OrderBy(g => g.Key)
            .Select(g => $"{TextOf(g.Key)} {g.Count()}");

        return $"模块接入状态：{string.Join("，", counts)}";
    }

    /// <summary>尚未接入的模块名列表。</summary>
    public static IReadOnlyList<string> NotIntegratedNames(IReadOnlyList<ModuleStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        return statuses
            .Where(s => s.State is ModuleIntegrationState.NotIntegrated or ModuleIntegrationState.Blocked)
            .Select(s => s.Name)
            .ToArray();
    }

    private static string TextOf(ModuleIntegrationState state) => state switch
    {
        ModuleIntegrationState.Integrated => "已接入",
        ModuleIntegrationState.ReadOnly => "只读接入",
        ModuleIntegrationState.NotIntegrated => "未接入",
        ModuleIntegrationState.Blocked => "已阻塞",
        _ => state.ToString()
    };
}
