using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 尚未适配的数据源：一律返回「不可用」，并说明原因。
/// </summary>
/// <remarks>
/// <b>不返回模拟数据。</b>任务书 §6.4 与提示词第 7 条要求：读不到就显示「不可用」，
/// 不得用看似合理的数值填充界面。
/// </remarks>
public sealed class UnavailableCpuTemperatureSource : ICpuTemperatureSource
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    /// <param name="reason">不可用原因，会作为读数详情展示。</param>
    public UnavailableCpuTemperatureSource(string reason) => _reason = reason;

    /// <inheritdoc />
    public string Key => "cpu.temperature.none";

    /// <inheritdoc />
    public string Description => "CPU 温度";

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SensorReading.Unavailable(Units.Celsius, Description, _reason));
    }
}

/// <summary>尚未适配的 CPU 频率来源。</summary>
public sealed class UnavailableCpuFrequencySource : ICpuFrequencySource
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    public UnavailableCpuFrequencySource(string reason) => _reason = reason;

    /// <inheritdoc />
    public string Key => "cpu.frequency.none";

    /// <inheritdoc />
    public string Description => "CPU 频率";

    /// <inheritdoc />
    public FrequencyAggregation Basis => FrequencyAggregation.AverageActive;

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SensorReading.Unavailable(Units.Megahertz, Description, _reason));
    }
}

/// <summary>尚未适配的 CPU 封装功耗来源。</summary>
public sealed class UnavailableCpuPowerSource : ICpuPowerSource
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    public UnavailableCpuPowerSource(string reason) => _reason = reason;

    /// <inheritdoc />
    public string Key => "cpu.power.none";

    /// <inheritdoc />
    public string Description => "CPU 封装功耗";

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SensorReading.Unavailable(Units.Watt, Description, _reason));
    }
}

/// <summary>尚未适配的风扇转速来源。</summary>
public sealed class UnavailableFanSpeedSource : IFanSpeedSource
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    public UnavailableFanSpeedSource(string reason) => _reason = reason;

    /// <inheritdoc />
    public string Key => "fan.rpm.none";

    /// <inheritdoc />
    public string Description => "风扇转速";

    /// <inheritdoc />
    public Task<FanReading> ReadAsync(FanChannel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FanReading.Unavailable(Description, _reason));
    }
}

/// <summary>尚未适配的独显来源。</summary>
public sealed class UnavailableGpuSource : IGpuSource
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    public UnavailableGpuSource(string reason) => _reason = reason;

    /// <inheritdoc />
    public string Key => "gpu.none";

    /// <inheritdoc />
    public string Description => "独显";

    /// <inheritdoc />
    public Task<GpuSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GpuSnapshot.Unavailable(Description, _reason));
    }
}

/// <summary>尚未适配的整机功耗来源。</summary>
public sealed class UnavailableSystemPowerSource : ISystemPowerSource
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    public UnavailableSystemPowerSource(string reason) => _reason = reason;

    /// <inheritdoc />
    public string Key => "power.system.none";

    /// <inheritdoc />
    public string Description => "整机功耗";

    /// <inheritdoc />
    public Task<SystemPowerReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(SystemPowerReading.Unavailable(_reason));
    }
}
