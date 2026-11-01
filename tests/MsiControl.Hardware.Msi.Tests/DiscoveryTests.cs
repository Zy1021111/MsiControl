using MsiControl.Hardware.Msi.Discovery;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests;

/// <summary>
/// 只读发现的容错行为测试。
/// </summary>
/// <remarks>
/// 这些用例**不依赖 MS-15M3**，在任意 Windows 上结果一致：
/// 验证「扫描失败不抛异常」与「取消原样抛出」两条契约。
/// </remarks>
public class DiscoveryTests
{
    [Fact]
    public async Task Scan_UnreachableNamespace_ReturnsReportInsteadOfThrowing()
    {
        // 防回归：WMI 命名空间不可达时，契约要求返回报告而不是抛异常
        // （IHardwareDiscovery：任何单项失败都以报告字段表达）。
        var discovery = new WmiHardwareDiscovery(
            namespacePath: @"root\MSIControl_DoesNotExist");

        var report = await discovery.ScanAsync(CancellationToken.None);

        Assert.False(report.IsMsiAcpiAvailable);
        Assert.NotNull(report.MsiAcpi.Error);
        Assert.NotEmpty(report.Notes);
        Assert.Empty(report.ThermalZones);
    }

    [Fact]
    public async Task ScanAsync_HonorsCancellation()
    {
        var discovery = new WmiHardwareDiscovery();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.ScanAsync(cts.Token));
    }

    [Fact]
    public void Scan_ReportCarriesScannedNamespace()
    {
        // 报告必须自证扫的是哪个命名空间，便于证据归档。
        var report = new HardwareDiscoveryReport(
            DateTimeOffset.UtcNow,
            @"root\WMI",
            new MsiAcpiClassProbe("MSI_ACPI", true, true, "ACPI\\PNP0C14\\0_0", true,
                Array.Empty<MsiAcpiMethodDescriptor>(), null),
            Array.Empty<MsiAcpiClassProbe>(),
            Array.Empty<ThermalZoneProbe>(),
            Array.Empty<string>());

        Assert.Equal(@"root\WMI", report.NamespacePath);
        Assert.True(report.IsMsiAcpiAvailable);
    }
}
