using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices.Ms15M3;

namespace MsiControl.Graphics;

/// <summary>
/// 把只读显示拓扑解释为显卡状态（纯函数，无硬件访问）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要推断：</b>本机未定位到任何可读取 MUX / 显卡模式的状态接口
/// （见 <c>GraphicsInterfaceEvidence</c>），因此「当前模式」只能由**已确证的显示拓扑**
/// 反推，而不是直读。推断规则本身可判定、可测试，且无法判定时一律返回
/// <see langword="null"/>，不用默认值冒充已知（任务提示词 §七）。
/// </para>
/// <para>
/// <b>推断规则（与任务 06 实测证据一致）：</b>
/// <list type="bullet">
/// <item>内置屏由<b>独显</b>驱动 → <see cref="GraphicsMode.Discrete"/>（内置屏已切到独显直连）。</item>
/// <item>内置屏由<b>核显</b>驱动，且存在由独显驱动的外接屏 → <see cref="GraphicsMode.Hybrid"/>
///       （两块 GPU 同时在输出，符合 MSHybrid 拓扑）。</item>
/// <item>其余情形（无内置屏、内置屏驱动方未知、或内置屏走核显但独显无任何输出）
///       → <see langword="null"/>：核显模式与「无外接屏的混合模式」在拓扑上不可区分，
///       不猜测。</item>
/// </list>
/// </para>
/// <para>
/// <b>能力标志的语义（2026-10-17 随 HWE §21 证据修订）：</b>
/// <see cref="GraphicsHardwareState.SupportsIntegrated"/> 等三项表达
/// 「本机 MUX 协议是否存在该模式的编码」：混合与独显直连两态有编码（支持），
/// 「核显模式」无编码（不支持）。实际可写还须通过 <c>graphics.mode</c>
/// 能力键的门控（<see cref="CapabilityLevel.Verified"/>），两级判定缺一不可。
/// </para>
/// </remarks>
public static class GraphicsModeInference
{
    /// <summary>
    /// 由显示拓扑推断当前实际模式。
    /// </summary>
    /// <param name="topology">只读显示拓扑。</param>
    /// <returns>推断出的模式；无法判定时为 <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> 为 <see langword="null"/>。</exception>
    public static GraphicsMode? InferCurrent(GraphicsDisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        if (!topology.IsKnown)
        {
            return null;
        }

        var internalDisplays = topology.InternalDisplays;

        // 本机设计为单内置屏。多块或零块内置屏时拓扑语义不明确，不推断。
        if (internalDisplays.Count != 1)
        {
            return null;
        }

        var internalGpu = internalDisplays[0].ParentGpuKind;

        if (internalGpu == GpuKind.Discrete)
        {
            return GraphicsMode.Discrete;
        }

        if (internalGpu == GpuKind.Integrated
            && topology.ExternalDisplays.Any(d => d.ParentGpuKind == GpuKind.Discrete))
        {
            return GraphicsMode.Hybrid;
        }

        return null;
    }

    /// <summary>
    /// 由显示拓扑构造显卡状态。
    /// </summary>
    /// <param name="topology">只读显示拓扑。</param>
    /// <returns>
    /// 显卡状态。<see cref="GraphicsHardwareState.Pending"/> 恒为 <see langword="null"/>：
    /// 待生效项由切换请求的结果驱动，不由拓扑推断产生。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="topology"/> 为 <see langword="null"/>.</exception>
    /// <remarks>
    /// <b>支持性语义（2026-10-17 随 HWE §21 证据修订）：</b>
    /// MS-15M3 的 MUX 协议（ElXreno/msi-gpu-switcher，MIT；本机只读对照通过）只有
    /// 「混合 / 独显直连」两态，二者均声明为受支持；「核显模式」无对应编码，
    /// 维持不支持——不猜测、不用混合模式冒充。能力开关仍由
    /// <c>graphics.mode</c> 能力键（<c>CapabilityLevel</c>）单独把守，两级判定缺一不可。
    /// </remarks>
    public static GraphicsHardwareState Build(GraphicsDisplayTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var current = InferCurrent(topology);

        return new GraphicsHardwareState(
            Current: current,
            Pending: null,
            RequiresRestart: false,
            ExternalDisplayAttached: topology.IsKnown && topology.HasExternalDisplay,
            ExternalDisplayDependsOnDiscreteGpu: topology.IsKnown ? topology.ExternalDependsOnDiscreteGpu : null,
            SupportsIntegrated: false,
            SupportsHybrid: topology.IsKnown,
            SupportsDiscrete: topology.IsKnown);
    }
}
