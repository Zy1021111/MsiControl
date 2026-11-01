using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices;
using Xunit;

namespace MsiControl.Fans.Tests;

public sealed class FanApplicationServiceTests
{
    private static FanApplicationService CreateService(
        FakeFanHardware hardware,
        FanCurveLimits? limits = null,
        DeviceCapabilities? capabilities = null)
        => new(
            hardware,
            limits ?? TestData.VerifiedLimits(),
            capabilities ?? TestData.VerifiedFanCapabilities());

    [Fact]
    public void Constructor_RejectsNulls()
    {
        var hardware = new FakeFanHardware();

        Assert.Throws<ArgumentNullException>(() => new FanApplicationService(null!, TestData.VerifiedLimits(), TestData.VerifiedFanCapabilities()));
        Assert.Throws<ArgumentNullException>(() => new FanApplicationService(hardware, null!, TestData.VerifiedFanCapabilities()));
        Assert.Throws<ArgumentNullException>(() => new FanApplicationService(hardware, TestData.VerifiedLimits(), null!));
    }

    [Fact]
    public async Task ApplyCurve_PassesCapabilityGate_OnRealBaseline_AndReportsAdapterUnsupported()
    {
        // 2026-10-17（HWE §23）：fan.curve 已升为 Verified，能力闸门放行；
        // 但 FakeFanHardware 的默认应用结果仍是 Unsupported——「能力通过」不等于「成功」，
        // 业务层必须如实转写适配层结果，不得伪造成功。
        var hardware = new FakeFanHardware { CanDecodeState = true, State = TestData.CustomState() };
        var service = CreateService(hardware, capabilities: Ms15M3Capabilities.Create());

        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.NotNull(result.Control);
        // 单项 Unsupported 在 ControlResult 归并规则下整体为 PartiallySucceeded；
        // 逐项仍是 Unsupported，业务层如实转写、不伪造成功。
        Assert.Equal(OperationStatus.PartiallySucceeded, result.Control!.Status);
        Assert.Equal(OperationStatus.Unsupported, Assert.Single(result.Control.Settings).Status);
        Assert.Equal(1, hardware.WriteCount);
    }

    [Fact]
    public async Task ApplyCurve_Fails_WhenCurveChannelDiffers()
    {
        var hardware = new FakeFanHardware { CanDecodeState = true, State = TestData.CustomState() };
        var service = CreateService(hardware);

        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Gpu, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("调用方违约", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public async Task ApplyCurve_IsBlocked_WhenCurveViolatesHardwareLimits()
    {
        var hardware = new FakeFanHardware { CanDecodeState = true, State = TestData.CustomState() };
        var service = CreateService(hardware, TestData.VerifiedLimits(maxNodes: 3));

        var curve = TestData.Curve(FanChannel.Cpu, (30, 10), (35, 15), (40, 20), (45, 25));
        var result = await service.ApplyCurveAsync(FanChannel.Cpu, curve, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains("超过硬件允许的 3", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public async Task ApplyCurve_IsBlocked_WhenConstraintsAreUnverified()
    {
        var hardware = new FakeFanHardware { CanDecodeState = true, State = TestData.CustomState() };
        var service = CreateService(hardware, FanCurveLimits.Ms15M3);

        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains("尚未全部取得实机证据", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public async Task ApplyCurve_IsBlocked_WhenBootSnapshotCannotBeCaptured()
    {
        var hardware = new FakeFanHardware { CanDecodeState = false };
        var service = CreateService(hardware);

        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains("首次写入前的有效快照", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
        Assert.False(service.BootSnapshot.HasSnapshot);
    }

    [Fact]
    public async Task ApplyCurve_Succeeds_WithReadBackConfirmationBasis()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = TestData.Accepted()
        };

        var service = CreateService(hardware);
        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsUnsubstantiatedSuccess);
        Assert.NotNull(result.ConfirmationBasis);
        Assert.Contains("读回", result.ConfirmationBasis!, StringComparison.Ordinal);
        Assert.True(hardware.ReadCount >= 2, "写入前取快照与写入后读回各需要一次读取。");
        Assert.NotNull(service.LastApplied);
        Assert.Equal(FanControlMode.CustomCurve, service.LastApplied!.Mode);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task ApplyCurve_Fails_WhenReadBackDiffers()
    {
        var other = new FanCurveSet(
            TestData.Curve(FanChannel.Cpu, (40, 90), (60, 90)),
            TestData.Curve(FanChannel.Gpu, (40, 90), (60, 90)));

        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = TestData.Accepted(),
            StateAfterWrite = TestData.CustomState(other)
        };

        var service = CreateService(hardware);
        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("不一致", result.Message, StringComparison.Ordinal);
        Assert.Null(service.LastApplied);
    }

    [Fact]
    public async Task ApplyCurve_IsPartial_WhenReadBackCannotConfirm()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = TestData.Accepted()
        };

        var service = CreateService(hardware);
        hardware.OnWrite = () => hardware.CanDecodeState = false;

        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
        Assert.Null(result.ConfirmationBasis);
        Assert.Null(service.LastApplied);
        Assert.Equal(FanReadStatus.TransportOnly, service.LastReadBack!.Status);
    }

    [Fact]
    public async Task ApplyCurve_FinishesReadBack_ThenRethrowsCancellation()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = TestData.Accepted(),
            CancelOnWrite = true
        };

        var service = CreateService(hardware);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None));

        Assert.NotNull(service.LastReadBack);
        Assert.True(hardware.ReadCount >= 2, "取消后仍必须完成状态核对。");
    }

    [Fact]
    public async Task ApplyCurve_ReportsHardwareFailure_WhenControlDoesNotSucceed()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = ControlResult.Single(
                SettingResult.Failed("fan.curve.cpu", "3 节点", "固件拒绝写入。"))
        };

        var service = CreateService(hardware);
        var result = await service.ApplyCurveAsync(FanChannel.Cpu, TestData.CurveSet().Cpu, CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("硬件未接受本次操作", result.Message, StringComparison.Ordinal);
        Assert.Contains("固件拒绝写入", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadState_UsesAdapterReadback_WhenAdapterCanDecode()
    {
        var hardware = new FakeFanHardware { CanDecodeState = true, State = TestData.CustomState() };
        var service = CreateService(hardware);

        var read = await service.ReadStateAsync(CancellationToken.None);

        Assert.Equal(FanReadStatus.Verified, read.Status);
        Assert.True(read.CanSnapshot);
        Assert.Same(read, service.LastReadBack);
    }

    [Fact]
    public async Task ReadState_IsTransportOnly_WhenAdapterCannotDecode()
    {
        var hardware = new FakeFanHardware { CanDecodeState = false };
        var service = CreateService(hardware);

        var read = await service.ReadStateAsync(CancellationToken.None);

        Assert.Equal(FanReadStatus.TransportOnly, read.Status);
        Assert.Equal(FanHardwareState.Unknown, read.State);
        Assert.NotNull(read.Raw);
        Assert.Contains("无法解读", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadState_IsUnavailable_WhenTransportFails()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = false,
            Raw = new FanRawReport(
                DateTimeOffset.Now,
                "Get_Fan",
                TransportSucceeded: false,
                Payload: Array.Empty<byte>(),
                Description: "WMI 调用失败（0x80041000）。")
        };

        var service = CreateService(hardware);
        var read = await service.ReadStateAsync(CancellationToken.None);

        Assert.Equal(FanReadStatus.Unavailable, read.Status);
        Assert.Contains("0x80041000", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadState_IsUnavailable_WhenHardwareThrows()
    {
        var hardware = new FakeFanHardware { ThrowOnRead = true };
        var service = CreateService(hardware);

        var read = await service.ReadStateAsync(CancellationToken.None);

        Assert.Equal(FanReadStatus.Unavailable, read.Status);
        Assert.Contains("读取风扇硬件状态失败", read.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadState_PropagatesCancellation()
    {
        var hardware = new FakeFanHardware();
        var service = CreateService(hardware);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.ReadStateAsync(cts.Token));
    }

    [Fact]
    public async Task SetAutomatic_PassesCapabilityGate_OnRealBaseline_AndReportsAdapterUnsupported()
    {
        // 2026-10-17（HWE §23）：fan.curve Verified 后能力闸门放行；
        // FakeFanHardware 默认的 SetAutomaticAsync 结果仍是 Unsupported，业务层如实转写。
        var hardware = new FakeFanHardware();
        var service = CreateService(hardware, capabilities: Ms15M3Capabilities.Create());

        var result = await service.SetAutomaticAsync(CancellationToken.None);

        Assert.False(result.IsBlocked);
        Assert.NotNull(result.Control);
        // 单项 Unsupported 在 ControlResult 归并规则下整体为 PartiallySucceeded；
        // 逐项仍是 Unsupported，业务层如实转写、不伪造成功。
        Assert.Equal(OperationStatus.PartiallySucceeded, result.Control!.Status);
        Assert.Equal(OperationStatus.Unsupported, Assert.Single(result.Control.Settings).Status);
        Assert.Equal(1, hardware.WriteCount);
    }

    [Fact]
    public async Task SetAutomatic_Succeeds_WhenReadBackIsAutomatic()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = new FanHardwareState(FanControlMode.Automatic, null, null),
            AutomaticResult = TestData.Accepted("fan.mode", "Automatic")
        };

        var service = CreateService(hardware);
        var result = await service.SetAutomaticAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(service.LastApplied);
        Assert.Equal(FanControlMode.Automatic, service.LastApplied!.Mode);
        Assert.Null(service.LastApplied.Curves);
    }

    [Fact]
    public async Task SetFullSpeed_IsBlocked_WhenCapabilityNotVerified()
    {
        // 2026-10-17 下午（HWE §17.2）：真实基线中 fan.fullspeed 已升 Verified（叶哥决定启用）。
        // 门控行为本身仍需覆盖：用显式构造的 Detected 能力集验证服务在能力闸门处阻断，
        // 不依赖（也不受）真实基线变化影响。
        var capabilities = new DeviceCapabilities(new[]
        {
            CapabilityStatus.Detected(
                CapabilityKeys.FanFullSpeed,
                "测试替身：强冷写入未取得证据（模拟旧基线）。")
        });
        var hardware = new FakeFanHardware();
        var service = CreateService(hardware, capabilities: capabilities);

        var result = await service.SetFullSpeedAsync(true, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains(CapabilityKeys.FanFullSpeed, result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public async Task SetFullSpeed_IsBlocked_WhenEntryStateCannotBeRead()
    {
        var hardware = new FakeFanHardware { CanDecodeState = false };
        var service = CreateService(hardware);

        var result = await service.SetFullSpeedAsync(true, CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains("进入强冷前", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public async Task SetFullSpeed_EntersAndRestores_WithReconciliation()
    {
        var curves = TestData.CurveSet();
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(curves),
            FullSpeedResult = TestData.Accepted("fan.fullspeed", "启用"),
            StateAfterWrite = new FanHardwareState(FanControlMode.FullSpeed, curves.Cpu, curves.Gpu)
        };

        var service = CreateService(hardware);

        var enable = await service.SetFullSpeedAsync(true, CancellationToken.None);
        Assert.True(enable.IsSuccess);
        Assert.NotNull(service.FullSpeedEntryState);

        hardware.StateAfterWrite = TestData.CustomState(curves);
        var disable = await service.SetFullSpeedAsync(false, CancellationToken.None);

        Assert.True(disable.IsSuccess);
        Assert.Null(service.FullSpeedEntryState);
        Assert.Equal(2, hardware.WriteCount);
    }

    [Fact]
    public async Task SetFullSpeed_ExitIsPartial_WhenReadBackDiffersFromEntry()
    {
        var curves = TestData.CurveSet();
        var other = new FanCurveSet(
            TestData.Curve(FanChannel.Cpu, (40, 50), (60, 50)),
            TestData.Curve(FanChannel.Gpu, (40, 50), (60, 50)));

        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(curves),
            FullSpeedResult = TestData.Accepted("fan.fullspeed", "启用"),
            StateAfterWrite = new FanHardwareState(FanControlMode.FullSpeed, curves.Cpu, curves.Gpu)
        };

        var service = CreateService(hardware);
        await service.SetFullSpeedAsync(true, CancellationToken.None);

        hardware.StateAfterWrite = TestData.CustomState(other);
        var disable = await service.SetFullSpeedAsync(false, CancellationToken.None);

        Assert.Equal(OperationStatus.PartiallySucceeded, disable.Status);
        Assert.Contains("请显式重新应用配置", disable.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreBootSnapshot_IsBlocked_WithoutSnapshot()
    {
        var hardware = new FakeFanHardware { CanDecodeState = false };
        var service = CreateService(hardware);

        var result = await service.RestoreBootSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains("没有可恢复的启动快照", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public async Task RestoreBootSnapshot_AppliesBothChannels()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = TestData.Accepted()
        };

        var service = CreateService(hardware);
        var read = await service.ReadStateAsync(CancellationToken.None);
        Assert.True(service.BootSnapshot.TryCapture(read));

        var result = await service.RestoreBootSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, hardware.WriteCount);
        Assert.Contains("ApplyCurveAsync:Cpu", hardware.Calls);
        Assert.Contains("ApplyCurveAsync:Gpu", hardware.Calls);
        Assert.NotNull(service.LastApplied);
    }

    [Fact]
    public async Task RestoreBootSnapshot_IsBlocked_WhenSnapshotViolatesCurrentLimits()
    {
        var hardware = new FakeFanHardware
        {
            CanDecodeState = true,
            State = TestData.CustomState(),
            ApplyResult = TestData.Accepted()
        };

        // 先用宽松限制取得快照，再用更严的限制恢复：必须拒绝，而不是强行下发。
        var service = CreateService(hardware, TestData.VerifiedLimits(maxNodes: 8));
        var read = await service.ReadStateAsync(CancellationToken.None);
        Assert.True(service.BootSnapshot.TryCapture(read));

        var strict = CreateServiceWithSameSnapshot(hardware, TestData.VerifiedLimits(maxNodes: 2), service.BootSnapshot);
        var result = await strict.RestoreBootSnapshotAsync(CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Contains("启动快照不满足当前硬件约束", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, hardware.WriteCount);
    }

    [Fact]
    public void CreateConfigurationState_CarriesLastAppliedAndReadBack()
    {
        var hardware = new FakeFanHardware { CanDecodeState = false };
        var service = CreateService(hardware);

        var state = service.CreateConfigurationState(TestData.CurveSet(), TestData.CurveSet());

        Assert.False(state.HasUnsavedChanges);
        Assert.True(state.HasUnappliedChanges);
        Assert.Null(state.LastApplied);
        Assert.Null(state.Hardware);
    }

    [Fact]
    public void Validate_DelegatesToDuplicatedValidator()
    {
        var service = CreateService(new FakeFanHardware());
        var validation = service.Validate(TestData.CurveSet().Cpu);

        Assert.True(validation.IsValid);
    }

    private static FanApplicationService CreateServiceWithSameSnapshot(
        FakeFanHardware hardware,
        FanCurveLimits limits,
        FanBootSnapshotStore store)
        => new(hardware, limits, TestData.VerifiedFanCapabilities(), store);
}
