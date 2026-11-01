using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Afterburner;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 通过 MSI Afterburner 共享内存（MAHM）读取 GPU 读数的数据源。
/// </summary>
/// <remarks>
/// <para>
/// 这是 <see cref="NvmlGpuSource"/> 的**可替换来源**：两者都实现 <see cref="IGpuSource"/> 与
/// <see cref="IExtendedSensorSource"/>，由 <see cref="CompositeGpuSource"/> 按偏好排序。
/// Afterburner 未安装/未运行/未启用监控项时，本数据源如实返回「不可用」并说明原因（任务书 §6.4 不造假）。
/// </para>
/// <para>
/// <b>能力边界（按本机 SDK 头文件 2026-10-17 实读核对）：</b>
/// </para>
/// <list type="bullet">
/// <item>GPU 功耗：<see cref="MonitoringSourceIds.GpuAbsolutePower"/>（0x61）<b>可读</b>（本机 SDK 为新版本）。</item>
/// <item>限制项：GPU 功耗上限（0x71）与温度上限（0x70）可读。</item>
/// <item>风扇转速：百分比（0x10）与 RPM（0x11）；百分比不是校准后百分比，
///       不得写入 <see cref="FanReading.ActualPercent"/>。</item>
/// <item>独显休眠判定：本来源无法探测 PnP 电源状态，由回落链与其他来源协同。</item>
/// </list>
/// <para>
/// <b>兼容性风险：</b>MAHM 是闭源私有内存布局；本实现按本机安装的 SDK 头文件核对
/// （签名 <c>'MAHM'</c>、x64 下 4 字节 time），并校验条目大小，不符即降级「不可用」。
/// </para>
/// </remarks>
public sealed class AfterburnerGpuSource : IGpuSource, IExtendedSensorSource
{
    private readonly IMahmReader _reader;

    /// <summary>构造数据源。</summary>
    /// <param name="reader">MAHM 读取器。</param>
    public AfterburnerGpuSource(IMahmReader reader)
        => _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <inheritdoc />
    public string Key => "gpu.afterburner";

    /// <inheritdoc />
    public string Description => "MSI Afterburner 共享内存（MAHM）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.ThermalRead;

    /// <inheritdoc />
    public Task<GpuSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.Now;
        var source = Description;

        var temperature = ReadCelsius(MonitoringSourceIds.GpuTemperature, now);
        var coreClock = ReadMegahertz(MonitoringSourceIds.CoreClock, now);

        SensorReading power;
        if (_reader.TryRead(MonitoringSourceIds.GpuAbsolutePower, null, out var watt, out _, out _, out var powerError))
        {
            power = watt is < 0 or > 500
                ? SensorReading.Unavailable(
                    Units.Watt,
                    source,
                    $"Afterburner 报告 GPU 功耗 {watt:0.#} W 超出合理区间，已按不可用处理。")
                : SensorReading.Valid(watt, Units.Watt, $"{Description}:GPU_ABS_POWER(0x61)", now);
        }
        else
        {
            power = SensorReading.Unavailable(
                Units.Watt,
                source,
                $"{powerError}（需在 Afterburner「设置→监控」中启用 GPU power 项。）");
        }

        FanReading fan;
        if (_reader.TryRead(MonitoringSourceIds.FanTachometer, null, out var rpm, out _, out _, out var rpmError)
            && rpm > 0)
        {
            fan = FanReading.Valid((int)Math.Round(rpm), source, now);
        }
        else if (_reader.TryRead(MonitoringSourceIds.FanSpeed, null, out var percent, out _, out _, out _)
                 && percent >= 0)
        {
            // 百分比不是校准后百分比，按其语义不得写入 ActualPercent。
            fan = FanReading.Unavailable(
                source,
                $"Afterburner 仅提供风扇百分比 {percent:0.#}%（非校准后百分比，见扩展传感器「GPU 风扇」），不提供 RPM。");
        }
        else
        {
            fan = FanReading.Unavailable(
                source,
                rpmError ?? "Afterburner 未提供风扇读数（RPM 与百分比均不可用）。");
        }

        return Task.FromResult(new GpuSnapshot(temperature, coreClock, power, fan));
    }

    /// <inheritdoc />
    Task<IReadOnlyList<ExtendedSensor>> IExtendedSensorSource.ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTimeOffset.Now;
        var result = new List<ExtendedSensor>(8)
        {
            PercentSensor(MonitoringSourceIds.GpuUsage, "gpu.utilization", "GPU 利用率", now),

            // 显存占用：0x32（FB usage，百分比）。0x31 是 MB 绝对值，不能当百分比。
            PercentSensor(MonitoringSourceIds.FbUsage, "gpu.vram", "显存占用", now),
            new ExtendedSensor(
                "gpu.memclock",
                "显存频率",
                ReadMegahertz(MonitoringSourceIds.MemoryClock, now))
        };

        // GPU 风扇：优先 RPM，其次百分比。
        if (_reader.TryRead(MonitoringSourceIds.FanTachometer, null, out var rpm, out _, out _, out _) && rpm > 0)
        {
            result.Add(new ExtendedSensor(
                "gpu.fan",
                "GPU 风扇（Afterburner RPM）",
                SensorReading.Valid(rpm, Units.Rpm, Description, now)));
        }
        else if (_reader.TryRead(MonitoringSourceIds.FanSpeed, null, out var percent, out _, out _, out _) && percent >= 0)
        {
            result.Add(new ExtendedSensor(
                "gpu.fan",
                "GPU 风扇（Afterburner 占空比）",
                SensorReading.Valid(Measurement.Clamp(percent, 0.0, 100.0), Units.Percent, Description, now)));
        }
        else
        {
            result.Add(new ExtendedSensor(
                "gpu.fan",
                "GPU 风扇（Afterburner）",
                SensorReading.Unavailable(Units.Rpm, Description, "Afterburner 未提供风扇读数。")));
        }

        // 限制项（本机 SDK 新增数据源）。
        if (_reader.TryRead(MonitoringSourceIds.GpuPowerLimit, null, out var powerLimit, out _, out _, out _))
        {
            result.Add(new ExtendedSensor(
                "gpu.powerlimit",
                "GPU 功耗限制",
                SensorReading.Valid(powerLimit, Units.Watt, $"{Description}:GPU_POWER_LIMIT(0x71)", now)));
        }
        else
        {
            result.Add(new ExtendedSensor(
                "gpu.powerlimit",
                "GPU 功耗限制",
                SensorReading.Unavailable(Units.Watt, Description, "Afterburner 未提供该监控项。")));
        }

        if (_reader.TryRead(MonitoringSourceIds.GpuTempLimit, null, out var tempLimit, out _, out _, out _))
        {
            result.Add(new ExtendedSensor(
                "gpu.temp.limit",
                "GPU 降频温度阈值",
                SensorReading.Valid(tempLimit, Units.Celsius, $"{Description}:GPU_TEMP_LIMIT(0x70)", now)));
        }
        else
        {
            result.Add(new ExtendedSensor(
                "gpu.temp.limit",
                "GPU 降频温度阈值",
                SensorReading.Unavailable(Units.Celsius, Description, "Afterburner 未提供该监控项。")));
        }

        return Task.FromResult<IReadOnlyList<ExtendedSensor>>(result);
    }

    private SensorReading ReadCelsius(uint sourceId, DateTimeOffset now)
    {
        if (!_reader.TryRead(sourceId, null, out var value, out _, out _, out var error))
        {
            return SensorReading.Unavailable(Units.Celsius, Description, error);
        }

        return value is < -20 or > 150
            ? SensorReading.Unavailable(
                Units.Celsius,
                Description,
                $"Afterburner 报告温度 {value:0.#} °C 超出合理区间，已按不可用处理。")
            : SensorReading.Valid(value, Units.Celsius, $"{Description}:{sourceId:X8}", now);
    }

    private SensorReading ReadMegahertz(uint sourceId, DateTimeOffset now)
    {
        if (!_reader.TryRead(sourceId, null, out var value, out _, out _, out var error))
        {
            return SensorReading.Unavailable(Units.Megahertz, Description, error);
        }

        return value is <= 0 or > 6000
            ? SensorReading.Unavailable(
                Units.Megahertz,
                Description,
                $"Afterburner 报告频率 {value:0.#} MHz 超出合理区间，已按不可用处理。")
            : SensorReading.Valid(value, Units.Megahertz, $"{Description}:{sourceId:X8}", now);
    }

    private ExtendedSensor PercentSensor(uint sourceId, string key, string displayName, DateTimeOffset now)
    {
        if (!_reader.TryRead(sourceId, null, out var value, out _, out _, out var error))
        {
            return new ExtendedSensor(
                key,
                displayName,
                SensorReading.Unavailable(Units.Percent, Description, error));
        }

        return new ExtendedSensor(
            key,
            displayName,
            SensorReading.Valid(
                Measurement.Clamp(value, 0.0, 100.0),
                Units.Percent,
                $"{Description}:{sourceId:X8}",
                now));
    }
}
