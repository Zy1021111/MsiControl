using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// CPU 封装功耗的多源回落链：按顺序询问，取第一个可用读数。
/// </summary>
/// <remarks>
/// <para>
/// <b>顺序即优先级：</b>Intel RAPL（平台计数器，无需第三方常驻、无需用户配置）优先，
/// MSI Afterburner 共享内存作为兜底（仅在 RAPL 不可用且 Afterburner 正在运行并启用
/// CPU power 监控项时才有值）。
/// </para>
/// <para>
/// <b>失败原因不隐藏：</b>全部来源都不可用时，返回的读数 <see cref="SensorReading.Detail"/>
/// 会按顺序拼接每一个来源的原因，界面悬浮提示可据此定位到底是 RAPL 还是 Afterburner 的问题；
/// 绝不回退成「0 W」或沿用上一个有效值。
/// </para>
/// <para>
/// <b>键唯一性：</b>作为复合源的 <see cref="Key"/> 是自己的一格（诊断按它退避计数），
/// 因此各子来源不再单独出现在诊断列表中——原因文本已包含在 <see cref="SensorReading.Detail"/> 中。
/// </para>
/// </remarks>
public sealed class CompositeCpuPowerSource : ICpuPowerSource
{
    private readonly IReadOnlyList<ICpuPowerSource> _sources;

    /// <summary>构造回落链。</summary>
    /// <param name="sources">按优先级排列的数据源（至少一个）。</param>
    public CompositeCpuPowerSource(params ICpuPowerSource[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Length == 0)
        {
            throw new ArgumentException("回落链至少需要一个数据源。", nameof(sources));
        }

        _sources = sources;
    }

    /// <inheritdoc />
    public string Key => "cpu.power.chain";

    /// <inheritdoc />
    public string Description => string.Join(" → ", _sources.Select(source => source.Description));

    /// <inheritdoc />
    public async Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        var reasons = new List<string>();

        foreach (var source in _sources)
        {
            var reading = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (reading.IsUsable)
            {
                return reading;
            }

            reasons.Add(string.IsNullOrWhiteSpace(reading.Detail)
                ? $"{source.Description}：本次不可用。"
                : reading.Detail!);
        }

        return SensorReading.Unavailable(
            Units.Watt,
            Description,
            string.Join(" ", reasons));
    }
}
