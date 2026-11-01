using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Internal;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 物理内存占用来源（扩展传感器）。
/// </summary>
/// <remarks>
/// 读取 <c>Win32_OperatingSystem</c> 的 <c>TotalVisibleMemorySize</c> 与 <c>FreePhysicalMemory</c>
/// （单位均为 KiB），计算占用的百分比。属操作系统标准计数器，不参与能力门控。
/// </remarks>
public sealed class MemoryUsageSource : IExtendedSensorSource
{
    /// <summary>扩展传感器键。</summary>
    public const string SensorKey = "system.memory";

    private readonly string _namespacePath;

    /// <summary>构造数据源。</summary>
    /// <param name="namespacePath">WMI 命名空间，默认 <c>root\cimv2</c>。</param>
    public MemoryUsageSource(string namespacePath = WmiRead.CimV2Namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePath);
        _namespacePath = namespacePath;
    }

    /// <inheritdoc />
    public string Key => "system.memory.wmi";

    /// <inheritdoc />
    public string Description => "Win32_OperatingSystem";

    /// <inheritdoc />
    public TimeSpan MinRefreshInterval => MonitoringOptions.DefaultExtendedRefresh;

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtendedSensor>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        long? totalKb = null;
        long? freeKb = null;

        WmiRead.Query(
            _namespacePath,
            "SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem",
            item =>
            {
                totalKb ??= WmiRead.Integer(item, "TotalVisibleMemorySize");
                freeKb ??= WmiRead.Integer(item, "FreePhysicalMemory");
            },
            out var error);

        SensorReading reading;
        if (error is not null)
        {
            reading = SensorReading.Unavailable(Units.Percent, Description, $"读取内存信息失败：{error}");
        }
        else if (totalKb is null or <= 0 || freeKb is null)
        {
            reading = SensorReading.Unavailable(Units.Percent, Description, "未取得有效的物理内存容量数据。");
        }
        else
        {
            var used = totalKb.Value - freeKb.Value;
            var percent = Measurement.Clamp(used * 100.0 / totalKb.Value, 0.0, 100.0);
            reading = SensorReading.Valid(
                percent,
                Units.Percent,
                $"{Description}（TotalVisibleMemorySize / FreePhysicalMemory）",
                DateTimeOffset.Now);
        }

        return Task.FromResult<IReadOnlyList<ExtendedSensor>>(
            new[] { new ExtendedSensor(SensorKey, "内存占用", reading) });
    }
}
