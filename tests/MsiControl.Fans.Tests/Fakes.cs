using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices;
using MsiControl.Hardware.Msi.Transport;

namespace MsiControl.Fans.Tests;

/// <summary>测试替身：可编程的风扇硬件。</summary>
internal sealed class FakeFanHardware : IFanHardware, IFanRawDiagnostics, IFanStateReadability
{
    public FanHardwareState State { get; set; } = FanHardwareState.Unknown;

    public bool CanDecodeState { get; set; }

    public string DecodeLimitation { get; set; } = "测试替身：字节语义未取得证据。";

    public FanRawReport Raw { get; set; } =
        new(DateTimeOffset.Now, "Get_Fan", true, Array.Empty<byte>(), "测试替身：通道可达但未解码。");

    public ControlResult ApplyResult { get; set; } =
        ControlResult.Single(SettingResult.Unsupported("fan.curve.cpu", "测试替身：默认拒绝。"));

    public ControlResult AutomaticResult { get; set; } =
        ControlResult.Single(SettingResult.Unsupported("fan.mode", "测试替身：默认拒绝。"));

    public ControlResult FullSpeedResult { get; set; } =
        ControlResult.Single(SettingResult.Unsupported("fan.fullspeed", "测试替身：默认拒绝。"));

    /// <summary>写入之后读回应看到的状态。设置后会在写入调用时替换 <see cref="State"/>。</summary>
    public FanHardwareState? StateAfterWrite { get; set; }

    /// <summary>写入发生时执行的回调，用于在写入前后切换替身行为。</summary>
    public Action? OnWrite { get; set; }

    /// <summary>读取时是否抛出异常。</summary>
    public bool ThrowOnRead { get; set; }

    /// <summary>写入时是否立即以取消结束（模拟「已开始写入后被取消」）。</summary>
    public bool CancelOnWrite { get; set; }

    public int ReadCount { get; private set; }

    public int WriteCount { get; private set; }

    public List<string> Calls { get; } = new();

    public Task<FanHardwareState> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        Calls.Add("ReadAsync");

        if (ThrowOnRead)
        {
            throw new InvalidOperationException("测试替身：读取失败。");
        }

        return Task.FromResult(State);
    }

    public Task<FanRawReport> ReadRawAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add("ReadRawAsync");
        return Task.FromResult(Raw);
    }

    public Task<ControlResult> ApplyCurveAsync(
        FanChannel channel,
        FanCurve curve,
        CancellationToken cancellationToken)
    {
        WriteCount++;
        Calls.Add($"ApplyCurveAsync:{channel}");
        cancellationToken.ThrowIfCancellationRequested();
        OnWrite?.Invoke();

        if (CancelOnWrite)
        {
            throw new OperationCanceledException("测试替身：写入过程中被取消。");
        }

        if (StateAfterWrite is { } next)
        {
            State = next;
        }

        return Task.FromResult(ApplyResult);
    }

    public Task<ControlResult> SetAutomaticAsync(CancellationToken cancellationToken)
    {
        WriteCount++;
        Calls.Add("SetAutomaticAsync");
        cancellationToken.ThrowIfCancellationRequested();
        OnWrite?.Invoke();

        if (StateAfterWrite is { } next)
        {
            State = next;
        }

        return Task.FromResult(AutomaticResult);
    }

    public Task<ControlResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken)
    {
        WriteCount++;
        Calls.Add($"SetFullSpeedAsync:{enabled}");
        cancellationToken.ThrowIfCancellationRequested();
        OnWrite?.Invoke();

        if (StateAfterWrite is { } next)
        {
            State = next;
        }

        return Task.FromResult(FullSpeedResult);
    }
}

/// <summary>测试替身：只读调用通道。</summary>
internal sealed class FakeTransport : IMsiAcpiTransport
{
    public MsiAcpiInvocationResult Result { get; set; } =
        MsiAcpiInvocationResult.Failed("测试替身：未设置返回。");

    public bool ThrowOnRead { get; set; }

    public List<string> Calls { get; } = new();

    public Task<MsiAcpiInvocationResult> InvokeReadAsync(
        string methodName,
        object? data,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(methodName);

        if (ThrowOnRead)
        {
            throw new InvalidOperationException("测试替身：通道抛错。");
        }

        return Task.FromResult(Result);
    }
}

/// <summary>测试替身：入参工厂。</summary>
internal sealed class FakePayloadFactory : IFanPayloadFactory
{
    public bool Throw { get; set; }

    public byte[]? LastBytes { get; private set; }

    public object Create(byte[] bytes)
    {
        LastBytes = bytes;
        if (Throw)
        {
            throw new InvalidOperationException("测试替身：无法构造入参。");
        }

        return "fake-package";
    }
}

/// <summary>测试用构造器。</summary>
internal static class TestData
{
    /// <summary>全约束均已确证的曲线限制。</summary>
    public static FanCurveLimits VerifiedLimits(
        int maxNodes = 8,
        double outputStepPercent = 1.0,
        double temperatureStepC = 1.0,
        bool temperatureEditable = true)
        => new(
            source: "测试替身：全部约束声明为已确证",
            level: CapabilityLevel.Verified,
            minTemperatureC: Measurement.MinCurveTemperatureC,
            maxTemperatureC: Measurement.MaxCurveTemperatureC,
            minOutputPercent: 0,
            maxOutputPercent: 100,
            maxNodes: maxNodes,
            temperatureEditable: temperatureEditable,
            temperatureStepC: temperatureStepC,
            outputStepPercent: outputStepPercent);

    /// <summary>全部风扇能力均已确证的设备能力集合（模拟「将来的已验证设备」）。</summary>
    public static DeviceCapabilities VerifiedFanCapabilities()
        => new(new[]
        {
            CapabilityStatus.Verified(CapabilityKeys.FanCurve, "测试替身：fan.curve 声明为已确证。"),
            CapabilityStatus.Verified(CapabilityKeys.FanFullSpeed, "测试替身：fan.fullspeed 声明为已确证。"),
            CapabilityStatus.Verified(CapabilityKeys.FanCalibration, "测试替身：fan.calibration 声明为已确证。")
        });

    /// <summary>构造一条合法曲线。</summary>
    public static FanCurve Curve(FanChannel channel, params (double TemperatureC, double Percent)[] points)
        => FanCurve.Create(
            channel,
            points.Select(p => new FanPoint(p.TemperatureC, p.Percent)).ToArray());

    /// <summary>构造一个双通道曲线对。</summary>
    public static FanCurveSet CurveSet()
        => new(
            Curve(FanChannel.Cpu, (40, 20), (60, 40), (80, 80)),
            Curve(FanChannel.Gpu, (40, 20), (60, 40), (80, 80)));

    /// <summary>构造一个写入成功并带确认依据的硬件结果。</summary>
    public static ControlResult Accepted(string setting = "fan.curve.cpu", string requested = "3 节点")
        => ControlResult.Single(SettingResult.Succeeded(setting, requested, requested, "测试替身：已读回。"));

    /// <summary>构造一个「读到双通道曲线」的硬件状态。</summary>
    public static FanHardwareState CustomState(FanCurveSet? set = null)
    {
        var curves = set ?? CurveSet();
        return new FanHardwareState(FanControlMode.CustomCurve, curves.Cpu, curves.Gpu);
    }

    /// <summary>构造一个有效转速读数的风扇读数。</summary>
    public static FanReading Reading(int rpm, DateTimeOffset sampledAt)
        => FanReading.Valid(rpm, "测试替身", sampledAt);

    /// <summary>构造一个双通道有效读数的风扇快照。</summary>
    public static FanSnapshot Snapshot(int cpuRpm, int gpuRpm, DateTimeOffset sampledAt)
        => new(Reading(cpuRpm, sampledAt), Reading(gpuRpm, sampledAt));
}
