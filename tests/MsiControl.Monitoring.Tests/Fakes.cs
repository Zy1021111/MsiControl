using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Sources;

namespace MsiControl.Monitoring.Tests;

/// <summary>可编程的 CPU 温度来源替身。</summary>
internal sealed class FakeTemperatureSource : ICpuTemperatureSource
{
    private readonly Func<SensorReading> _factory;

    public FakeTemperatureSource(string key, Func<SensorReading> factory)
    {
        Key = key;
        _factory = factory;
    }

    public string Key { get; }

    public string Description => $"替身温度源 {Key}";

    public string? RequiredCapabilityKey { get; init; }

    public int CallCount { get; private set; }

    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_factory());
    }
}

/// <summary>可编程的 CPU 频率来源替身。</summary>
internal sealed class FakeFrequencySource : ICpuFrequencySource
{
    private readonly Func<SensorReading> _factory;

    public FakeFrequencySource(Func<SensorReading> factory) => _factory = factory;

    public string Key => "fake.frequency";

    public string Description => "替身频率源";

    public FrequencyAggregation Basis { get; init; } = FrequencyAggregation.MaximumActive;

    public int CallCount { get; private set; }

    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_factory());
    }
}

/// <summary>可编程的 CPU 功耗来源替身。</summary>
internal sealed class FakePowerSource : ICpuPowerSource
{
    private readonly Func<SensorReading> _factory;

    public FakePowerSource(Func<SensorReading> factory) => _factory = factory;

    public string Key => "fake.cpupower";

    public string Description => "替身 CPU 功耗源";

    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(_factory());
}

/// <summary>可编程的风扇来源替身。</summary>
internal sealed class FakeFanSource : IFanSpeedSource
{
    private readonly Func<FanChannel, FanReading> _factory;

    public FakeFanSource(Func<FanChannel, FanReading> factory) => _factory = factory;

    public string Key => "fake.fan";

    public string Description => "替身风扇源";

    public string? RequiredCapabilityKey { get; init; }

    public List<FanChannel> Channels { get; } = new();

    public Task<FanReading> ReadAsync(FanChannel channel, CancellationToken cancellationToken)
    {
        Channels.Add(channel);
        return Task.FromResult(_factory(channel));
    }
}

/// <summary>可编程的独显来源替身。</summary>
internal sealed class FakeGpuSource : IGpuSource
{
    private readonly Func<CancellationToken, Task<GpuSnapshot>> _read;

    public FakeGpuSource(Func<GpuSnapshot> factory)
        => _read = _ => Task.FromResult(factory());

    public FakeGpuSource(Func<CancellationToken, Task<GpuSnapshot>> asyncFactory)
        => _read = asyncFactory;

    public string Key => "fake.gpu";

    public string Description => "替身独显源";

    public string? RequiredCapabilityKey { get; init; }

    public int CallCount { get; private set; }

    public Task<GpuSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return _read(cancellationToken);
    }
}

/// <summary>可编程的整机功耗来源替身。</summary>
internal sealed class FakeSystemPowerSource : ISystemPowerSource
{
    private readonly Func<SystemPowerReading> _factory;

    public FakeSystemPowerSource(Func<SystemPowerReading> factory) => _factory = factory;

    public string Key => "fake.systempower";

    public string Description => "替身整机功耗源";

    public string? RequiredCapabilityKey { get; init; }

    public int CallCount { get; private set; }

    public Task<SystemPowerReading> ReadAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_factory());
    }
}

/// <summary>可编程的扩展传感器来源替身。</summary>
internal sealed class FakeExtendedSource : IExtendedSensorSource
{
    private readonly Func<int, IReadOnlyList<ExtendedSensor>> _factory;

    public FakeExtendedSource(string key, Func<int, IReadOnlyList<ExtendedSensor>> factory)
    {
        Key = key;
        _factory = factory;
    }

    public string Key { get; }

    public string Description => $"替身扩展源 {Key}";

    public string? RequiredCapabilityKey { get; init; }

    public TimeSpan MinRefreshInterval { get; init; }

    public int CallCount { get; private set; }

    public Task<IReadOnlyList<ExtendedSensor>> ReadAsync(CancellationToken cancellationToken)
    {
        CallCount++;
        return Task.FromResult(_factory(CallCount));
    }
}

/// <summary>测试用数据构造帮助器。</summary>
internal static class Fake
{
    public static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.FromHours(8));

    public static SensorReading Celsius(double value, string source = "替身")
        => SensorReading.Valid(value, Units.Celsius, source, Now);

    public static SensorReading Megahertz(double value, string source = "替身")
        => SensorReading.Valid(value, Units.Megahertz, source, Now);

    public static SensorReading Watt(double value, string source = "替身")
        => SensorReading.Valid(value, Units.Watt, source, Now);

    public static SensorReading Percent(double value, string source = "替身")
        => SensorReading.Valid(value, Units.Percent, source, Now);

    public static DeviceCapabilities Capabilities(params CapabilityStatus[] statuses)
        => new(statuses);
}
