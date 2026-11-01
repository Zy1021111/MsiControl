using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Internal;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 电池侧功率来源。
/// </summary>
/// <remarks>
/// <para>
/// 读取 Windows 标准 WMI 类 <c>root\WMI:BatteryStatus</c> 的
/// <c>PowerOnline</c>、<c>Charging</c>、<c>Discharging</c>、<c>DischargeRate</c>、<c>ChargeRate</c>
/// （放电/充电速率单位为 mW）。
/// </para>
/// <para>
/// <b>口径纪律（任务书 §6.5）：</b>本数据源只表达<b>电池侧</b>功率，口径固定为
/// <see cref="PowerSourceKind.BatterySide"/>。绝不把 CPU 与 GPU 功耗之和当作整机功耗。
/// 电池未放电时**不返回 0 W**，而是返回「不可用」并说明原因——0 W 会被误读为「整机功耗为零」。
/// </para>
/// <para>
/// 该数据源同时以扩展传感器形式暴露充放电功率；其能力键为
/// <see cref="CapabilityKeys.BatteryPowerRead"/>（本机为 <c>Detected</c>，允许只读展示）。
/// </para>
/// </remarks>
public sealed class BatteryStatusPowerSource : ISystemPowerSource, IExtendedSensorSource
{
    /// <summary>WMI 类名。</summary>
    public const string ClassName = "BatteryStatus";

    /// <summary>扩展传感器键：放电功率。</summary>
    public const string DischargeSensorKey = "power.battery.discharge";

    /// <summary>扩展传感器键：充电功率。</summary>
    public const string ChargeSensorKey = "power.battery.charge";

    private readonly string _namespacePath;

    /// <summary>构造数据源。</summary>
    /// <param name="namespacePath">WMI 命名空间，默认 <c>root\WMI</c>。</param>
    public BatteryStatusPowerSource(string namespacePath = WmiRead.WmiNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePath);
        _namespacePath = namespacePath;
    }

    /// <inheritdoc />
    public string Key => "power.battery.batterystatus";

    /// <inheritdoc />
    public string Description => "root\\WMI:BatteryStatus（电池侧）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.BatteryPowerRead;

    /// <inheritdoc />
    public TimeSpan MinRefreshInterval => MonitoringOptions.DefaultExtendedRefresh;

    /// <inheritdoc />
    public Task<SystemPowerReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sample = Sample();

        if (sample.Error is not null)
        {
            return Task.FromResult(SystemPowerReading.Unavailable($"读取电池状态失败：{sample.Error}"));
        }

        if (sample.Discharging != true)
        {
            var state = sample.PowerOnline == true ? "已接交流电" : "状态未知";
            return Task.FromResult(SystemPowerReading.Unavailable(
                $"电池当前未放电（{state}），电池侧无整机功耗读数可用。"));
        }

        if (sample.DischargeRateMw is not { } rate)
        {
            return Task.FromResult(SystemPowerReading.Unavailable("电池正在放电，但系统未提供放电速率。"));
        }

        var source = $"{Description}:DischargeRate";
        return Task.FromResult(new SystemPowerReading(
            PowerSourceKind.BatterySide,
            SensorReading.Valid(rate / 1000.0, Units.Watt, source, DateTimeOffset.Now)));
    }

    /// <inheritdoc />
    Task<IReadOnlyList<ExtendedSensor>> IExtendedSensorSource.ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sample = Sample();
        var now = DateTimeOffset.Now;

        SensorReading discharge;
        if (sample.Error is not null)
        {
            discharge = SensorReading.Unavailable(Units.Watt, Description, sample.Error);
        }
        else if (sample.Discharging != true)
        {
            discharge = SensorReading.Unavailable(
                Units.Watt,
                Description,
                sample.PowerOnline == true ? "电池当前未放电（已接交流电）。" : "电池当前未放电。");
        }
        else if (sample.DischargeRateMw is not { } rate)
        {
            discharge = SensorReading.Unavailable(Units.Watt, Description, "系统未提供放电速率。");
        }
        else
        {
            discharge = SensorReading.Valid(rate / 1000.0, Units.Watt, $"{Description}:DischargeRate", now);
        }

        SensorReading charge;
        if (sample.Error is not null)
        {
            charge = SensorReading.Unavailable(Units.Watt, Description, sample.Error);
        }
        else if (sample.Charging != true)
        {
            charge = SensorReading.Unavailable(
                Units.Watt,
                Description,
                sample.PowerOnline == true ? "电池当前未充电（已充满或未接交流电）。" : "电池当前未充电。");
        }
        else if (sample.ChargeRateMw is not { } rate)
        {
            charge = SensorReading.Unavailable(Units.Watt, Description, "系统未提供充电速率。");
        }
        else
        {
            charge = SensorReading.Valid(rate / 1000.0, Units.Watt, $"{Description}:ChargeRate", now);
        }

        return Task.FromResult<IReadOnlyList<ExtendedSensor>>(new[]
        {
            new ExtendedSensor(DischargeSensorKey, "电池放电功率", discharge),
            new ExtendedSensor(ChargeSensorKey, "电池充电功率", charge)
        });
    }

    private BatterySample Sample()
    {
        bool? powerOnline = null;
        bool? charging = null;
        bool? discharging = null;
        long? dischargeRate = null;
        long? chargeRate = null;
        var seen = false;

        WmiRead.Query(
            _namespacePath,
            "SELECT PowerOnline, Charging, Discharging, DischargeRate, ChargeRate FROM BatteryStatus",
            item =>
            {
                if (seen)
                {
                    return;
                }

                seen = true;
                powerOnline = WmiRead.Bool(item, "PowerOnline");
                charging = WmiRead.Bool(item, "Charging");
                discharging = WmiRead.Bool(item, "Discharging");
                dischargeRate = WmiRead.Integer(item, "DischargeRate");
                chargeRate = WmiRead.Integer(item, "ChargeRate");
            },
            out var error);

        if (error is not null)
        {
            return new BatterySample(null, null, null, null, null, error);
        }

        if (!seen)
        {
            return new BatterySample(null, null, null, null, null, "本机未枚举到 BatteryStatus 实例（可能无电池）。");
        }

        return new BatterySample(powerOnline, charging, discharging, dischargeRate, chargeRate, null);
    }

    private sealed record BatterySample(
        bool? PowerOnline,
        bool? Charging,
        bool? Discharging,
        long? DischargeRateMw,
        long? ChargeRateMw,
        string? Error);
}
