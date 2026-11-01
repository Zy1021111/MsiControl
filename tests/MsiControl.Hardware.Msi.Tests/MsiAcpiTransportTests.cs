using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Discovery;
using MsiControl.Hardware.Msi.Transport;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests;

/// <summary>
/// <c>MSI_ACPI</c> 只读通道与发现层的回归测试。
/// </summary>
/// <remarks>
/// 这里只测**不依赖真实 WMI** 的纯逻辑（方法名白名单、只读门控、常量一致性）。
/// 真实 WMI 调用属实机验证（任务书 §19），不在此处执行，避免测试依赖具体机器。
/// </remarks>
public class MsiAcpiTransportTests
{
    // ---------- 方法名登记 ----------

    [Fact]
    public void MethodNames_HaveExpectedCounts()
    {
        Assert.Equal(21, MsiAcpiMethodNames.Read.Count);
        Assert.Equal(19, MsiAcpiMethodNames.Write.Count);
        Assert.Equal(40, MsiAcpiMethodNames.All.Count); // 证据清单 §2.3：共 40 个方法
    }

    [Fact]
    public void MethodNames_HaveNoDuplicatesAcrossReadAndWrite()
    {
        Assert.Equal(MsiAcpiMethodNames.All.Count, MsiAcpiMethodNames.All.Distinct().Count());
        Assert.Empty(MsiAcpiMethodNames.Read.Intersect(MsiAcpiMethodNames.Write, StringComparer.Ordinal));
    }

    [Fact]
    public void MethodNames_ReadMethodsAreAllGetPrefixed()
    {
        Assert.All(MsiAcpiMethodNames.Read, name => Assert.StartsWith("Get", name));
    }

    [Fact]
    public void MethodNames_WriteMethodsAreAllSetPrefixed()
    {
        Assert.All(MsiAcpiMethodNames.Write, name => Assert.StartsWith("Set", name));
    }

    // ---------- 只读判定（白名单） ----------

    [Fact]
    public void IsReadOnly_TrueForRegisteredReadMethods()
    {
        Assert.All(MsiAcpiMethodNames.Read, name => Assert.True(MsiAcpiMethodNames.IsReadOnly(name)));
    }

    [Fact]
    public void IsReadOnly_FalseForRegisteredWriteMethods()
    {
        Assert.All(MsiAcpiMethodNames.Write, name => Assert.False(MsiAcpiMethodNames.IsReadOnly(name)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Get_NotRegistered")]   // 白名单：未登记的名称不按只读放行
    [InlineData("Set_Fan")]
    public void IsReadOnly_FalseForUnregisteredOrWriteNames(string? name)
    {
        Assert.False(MsiAcpiMethodNames.IsReadOnly(name!));
    }

    // ---------- 只读门控 ----------

    [Fact]
    public async Task Transport_BlocksWriteMethods()
    {
        // 关键约定：本任务不开放写入通道（任务书 §16.1 / 任务 01 纪律）。
        var transport = new WmiMsiAcpiTransport();

        var result = await transport.InvokeReadAsync("Set_Fan", null, CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.True(result.HasError);
        Assert.Contains("只读", result.Error!);
    }

    [Fact]
    public async Task Transport_BlocksUnregisteredMethodName()
    {
        var transport = new WmiMsiAcpiTransport();

        var result = await transport.InvokeReadAsync("Get_NotRegistered", null, CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.True(result.HasError);
    }

    [Fact]
    public async Task Transport_BlocksEmptyMethodName()
    {
        var transport = new WmiMsiAcpiTransport();

        var result = await transport.InvokeReadAsync("   ", null, CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.True(result.HasError);
    }

    [Fact]
    public async Task Transport_RespectsCancellationBeforeDispatch()
    {
        var transport = new WmiMsiAcpiTransport();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => transport.InvokeReadAsync("Get_Fan", null, cts.Token));
    }

    // ---------- 调用结果语义 ----------

    [Fact]
    public void InvocationResult_OkMeansAcceptedNotEffective()
    {
        // 防回归：Accepted 只是「固件接受调用」，不等于「设置已生效」。
        var ok = MsiAcpiInvocationResult.Ok(new byte[] { 1, 2, 3 });

        Assert.True(ok.IsAccepted);
        Assert.False(ok.IsAcceptanceUnknown);
        Assert.False(ok.HasError);
        var bytes = Assert.IsType<byte[]>(ok.Data);
        Assert.Equal(3, bytes.Length);
    }

    [Fact]
    public void InvocationResult_AcceptanceUnknownWhenProviderHidesReturnValue()
    {
        // 实机事实（证据清单 §2.6）：OutParameters 不含 ReturnValue，接受与否无法判定。
        var unknown = MsiAcpiInvocationResult.AcceptedUnconfirmed(new object(), "未暴露 ReturnValue");

        Assert.True(unknown.IsAcceptanceUnknown);
        Assert.False(unknown.IsAccepted);
        Assert.True(unknown.HasError);
    }

    [Fact]
    public void InvocationResult_BlockedCarriesReason()
    {
        var blocked = MsiAcpiInvocationResult.Blocked("通道未开放");

        Assert.False(blocked.IsAccepted);
        Assert.Equal("通道未开放", blocked.Error);
    }

    // ---------- 发现层与能力基线一致性 ----------

    [Fact]
    public void Discovery_ExcludedClassNamesMatchCapabilityBaseline()
    {
        // 两处必须一致：发现层探测的排除类，就是能力基线条目中的排除类。
        Assert.Equal(
            Ms15M3Capabilities.UnsupportedHardwareClasses.OrderBy(x => x, StringComparer.Ordinal),
            WmiHardwareDiscovery.ExcludedClassNames.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void Discovery_ExcludedClassNames_HaveFiveEntries()
    {
        Assert.Equal(5, WmiHardwareDiscovery.ExcludedClassNames.Count);
    }

    [Fact]
    public void DiscoveryReport_IsMsiAcpiAvailable_RequiresClassAndInstance()
    {
        var noInstance = new HardwareDiscoveryReport(
            DateTimeOffset.UtcNow,
            WmiHardwareDiscovery.DefaultNamespacePath,
            new MsiAcpiClassProbe("MSI_ACPI", ClassPresent: true, InstancePresent: false,
                null, null, Array.Empty<MsiAcpiMethodDescriptor>(), "0x8004100C"),
            Array.Empty<MsiAcpiClassProbe>(),
            Array.Empty<ThermalZoneProbe>(),
            Array.Empty<string>());

        Assert.False(noInstance.IsMsiAcpiAvailable);
    }

    [Fact]
    public void ThermalZoneProbe_StoresConvertedCelsius()
    {
        // 3332 十分之一开尔文 ≈ 60.05 °C（证据清单 §5）。
        var zone = new ThermalZoneProbe("ACPI\\ThermalZone\\THRM_0", true, 60.05, 110.05);

        Assert.Equal(60.05, zone.CurrentCelsius!.Value, 2);
        Assert.Equal(110.05, zone.CriticalCelsius!.Value, 2);
    }
}
