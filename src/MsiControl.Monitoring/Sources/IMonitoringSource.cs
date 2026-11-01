using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 所有监控数据源的共同元信息。
/// </summary>
/// <remarks>
/// <para>
/// 数据源不做任何硬件写入，也**不持有采样循环**：<see cref="MonitoringProvider"/> 每被调用一次，
/// 就在同一次调用内依次询问各个数据源。这样「整个应用只有一个采样任务」的约束天然成立。
/// </para>
/// <para>
/// <see cref="Key"/> 用于诊断与退避计数，必须稳定且唯一。
/// </para>
/// </remarks>
public interface IMonitoringSource
{
    /// <summary>稳定标识，用于诊断与退避计数。例如 <c>cpu.temperature.thermalzone</c>。</summary>
    string Key { get; }

    /// <summary>数据来源说明。会写入 <see cref="SensorReading.Source"/>，供界面追溯。</summary>
    string Description { get; }

    /// <summary>该数据源建议的超时。为 <see langword="null"/> 时使用 <see cref="MonitoringOptions.SourceTimeout"/>。</summary>
    TimeSpan? SuggestedTimeout => null;

    /// <summary>
    /// 该数据源所依赖的能力键（取值见 <see cref="CapabilityKeys"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为 <see langword="null"/> 表示该数据源读取的是**操作系统标准计数器**（性能计数器、
    /// 内存总量等），不是厂商私有能力，因此不参与 <c>§16.1</c> 的能力门控；
    /// 其可用性由读取结果本身表达（读不到即 <see cref="ReadingStatus.Unavailable"/>）。
    /// </para>
    /// <para>
    /// 非 <see langword="null"/> 时，<see cref="MonitoringProvider"/> 会在调用前检查
    /// <see cref="DeviceCapabilities.AllowsRead(string)"/>：不满足则**不调用**该数据源，
    /// 直接给出禁用的原因文案（任务书 §16.1：<c>Unknown</c> 与 <c>Unsupported</c> 不得调用）。
    /// </para>
    /// </remarks>
    string? RequiredCapabilityKey => null;
}

/// <summary>CPU 温度来源。</summary>
public interface ICpuTemperatureSource : IMonitoringSource
{
    /// <summary>读取 CPU 温度。</summary>
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>CPU 统一频率来源。</summary>
public interface ICpuFrequencySource : IMonitoringSource
{
    /// <summary>频率统计口径，会写入 <see cref="CpuSnapshot.FrequencyBasis"/> 并由界面注明。</summary>
    FrequencyAggregation Basis { get; }

    /// <summary>读取统一频率（内部单位 MHz）。</summary>
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>CPU 封装功耗来源。</summary>
public interface ICpuPowerSource : IMonitoringSource
{
    /// <summary>读取 CPU 封装功耗（瓦）。</summary>
    Task<SensorReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>风扇转速来源。读数属任务 03，风扇**控制**属任务 04。</summary>
public interface IFanSpeedSource : IMonitoringSource
{
    /// <summary>读取指定通道的风扇读数。</summary>
    Task<FanReading> ReadAsync(FanChannel channel, CancellationToken cancellationToken);
}

/// <summary>独显来源（默认对应 RTX 5070 Ti Laptop）。</summary>
public interface IGpuSource : IMonitoringSource
{
    /// <summary>读取独显四项。独显断电时必须返回 <see cref="GpuSnapshot.Sleeping"/>。</summary>
    Task<GpuSnapshot> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>整机功耗来源。三种口径不可混用、不可相加。</summary>
public interface ISystemPowerSource : IMonitoringSource
{
    /// <summary>读取整机功耗。</summary>
    Task<SystemPowerReading> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>扩展传感器来源，按能力折叠展示。</summary>
public interface IExtendedSensorSource : IMonitoringSource
{
    /// <summary>
    /// 最小刷新间隔。为 <see cref="TimeSpan.Zero"/> 表示每个采样周期都重新读取。
    /// </summary>
    /// <remarks>
    /// 大于零时，<see cref="MonitoringProvider"/> 在该间隔内**沿用上次结果**，并会在
    /// 显示名后追加「（每 N 秒刷新）」，同时保留读数自身的采样时刻——
    /// 界面据此可判断数据年龄，不会被误当作本周期的新读数。
    /// </remarks>
    TimeSpan MinRefreshInterval => TimeSpan.Zero;

    /// <summary>读取扩展传感器条目。</summary>
    Task<IReadOnlyList<ExtendedSensor>> ReadAsync(CancellationToken cancellationToken);
}
