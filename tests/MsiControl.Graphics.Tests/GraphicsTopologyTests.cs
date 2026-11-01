using MsiControl.Hardware.Msi.Devices.Ms15M3;
using Xunit;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// 显示拓扑模型与 GPU 分类的回归测试。
/// </summary>
/// <remarks>
/// 重点验证任务书 §9.2 的三态语义：外接屏是否依赖独显必须是
/// <c>true</c> / <c>false</c> / <c>null</c> 三态，**不能把「未知」当成「无风险」**。
/// </remarks>
public class GraphicsTopologyTests
{
    [Fact]
    public void ExternalDependsOnDiscreteGpu_TrueWhenAllExternalDrivenByDiscrete()
    {
        var topology = TopologyFactory.HybridWithExternalOnDiscrete();

        Assert.True(topology.ExternalDependsOnDiscreteGpu);
        Assert.True(topology.HasExternalDisplay);
        Assert.True(topology.HasInternalDisplay);
    }

    [Fact]
    public void ExternalDependsOnDiscreteGpu_FalseWhenExternalDrivenByIntegrated()
    {
        var topology = TopologyFactory.Create(
            TopologyFactory.Internal(GpuKind.Integrated),
            TopologyFactory.External(GpuKind.Integrated));

        Assert.False(topology.ExternalDependsOnDiscreteGpu);
    }

    [Fact]
    public void ExternalDependsOnDiscreteGpu_NullWhenExternalGpuUnknown()
    {
        // 未知必须表达为 null，供上层按「有风险」处理。
        var topology = TopologyFactory.Create(
            TopologyFactory.Internal(GpuKind.Integrated),
            TopologyFactory.External(GpuKind.Unknown));

        Assert.Null(topology.ExternalDependsOnDiscreteGpu);
    }

    [Fact]
    public void ExternalDependsOnDiscreteGpu_FalseWhenNoExternalDisplay()
    {
        var topology = TopologyFactory.InternalOnDiscrete();

        Assert.False(topology.ExternalDependsOnDiscreteGpu);
        Assert.False(topology.HasExternalDisplay);
        Assert.Empty(topology.ExternalDisplays);
    }

    [Fact]
    public void IsKnown_RequiresBothGpuAndDisplayFacts()
    {
        var noDisplays = new GraphicsDisplayTopology(
            new[] { TopologyFactory.IntelIntegrated },
            Array.Empty<DisplayAttachment>(),
            Array.Empty<string>());

        var noGpus = new GraphicsDisplayTopology(
            Array.Empty<GpuDescriptor>(),
            new[] { TopologyFactory.Internal(GpuKind.Integrated) },
            Array.Empty<string>());

        Assert.False(noDisplays.IsKnown);       // 有 GPU 但没判定任何显示器 → 仍属未知
        Assert.False(noGpus.IsKnown);
        Assert.False(GraphicsDisplayTopology.Unknown.IsKnown);
        Assert.True(TopologyFactory.HybridWithExternalOnDiscrete().IsKnown);
    }

    [Fact]
    public void Unknown_HasNoFacts()
    {
        var unknown = GraphicsDisplayTopology.Unknown;

        Assert.False(unknown.IsKnown);
        Assert.False(unknown.HasInternalDisplay);
        Assert.False(unknown.HasExternalDisplay);
        Assert.Empty(unknown.Displays);
        Assert.NotEmpty(unknown.Notes);   // 必须说明为何未知
    }

    // ---------- GPU 分类 ----------

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_7D67&SUBSYS_14741462&REV_06\3&11583659&0&10", "Intel(R) Graphics", GpuKind.Integrated)]
    [InlineData(@"PCI\VEN_10DE&DEV_2F18&SUBSYS_14741462&REV_A1\D1FBA195EC2DB04800", "NVIDIA GeForce RTX 5070 Ti Laptop GPU", GpuKind.Discrete)]
    [InlineData(@"PCI\VEN_1002&DEV_1114&SUBSYS_12345678&REV_00\000000", "AMD Radeon RX 7900M", GpuKind.Discrete)]
    [InlineData(@"PCI\VEN_1002&DEV_1234&SUBSYS_12345678&REV_00\000000", "AMD Radeon Graphics", GpuKind.Integrated)]
    [InlineData(@"PCI\VEN_1002&DEV_1234&SUBSYS_12345678&REV_00\000000", "AMD Radeon Pro W7900", GpuKind.Discrete)]
    [InlineData(null, null, GpuKind.Unknown)]
    [InlineData(@"ROOT\BasicDisplay\0000", "Microsoft Basic Display Adapter", GpuKind.Unknown)]
    public void ClassifyGpu_ByPciVendorAndModelName(string? instanceId, string? name, GpuKind expected)
    {
        Assert.Equal(expected, GraphicsTopologyProbe.ClassifyGpu(instanceId, name));
    }

    [Fact]
    public void ClassifyGpu_AmdVendorWithoutConclusiveName_IsUnknown()
    {
        // 无法确定时必须返回未知，不得猜成核显或独显。
        Assert.Equal(
            GpuKind.Unknown,
            GraphicsTopologyProbe.ClassifyGpu(@"PCI\VEN_1002&DEV_9999&SUBSYS_0&REV_0\0", "AMD Graphics"));
    }

    [Theory]
    [InlineData(@"DISPLAY\AUO7DB2\4&294364ca&0&UID8388688_0", @"DISPLAY\AUO7DB2\4&294364ca&0&UID8388688")]
    [InlineData(@"DISPLAY\SAC2563\5&26ab8a4b&1&UID4356_0", @"DISPLAY\SAC2563\5&26ab8a4b&1&UID4356")]
    [InlineData(@"DISPLAY\X\Y", @"DISPLAY\X\Y")]
    public void NormalizeMonitorInstanceName_TrimsTrailingSuffix(string input, string expected)
    {
        Assert.Equal(expected, GraphicsTopologyProbe.NormalizeMonitorInstanceName(input));
    }

    [Fact]
    public void OutputTechnologyInternal_MatchesDisplayConfigConstant()
    {
        // DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000
        Assert.Equal(0x8000_0000L, GraphicsTopologyProbe.OutputTechnologyInternal);
    }
}
