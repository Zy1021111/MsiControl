using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices;
using MsiControl.Hardware.Msi.Transport;
using Xunit;

namespace MsiControl.Fans.Tests;

public sealed class FanAcpiPayloadTests
{
    [Fact]
    public void RequestShape_MatchesMeasuredEvidence()
    {
        Assert.Equal(16, FanAcpiPayload.RequestLength);
        Assert.Equal(32, FanAcpiPayload.FanResponseLength);
        Assert.Equal("Package", FanAcpiPayload.PackageClassName);
        Assert.Equal("Bytes", FanAcpiPayload.BytesPropertyName);

        var request = FanAcpiPayload.CreateReadRequest();
        Assert.Equal(FanAcpiPayload.RequestLength, request.Length);
        Assert.All(request, b => Assert.Equal((byte)0, b));
    }

    [Fact]
    public void WriteComposition_IsAlwaysRefused()
    {
        // 写入载荷编码没有可核对的实机证据，因此这道闸门必须恒为关闭。
        Assert.False(FanAcpiPayload.CanComposeWriteRequest);
    }

    [Fact]
    public void TryDecodeFanReport_AlwaysRefusesWithReason()
    {
        var payload = Enumerable.Repeat((byte)1, FanAcpiPayload.FanResponseLength).ToArray();

        Assert.False(FanAcpiPayload.TryDecodeFanReport(payload, out var reading, out var reason));
        Assert.Null(reading);
        Assert.Contains("尚无实机核证", reason, StringComparison.Ordinal);
        Assert.Contains("不允许把缓冲区解释为转速", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TryDecodeFanReport_NullPayload_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FanAcpiPayload.TryDecodeFanReport(null!, out _, out _));
    }

    [Fact]
    public void TryExtractBytes_RejectsNonEmbeddedInstances()
    {
        Assert.False(FanAcpiPayload.TryExtractBytes(null, out var empty, out var nullError));
        Assert.Empty(empty);
        Assert.Contains("为空", nullError!, StringComparison.Ordinal);

        Assert.False(FanAcpiPayload.TryExtractBytes("not-an-object", out _, out var typeError));
        Assert.Contains("不是内嵌", typeError!, StringComparison.Ordinal);
    }

    [Fact]
    public void CreatePackage_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => FanAcpiPayload.CreatePackage(null!, new byte[16]));
        Assert.Throws<ArgumentNullException>(() => FanAcpiPayload.CreatePackage(new System.Management.ManagementScope(@"root\WMI"), null!));
    }

    [Fact]
    public void WmiPackagePayloadFactory_RejectsEmptyNamespace()
    {
        Assert.Throws<ArgumentException>(() => new WmiPackagePayloadFactory("  "));
        Assert.Throws<ArgumentNullException>(() => new WmiPackagePayloadFactory(null!));
    }

    [Fact]
    public void RawReport_ToHex_FormatsBytes()
    {
        var report = new FanRawReport(
            DateTimeOffset.Now,
            "Get_Fan",
            true,
            new byte[] { 0x01, 0x00, 0xD7 },
            "测试");

        Assert.Equal("01 00 D7", report.ToHex());
        Assert.Equal(3, report.Length);
        Assert.Equal(string.Empty, new FanRawReport(DateTimeOffset.Now, "Get_Fan", false, Array.Empty<byte>(), "无").ToHex());
    }
}

public sealed class FanCurveLimitsTests
{
    [Fact]
    public void Ms15M3Limits_KeepUnverifiedConstraintsAsNull()
    {
        var limits = FanCurveLimits.Ms15M3;

        Assert.Equal(CapabilityLevel.Detected, limits.Level);
        Assert.False(limits.IsVerified);
        Assert.Null(limits.MaxNodes);
        Assert.Null(limits.TemperatureEditable);
        Assert.Null(limits.TemperatureStepC);
        Assert.Null(limits.OutputStepPercent);
        Assert.False(limits.AreAllConstraintsVerified);
        Assert.Equal(4, limits.UnverifiedConstraints.Count);
        Assert.Contains("HWE", limits.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedLimits_MarkAllConstraintsVerified()
    {
        var limits = TestData.VerifiedLimits();

        Assert.True(limits.IsVerified);
        Assert.True(limits.AreAllConstraintsVerified);
        Assert.Empty(limits.UnverifiedConstraints);
    }

    [Fact]
    public void Constructor_RejectsInvalidRanges()
    {
        Assert.Throws<ArgumentException>(() => new FanCurveLimits(string.Empty, CapabilityLevel.Verified, -20, 130, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, 90, 40, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, -200, 130, 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, -20, 130, 0, 200));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, -20, 130, 40, 30));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, -20, 130, 0, 100, maxNodes: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, -20, 130, 0, 100, outputStepPercent: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FanCurveLimits("测试", CapabilityLevel.Verified, -20, 130, 0, 100, temperatureStepC: -1));
    }
}

public sealed class FanHardwareMs15M3Tests
{
    private static DeviceCapabilities TargetDeviceCapabilities()
        => CapabilityBaseline.ForDevice(new DeviceIdentity("MSI Vector 16 HX AI A2XWHG", "MS-15M3"));

    private static (FanHardwareMs15M3 Hardware, FakeTransport Transport, FakePayloadFactory Factory) Create(
        DeviceCapabilities? capabilities = null)
    {
        var transport = new FakeTransport();
        var factory = new FakePayloadFactory();
        var hardware = new FanHardwareMs15M3(
            transport,
            capabilities ?? TargetDeviceCapabilities(),
            factory);

        return (hardware, transport, factory);
    }

    [Fact]
    public void Constructor_RejectsNullTransport()
    {
        Assert.Throws<ArgumentNullException>(() => new FanHardwareMs15M3(null!));
    }

    [Fact]
    public void WriteFlags_AreOff_OnTargetDevice()
    {
        var (hardware, _, _) = Create();

        Assert.False(hardware.IsWriteEnabled);
        Assert.False(hardware.IsFullSpeedEnabled);
        Assert.False(hardware.CanDecodeState);
        Assert.NotEmpty(hardware.DecodeLimitation);
        Assert.Equal(FanCurveLimits.Ms15M3, hardware.Limits);
    }

    [Fact]
    public void UnadaptedDevice_FallsBackToEmptyCapabilities()
    {
        var hardware = new FanHardwareMs15M3(new FakeTransport());

        Assert.False(hardware.IsWriteEnabled);
    }

    [Fact]
    public async Task ApplyCurve_IsUnsupported_BecauseWritePayloadIsUnverified()
    {
        var (hardware, _, _) = Create();

        var result = await hardware.ApplyCurveAsync(
            FanChannel.Cpu,
            TestData.CurveSet().Cpu,
            CancellationToken.None);

        // 逐项状态是权威结论：Unsupported。
        var problem = Assert.Single(result.Problems);
        Assert.Equal(OperationStatus.Unsupported, problem.Status);
        Assert.Contains("写入载荷编码未取得实机证据", problem.Message!, StringComparison.Ordinal);
        Assert.Contains("Set_Fan", problem.Message!, StringComparison.Ordinal);

        // 整体状态受 Core 的 ControlResult.From 归并规则支配：
        // 「无失败但存在 Unsupported」→ PartiallySucceeded。
        // 这条规则对「整组都是不支持」的结果并不贴切，已作为接口观察上报任务 01
        // （见 docs/风扇控制说明.md §7）。业务层（FanApplicationService）不依赖它，
        // 被拦下时直接返回 Unsupported 且不产生 ControlResult。
        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task ApplyCurve_Fails_WhenCurveChannelDiffers()
    {
        var (hardware, _, _) = Create();

        var result = await hardware.ApplyCurveAsync(
            FanChannel.Cpu,
            TestData.CurveSet().Gpu,
            CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("调用方违约", Assert.Single(result.Problems).Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAutomatic_IsUnsupported_AndExplainsCapability()
    {
        var (hardware, _, _) = Create();

        var result = await hardware.SetAutomaticAsync(CancellationToken.None);

        // fan.curve（HWE §23）Verified 后能力闸门放行；本 WMI 适配层的载荷编码仍未证实，
        // 如实报告 Unsupported 并指明真实通道（EC 0xD4），不伪造成功。
        var problem = Assert.Single(result.Problems);
        Assert.Equal(OperationStatus.Unsupported, problem.Status);
        Assert.Contains("Set_Fan", problem.Message!, StringComparison.Ordinal);
        Assert.Contains("0xD4", problem.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetFullSpeed_IsUnsupported_AndExplainsMissingInterface()
    {
        var (hardware, _, _) = Create();

        var result = await hardware.SetFullSpeedAsync(true, CancellationToken.None);

        var problem = Assert.Single(result.Problems);
        Assert.Equal(OperationStatus.Unsupported, problem.Status);
        var message = problem.Message!;
        // 2026-10-17：强冷写路径在 EC 适配器（HWE §17.2），WMI 适配器不承载该写入。
        Assert.Contains("EC", message, StringComparison.Ordinal);
        Assert.Contains("启用", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_ReturnsUnknownState_InsteadOfInventingOne()
    {
        var (hardware, transport, _) = Create();
        transport.Result = MsiAcpiInvocationResult.Failed("测试：无返回。");

        var state = await hardware.ReadAsync(CancellationToken.None);

        Assert.Equal(FanHardwareState.Unknown, state);
        Assert.Null(state.CpuCurve);
        Assert.Null(state.GpuCurve);
    }

    [Fact]
    public async Task ReadRaw_SendsSixteenBytePackageRequest()
    {
        var (hardware, transport, factory) = Create();
        transport.Result = MsiAcpiInvocationResult.Failed("测试：无返回。");

        var report = await hardware.ReadRawAsync(CancellationToken.None);

        Assert.Equal("Get_Fan", report.Operation);
        Assert.Contains("Get_Fan", transport.Calls);
        Assert.NotNull(factory.LastBytes);
        Assert.Equal(FanAcpiPayload.RequestLength, factory.LastBytes!.Length);
    }

    [Fact]
    public async Task ReadRaw_ReportsFailure_WhenTransportReturnsError()
    {
        var (hardware, transport, _) = Create();
        transport.Result = MsiAcpiInvocationResult.Failed("WMI 调用失败（0x80041008）。");

        var report = await hardware.ReadRawAsync(CancellationToken.None);

        Assert.False(report.TransportSucceeded);
        Assert.Contains("0x80041008", report.Description, StringComparison.Ordinal);
        Assert.Equal(0, report.Length);
    }

    [Fact]
    public async Task ReadRaw_ReportsFailure_WhenTransportThrows()
    {
        var (hardware, transport, _) = Create();
        transport.ThrowOnRead = true;

        var report = await hardware.ReadRawAsync(CancellationToken.None);

        Assert.False(report.TransportSucceeded);
        Assert.Contains("调用 Get_Fan 失败", report.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRaw_ReportsFailure_WhenPayloadCannotBeBuilt()
    {
        var (hardware, _, factory) = Create();
        factory.Throw = true;

        var report = await hardware.ReadRawAsync(CancellationToken.None);

        Assert.False(report.TransportSucceeded);
        Assert.Contains("构造 Data 入参失败", report.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRaw_ReportsFailure_WhenReturnedDataIsNotAPackage()
    {
        var (hardware, transport, _) = Create();
        transport.Result = MsiAcpiInvocationResult.AcceptedUnconfirmed("不是内嵌实例", "测试");

        var report = await hardware.ReadRawAsync(CancellationToken.None);

        Assert.False(report.TransportSucceeded);
        Assert.Contains("无法取出缓冲区", report.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRaw_PropagatesCancellation()
    {
        var (hardware, _, _) = Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => hardware.ReadRawAsync(cts.Token));
    }

    [Fact]
    public void SettingKeyFor_MapsChannels()
    {
        Assert.Equal("fan.curve.cpu", FanHardwareMs15M3.SettingKeyFor(FanChannel.Cpu));
        Assert.Equal("fan.curve.gpu", FanHardwareMs15M3.SettingKeyFor(FanChannel.Gpu));
    }
}
