using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using Xunit;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// 拓扑 → 显卡状态推断的回归测试。
/// </summary>
/// <remarks>
/// 重点验证两件事：
/// ① 无法判定时必须返回 <see langword="null"/>，不得用默认值冒充已知；
/// ② 支持性标志表达「MUX 协议是否有该模式的编码」（HWE §21）：
///    混合与独显直连有编码，核显模式无编码。
/// </remarks>
public class GraphicsModeInferenceTests
{
    [Fact]
    public void InferCurrent_HybridWhenInternalOnIntegratedAndExternalOnDiscrete()
    {
        var topology = TopologyFactory.HybridWithExternalOnDiscrete();

        Assert.Equal(GraphicsMode.Hybrid, GraphicsModeInference.InferCurrent(topology));
    }

    [Fact]
    public void InferCurrent_DiscreteWhenInternalDrivenByDiscrete()
    {
        var topology = TopologyFactory.InternalOnDiscrete();

        Assert.Equal(GraphicsMode.Discrete, GraphicsModeInference.InferCurrent(topology));
    }

    [Fact]
    public void InferCurrent_NullWhenInternalOnIntegratedWithoutDiscreteOutput()
    {
        // 核显驱动内置屏、独显无任何输出：可能是核显模式，也可能是混合模式但未接外接屏。
        // 拓扑上不可区分，必须返回 null，不得猜。
        var topology = TopologyFactory.Create(TopologyFactory.Internal(GpuKind.Integrated));

        Assert.Null(GraphicsModeInference.InferCurrent(topology));
    }

    [Fact]
    public void InferCurrent_NullWhenTopologyUnknown()
    {
        Assert.Null(GraphicsModeInference.InferCurrent(GraphicsDisplayTopology.Unknown));
    }

    [Fact]
    public void InferCurrent_NullWhenInternalGpuUnknown()
    {
        var topology = TopologyFactory.Create(TopologyFactory.Internal(GpuKind.Unknown));

        Assert.Null(GraphicsModeInference.InferCurrent(topology));
    }

    [Fact]
    public void InferCurrent_NullWhenNoInternalDisplay()
    {
        var topology = TopologyFactory.Create(TopologyFactory.External(GpuKind.Discrete));

        Assert.Null(GraphicsModeInference.InferCurrent(topology));
    }

    // ---------- Build ----------

    [Fact]
    public void Build_HybridMachine_SupportsBothMuxModesButNotIntegrated()
    {
        var state = GraphicsModeInference.Build(TopologyFactory.HybridWithExternalOnDiscrete());

        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.Null(state.Pending);                     // 待生效项由切换结果驱动，不由推断产生
        Assert.False(state.RequiresRestart);
        Assert.True(state.ExternalDisplayAttached);
        Assert.True(state.ExternalDisplayDependsOnDiscreteGpu);

        // MUX 协议（HWE §21）只有「混合 / 独显直连」两态编码 → 两者均声明支持；
        // 「核显模式」无编码 → 不支持，不猜测、不冒充。
        Assert.True(state.SupportsHybrid);
        Assert.True(state.SupportsDiscrete);
        Assert.False(state.SupportsIntegrated);
        Assert.True(state.Supports(GraphicsMode.Hybrid));
        Assert.True(state.Supports(GraphicsMode.Discrete));
        Assert.False(state.Supports(GraphicsMode.Integrated));
    }

    [Fact]
    public void Build_DiscreteMachine_SupportsBothMuxModesButNotIntegrated()
    {
        var state = GraphicsModeInference.Build(TopologyFactory.InternalOnDiscrete());

        Assert.Equal(GraphicsMode.Discrete, state.Current);
        Assert.True(state.SupportsDiscrete);
        Assert.True(state.SupportsHybrid);
        Assert.False(state.SupportsIntegrated);
    }

    [Fact]
    public void Build_UnknownTopology_MakesNoClaims()
    {
        var state = GraphicsModeInference.Build(GraphicsDisplayTopology.Unknown);

        Assert.Null(state.Current);
        Assert.Null(state.Pending);
        Assert.Null(state.ExternalDisplayDependsOnDiscreteGpu);  // 未知，不是「无风险」
        Assert.False(state.ExternalDisplayAttached);             // 不声称已接外接屏
        Assert.False(state.SupportsIntegrated);
        Assert.False(state.SupportsHybrid);                      // 拓扑未知时不声明 MUX 支持
        Assert.False(state.SupportsDiscrete);
        Assert.False(state.HasPendingChange);
    }

    [Fact]
    public void Build_HybridMachine_TopologyRiskFollowsCoreRules()
    {
        var state = GraphicsModeInference.Build(TopologyFactory.HybridWithExternalOnDiscrete());

        // 切到核显模式（独显彻底退出输出）会断开依赖独显的外接屏。
        Assert.True(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));
        Assert.False(state.WouldDisconnectExternalDisplay(GraphicsMode.Discrete));

        // Core 规则修订（2026-10-17，依据 HWE §12.1 实测）：混合模式下独显仍在输出
        // （本机实测：外接屏正是由独显驱动的），因此切到混合不再被判为断开风险。
        Assert.False(state.WouldDisconnectExternalDisplay(GraphicsMode.Hybrid));
    }

    [Fact]
    public void Build_ExternalOnUnknownGpu_ReportsUnknownRisk()
    {
        var topology = TopologyFactory.Create(
            TopologyFactory.Internal(GpuKind.Integrated),
            TopologyFactory.External(GpuKind.Unknown));

        var state = GraphicsModeInference.Build(topology);

        // 拓扑里已有外接屏，但驱动方未知 → 风险必须为 null（未知），不得当成无风险。
        Assert.True(state.ExternalDisplayAttached);
        Assert.Null(state.ExternalDisplayDependsOnDiscreteGpu);
        Assert.Null(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));
    }

    [Fact]
    public void Build_NoExternalDisplay_NoDisconnectRisk()
    {
        var state = GraphicsModeInference.Build(TopologyFactory.InternalOnDiscrete());

        Assert.False(state.ExternalDisplayAttached);
        Assert.False(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));
    }
}
