using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Sources;
using Xunit;

namespace MsiControl.Monitoring.Tests;

/// <summary>
/// <see cref="MonitoringProvider"/> 的状态语义与降级行为。
/// </summary>
/// <remarks>
/// 全部使用测试替身，不接触真实硬件；本机以外的机器也应全绿。
/// </remarks>
public sealed class MonitoringProviderTests
{
    private static readonly DateTimeOffset Now = Fake.Now;

    // ── 空状态 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 未提供任何数据源时_全部槽位为不可用且不抛异常()
    {
        using var provider = new MonitoringProvider(DeviceCapabilities.Empty);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, snapshot.Cpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Cpu.FrequencyMhz.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Cpu.PackagePowerWatt.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Cpu.Fan.Rpm.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Gpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Fans.Cpu.Rpm.Status);
        Assert.False(snapshot.SystemPower.IsAvailable);
        Assert.Empty(snapshot.Extended);

        // 不可用读数不得携带数值，否则界面可能把陈旧值当作当前读数。
        Assert.Null(snapshot.Cpu.Temperature.Value);
        Assert.Null(snapshot.Gpu.Temperature.Value);
    }

    // ── 温度来源链 ────────────────────────────────────────────────────

    [Fact]
    public async Task 温度来源链_取第一个可用来源()
    {
        var first = new FakeTemperatureSource("first", () => SensorReading.Unavailable(Units.Celsius, "first", "读不到"));
        var second = new FakeTemperatureSource("second", () => Fake.Celsius(61.5));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { first, second });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, snapshot.Cpu.Temperature.Status);
        Assert.Equal(61.5, snapshot.Cpu.Temperature.Value);
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task 温度来源链_首个可用时不再调用后续来源()
    {
        var first = new FakeTemperatureSource("first", () => Fake.Celsius(50));
        var second = new FakeTemperatureSource("second", () => Fake.Celsius(60));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { first, second });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(50, snapshot.Cpu.Temperature.Value);
        Assert.Equal(0, second.CallCount);
    }

    [Fact]
    public async Task 温度来源链_全部不可用时合并各自原因()
    {
        var first = new FakeTemperatureSource("first", () => SensorReading.Unavailable(Units.Celsius, "first", "原因甲"));
        var second = new FakeTemperatureSource("second", () => SensorReading.Unavailable(Units.Celsius, "second", "原因乙"));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { first, second });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, snapshot.Cpu.Temperature.Status);
        Assert.Contains("原因甲", snapshot.Cpu.Temperature.Detail, StringComparison.Ordinal);
        Assert.Contains("原因乙", snapshot.Cpu.Temperature.Detail, StringComparison.Ordinal);
    }

    // ── 不得沿用上一有效值 ────────────────────────────────────────────

    [Fact]
    public async Task 读取失败时_不沿用上一次的有效值()
    {
        var call = 0;
        var temperature = new FakeTemperatureSource("t", () => ++call == 1
            ? Fake.Celsius(70)
            : SensorReading.Unavailable(Units.Celsius, "t", "第二次读取失败"));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { temperature });

        var first = await provider.ReadAsync(CancellationToken.None);
        var second = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(70, first.Cpu.Temperature.Value);
        Assert.Equal(ReadingStatus.Unavailable, second.Cpu.Temperature.Status);
        Assert.Null(second.Cpu.Temperature.Value);
        Assert.False(second.Cpu.Temperature.IsUsable);
    }

    [Fact]
    public async Task 数据源抛异常时_转为不可用并记录原因()
    {
        var temperature = new FakeTemperatureSource("t", () => throw new InvalidOperationException("模拟故障"));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { temperature });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, snapshot.Cpu.Temperature.Status);
        Assert.Contains("模拟故障", snapshot.Cpu.Temperature.Detail, StringComparison.Ordinal);
    }

    // ── 独显：休眠 vs 不可用 ──────────────────────────────────────────

    [Fact]
    public async Task 独显休眠_返回休眠而不是不可用()
    {
        var gpu = new FakeGpuSource(() => GpuSnapshot.Sleeping("替身", "独显已断电"));

        using var provider = new MonitoringProvider(DeviceCapabilities.Empty, gpuSource: gpu);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.True(snapshot.Gpu.IsSleeping);
        Assert.Equal(ReadingStatus.Sleeping, snapshot.Gpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Sleeping, snapshot.Gpu.CoreFrequencyMhz.Status);
        Assert.Equal(ReadingStatus.Sleeping, snapshot.Gpu.PowerWatt.Status);
        Assert.Equal(ReadingStatus.Sleeping, snapshot.Gpu.Fan.Rpm.Status);

        // 独显休眠时其风扇槽位也应为休眠，而不是「不可用」。
        Assert.Equal(ReadingStatus.Sleeping, snapshot.Fans.Gpu.Rpm.Status);
    }

    [Fact]
    public async Task 独显读取失败_返回不可用而不是休眠()
    {
        var gpu = new FakeGpuSource(() => GpuSnapshot.Unavailable("替身", "驱动读取失败"));

        using var provider = new MonitoringProvider(DeviceCapabilities.Empty, gpuSource: gpu);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.False(snapshot.Gpu.IsSleeping);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Gpu.Temperature.Status);
    }

    [Fact]
    public async Task 独显有效_四项写入快照()
    {
        var gpu = new FakeGpuSource(() => new GpuSnapshot(
            Fake.Celsius(55),
            Fake.Megahertz(1800),
            Fake.Watt(60),
            FanReading.Unavailable("替身")));

        using var provider = new MonitoringProvider(DeviceCapabilities.Empty, gpuSource: gpu);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(55, snapshot.Gpu.Temperature.Value);
        Assert.Equal(1800, snapshot.Gpu.CoreFrequencyMhz.Value);
        Assert.Equal(60, snapshot.Gpu.PowerWatt.Value);
    }

    // ── 能力门控 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 能力为未知时_不调用被门控的数据源()
    {
        var gpu = new FakeGpuSource(() => GpuSnapshot.Sleeping("替身"))
        {
            RequiredCapabilityKey = CapabilityKeys.ThermalRead
        };
        var extended = new FakeExtendedSource("ext", _ => new[] { new ExtendedSensor("k", "K", Fake.Percent(1)) })
        {
            RequiredCapabilityKey = CapabilityKeys.BatteryPowerRead
        };

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            gpuSource: gpu,
            extendedSources: new IExtendedSensorSource[] { extended });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(0, gpu.CallCount);
        Assert.Equal(0, extended.CallCount);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Gpu.Temperature.Status);
        Assert.Contains("Unknown", snapshot.Gpu.Temperature.Detail, StringComparison.Ordinal);

        // 被门控的扩展源必须给出可见原因，而不是静默消失。
        var placeholder = Assert.Single(snapshot.Extended);
        Assert.Contains("Unknown", placeholder.Reading.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("不允许读取", placeholder.Reading.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("未调用", placeholder.Reading.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 能力为已确证时_允许只读调用()
    {
        var gpu = new FakeGpuSource(() => GpuSnapshot.Sleeping("替身"))
        {
            RequiredCapabilityKey = CapabilityKeys.ThermalRead
        };

        var capabilities = Fake.Capabilities(
            CapabilityStatus.Verified(CapabilityKeys.ThermalRead, "测试用证据"));

        using var provider = new MonitoringProvider(capabilities, gpuSource: gpu);

        await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(1, gpu.CallCount);
    }

    [Fact]
    public async Task 能力为线索时_允许只读调用()
    {
        var gpu = new FakeGpuSource(() => GpuSnapshot.Sleeping("替身"))
        {
            RequiredCapabilityKey = CapabilityKeys.ThermalRead
        };

        var capabilities = Fake.Capabilities(
            CapabilityStatus.Detected(CapabilityKeys.ThermalRead, "测试用线索"));

        using var provider = new MonitoringProvider(capabilities, gpuSource: gpu);

        await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(1, gpu.CallCount);
    }

    [Fact]
    public async Task 整机功耗槽位_能力未达可读时_恒为不可用且不调用来源()
    {
        var power = new FakeSystemPowerSource(() => new SystemPowerReading(
            PowerSourceKind.BatterySide,
            Fake.Watt(45)));
        var capabilities = Fake.Capabilities(
            CapabilityStatus.Detected(CapabilityKeys.BatteryPowerRead, "测试用线索"));

        using var provider = new MonitoringProvider(
            capabilities,
            systemPowerSources: new ISystemPowerSource[] { power });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(0, power.CallCount);
        Assert.False(snapshot.SystemPower.IsAvailable);
        Assert.Equal(PowerSourceKind.Unknown, snapshot.SystemPower.Kind);
        Assert.Null(snapshot.SystemPower.Reading.Value);
        Assert.Contains("power.system", snapshot.SystemPower.Reading.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 整机功耗槽位_能力允许时_返回电池侧口径且不与CPU_GPU相加()
    {
        var power = new FakeSystemPowerSource(() => new SystemPowerReading(
            PowerSourceKind.BatterySide,
            Fake.Watt(45)));
        var capabilities = Fake.Capabilities(
            CapabilityStatus.Verified(CapabilityKeys.SystemPowerRead, "测试用证据"));

        using var provider = new MonitoringProvider(
            capabilities,
            systemPowerSources: new ISystemPowerSource[] { power });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.True(snapshot.SystemPower.IsAvailable);
        Assert.Equal(PowerSourceKind.BatterySide, snapshot.SystemPower.Kind);
        Assert.Equal(45, snapshot.SystemPower.Reading.Value);
    }

    // ── 超时与退避 ────────────────────────────────────────────────────

    [Fact]
    public async Task 数据源超时_转为不可用并说明原因()
    {
        var never = new FakeTemperatureSource("slow", () => Fake.Celsius(1));
        var slowGpu = new FakeGpuSource(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            return GpuSnapshot.Sleeping("替身");
        });

        var options = new MonitoringOptions
        {
            SourceTimeout = TimeSpan.FromMilliseconds(200),
            FailureBackoffThreshold = 10
        };

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            options,
            cpuTemperatureSources: new ICpuTemperatureSource[] { never },
            gpuSource: slowGpu);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, snapshot.Gpu.Temperature.Status);
        Assert.Contains("超时", snapshot.Gpu.Temperature.Detail ?? string.Empty, StringComparison.Ordinal);

        // 单个数据源超时不应影响其他数据源。
        Assert.Equal(ReadingStatus.Valid, snapshot.Cpu.Temperature.Status);
    }

    [Fact]
    public async Task 连续失败达到阈值后_进入退避且不再重复调用()
    {
        var failing = new FakeTemperatureSource("bad", () => throw new InvalidOperationException("持续故障"));

        var options = new MonitoringOptions
        {
            FailureBackoffThreshold = 2,
            FailureBackoffDuration = TimeSpan.FromMinutes(10)
        };

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            options,
            cpuTemperatureSources: new ICpuTemperatureSource[] { failing });

        await provider.ReadAsync(CancellationToken.None);
        await provider.ReadAsync(CancellationToken.None);
        Assert.Equal(2, failing.CallCount);

        var third = await provider.ReadAsync(CancellationToken.None);
        var fourth = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, failing.CallCount);
        Assert.Contains("退避", third.Cpu.Temperature.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("退避", fourth.Cpu.Temperature.Detail ?? string.Empty, StringComparison.Ordinal);

        var diagnostic = Assert.Single(provider.Diagnostics.Sources);
        Assert.True(diagnostic.IsBackedOff);
        Assert.Equal(2, diagnostic.ConsecutiveFailures);
    }

    [Fact]
    public async Task 取消令牌已取消时_抛出_OperationCanceledException()
    {
        var temperature = new FakeTemperatureSource("t", () => Fake.Celsius(40));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { temperature });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync(cts.Token));
        Assert.Equal(0, temperature.CallCount);
    }

    [Fact]
    public async Task 数据源内部取消_在调用方取消时原样抛出()
    {
        var gpu = new FakeGpuSource(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
            return GpuSnapshot.Sleeping("替身");
        });

        var options = new MonitoringOptions { SourceTimeout = TimeSpan.FromSeconds(30) };

        using var provider = new MonitoringProvider(DeviceCapabilities.Empty, options, gpuSource: gpu);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync(cts.Token));
    }

    // ── 扩展传感器节流 ────────────────────────────────────────────────

    [Fact]
    public async Task 扩展源_刷新间隔内只读取一次且显示名注明刷新周期()
    {
        var extended = new FakeExtendedSource(
            "slow",
            _ => new[] { new ExtendedSensor("k", "内存占用", Fake.Percent(50)) })
        {
            MinRefreshInterval = TimeSpan.FromMinutes(5)
        };

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            extendedSources: new IExtendedSensorSource[] { extended });

        var first = await provider.ReadAsync(CancellationToken.None);
        var second = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(1, extended.CallCount);
        Assert.Contains("每 300 秒刷新", first.Extended[0].DisplayName, StringComparison.Ordinal);
        Assert.Contains("每 300 秒刷新", second.Extended[0].DisplayName, StringComparison.Ordinal);

        // 沿用缓存不等于伪造新值：采样时刻保持为真实读取时刻。
        Assert.Equal(first.Extended[0].Reading.SampledAt, second.Extended[0].Reading.SampledAt);
    }

    [Fact]
    public async Task 扩展源_间隔为零时每个采样周期都重新读取()
    {
        var extended = new FakeExtendedSource(
            "fast",
            _ => new[] { new ExtendedSensor("k", "K", Fake.Percent(1)) });

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            extendedSources: new IExtendedSensorSource[] { extended });

        await provider.ReadAsync(CancellationToken.None);
        await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, extended.CallCount);
    }

    [Fact]
    public async Task 扩展源_重复登记同一个键时去重并记录说明()
    {
        var a = new FakeExtendedSource("dup", _ => new[] { new ExtendedSensor("a", "A", Fake.Percent(1)) });
        var b = new FakeExtendedSource("dup", _ => new[] { new ExtendedSensor("b", "B", Fake.Percent(2)) });

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            extendedSources: new IExtendedSensorSource[] { a, b });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Single(snapshot.Extended);
        Assert.Equal(0, b.CallCount);
        Assert.Contains(
            provider.Diagnostics.Notes,
            note => note.Contains("重复登记", StringComparison.Ordinal));
    }

    // ── 诊断 ──────────────────────────────────────────────────────────

    [Fact]
    public async Task 诊断关闭时_不产生诊断条目()
    {
        var temperature = new FakeTemperatureSource("t", () => Fake.Celsius(40));

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { temperature });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.DoesNotContain(snapshot.Extended, s => s.Key.StartsWith("diag.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 诊断开启时_记录来源与更新时间()
    {
        var temperature = new FakeTemperatureSource("t", () => Fake.Celsius(40));

        var options = new MonitoringOptions { EnableDiagnostics = true };

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            options,
            cpuTemperatureSources: new ICpuTemperatureSource[] { temperature });

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Contains(snapshot.Extended, s => s.Key == "diag.sample.duration");
        var sourceEntry = Assert.Single(snapshot.Extended, s => s.Key == "diag.source.t");
        Assert.Equal(ReadingStatus.Valid, sourceEntry.Reading.Status);
        Assert.NotNull(sourceEntry.Reading.SampledAt);

        var diagnostic = Assert.Single(provider.Diagnostics.Sources);
        Assert.Equal("t", diagnostic.Key);
        Assert.NotNull(diagnostic.LastSuccessAt);
    }

    // ── 快照其余部分 ──────────────────────────────────────────────────

    [Fact]
    public async Task 频率口径_由数据源传播到快照()
    {
        var frequency = new FakeFrequencySource(() => Fake.Megahertz(4520))
        {
            Basis = FrequencyAggregation.MaximumActive
        };

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuFrequencySource: frequency);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(FrequencyAggregation.MaximumActive, snapshot.Cpu.FrequencyBasis);
        Assert.Equal(4520, snapshot.Cpu.FrequencyMhz.Value);
    }

    [Fact]
    public async Task 未提供频率源时_口径为默认的活跃核心均值()
    {
        using var provider = new MonitoringProvider(DeviceCapabilities.Empty);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(FrequencyAggregation.AverageActive, snapshot.Cpu.FrequencyBasis);
    }

    [Fact]
    public async Task 双风扇_两个通道分别读取()
    {
        var fan = new FakeFanSource(channel => FanReading.Valid(
            channel == FanChannel.Cpu ? 2400 : 2600,
            "替身",
            Now));

        using var provider = new MonitoringProvider(DeviceCapabilities.Empty, fanSpeedSource: fan);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, fan.Channels.Count);
        Assert.Contains(FanChannel.Cpu, fan.Channels);
        Assert.Contains(FanChannel.Gpu, fan.Channels);
        Assert.Equal(2400, snapshot.Fans.Cpu.Rpm.Value);
        Assert.Equal(2600, snapshot.Fans.Gpu.Rpm.Value);
    }

    [Fact]
    public async Task 未校准时_实际百分比为空且回落依据为RPM()
    {
        var fan = new FakeFanSource(channel => FanReading.Valid(2400, "替身", Now));

        using var provider = new MonitoringProvider(DeviceCapabilities.Empty, fanSpeedSource: fan);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.False(snapshot.Fans.Cpu.HasCalibration);
        Assert.Null(snapshot.Fans.Cpu.ActualPercent);
    }

    [Fact]
    public async Task 快照过期标记_只把原本有效的读数转过期()
    {
        var temperature = new FakeTemperatureSource("t", () => Fake.Celsius(40));
        var fan = new FakeFanSource(channel => channel == FanChannel.Cpu
            ? FanReading.Valid(2400, "替身", Now)
            : FanReading.Unavailable("替身", "GPU 风扇读不到"));

        var extended = new FakeExtendedSource("ext", _ => new[]
        {
            new ExtendedSensor("k1", "有效项", Fake.Percent(10)),
            new ExtendedSensor("k2", "不可用项", SensorReading.Unavailable(Units.Percent, "替身", "读不到"))
        });

        using var provider = new MonitoringProvider(
            DeviceCapabilities.Empty,
            cpuTemperatureSources: new ICpuTemperatureSource[] { temperature },
            fanSpeedSource: fan,
            extendedSources: new IExtendedSensorSource[] { extended });

        var snapshot = await provider.ReadAsync(CancellationToken.None);
        var stale = snapshot.MarkStale("采样超时");

        Assert.Equal(ReadingStatus.Stale, stale.Cpu.Temperature.Status);
        Assert.Equal(40, stale.Cpu.Temperature.Value);

        // 双风扇与扩展传感器都必须被覆盖。
        Assert.Equal(ReadingStatus.Stale, stale.Fans.Cpu.Rpm.Status);
        Assert.Equal(ReadingStatus.Unavailable, stale.Fans.Gpu.Rpm.Status);
        Assert.Equal(ReadingStatus.Stale, stale.Extended[0].Reading.Status);
        Assert.Equal(ReadingStatus.Unavailable, stale.Extended[1].Reading.Status);

        // 整机功耗槽位为不可用，过期标记不应把它改写成「过期」。
        Assert.Equal(ReadingStatus.Unavailable, stale.SystemPower.Reading.Status);
    }

    [Fact]
    public async Task 采样时刻为本地时间且可用于有效期判定()
    {
        using var provider = new MonitoringProvider(DeviceCapabilities.Empty);

        var snapshot = await provider.ReadAsync(CancellationToken.None);

        Assert.True(snapshot.IsFreshAt(DateTimeOffset.Now, TimeSpan.FromSeconds(30)));
        Assert.False(snapshot.IsFreshAt(DateTimeOffset.Now, TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void 非法参数组合_构造期即被拒绝()
    {
        Assert.Throws<ArgumentException>(() => new MonitoringProvider(
            DeviceCapabilities.Empty,
            new MonitoringOptions { SourceTimeout = TimeSpan.Zero }));

        Assert.Throws<ArgumentException>(() => new MonitoringProvider(
            DeviceCapabilities.Empty,
            new MonitoringOptions { FailureBackoffThreshold = 0 }));
    }

    [Fact]
    public async Task 释放后继续采样_抛出_ObjectDisposedException()
    {
        var provider = new MonitoringProvider(DeviceCapabilities.Empty);
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => provider.ReadAsync(CancellationToken.None));
    }
}
