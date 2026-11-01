namespace MsiControl.Core.Models;

/// <summary>
/// 显卡模式硬件状态。
/// </summary>
/// <remarks>
/// <para>
/// 本类型同时承载「当前模式」与「待生效模式」，对应任务书 §9 要求界面
/// <b>不覆盖实际状态</b>：<see cref="Current"/> 是现实，<see cref="Pending"/> 是意图。
/// </para>
/// <para>
/// 切换前必须确认外接屏的实际连接路径，不能仅根据线缆名称推断拓扑（任务书 §9.2）。
/// </para>
/// </remarks>
/// <param name="Current">当前实际生效的模式。未知时为 <see langword="null"/>。</param>
/// <param name="Pending">已请求但尚未生效的模式。无待生效项时为 <see langword="null"/>。</param>
/// <param name="RequiresRestart">待生效模式生效是否需要重启或 S5 关机再开机。</param>
/// <param name="ExternalDisplayAttached">当前是否连接了外接显示器。</param>
/// <param name="ExternalDisplayDependsOnDiscreteGpu">外接显示器是否依赖独显输出。未知时为 <see langword="null"/>。</param>
/// <param name="SupportsIntegrated">是否支持核显模式。</param>
/// <param name="SupportsHybrid">是否支持混合模式。</param>
/// <param name="SupportsDiscrete">是否支持独显直连。</param>
public sealed record GraphicsHardwareState(
    GraphicsMode? Current = null,
    GraphicsMode? Pending = null,
    bool RequiresRestart = false,
    bool ExternalDisplayAttached = false,
    bool? ExternalDisplayDependsOnDiscreteGpu = null,
    bool SupportsIntegrated = false,
    bool SupportsHybrid = false,
    bool SupportsDiscrete = false)
{
    /// <summary>尚未适配的未知状态。</summary>
    public static GraphicsHardwareState Unknown { get; } = new();

    /// <summary>是否存在尚未生效的模式变更。</summary>
    public bool HasPendingChange => Pending.HasValue && Pending != Current;

    /// <summary>指定模式是否被本机支持。</summary>
    public bool Supports(GraphicsMode mode) => mode switch
    {
        GraphicsMode.Integrated => SupportsIntegrated,
        GraphicsMode.Hybrid => SupportsHybrid,
        GraphicsMode.Discrete => SupportsDiscrete,
        _ => false
    };

    /// <summary>
    /// 判断切换到目标模式是否会中断外接显示。
    /// </summary>
    /// <returns>存在中断风险时返回 <see langword="true"/>；无法判断时返回 <see langword="null"/>.</returns>
    /// <remarks>
    /// <para>
    /// 供界面在切换前给出明确提示。返回 <see langword="null"/> 表示拓扑尚未确认，
    /// 此时<b>不得默认按「无风险」处理</b>。
    /// </para>
    /// <para>
    /// <b>规则修订（2026-10-17，依据 HWE §12.1 实测与本机 EC/UEFI 切换协议 §21）：</b>
    /// 只有「核显模式」（独显彻底退出输出）会使依赖独显的外接屏断开；
    /// 混合模式下独显仍在输出（本机实测：混合拓扑中外接屏正是由独显驱动的），
    /// 独显直连亦保留独显输出。旧规则「任何非独显直连目标都有风险」把混合模式一并
    /// 判为有风险，与本机实测相悖，已按证据修订。
    /// </para>
    /// </remarks>
    public bool? WouldDisconnectExternalDisplay(GraphicsMode target)
    {
        if (!ExternalDisplayAttached)
        {
            return false;
        }

        if (ExternalDisplayDependsOnDiscreteGpu is null)
        {
            return null;
        }

        return ExternalDisplayDependsOnDiscreteGpu.Value && target == GraphicsMode.Integrated;
    }
}
