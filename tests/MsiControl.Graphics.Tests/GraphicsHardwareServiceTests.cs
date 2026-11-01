using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using Xunit;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// 显卡模块服务（<see cref="IGraphicsHardware"/> 实现）的回归测试。
/// </summary>
/// <remarks>
/// 全部使用测试替身，不接触真实硬件，也绝不允许在自动化测试中尝试写入。
/// </remarks>
public class GraphicsHardwareServiceTests
{
    private static readonly DeviceCapabilities UnknownCapabilities = DeviceCapabilities.Empty;

    private static readonly DeviceCapabilities VerifiedCapabilities = new(new[]
    {
        CapabilityStatus.Verified(CapabilityKeys.GraphicsMode, "测试用证据：仅用于覆盖策略分支。")
    });

    [Fact]
    public async Task ReadAsync_ReturnsInferredStateFromProbe()
    {
        var probe = new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete());
        var service = new GraphicsHardwareService(probe, UnknownCapabilities);

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(1, probe.CallCount);
        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.True(state.ExternalDisplayAttached);
        Assert.True(state.ExternalDisplayDependsOnDiscreteGpu);
    }

    [Fact]
    public async Task ReadTopologyAsync_ExposesRawTopologyForDiagnostics()
    {
        var topology = TopologyFactory.HybridWithExternalOnDiscrete();
        var service = new GraphicsHardwareService(new FakeTopologyProbe(topology), UnknownCapabilities);

        var result = await service.ReadTopologyAsync(CancellationToken.None);

        Assert.Same(topology, result);
    }

    [Fact]
    public async Task RequestModeAsync_WithUnknownCapability_ReportsUnsupportedAndNeverWrites()
    {
        var writer = new RecordingWriter();
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            writer);

        var result = await service.RequestModeAsync(GraphicsMode.Discrete, CancellationToken.None);

        Assert.Equal(0, writer.WriteCount);
        Assert.Single(result.Settings);
        Assert.Equal(OperationStatus.Unsupported, result.Settings[0].Status);
        Assert.Equal(CapabilityKeys.GraphicsMode, result.Settings[0].Setting);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task RequestModeAsync_WhenPolicyBlocks_DoesNotCallWriter()
    {
        // 能力为 Verified，但拓扑未知 → 策略拒绝，写入方不得被调用。
        var writer = new RecordingWriter();
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(GraphicsDisplayTopology.Unknown),
            VerifiedCapabilities,
            writer);

        var result = await service.RequestModeAsync(GraphicsMode.Discrete, CancellationToken.None);

        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(OperationStatus.Failed, result.Settings[0].Status);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task RequestModeAsync_NoOpRequest_DoesNotInvokeWriter()
    {
        // 「已是目标模式」虽然结论为允许，但没有任何东西要写，
        // 不得把它变成一次多余的写入尝试。
        var writer = new RecordingWriter();
        var probe = new FakeTopologyProbe(TopologyFactory.InternalOnDiscrete());
        var service = new GraphicsHardwareService(probe, VerifiedCapabilities, writer);

        var result = await service.RequestModeAsync(GraphicsMode.Discrete, CancellationToken.None);

        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(OperationStatus.Succeeded, result.Settings[0].Status);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task RequestModeAsync_AtTargetButOtherModePending_StillDelegatesToWriter()
    {
        // HWE §27 回归：真实场景是「上电后拓扑仍是混合，但之前提交的目标是独显直连」。
        // 用户此时点「混合」，必须先经 EC/UEFI 把目标改回混合——否则重启后落到独显直连，
        // 表现为「切回原模式没用」。这条测试用真实的三载体读回路径（不是注入状态构造器）覆盖它。
        var writer = new RecordingWriter();
        var readback = new GraphicsMuxReadback(
            Registered: GraphicsMode.Discrete,   // EC 0x2E bit6=1（已写为目标值）
            TriggerPending: true,                // EC 0xD1 非零：切换已受理、待重启
            UefiTarget: GraphicsMode.Discrete,
            Error: null);

        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            VerifiedCapabilities,
            writer,
            stateReader: new FakeStateReader(readback));

        var result = await service.RequestModeAsync(GraphicsMode.Hybrid, CancellationToken.None);

        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(GraphicsMode.Hybrid, writer.LastMode);
        Assert.Equal(OperationStatus.Succeeded, result.Settings[0].Status);
    }

    [Fact]
    public async Task RequestModeAsync_WhenWriteIsRequired_DelegatesToWriter()
    {
        // 这条分支在生产环境**不可达**：本机没有切换接口，推断只会把「当前正在生效的模式」
        // 标为受支持，而切到当前模式属于空操作。为让委派逻辑仍有回归覆盖，
        // 这里通过内部重载注入受控状态构造器。
        var writer = new RecordingWriter();
        var state = GraphicsModeInference.Build(TopologyFactory.InternalOnDiscrete()) with
        {
            SupportsHybrid = true
        };

        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.InternalOnDiscrete()),
            VerifiedCapabilities,
            writer,
            _ => state);

        var result = await service.RequestModeAsync(GraphicsMode.Hybrid, CancellationToken.None);

        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(GraphicsMode.Hybrid, writer.LastMode);
        Assert.Equal(OperationStatus.Succeeded, result.Settings[0].Status);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task RequestModeAsync_AlwaysReReadsStateBeforeDeciding()
    {
        // 待生效与拓扑风险都取决于当下拓扑，不能复用缓存。
        var probe = new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete());
        var service = new GraphicsHardwareService(probe, UnknownCapabilities);

        await service.RequestModeAsync(GraphicsMode.Discrete, CancellationToken.None);

        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task RequestModeAsync_WithUnknownCapability_AllTargetsRejectedWithoutReachingWriter()
    {
        // 能力未登记/未达 Verified（等价于生产回退路径）：任何请求都在策略层被拒。
        var writer = new RecordingWriter();
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            writer);

        foreach (var mode in Enum.GetValues<GraphicsMode>())
        {
            var result = await service.RequestModeAsync(mode, CancellationToken.None);

            Assert.Equal(OperationStatus.Unsupported, result.Settings[0].Status);
            Assert.Empty(result.Validate());
        }

        Assert.Equal(0, writer.WriteCount);
    }

    [Fact]
    public async Task RequestModeAsync_EvenWithVerifiedCapability_RejectsIntegratedTarget()
    {
        // 「核显模式」在 MUX 协议中没有编码（HWE §21），即便能力为 Verified 也不支持。
        var writer = new RecordingWriter();
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            VerifiedCapabilities,
            writer);

        var result = await service.RequestModeAsync(GraphicsMode.Integrated, CancellationToken.None);

        Assert.Equal(0, writer.WriteCount);
        Assert.Equal(OperationStatus.Unsupported, result.Settings[0].Status);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task RequestModeAsync_VerifiedCapability_RealSwitchReachesWriter()
    {
        // 生产可达路径：混合拓扑 + graphics.mode 达 Verified → 切「独显直连」委派写入方。
        var writer = new RecordingWriter();
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            VerifiedCapabilities,
            writer);

        var result = await service.RequestModeAsync(GraphicsMode.Discrete, CancellationToken.None);

        Assert.Equal(1, writer.WriteCount);
        Assert.Equal(GraphicsMode.Discrete, writer.LastMode);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => new GraphicsHardwareService(
            null!, UnknownCapabilities));

        Assert.Throws<ArgumentNullException>(() => new GraphicsHardwareService(
            new FakeTopologyProbe(GraphicsDisplayTopology.Unknown), null!));
    }

    [Fact]
    public async Task UnsupportedGraphicsModeWriter_AlwaysReportsUnsupportedWithEvidence()
    {
        var result = await UnsupportedGraphicsModeWriter.Instance.WriteAsync(
            GraphicsMode.Discrete,
            GraphicsModeInference.Build(TopologyFactory.HybridWithExternalOnDiscrete()),
            CancellationToken.None);

        Assert.Equal(CapabilityKeys.GraphicsMode, result.Settings[0].Setting);
        Assert.Equal(OperationStatus.Unsupported, result.Settings[0].Status);
        Assert.Empty(result.Validate());
        Assert.Contains("0x8004100C", result.Settings[0].Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_NoPending_UsesRegisteredMuxMode()
    {
        // 无切换在途：EC 0x2E 登记值即实际生效值（HWE §21.2），覆盖拓扑推断。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(GraphicsMode.Discrete, TriggerPending: false, GraphicsMode.Discrete, null)));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Discrete, state.Current);
        Assert.Null(state.Pending);
        Assert.False(state.RequiresRestart);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_UefiTargetDiffers_ReportsPendingWithoutTrigger()
    {
        // 外部工具只改 UEFI 目标：EC 登记值仍是实际态，UEFI 目标是待生效项。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(GraphicsMode.Hybrid, TriggerPending: false, GraphicsMode.Discrete, null)));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.Equal(GraphicsMode.Discrete, state.Pending);
        Assert.True(state.RequiresRestart);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_TriggerPending_PrefersInferredActualMode()
    {
        // 切换在途（0xD1 触发位置位）：EC 已写为目标、硬件实际仍是另一态；
        // 实际态取拓扑推断（本机混合拓扑可判定）。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(GraphicsMode.Discrete, TriggerPending: true, GraphicsMode.Discrete, null)));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.Equal(GraphicsMode.Discrete, state.Pending);
        Assert.True(state.RequiresRestart);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_TriggerPendingAndInferenceUnknown_UsesOppositeOfRegistered()
    {
        // 内置屏走核显且无外接屏：拓扑推不出混合/核显之分（推断为 null）。
        // 两态 MUX 下触发位只在 EC 写成功后置位，故写入前寄存器必为另一态——取反面有协议依据。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.Create(TopologyFactory.Internal(GpuKind.Integrated))),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(GraphicsMode.Discrete, TriggerPending: true, GraphicsMode.Discrete, null)));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.Equal(GraphicsMode.Discrete, state.Pending);
        Assert.True(state.RequiresRestart);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_TriggerPendingHybridTarget_InfersDiscreteActual()
    {
        // 对称方向：登记目标为混合、推断不出时实际态取反面（独显直连）。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.Create(TopologyFactory.Internal(GpuKind.Integrated))),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(GraphicsMode.Hybrid, TriggerPending: true, GraphicsMode.Hybrid, null)));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Discrete, state.Current);
        Assert.Equal(GraphicsMode.Hybrid, state.Pending);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_TransientReadFailure_FallsBackToInference()
    {
        // 读回方返回 null（瞬时失败）：保持拓扑推断，不编造直读值。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            stateReader: new FakeStateReader(null));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.Null(state.Pending);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_ChannelUnavailable_FallsBackToInference()
    {
        // 读回方带 Error（通道不可用）：回落推断，与未注入读回方行为一致。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(null, false, null, "PawnIO 通道不可用")));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.Equal(GraphicsMode.Hybrid, state.Current);
    }

    [Fact]
    public async Task ReadAsync_WithStateReader_PreservesTopologyFlags()
    {
        // 读回只改「当前/待生效」两项，拓扑风险字段仍来自探测。
        var service = new GraphicsHardwareService(
            new FakeTopologyProbe(TopologyFactory.HybridWithExternalOnDiscrete()),
            UnknownCapabilities,
            stateReader: new FakeStateReader(
                new GraphicsMuxReadback(GraphicsMode.Hybrid, TriggerPending: false, GraphicsMode.Hybrid, null)));

        var state = await service.ReadAsync(CancellationToken.None);

        Assert.True(state.ExternalDisplayAttached);
        Assert.True(state.ExternalDisplayDependsOnDiscreteGpu);
        Assert.True(state.SupportsHybrid);
        Assert.True(state.SupportsDiscrete);
        Assert.False(state.SupportsIntegrated);
    }

    private sealed class FakeStateReader : IGraphicsModeStateReader
    {
        public FakeStateReader(GraphicsMuxReadback? result) => Result = result;

        public GraphicsMuxReadback? Result { get; }

        public int CallCount { get; private set; }

        public Task<GraphicsMuxReadback?> ReadAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeTopologyProbe : IDisplayTopologyProbe
    {
        private readonly GraphicsDisplayTopology _topology;

        public FakeTopologyProbe(GraphicsDisplayTopology topology) => _topology = topology;

        public int CallCount { get; private set; }

        public Task<GraphicsDisplayTopology> ProbeAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_topology);
        }
    }

    private sealed class RecordingWriter : IGraphicsModeWriter
    {
        public int WriteCount { get; private set; }

        public GraphicsMode? LastMode { get; private set; }

        public Task<ControlResult> WriteAsync(
            GraphicsMode mode,
            GraphicsHardwareState currentState,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            LastMode = mode;
            cancellationToken.ThrowIfCancellationRequested();

            return Task.FromResult(ControlResult.Single(SettingResult.Succeeded(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                GraphicsSwitchPolicy.Describe(mode),
                GraphicsSwitchPolicy.Describe(mode),
                confirmationBasis: "测试替身：写入后读回一致。")));
        }
    }
}
