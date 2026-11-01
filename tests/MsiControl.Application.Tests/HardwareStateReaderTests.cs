using MsiControl.Application.Control;
using MsiControl.Application.Host;
using MsiControl.Application.Integration;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Capabilities;
using Xunit;

namespace MsiControl.Application.Tests;

/// <summary>
/// 宿主状态读回（<see cref="IHardwareStateReader"/> 实现）测试。
/// </summary>
/// <remarks>
/// 读回链路是「详细编辑区当前生效控制模式」与「实际性能模式」徽标的数据来源；
/// 失败必须返回 <see langword="null"/> 而不是编造状态（任务书 §1 原则 6）。
/// </remarks>
public sealed class HardwareStateReaderTests
{
    [Fact]
    public async Task ReadFanStateAsync_WithFanHardware_ReturnsHardwareState()
    {
        var (host, fan, _, _) = CreateHost();
        using var _ = host;
        Assert.NotNull(fan);
        fan.ReadHandler = _ => Task.FromResult(new FanHardwareState(
            FanControlMode.Silent,
            CpuCurve: null,
            GpuCurve: null,
            SupportsFullSpeed: true,
            CalibrationAvailable: false));

        var state = await host.ReadFanStateAsync();

        Assert.NotNull(state);
        Assert.Equal(FanControlMode.Silent, state.Mode);
        Assert.True(state.SupportsFullSpeed);
        Assert.Equal(1, fan.ReadCalls);
    }

    [Fact]
    public async Task ReadPerformanceStateAsync_WithPerformanceHardware_ReturnsActiveStage()
    {
        var (host, _, performance, _) = CreateHost();
        using var _ = host;
        performance.ReadHandler = _ => Task.FromResult(
            new PerformanceHardwareState(ActiveStage: PerformanceStage.Balanced, FirmwareScenario: "0xC1 comfort"));

        var state = await host.ReadPerformanceStateAsync();

        Assert.NotNull(state);
        Assert.Equal(PerformanceStage.Balanced, state.ActiveStage);
        Assert.Equal("0xC1 comfort", state.FirmwareScenario);
    }

    [Fact]
    public async Task ReadFanStateAsync_WithoutFanHardware_ReturnsNull()
    {
        // CreateExplicit 不传风扇适配层：读回源必须如实返回 null，不得编造。
        var (host, fan, _, _) = CreateHost(withFan: false);
        using var _ = host;

        Assert.Null(fan);

        var state = await host.ReadFanStateAsync();

        Assert.Null(state);
    }

    [Fact]
    public async Task ReadFanStateAsync_WhenHardwareThrows_ReturnsNullAndLogsThrottled()
    {
        var (host, fan, _, log) = CreateHost();
        using var _ = host;
        Assert.NotNull(fan);
        fan.ReadHandler = _ => throw new InvalidOperationException("模拟 EC 读取失败");

        var first = await host.ReadFanStateAsync();
        var second = await host.ReadFanStateAsync();

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(2, fan.ReadCalls);
        // 60 秒限流：连续两次失败只记一条 Warning。
        Assert.Equal(1, log.Entries.Count(e => e.Category == "Readback"));
    }

    [Fact]
    public async Task ReadPerformanceStateAsync_WhenHardwareThrows_ReturnsNull()
    {
        var (host, _, performance, _) = CreateHost();
        using var _ = host;
        performance.ReadHandler = _ => throw new TimeoutException("模拟 EC 瞬时超时");

        var state = await host.ReadPerformanceStateAsync();

        Assert.Null(state);
    }

    [Fact]
    public async Task ReadGraphicsStateAsync_WithGraphicsHardware_ReturnsState()
    {
        var deviceInfo = CreateDeviceInfo();
        var capabilities = CapabilityBaseline.ForDevice(deviceInfo.Device);
        var graphics = new FakeGraphicsHardware
        {
            State = new GraphicsHardwareState(Current: GraphicsMode.Hybrid, SupportsHybrid: true)
        };
        var host = ApplicationHost.CreateExplicit(
            deviceInfo,
            capabilities,
            new ControlCoordinator(capabilities, new FakePerformanceHardware()),
            new FakePowerSupplyWatcher(),
            performanceHardware: new FakePerformanceHardware(),
            options: new ApplicationHostOptions
            {
                AutoStartPowerPolicy = false,
                ScanThirdPartyConflicts = false,
                AutoRestoreOnStartup = false
            },
            graphicsHardware: graphics);
        using var _ = host;

        var state = await host.ReadGraphicsStateAsync();

        Assert.NotNull(state);
        Assert.Equal(GraphicsMode.Hybrid, state.Current);
        Assert.True(state.SupportsHybrid);
    }

    [Fact]
    public async Task ReadGraphicsStateAsync_WithoutGraphicsHardware_ReturnsNull()
    {
        // CreateExplicit 不传显卡模块：读回源必须如实返回 null，不得编造。
        var (host, _, _, _) = CreateHost();
        using var _ = host;

        var state = await host.ReadGraphicsStateAsync();

        Assert.Null(state);
    }

    [Fact]
    public async Task ReadGraphicsStateAsync_WhenHardwareThrows_ReturnsNull()
    {
        var deviceInfo = CreateDeviceInfo();
        var capabilities = CapabilityBaseline.ForDevice(deviceInfo.Device);
        var host = ApplicationHost.CreateExplicit(
            deviceInfo,
            capabilities,
            new ControlCoordinator(capabilities, new FakePerformanceHardware()),
            new FakePowerSupplyWatcher(),
            performanceHardware: new FakePerformanceHardware(),
            options: new ApplicationHostOptions
            {
                AutoStartPowerPolicy = false,
                ScanThirdPartyConflicts = false,
                AutoRestoreOnStartup = false
            },
            graphicsHardware: new ThrowingGraphicsHardware());
        using var _ = host;

        var state = await host.ReadGraphicsStateAsync();

        Assert.Null(state);
    }

    private static DeviceInfoResult CreateDeviceInfo() => new(
        new DeviceIdentity(
            "Vector 16 HX AI A2XWHG",
            DeviceIdentity.TargetBoardCode,
            BiosVersion: "E15M3IMS.117",
            WindowsBuild: "26300"),
        new[] { "测试替身提供的标识。" });

    private sealed class ThrowingGraphicsHardware : IGraphicsHardware
    {
        public Task<GraphicsHardwareState> ReadAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("模拟显卡读回失败");

        public Task<ControlResult> RequestModeAsync(GraphicsMode mode, CancellationToken cancellationToken)
            => throw new NotSupportedException("测试替身不实现切换。");
    }

    private static (ApplicationHost Host, FakeFanHardware? Fan, FakePerformanceHardware Performance, RecordingLog Log) CreateHost(
        bool withFan = true)
    {
        var fan = withFan ? new FakeFanHardware() : null;
        var performance = new FakePerformanceHardware();
        var log = new RecordingLog();

        var deviceInfo = new DeviceInfoResult(
            new DeviceIdentity(
                "Vector 16 HX AI A2XWHG",
                DeviceIdentity.TargetBoardCode,
                BiosVersion: "E15M3IMS.117",
                WindowsBuild: "26300"),
            new[] { "测试替身提供的标识。" });
        var capabilities = CapabilityBaseline.ForDevice(deviceInfo.Device);

        var host = ApplicationHost.CreateExplicit(
            deviceInfo,
            capabilities,
            new ControlCoordinator(capabilities, performance),
            new FakePowerSupplyWatcher(),
            performanceHardware: performance,
            options: new ApplicationHostOptions
            {
                AutoStartPowerPolicy = false,
                ScanThirdPartyConflicts = false,
                AutoRestoreOnStartup = false
            },
            log: log,
            fanHardware: fan);

        return (host, fan, performance, log);
    }
}
