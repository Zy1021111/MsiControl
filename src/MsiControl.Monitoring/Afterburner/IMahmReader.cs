namespace MsiControl.Monitoring.Afterburner;

/// <summary>
/// MAHM 共享内存读取抽象（供数据源使用，可测试替身替换）。
/// </summary>
/// <remarks>
/// 抽象的本意是消除环境依赖：单元测试可注入可编程替身，
/// 不再依赖「本机是否安装/运行 Afterburner」这一外部状态。
/// </remarks>
public interface IMahmReader
{
    /// <summary>
    /// 是否检测到 Afterburner 共享内存存在。
    /// </summary>
    /// <param name="reason">不可用原因；可用时为 <see langword="null"/>。</param>
    bool IsAvailable(out string? reason);

    /// <summary>
    /// 读取一条指定 ID 的监控读数。
    /// </summary>
    /// <param name="sourceId">数据源 ID（<see cref="MonitoringSourceIds"/>）。</param>
    /// <param name="gpuIndex">
    /// 逻辑 GPU 索引；传 <see cref="MonitoringSourceIds.GlobalGpuIndex"/> 取全局汇总条目
    /// （CPU 类），传 <see langword="null"/> 取第一个 GPU 专属条目。
    /// </param>
    /// <param name="value">成功时为读数值。</param>
    /// <param name="sourceName">数据源名。</param>
    /// <param name="units">单位。</param>
    /// <param name="error">失败原因。</param>
    bool TryRead(
        uint sourceId,
        uint? gpuIndex,
        out float value,
        out string? sourceName,
        out string? units,
        out string? error);

    /// <summary>
    /// 枚举共享内存中的全部监控条目（诊断用）。
    /// </summary>
    bool TryReadAll(out IReadOnlyList<MahmEntrySnapshot> entries, out string? error);
}
