using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport;
using MsiControl.Monitoring.Pdh;
using MsiControl.Monitoring.Sources;
using Xunit;

namespace MsiControl.Monitoring.Tests;

/// <summary>可编程的 MSI_ACPI 通道替身。</summary>
internal sealed class FakeAcpiTransport : IMsiAcpiTransport
{
    private readonly Func<string, object?, MsiAcpiInvocationResult> _handler;

    public FakeAcpiTransport(Func<string, object?, MsiAcpiInvocationResult> handler) => _handler = handler;

    public int CallCount { get; private set; }

    public string? LastMethod { get; private set; }

    public Task<MsiAcpiInvocationResult> InvokeReadAsync(
        string methodName,
        object? data,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastMethod = methodName;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_handler(methodName, data));
    }
}

/// <summary>单个数据源的解析与判定逻辑。</summary>
public sealed class SourceTests
{
    /// <summary>本机 2026-10-16 实测的 <c>Get_EC</c> 原始返回字节（证据清单 §11）。</summary>
    private static readonly byte[] RealEcPayload =
    {
        1, 129, 49, 53, 77, 51, 69, 77, 83, 49, 46, 49, 49, 51, 49, 49,
        50, 54, 50, 48, 50, 53, 49, 52, 58, 53, 57, 58, 50, 56, 0, 0
    };

    // ── EC 固件响应 ───────────────────────────────────────────────────

    [Fact]
    public void EC_文本提取_跳过前导不可打印字节并在首个不可打印字节处停止()
    {
        var text = MsiAcpiEcFirmwareSource.ExtractPrintableText(RealEcPayload);

        Assert.Equal("15M3EMS1.1131126202514:59:28", text);
    }

    [Fact]
    public void EC_文本提取_全为不可打印字节时返回空()
    {
        Assert.Null(MsiAcpiEcFirmwareSource.ExtractPrintableText(new byte[] { 0, 1, 2, 0 }));
        Assert.Null(MsiAcpiEcFirmwareSource.ExtractPrintableText(Array.Empty<byte>()));
    }

    [Fact]
    public void EC_字节提取_支持字节数组()
    {
        Assert.True(MsiAcpiEcFirmwareSource.TryExtractBytes(RealEcPayload, out var bytes));
        Assert.Equal(RealEcPayload.Length, bytes.Length);
    }

    [Fact]
    public void EC_字节提取_支持一般数组()
    {
        var boxed = new object[] { (byte)1, (byte)2, (byte)3 };
        Assert.True(MsiAcpiEcFirmwareSource.TryExtractBytes(boxed, out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public void EC_字节提取_不支持的类型被拒绝()
    {
        Assert.False(MsiAcpiEcFirmwareSource.TryExtractBytes("字符串", out var bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public async Task EC_读取成功_保留原始文本与十六进制()
    {
        var transport = new FakeAcpiTransport((_, _) => MsiAcpiInvocationResult.Ok(RealEcPayload));
        var source = new MsiAcpiEcFirmwareSource(transport);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.True(reading.IsAvailable);
        Assert.Equal("15M3EMS1.1131126202514:59:28", reading.RawAscii);
        Assert.Equal(Convert.ToHexString(RealEcPayload), reading.RawHex);
        Assert.Equal("Get_EC", transport.LastMethod);
    }

    [Fact]
    public async Task EC_首次成功后_不再重复调用通道()
    {
        var transport = new FakeAcpiTransport((_, _) => MsiAcpiInvocationResult.Ok(RealEcPayload));
        var source = new MsiAcpiEcFirmwareSource(transport);

        await source.ReadAsync(CancellationToken.None);
        await source.ReadAsync(CancellationToken.None);
        await source.ReadAsync(CancellationToken.None);

        Assert.Equal(1, transport.CallCount);
    }

    [Fact]
    public async Task EC_失败后_下次仍然重试()
    {
        var attempt = 0;
        var transport = new FakeAcpiTransport((_, _) =>
            ++attempt == 1
                ? MsiAcpiInvocationResult.Failed("首次失败")
                : MsiAcpiInvocationResult.Ok(RealEcPayload));
        var source = new MsiAcpiEcFirmwareSource(transport);

        var first = await source.ReadAsync(CancellationToken.None);
        var second = await source.ReadAsync(CancellationToken.None);

        Assert.False(first.IsAvailable);
        Assert.Contains("首次失败", first.Error, StringComparison.Ordinal);
        Assert.True(second.IsAvailable);
        Assert.Equal(2, transport.CallCount);
    }

    [Fact]
    public async Task EC_无返回数据时_给出不可用结果()
    {
        var transport = new FakeAcpiTransport((_, _) => MsiAcpiInvocationResult.AcceptedUnconfirmed(null, "未暴露 ReturnValue"));
        var source = new MsiAcpiEcFirmwareSource(transport);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.False(reading.IsAvailable);
        Assert.NotNull(reading.Error);
    }

    // ── 独显电源状态判定 ──────────────────────────────────────────────

    [Theory]
    [InlineData(3, false)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(13, true)]
    [InlineData(14, true)]
    [InlineData(15, true)]
    [InlineData(16, true)]
    [InlineData(17, true)]
    [InlineData(2, false)]
    public void 独显可用性_按CIM取值判定是否处于省电状态(int availability, bool expected)
    {
        Assert.Equal(expected, GpuPresenceProbe.IsPowerSaveAvailability(availability));
    }

    // ── 处理器频率口径计算 ────────────────────────────────────────────

    [Fact]
    public void 处理器核_有效频率等于标称频率乘以性能百分比()
    {
        var core = new ProcessorCounterSample("0,3", 172.0, 44.0, 2700.0);

        Assert.True(core.IsCore);
        Assert.False(core.IsTotal);
        Assert.Equal(4644.0, core.EffectiveMhz, 3);
    }

    [Theory]
    [InlineData("_Total", true)]
    [InlineData("0,_Total", true)]
    [InlineData("0,0", false)]
    public void 处理器实例_总计判定(string name, bool expected)
    {
        var sample = new ProcessorCounterSample(name, 100, 10, 2500);
        Assert.Equal(expected, sample.IsTotal);
    }

    // ── 装配 ──────────────────────────────────────────────────────────

    [Fact]
    public void 工厂_可在无硬件证据的机器上装配而不抛异常()
    {
        using var provider = MonitoringProviderFactory.CreateDefault(DeviceCapabilities.Empty);

        Assert.NotNull(provider);
        Assert.Null(provider.Latest);
        Assert.NotNull(provider.Diagnostics);
    }

    [Fact]
    public void 工厂_整机功耗不可用原因包含关键口径说明()
    {
        Assert.Contains("BatterySide", MonitoringProviderFactory.SystemPowerUnavailableReason, StringComparison.Ordinal);
        Assert.Contains("CPU", MonitoringProviderFactory.SystemPowerUnavailableReason, StringComparison.Ordinal);
    }
}
