using MsiControl.Hardware.Msi.Devices.Ms15M3;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// 测试用显示拓扑构造器。
/// </summary>
/// <remarks>
/// 数据取自任务 06 阶段一对本机的只读实测（见 <c>docs/hardware-evidence.md</c> 追加条目），
/// 便于回归测试直接复现真实拓扑形态，而不是凭空编造。
/// </remarks>
internal static class TopologyFactory
{
    public static readonly GpuDescriptor IntelIntegrated = new(
        @"PCI\VEN_8086&DEV_7D67&SUBSYS_14741462&REV_06\3&11583659&0&10",
        "Intel(R) Graphics",
        GpuKind.Integrated);

    public static readonly GpuDescriptor NvidiaDiscrete = new(
        @"PCI\VEN_10DE&DEV_2F18&SUBSYS_14741462&REV_A1\D1FBA195EC2DB04800",
        "NVIDIA GeForce RTX 5070 Ti Laptop GPU",
        GpuKind.Discrete);

    public const string InternalMonitorInstanceId = @"DISPLAY\AUO7DB2\4&294364CA&0&UID8388688";
    public const string ExternalMonitorInstanceId = @"DISPLAY\SAC2563\5&26AB8A4B&1&UID4356";

    /// <summary>内置屏（AUO 面板）。</summary>
    public static DisplayAttachment Internal(GpuKind parentKind) => new(
        InternalMonitorInstanceId,
        IsInternal: true,
        VideoOutputTechnology: GraphicsTopologyProbe.OutputTechnologyInternal,
        ParentGpuInstanceId: parentKind == GpuKind.Discrete ? NvidiaDiscrete.InstanceId : IntelIntegrated.InstanceId,
        ParentGpuKind: parentKind);

    /// <summary>外接屏（DisplayPort 外接，<c>VideoOutputTechnology = 10</c>）。</summary>
    public static DisplayAttachment External(GpuKind parentKind) => new(
        ExternalMonitorInstanceId,
        IsInternal: false,
        VideoOutputTechnology: 10,
        ParentGpuInstanceId: parentKind == GpuKind.Discrete ? NvidiaDiscrete.InstanceId : IntelIntegrated.InstanceId,
        ParentGpuKind: parentKind);

    /// <summary>构造拓扑。</summary>
    public static GraphicsDisplayTopology Create(params DisplayAttachment[] displays)
        => new(new[] { IntelIntegrated, NvidiaDiscrete }, displays, Array.Empty<string>());

    /// <summary>本机实测形态：内置屏走核显、外接屏走独显（混合模式）。</summary>
    public static GraphicsDisplayTopology HybridWithExternalOnDiscrete()
        => Create(Internal(GpuKind.Integrated), External(GpuKind.Discrete));

    /// <summary>内置屏由独显驱动（独显直连形态）。</summary>
    public static GraphicsDisplayTopology InternalOnDiscrete()
        => Create(Internal(GpuKind.Discrete));

    /// <summary>独显直连且接了外接屏：内置屏与外接屏都由独显驱动。</summary>
    /// <remarks>用于构造「切换到核显会断开外接屏」的场景。</remarks>
    public static GraphicsDisplayTopology DiscreteWithExternalOnDiscrete()
        => Create(Internal(GpuKind.Discrete), External(GpuKind.Discrete));

    /// <summary>外接屏的驱动方无法判定。</summary>
    public static GraphicsDisplayTopology ExternalOnUnknownGpu()
        => Create(Internal(GpuKind.Integrated), External(GpuKind.Unknown));
}
