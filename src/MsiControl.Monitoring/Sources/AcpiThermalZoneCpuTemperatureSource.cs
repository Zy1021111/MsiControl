using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Internal;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 通用 ACPI 热区温度来源（CPU 温度的兜底数据源）。
/// </summary>
/// <remarks>
/// <para>
/// 读取 Windows 标准 ACPI 热区类 <c>MSAcpi_ThermalZoneTemperature</c>（非 MSI 私有）。
/// 本机已确证该字段返回有效数据（证据清单 §5，2026-10-16）。
/// </para>
/// <para>
/// <b>重要限定：</b>该值代表 ACPI 热区，<b>不是 CPU 封装温度</b>。本机仅有 1 个热区、
/// 粒度粗、刷新慢，因此只能兜底，不能替代 CPU/GPU 专用温度源（任务书 §6.4、证据清单 §5）。
/// 来源字符串里明确写入了这一点，界面据此可如实展示。
/// </para>
/// <para>
/// <b>单位：</b>原始值为「十分之一开尔文」，换算一律走
/// <see cref="Measurement.DeciKelvinToCelsius"/>，禁止自行 <c>/10 - 273.15</c>。
/// </para>
/// </remarks>
public sealed class AcpiThermalZoneCpuTemperatureSource : ICpuTemperatureSource
{
    /// <summary>WMI 类名。</summary>
    public const string ClassName = "MSAcpi_ThermalZoneTemperature";

    /// <summary>摄氏度合理区间下限。低于此值视为无效读数。</summary>
    public const double MinPlausibleCelsius = -20.0;

    /// <summary>摄氏度合理区间上限。高于此值视为无效读数。</summary>
    public const double MaxPlausibleCelsius = 150.0;

    private readonly string _namespacePath;

    /// <summary>构造数据源。</summary>
    /// <param name="namespacePath">WMI 命名空间，默认 <c>root\WMI</c>。</param>
    public AcpiThermalZoneCpuTemperatureSource(string namespacePath = WmiRead.WmiNamespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePath);
        _namespacePath = namespacePath;
    }

    /// <inheritdoc />
    public string Key => "cpu.temperature.thermalzone";

    /// <inheritdoc />
    public string Description => "ACPI 热区（兜底，非 CPU 封装温度）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.ThermalFallback;

    /// <summary>最近一次读取到的热区实例名，供诊断追溯。</summary>
    public string? LastInstanceName { get; private set; }

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    private SensorReading Read()
    {
        string? instanceName = null;
        double? celsius = null;
        string? firstProblem = null;

        var found = WmiRead.Query(
            _namespacePath,
            $"SELECT InstanceName, Active, CurrentTemperature FROM {ClassName}",
            item =>
            {
                var name = WmiRead.String(item, "InstanceName");
                var active = WmiRead.Bool(item, "Active");
                var raw = WmiRead.Double(item, "CurrentTemperature");

                if (raw is null || raw <= 0)
                {
                    firstProblem ??= $"{name ?? "(未知实例)"} 的 CurrentTemperature 为空或非正值。";
                    return;
                }

                var converted = Measurement.DeciKelvinToCelsius(raw.Value);
                if (converted is < MinPlausibleCelsius or > MaxPlausibleCelsius)
                {
                    firstProblem ??= $"{name ?? "(未知实例)"} 换算结果为 {converted:0.##} °C，超出合理区间。";
                    return;
                }

                // 优先取 Active 实例；若首个有效实例未激活，仍记录但继续找激活的那个。
                if (celsius is null || active == true)
                {
                    celsius = converted;
                    instanceName = name;
                }
            },
            out var error);

        if (celsius.HasValue)
        {
            LastInstanceName = instanceName;
            return SensorReading.Valid(
                celsius.Value,
                Units.Celsius,
                $"{ClassName}（{instanceName ?? "未知热区"}，ACPI 热区，非 CPU 封装温度）",
                DateTimeOffset.Now);
        }

        LastInstanceName = null;

        if (error is not null)
        {
            return SensorReading.Unavailable(Units.Celsius, ClassName, $"读取 ACPI 热区失败：{error}");
        }

        if (found == 0)
        {
            return SensorReading.Unavailable(Units.Celsius, ClassName, "本机未枚举到任何 ACPI 热区实例。");
        }

        return SensorReading.Unavailable(
            Units.Celsius,
            ClassName,
            firstProblem ?? "ACPI 热区存在，但所有实例都未返回有效温度。");
    }
}
