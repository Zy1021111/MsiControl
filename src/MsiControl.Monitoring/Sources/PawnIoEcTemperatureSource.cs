using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// CPU 温度来源：读取 MS-15M3 EC 的 CPU 实时温度寄存器（PawnIO 通道）。
/// </summary>
/// <remarks>
/// <para>
/// EC 寄存器 <see cref="Ms15M3EcRegisterMap.CpuTemperature"/>（0x68）是<b>真实 CPU 温度</b>，
/// 精度与刷新率都优于通用 ACPI 热区（后者粒度粗、刷新慢，只能兜底）。
/// 因此装配时本来源排在热区兜底<b>之前</b>；EC 通道不可用（未提权/未装驱动）时自动回落热区。
/// </para>
/// <para>
/// 同时以扩展传感器形式提供 GPU 温度（0x80），与 NVML 读数交叉核对。
/// </para>
/// </remarks>
public sealed class PawnIoEcTemperatureSource : ICpuTemperatureSource, IExtendedSensorSource
{
    /// <summary>摄氏度合理区间上限。EC 字节值超出即视为读数异常。</summary>
    public const double MaxPlausibleCelsius = 120.0;

    private readonly IEcRegisterAccess _access;

    /// <summary>构造数据源。</summary>
    /// <param name="access">EC 寄存器只读访问。</param>
    public PawnIoEcTemperatureSource(IEcRegisterAccess access)
        => _access = access ?? throw new ArgumentNullException(nameof(access));

    /// <inheritdoc />
    public string Key => "cpu.temperature.ec";

    /// <inheritdoc />
    public string Description => "EC 实时温度（PawnIO）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.ThermalRead;

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadCelsius(Ms15M3EcRegisterMap.CpuTemperature));
    }

    /// <inheritdoc />
    Task<IReadOnlyList<ExtendedSensor>> IExtendedSensorSource.ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<ExtendedSensor>>(
            new[]
            {
                new ExtendedSensor(
                    "gpu.temperature.ec",
                    "GPU 温度（EC 交叉核对）",
                    ReadCelsius(Ms15M3EcRegisterMap.GpuTemperature))
            });
    }

    private SensorReading ReadCelsius(Ms15M3EcRegisterMap.EcRegister register)
    {
        var error = _access.AvailabilityError;
        if (error is not null)
        {
            return SensorReading.Unavailable(Units.Celsius, Description, error);
        }

        byte raw;
        try
        {
            raw = _access.ReadRegister(register.Address);
        }
        catch (Exception ex)
        {
            return SensorReading.Unavailable(
                Units.Celsius,
                Description,
                $"读取 {register.Description}（0x{register.Address:X2}）失败：{ex.Message}");
        }

        if (raw is 0 or > (byte)MaxPlausibleCelsius)
        {
            return SensorReading.Unavailable(
                Units.Celsius,
                Description,
                $"EC 报告{register.Description}为 {raw}，超出 1–{MaxPlausibleCelsius:0} 合理区间，已按不可用处理。");
        }

        return SensorReading.Valid(
            raw,
            Units.Celsius,
            $"{Description}:0x{register.Address:X2}",
            DateTimeOffset.Now);
    }
}
