using MsiControl.Core.Models;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 独显来源的有序回落链：按偏好顺序尝试多个来源，取第一个读数可用者。
/// </summary>
/// <remarks>
/// <para>
/// 用途：把 NVML（原生、无需第三方常驻、数据最全）与 MSI Afterburner 共享内存
/// （MAHM，需 Afterburner 常驻）同时接入，按偏好排序。默认 NVML 优先、Afterburner 兜底；
/// 显式要求 Afterburner 优先时反向排序。Afterburner 未安装时本链自动落到 NVML，
/// 界面不受影响——这就是「接入但不破坏现有可用读数」的落点。
/// </para>
/// <para>
/// <b>回落规则（刻意保守）：</b>
/// </para>
/// <list type="bullet">
/// <item>来源返回<b>休眠</b>（独显已断电）→ 立即采纳。休眠是确定性的设备状态，
///       换来源也读不到，继续尝试只会徒增开销。</item>
/// <item>来源温度<b>可用</b> → 立即采纳。</item>
/// <item>来源<b>不可用</b>（驱动缺失、Afterburner 未运行等）→ 尝试下一个来源；
///       全部失败时把每条路径的原因合并呈现，界面能看到分别为什么不行。</item>
/// </list>
/// </remarks>
public sealed class CompositeGpuSource : IGpuSource, IExtendedSensorSource
{
    private readonly IReadOnlyList<IGpuSource> _sources;
    private readonly IReadOnlyList<IExtendedSensorSource> _extendedSources;

    /// <summary>构造回落链。</summary>
    /// <param name="sources">按偏好顺序排列的独显来源。每个来源须同时实现 <see cref="IExtendedSensorSource"/>。</param>
    public CompositeGpuSource(IReadOnlyList<IGpuSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("回落链至少需要一个独显来源。", nameof(sources));
        }

        _sources = sources;
        _extendedSources = sources
            .Select(source => source as IExtendedSensorSource)
            .ToList()!;
    }

    /// <inheritdoc />
    public string Key => "gpu.composite";

    /// <inheritdoc />
    public string Description => "独显（多来源回落链）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey
        => _sources.Select(source => source.RequiredCapabilityKey).FirstOrDefault(key => key is not null);

    /// <inheritdoc />
    public async Task<GpuSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        GpuSnapshot? last = null;
        var reasons = new List<string>();

        foreach (var source in _sources)
        {
            var snapshot = await source.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (snapshot.IsSleeping)
            {
                return snapshot;
            }

            if (snapshot.Temperature.IsUsable)
            {
                return snapshot;
            }

            reasons.Add($"{source.Description}：{snapshot.Temperature.Detail ?? "不可用"}");
            last = snapshot;
        }

        if (last is null || reasons.Count == 0)
        {
            return last ?? GpuSnapshot.Unavailable(Description, "回落链为空。");
        }

        return GpuSnapshot.Unavailable(Description, string.Join("；", reasons));
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<ExtendedSensor>> IExtendedSensorSource.ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ExtendedSensor>? firstUnavailable = null;

        foreach (var source in _extendedSources)
        {
            var entries = await source.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (entries.Any(entry => entry.Reading.IsUsable))
            {
                return entries;
            }

            firstUnavailable ??= entries;
        }

        return firstUnavailable ?? Array.Empty<ExtendedSensor>();
    }
}
