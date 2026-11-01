using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using MsiControl.Hardware.Msi.Transport;
using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>
/// MS-15M3 性能硬件适配的行为约束测试。
/// </summary>
/// <remarks>
/// 这些用例锁定的核心是「<b>不许显示虚假成功</b>」：
/// 能力未达 <see cref="CapabilityLevel.Verified"/> 时不得写入；
/// 协议未验证时不得写入；不得把原始字节流解释成瓦数；
/// 每一项都必须记录请求值并给出不可用原因。
/// </remarks>
public sealed class Ms15M3PerformanceHardwareTests
{
    private static Ms15M3PerformanceHardware Create(
        IMsiAcpiTransport transport,
        DeviceCapabilities capabilities)
        => new(transport, capabilities);

    private static PerformanceSettings BalancedSettings()
        => PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings;

    [Fact]
    public async Task ApplyAsync_ReportsUnsupportedPerItem_WithReasonAndRequestedValue()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, Ms15M3Capabilities.Create());

        var result = await hardware.ApplyAsync(BalancedSettings(), CancellationToken.None);

        Assert.Equal(5, result.Settings.Count);
        Assert.All(result.Settings, item =>
        {
            Assert.Equal(OperationStatus.Unsupported, item.Status);
            Assert.False(string.IsNullOrWhiteSpace(item.Requested));
        });

        // 能力未达可写的项（档位 / 睿频 / GPU）：说明里点明「未达」。
        Assert.All(
            result.Settings.Where(s => s.Setting is "stage" or "cpu.turbo" or "gpu.limit"),
            item => Assert.Contains("未达", item.Message));

        // CPU 功耗墙自 HWE §26 起已在能力基线里达可写，但**本 WMI 通道**不承载：
        // 必须如实说明真实通道（MSR 0x610），不得给出成功结果。
        Assert.All(
            result.Settings.Where(s => s.Setting is "cpu.longterm" or "cpu.shortterm"),
            item => Assert.Contains("MSR 0x610", item.Message));

        Assert.Contains(result.Settings, s => s.Setting == "cpu.longterm");
        Assert.Contains(result.Settings, s => s.Setting == "gpu.limit");
        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
    }

    [Fact]
    public async Task ApplyAsync_NeverIssuesHardwareCall()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, Ms15M3Capabilities.Create());

        await hardware.ApplyAsync(BalancedSettings(), CancellationToken.None);

        Assert.Empty(transport.InvokedMethods);
    }

    [Fact]
    public async Task ApplyAsync_WithEmptyCapabilities_StillExplainsReason()
    {
        var hardware = Create(new FakeMsiAcpiTransport(), DeviceCapabilities.Empty);

        var result = await hardware.ApplyAsync(BalancedSettings(), CancellationToken.None);

        Assert.All(result.Settings, item =>
        {
            Assert.Equal(OperationStatus.Unsupported, item.Status);
            Assert.Contains("待验证", item.Message);
        });
    }

    [Fact]
    public async Task ApplyAsync_RejectsContradictoryConfiguration_BeforeTouchingHardware()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, Ms15M3Capabilities.Create());

        var settings = BalancedSettings() with
        {
            CpuLongTerm = PowerLimit.Custom(60m),
            CpuShortTerm = PowerLimit.Custom(50m)
        };

        var result = await hardware.ApplyAsync(settings, CancellationToken.None);

        var single = Assert.Single(result.Settings);
        Assert.Equal(Ms15M3PerformanceHardware.ConfigurationCheckKey, single.Setting);
        Assert.Equal(OperationStatus.Failed, single.Status);
        Assert.Equal("平衡", single.Requested);
        Assert.Empty(transport.InvokedMethods);
    }

    [Fact]
    public async Task ApplyAsync_IncludesGpuEcoItem_OnlyWhenFallbackIsPresent()
    {
        var hardware = Create(new FakeMsiAcpiTransport(), Ms15M3Capabilities.Create());
        var fallback = GpuEcoFallback.Create(
            "目标低于下界，改用下界。",
            PowerLimit.Custom(35m),
            "不保证精确达到目标瓦数。");

        var settings = BalancedSettings() with { GpuLimit = PowerLimit.Custom(20m), GpuEcoFallback = fallback };

        var result = await hardware.ApplyAsync(settings, CancellationToken.None);

        var eco = Assert.Single(result.Settings, s => s.Setting == "gpu.eco");
        Assert.Equal(OperationStatus.Unsupported, eco.Status);
        Assert.Equal("35 W", eco.Requested);
    }

    [Fact]
    public async Task ApplyAsync_ResultsAlwaysPassComplianceCheck()
    {
        var hardware = Create(new FakeMsiAcpiTransport(), Ms15M3Capabilities.Create());

        var result = await hardware.ApplyAsync(BalancedSettings(), CancellationToken.None);

        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task ReadAsync_ReturnsUnknownState_EvenWhenProbeReturnsData()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, Ms15M3Capabilities.Create());

        var state = await hardware.ReadAsync(CancellationToken.None);

        Assert.Null(state.ActiveStage);
        Assert.False(state.HasAnyReadBack);
        Assert.NotNull(hardware.LastProbe);
        Assert.True(hardware.LastProbe!.AnyDataReturned);
    }

    [Fact]
    public async Task ReadAsync_OnlyInvokesReadMethods_AndPassesNoData()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, Ms15M3Capabilities.Create());

        await hardware.ReadAsync(CancellationToken.None);

        Assert.NotEmpty(transport.InvokedMethods);
        Assert.All(transport.InvokedMethods, name => Assert.StartsWith("Get", name, StringComparison.Ordinal));
        Assert.All(transport.PassedData, data => Assert.Null(data));
    }

    [Fact]
    public async Task ReadAsync_WithEmptyCapabilities_SkipsEveryCall()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, DeviceCapabilities.Empty);

        await hardware.ReadAsync(CancellationToken.None);

        Assert.Empty(transport.InvokedMethods);
        Assert.NotNull(hardware.LastProbe);
        Assert.All(
            hardware.LastProbe!.Entries,
            entry => Assert.Equal(PerformanceProbeOutcome.SkippedByCapability, entry.Outcome));
    }

    [Fact]
    public async Task ReadAsync_ReportsUnknownAcceptance_WhenProviderHasNoReturnValue()
    {
        var hardware = Create(new FakeMsiAcpiTransport(), Ms15M3Capabilities.Create());

        var state = await hardware.ReadAsync(CancellationToken.None);

        Assert.False(state.HasAnyReadBack);
        Assert.True(hardware.LastProbe!.HasUnknownAcceptance);
        Assert.Contains(hardware.LastProbe.Notes, note => note.Contains("不得默认成功", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Probe_RecordsHexPreview_WithoutInterpretingIt()
    {
        var transport = new FakeMsiAcpiTransport();
        var hardware = Create(transport, Ms15M3Capabilities.Create());

        await hardware.ReadAsync(CancellationToken.None);

        var entry = Assert.Single(hardware.LastProbe!.Entries, e => e.MethodName == "Get_Range");
        Assert.Equal("System.Byte[]，长度 4", entry.DataShape);
        Assert.Equal("01 02 03 04", entry.DataHexPreview);
        Assert.Contains(
            hardware.LastProbe.Notes,
            note => note.Contains("不代表对其语义的解释", StringComparison.Ordinal));
    }

    [Fact]
    public void WritePaths_AreGuardedByProtocolStatus()
    {
        Assert.False(PerformanceProtocolStatus.DataLayoutVerified);
        Assert.False(PerformanceProtocolStatus.CanReleaseWrite);
        Assert.Contains("Verified", PerformanceProtocolStatus.WritePrerequisiteText, StringComparison.Ordinal);
        Assert.NotEmpty(PerformanceProtocolStatus.UnverifiedItems);
    }
}
