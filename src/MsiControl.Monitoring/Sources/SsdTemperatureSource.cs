using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Internal;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// SSD 温度来源（扩展传感器）。
/// </summary>
/// <remarks>
/// <para>
/// 依次尝试 <c>root\Microsoft\Windows\Storage:MSFT_StorageReliabilityCounter</c>（NVMe/SATA
/// 通过 Storage API 上报的 SMART 温度，无需管理员）与
/// <c>root\WMI:MSStorageDriver_ATAPISmartData</c>（传统 ATA SMART，通常需要管理员）。
/// 两个来源在本机均无可用实例（2026-10-16 只读探测），因此如实返回「不可用」并说明原因。
/// </para>
/// <para>
/// <b>只探测一次：</b>该来源属「能力是否存在」而非「实时变化」，首次探测后缓存结果，
/// 不在每秒采样中重复查询（任务书 §20「扩展传感器按需采样」）。
/// </para>
/// </remarks>
public sealed class SsdTemperatureSource : IExtendedSensorSource
{
    /// <summary>扩展传感器键。</summary>
    public const string SensorKey = "storage.ssd.temperature";

    private const string ReliabilityClass = "MSFT_StorageReliabilityCounter";
    private const string ReliabilityNamespace = @"root\Microsoft\Windows\Storage";
    private const string SmartClass = "MSStorageDriver_ATAPISmartData";

    private readonly object _gate = new();
    private ExtendedSensor? _cached;

    /// <inheritdoc />
    public string Key => "storage.ssd.temperature.source";

    /// <inheritdoc />
    public string Description => "SMART / 存储 API";

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtendedSensor>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _cached ??= Probe();
            return Task.FromResult<IReadOnlyList<ExtendedSensor>>(new[] { _cached });
        }
    }

    private ExtendedSensor Probe()
    {
        var now = DateTimeOffset.Now;

        double? temperature = null;
        string? source = null;

        // 1) 存储可靠性计数器（无需管理员）。
        WmiRead.Query(
            ReliabilityNamespace,
            $"SELECT DeviceId, Temperature FROM {ReliabilityClass}",
            item =>
            {
                if (temperature is not null)
                {
                    return;
                }

                var raw = WmiRead.Double(item, "Temperature");
                if (raw is { } value && value > 0)
                {
                    temperature = value;
                    source = $"{ReliabilityClass}.Temperature";
                }
            },
            out _);

        // 2) 传统 ATA SMART（通常需要管理员，本机无实例）。
        if (temperature is null)
        {
            WmiRead.Query(
                WmiRead.WmiNamespace,
                $"SELECT InstanceName, Temperature FROM {SmartClass}",
                item =>
                {
                    if (temperature is not null)
                    {
                        return;
                    }

                    var raw = WmiRead.Double(item, "Temperature");
                    if (raw is { } value && value > 0)
                    {
                        temperature = value;
                        source = $"{SmartClass}.Temperature";
                    }
                },
                out _);
        }

        if (temperature is { } celsius)
        {
            return new ExtendedSensor(
                SensorKey,
                "SSD 温度",
                SensorReading.Valid(celsius, Units.Celsius, source ?? Description, now));
        }

        return new ExtendedSensor(
            SensorKey,
            "SSD 温度",
            SensorReading.Unavailable(
                Units.Celsius,
                Description,
                "本机无可读取的 SSD 温度来源（MSFT_StorageReliabilityCounter 与 MSStorageDriver_ATAPISmartData 均无实例）。"));
    }
}
