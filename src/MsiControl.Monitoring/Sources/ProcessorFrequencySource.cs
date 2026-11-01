using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Pdh;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// CPU 统一有效频率来源（基于 PDH 处理器性能计数器）。
/// </summary>
/// <remarks>
/// <para>
/// 口径按任务书 §6.5 与接口约定 §2.2 明确定为
/// <see cref="FrequencyAggregation.AverageActive"/>：**所有活跃核心的有效频率均值**。
/// 每个核心的有效频率 = <c>Processor Frequency（标称 MHz）× % Processor Performance / 100</c>。
/// 不显示基频、不拆分 P/E 核。
/// </para>
/// <para>
/// 「活跃」的判定：<c>% Processor Time &gt;= 1</c> 且有正的有效频率。
/// 全部核心都低于该阈值时（整机接近空闲），退化为**所有核心有效频率的均值**，
/// 此时数值代表空闲时钟而非负载时钟——这一点写进了读数的来源说明。
/// </para>
/// <para>
/// 该数据源读取的是操作系统标准计数器（PDH），不是厂商私有能力，故不参与能力门控。
/// </para>
/// </remarks>
public sealed class ProcessorFrequencySource : ICpuFrequencySource
{
    /// <summary>判定核心「活跃」的利用率阈值（百分比）。</summary>
    public const double ActiveUtilityThreshold = 1.0;

    /// <summary>可接受的有效频率下限（MHz）。低于此值视为读数异常。</summary>
    public const double MinPlausibleMhz = 100.0;

    /// <summary>可接受的有效频率上限（MHz）。高于此值视为读数异常。</summary>
    public const double MaxPlausibleMhz = 8000.0;

    private readonly PdhProcessorSampler _sampler;

    /// <summary>构造数据源。</summary>
    /// <param name="sampler">共享的处理器性能计数采样器。</param>
    public ProcessorFrequencySource(PdhProcessorSampler sampler)
        => _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));

    /// <inheritdoc />
    public string Key => "cpu.frequency.pdh";

    /// <inheritdoc />
    public string Description => "处理器性能计数器（活跃核心有效频率均值）";

    /// <inheritdoc />
    public FrequencyAggregation Basis => FrequencyAggregation.AverageActive;

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read());
    }

    private SensorReading Read()
    {
        var snapshot = _sampler.Sample();

        if (!snapshot.HasData)
        {
            var reason = snapshot.Error ?? "处理器性能计数器未返回任何实例。";
            return SensorReading.Unavailable(
                Units.Megahertz,
                PdhProcessorSampler.SourceName,
                $"无法读取处理器性能计数器：{reason}");
        }

        var cores = snapshot.Samples.Where(s => s.IsCore).ToArray();
        var active = cores
            .Where(s => s.PercentUtility >= ActiveUtilityThreshold && s.EffectiveMhz > 0)
            .ToArray();

        var basis = active.Length > 0 ? active : cores.Where(s => s.EffectiveMhz > 0).ToArray();
        var basisNote = active.Length > 0 ? "活跃核心" : "全部核心（整机接近空闲）";

        if (basis.Length == 0)
        {
            var total = snapshot.Samples.FirstOrDefault(s => s.IsTotal);
            if (total is null || total.EffectiveMhz <= 0)
            {
                return SensorReading.Unavailable(
                    Units.Megahertz,
                    PdhProcessorSampler.SourceName,
                    "处理器性能计数器实例存在，但未得到可用的有效频率。");
            }

            return Judge(total.EffectiveMhz, total.Name, "总计实例");
        }

        var mean = basis.Average(s => s.EffectiveMhz);
        return Judge(mean, basis.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), basisNote);
    }

    private SensorReading Judge(double mhz, string instanceInfo, string basisNote)
    {
        if (mhz is < MinPlausibleMhz or > MaxPlausibleMhz)
        {
            return SensorReading.Unavailable(
                Units.Megahertz,
                PdhProcessorSampler.SourceName,
                $"有效频率 {mhz:0.#} MHz 超出合理区间 {MinPlausibleMhz:0}–{MaxPlausibleMhz:0} MHz，已按不可用处理。");
        }

        return SensorReading.Valid(
            mhz,
            Units.Megahertz,
            $"{PdhProcessorSampler.SourceName}（{basisNote}均值，{instanceInfo}）",
            DateTimeOffset.Now);
    }
}

/// <summary>
/// CPU 利用率来源（扩展传感器）。
/// </summary>
/// <remarks>
/// 与 <see cref="ProcessorFrequencySource"/> 共用同一份 PDH 采样器；PDH 读取为亚毫秒级，
/// 各自采样也不会造成可感知的开销。
/// </remarks>
public sealed class ProcessorUtilizationSource : IExtendedSensorSource
{
    private readonly PdhProcessorSampler _sampler;

    /// <summary>构造数据源。</summary>
    /// <param name="sampler">共享的处理器性能计数采样器。</param>
    public ProcessorUtilizationSource(PdhProcessorSampler sampler)
        => _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));

    /// <inheritdoc />
    public string Key => "cpu.utilization.pdh";

    /// <inheritdoc />
    public string Description => "处理器性能计数器";

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtendedSensor>> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = _sampler.Sample();
        var total = snapshot.Samples.FirstOrDefault(s => string.Equals(s.Name, "_Total", StringComparison.Ordinal))
                    ?? snapshot.Samples.FirstOrDefault(s => s.IsTotal);

        SensorReading reading;
        if (total is null)
        {
            reading = SensorReading.Unavailable(
                Units.Percent,
                PdhProcessorSampler.SourceName,
                snapshot.Error ?? "处理器性能计数器未返回 _Total 实例。");
        }
        else
        {
            reading = SensorReading.Valid(
                Measurement.Clamp(total.PercentUtility, 0.0, 100.0),
                Units.Percent,
                $"{PdhProcessorSampler.SourceName}（_Total.% Processor Time）",
                DateTimeOffset.Now);
        }

        return Task.FromResult<IReadOnlyList<ExtendedSensor>>(
            new[] { new ExtendedSensor("cpu.utilization", "CPU 利用率", reading) });
    }
}
