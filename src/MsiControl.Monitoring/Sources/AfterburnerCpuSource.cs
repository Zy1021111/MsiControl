using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Afterburner;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 通过 MSI Afterburner 共享内存（MAHM）读取 CPU 封装功耗与 CPU 温度的数据源。
/// </summary>
/// <remarks>
/// <para>
/// <b>补上 CPU 封装功耗</b>：MAHM 新版数据源 <see cref="MonitoringSourceIds.CpuPower"/>（0x100）
/// 提供 CPU 功耗（瓦），本机 SDK（2026-10-17 实读）已包含。此前 CPU 封装功耗因
/// <c>MSI_ACPI.Get_Power</c> 编码未验证而不可用——本来源是当前的可行补位路径，
/// 前提是 Afterburner 运行且「CPU power」监控项已启用。
/// </para>
/// <para>
/// <b>口径纪律（任务书 §6.4）：</b>CPU 功耗与 GPU 功耗是两个独立读数，
/// 绝不相加、绝不冒充整机功耗（<c>power.system</c> 仍按能力门控恒为「不可用」）。
/// </para>
/// <para>
/// <b>不参与能力门控：</b>MAHM 是操作系统级第三方标准接口，不是厂商私有能力；
/// 其可用性由读取结果本身表达（未运行/未启用即「不可用」并说明）。
/// </para>
/// </remarks>
public sealed class AfterburnerCpuSource : ICpuPowerSource, ICpuTemperatureSource
{
    private readonly IMahmReader _reader;

    /// <summary>构造数据源。</summary>
    /// <param name="reader">MAHM 读取器。</param>
    public AfterburnerCpuSource(IMahmReader reader)
        => _reader = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <inheritdoc />
    public string Key => "cpu.power.afterburner";

    /// <inheritdoc />
    public string Description => "MSI Afterburner 共享内存（MAHM）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.ThermalRead;

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadPower());
    }

    /// <inheritdoc />
    Task<SensorReading> ICpuTemperatureSource.ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // CPU 类条目取全局汇总（0xFFFFFFFF = "CPU temperature" 总值）；逐核条目不是想要的口径。
        if (!_reader.TryRead(
                MonitoringSourceIds.CpuTemperature,
                MonitoringSourceIds.GlobalGpuIndex,
                out var celsius,
                out _,
                out _,
                out var error))
        {
            return Task.FromResult(SensorReading.Unavailable(
                Units.Celsius,
                Description,
                $"{error}（需在 Afterburner「设置→监控」中启用 CPU temperature 项。）"));
        }

        return Task.FromResult(
            celsius is < -20 or > 150
                ? SensorReading.Unavailable(
                    Units.Celsius,
                    Description,
                    $"Afterburner 报告 CPU 温度 {celsius:0.#} °C 超出合理区间，已按不可用处理。")
                : SensorReading.Valid(celsius, Units.Celsius, $"{Description}:CPU_TEMPERATURE(0x80)", DateTimeOffset.Now));
    }

    private SensorReading ReadPower()
    {
        // CPU 功耗取全局汇总条目（0x100，单位瓦）。
        if (!_reader.TryRead(
                MonitoringSourceIds.CpuPower,
                MonitoringSourceIds.GlobalGpuIndex,
                out var watt,
                out _,
                out _,
                out var error))
        {
            return SensorReading.Unavailable(
                Units.Watt,
                Description,
                $"{error}（需在 Afterburner「设置→监控」中启用 CPU power 项。）");
        }

        return watt is < 0 or > 300
            ? SensorReading.Unavailable(
                Units.Watt,
                Description,
                $"Afterburner 报告 CPU 功耗 {watt:0.#} W 超出合理区间，已按不可用处理。")
            : SensorReading.Valid(watt, Units.Watt, $"{Description}:CPU_POWER(0x100)", DateTimeOffset.Now);
    }
}
