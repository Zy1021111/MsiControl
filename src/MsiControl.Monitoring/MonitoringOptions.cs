namespace MsiControl.Monitoring;

/// <summary>
/// 监控模块的运行参数。
/// </summary>
/// <remarks>
/// <para>
/// <b>本类型不含采样间隔。</b>采样间隔由桌面壳的 <c>SnapshotStream</c>（任务 02）持有并下发，
/// 整个应用只有一个采样循环；监控模块只负责「被调用一次就读一次」。
/// </para>
/// <para>
/// 这里的间隔类参数只用于**同一采样循环内部**降低慢速数据源的读取频率
/// （任务书 §20「扩展传感器按需采样」），不会产生任何独立的计时器或线程。
/// </para>
/// </remarks>
public sealed record MonitoringOptions
{
    /// <summary>默认参数。</summary>
    public static MonitoringOptions Default { get; } = new();

    /// <summary>默认的扩展传感器刷新间隔。数值取自本机实测的读取代价，见 docs/监控模块说明.md。</summary>
    public static readonly TimeSpan DefaultExtendedRefresh = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 单个数据源的超时。超时按「该读数本次不可用」处理，不阻塞整个采样周期。
    /// </summary>
    /// <remarks>
    /// WMI 调用本身不可中断，因此超时后只是**放弃等待**，不会杀死底层调用。
    /// 连续超时会触发退避（见 <see cref="FailureBackoffThreshold"/>），避免线程堆积。
    /// </remarks>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>独显在线性探测（PnP 枚举）的刷新间隔。</summary>
    public TimeSpan GpuPresenceProbeInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>连续失败多少次后进入退避。</summary>
    public int FailureBackoffThreshold { get; init; } = 3;

    /// <summary>退避时长。退避期间跳过该数据源，避免反复重试拖慢采样。</summary>
    public TimeSpan FailureBackoffDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 是否把通用 ACPI 热区（<c>MSAcpi_ThermalZoneTemperature</c>）作为 CPU 温度的兜底来源。
    /// </summary>
    /// <remarks>
    /// 关闭后 CPU 温度在精确来源不可用时返回「不可用」，而不是给出热区值。
    /// 默认开启：热区是本机唯一已确证返回数据的温度源（证据清单 §5）。
    /// </remarks>
    public bool UseThermalZoneFallback { get; init; } = true;

    /// <summary>
    /// 是否启用诊断采集：记录每个数据源的来源说明、最近成功时刻、耗时与错误。
    /// </summary>
    /// <remarks>对应任务书 §6.4「开启有效调试标志时记录数据来源与更新时间」。</remarks>
    public bool EnableDiagnostics { get; init; }

    /// <summary>校验参数组合。返回问题列表，为空表示合法。</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (SourceTimeout <= TimeSpan.Zero)
        {
            errors.Add("SourceTimeout 必须为正。");
        }

        if (GpuPresenceProbeInterval <= TimeSpan.Zero)
        {
            errors.Add("GpuPresenceProbeInterval 必须为正。");
        }

        if (FailureBackoffThreshold < 1)
        {
            errors.Add("FailureBackoffThreshold 至少为 1。");
        }

        if (FailureBackoffDuration < TimeSpan.Zero)
        {
            errors.Add("FailureBackoffDuration 不得为负。");
        }

        return errors;
    }
}
