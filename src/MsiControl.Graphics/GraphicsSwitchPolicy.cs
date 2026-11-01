using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Graphics;

/// <summary>
/// 一次显卡模式切换请求的评估结论。
/// </summary>
/// <remarks>
/// <para>
/// 由 <see cref="GraphicsSwitchPolicy"/> 产出。它**只表达「能不能切、为什么」**，
/// 不代表任何硬件已被写入。
/// </para>
/// <para>
/// <see cref="Allowed"/> 为 <see langword="true"/> 时，
/// <see cref="Status"/> 只可能是 <see cref="OperationStatus.PendingRestart"/>
/// （需重启或 S5 关机再开机生效）或 <see cref="OperationStatus.Succeeded"/>（已处于目标模式，无需切换）。
/// </para>
/// </remarks>
/// <param name="Allowed">是否允许继续执行写入。</param>
/// <param name="Status">对应到操作结果类型的状态。</param>
/// <param name="Reason">供界面与日志展示的说明，必非空。</param>
/// <param name="RequiresRestart">是否必须重启或 S5 关机再开机才生效。</param>
public sealed record GraphicsSwitchDecision(
    bool Allowed,
    OperationStatus Status,
    string Reason,
    bool RequiresRestart)
{
    /// <summary>
    /// 是否**真的需要调用写入方**。
    /// </summary>
    /// <remarks>
    /// 只有「允许且会产生待重启项」才需要落盘写入。
    /// 「已是目标模式」这类空操作虽然结论为允许，但没有任何东西要写，
    /// 若仍然委派写入方，会把「无需操作」错误地变成一次写入尝试。
    /// </remarks>
    public bool RequiresWrite => Allowed && Status == OperationStatus.PendingRestart;

    /// <summary>允许执行，并给出生效方式说明。</summary>
    public static GraphicsSwitchDecision Allow(bool requiresRestart, string reason)
        => new(true, requiresRestart ? OperationStatus.PendingRestart : OperationStatus.Succeeded, reason, requiresRestart);

    /// <summary>本机不具备该能力，拒绝执行。必须说明依据。</summary>
    public static GraphicsSwitchDecision Unsupported(string reason) => new(false, OperationStatus.Unsupported, reason, false);

    /// <summary>具备能力但前置条件不满足（如外接屏将断开、拓扑未知），拒绝执行。</summary>
    public static GraphicsSwitchDecision Blocked(string reason) => new(false, OperationStatus.Failed, reason, false);

    /// <summary>
    /// 转换为共享契约层的逐项结果。
    /// </summary>
    /// <param name="settingKey">设置项标识。</param>
    /// <param name="requested">请求值的可读表达。</param>
    /// <returns>逐项结果。成功分支携带非空的确认依据，满足 <c>ControlResult.Validate()</c> 约定。</returns>
    public SettingResult ToSettingResult(string settingKey, string requested)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);

        return Status switch
        {
            OperationStatus.Unsupported => SettingResult.Unsupported(settingKey, Reason),
            OperationStatus.PendingRestart => SettingResult.PendingRestart(settingKey, requested, Reason),
            OperationStatus.Succeeded => SettingResult.Succeeded(settingKey, requested, null, Reason),
            _ => SettingResult.Failed(settingKey, requested, Reason)
        };
    }
}

/// <summary>
/// 显卡模式切换策略（纯函数，无硬件访问）。
/// </summary>
/// <remarks>
/// <para>
/// 把任务书 §9 的规则落成可判定的顺序检查。策略被刻意做成**无副作用**，
/// 因此可以脱离硬件完整测试——这是本项目「不显示虚假成功」原则在显卡模块上的落地方式。
/// </para>
/// <para>
/// 检查顺序固定为：能力门控 → 拓扑可用性 → 目标模式支持性 → 是否已是目标模式 → 外接屏风险三态。
/// 前一项不通过即短路返回，不会因后续检查掩盖前一项的原因。
/// </para>
/// <para>
/// <b>「已是目标模式」必须连同「待生效目标」一起判断</b>（2026-10-28，HWE §27）：
/// 只比 <c>Current</c> 会把「已提交、待重启生效的另一态」当成不存在，
/// 于是切回原模式被误判为无需写入，重启后仍是之前提交的那一态。
/// </para>
/// <para>
/// <b>本策略不负责写入</b>，也不判断「重启还是 S5」的具体差别——
/// 该差别由实际写入方在结果说明中给出，避免在无证据时替用户断言。
/// </para>
/// </remarks>
public static class GraphicsSwitchPolicy
{
    /// <summary>设置项标识。与 <see cref="CapabilityKeys.GraphicsMode"/> 对应，便于界面按同一键展示。</summary>
    public const string GraphicsModeSettingKey = CapabilityKeys.GraphicsMode;

    /// <summary>
    /// 评估一次切换请求。
    /// </summary>
    /// <param name="state">当前显卡状态（含当前模式、外接屏拓扑信息与支持性）。</param>
    /// <param name="target">目标模式。</param>
    /// <param name="capabilities">设备能力集合。</param>
    /// <param name="topologyKnown">
    /// 显示拓扑是否已确证。<see langword="false"/> 时**不得**按「无风险」处理（任务书 §9.2）。
    /// </param>
    /// <returns>评估结论。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="state"/> 或 <paramref name="capabilities"/> 为 <see langword="null"/>。</exception>
    public static GraphicsSwitchDecision Evaluate(
        GraphicsHardwareState state,
        GraphicsMode target,
        DeviceCapabilities capabilities,
        bool topologyKnown)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(capabilities);

        // 1) 能力门控：未达 Verified 一律不得写入（任务书 §16.1、约定 §1.1）。
        var capability = capabilities.Get(CapabilityKeys.GraphicsMode);
        if (!capability.AllowsWrite)
        {
            return GraphicsSwitchDecision.Unsupported(
                $"显卡模式切换未通过能力门控（当前等级 {capability.Level}）：{capability.DescribeUnavailable()}");
        }

        // 2) 拓扑未确认：无法评估外接屏是否断开，按任务书 §9.2 拒绝而非默认放行。
        if (!topologyKnown)
        {
            return GraphicsSwitchDecision.Blocked(
                "无法确认显示拓扑（外接屏的连接路径未知），不能在无法评估外接屏是否断开的前提下切换显卡模式。");
        }

        // 3) 目标模式支持性：只对已观察到在生效的模式声明支持。
        if (!state.Supports(target))
        {
            return GraphicsSwitchDecision.Unsupported(
                $"本机未取得「{Describe(target)}」模式的切换能力证据，不提供该模式。");
        }

        // 4) 已处于目标模式且没有待生效变更：无需切换，也不产生待重启项。
        if (state.Current == target && !state.HasPendingChange)
        {
            return GraphicsSwitchDecision.Allow(
                requiresRestart: false,
                $"当前实际模式已是「{Describe(target)}」，无需切换。");
        }

        // 4b) 当前实际已是目标，但存在**另一态**的待生效目标（HWE §27）：
        //     必须写入把目标改回来，否则重启后会落到之前提交的那一态——
        //     用户看到的就是「切回去没用」。
        if (state.Current == target && state.HasPendingChange)
        {
            var pending = state.Pending!.Value;
            return GraphicsSwitchDecision.Allow(
                requiresRestart: true,
                $"当前实际模式已是「{Describe(target)}」，但已提交待生效的目标是"
                + $"「{Describe(pending)}」。本次会把切换目标改回「{Describe(target)}」，"
                + "重启后才抵消极之前的待生效变更。");
        }

        // 5) 外接屏风险三态：null 表示拓扑未确认，必须按「有风险」处理（任务书 §9.2）。
        var mayDisconnect = state.WouldDisconnectExternalDisplay(target);
        if (mayDisconnect is null)
        {
            return GraphicsSwitchDecision.Blocked(
                "外接屏的连接路径未知，无法判断切换后外接屏是否会断开，因此拒绝本次切换。");
        }

        if (mayDisconnect.Value)
        {
            return GraphicsSwitchDecision.Blocked(
                $"目标模式「{Describe(target)}」会使外接屏断开（外接屏由独显驱动，该模式不再保留独显输出），已拒绝。");
        }

        return GraphicsSwitchDecision.Allow(
            requiresRestart: true,
            "策略允许切换。写入后生效需要重启设备；"
            + "具体为「普通重启」还是「S5 关机再开机」，取决于实际固件路径，"
            + "须由写入方按实测结果给出，不得笼统写「需重启」（任务 06 提示词 §三.3）。"
            + "本程序不会自动重启设备。");
    }

    /// <summary>
    /// 显卡模式的中文名。
    /// </summary>
    /// <param name="mode">模式。</param>
    /// <returns>中文名；未识别的枚举值返回其名称。</returns>
    /// <remarks>
    /// 与界面层 <c>ModeFormatter.GraphicsText</c> 保持同一措辞。
    /// 此处自带一份是为了让本模块（不含 UI）能独立产出可读的原因文案。
    /// </remarks>
    public static string Describe(GraphicsMode mode) => mode switch
    {
        GraphicsMode.Integrated => "核显",
        GraphicsMode.Hybrid => "混合",
        GraphicsMode.Discrete => "独显直连",
        _ => mode.ToString()
    };
}
