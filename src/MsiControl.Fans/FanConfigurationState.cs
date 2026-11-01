using MsiControl.Core;

namespace MsiControl.Fans;

/// <summary>
/// 风扇配置的**四态**模型。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §7.4 要求明确区分四件事，本类型是这四件事的唯一落点：
/// </para>
/// <list type="number">
/// <item><see cref="Editing"/> —— 编辑中（可能尚未保存）。</item>
/// <item><see cref="Saved"/> —— 本地已保存。</item>
/// <item><see cref="LastApplied"/> —— 最近一次**成功应用并被读回确认**的配置。</item>
/// <item><see cref="Hardware"/> —— 当前从硬件读到的状态（可能与前三者都不同）。</item>
/// </list>
/// <para>
/// 界面**不得**用其中任何一个冒充另一个。特别是：把 <see cref="Saved"/> 显示成
/// 「当前生效」会在写入失败时直接说谎（任务书 §1 原则 6）。
/// </para>
/// <para>
/// 「恢复启动曲线」恢复的是本次启动后、<b>首次写入前</b>读到的快照，
/// <b>不是</b> BIOS 出厂重置——见 <see cref="FanBootSnapshotStore"/>。
/// </para>
/// </remarks>
/// <param name="Editing">编辑中的配置。</param>
/// <param name="Saved">本地保存的配置。</param>
/// <param name="LastApplied">最近成功应用的记录（含模式与确认依据）。</param>
/// <param name="Hardware">当前读到的硬件状态。</param>
public sealed record FanConfigurationState(
    FanCurveSet? Editing,
    FanCurveSet? Saved,
    FanAppliedRecord? LastApplied,
    FanReadResult? Hardware)
{
    /// <summary>全部四态均为空的状态。</summary>
    public static FanConfigurationState Empty { get; } = new(null, null, null, null);

    /// <summary>编辑中的配置是否与本地保存的配置不同。</summary>
    public bool HasUnsavedChanges => !FanCurveComparison.AreEquivalent(Editing, Saved);

    /// <summary>
    /// 本地保存的配置是否与最近成功应用的配置不同。
    /// </summary>
    /// <remarks>最近成功应用的是固件自动策略时，任何非空的本地曲线都算「尚未应用」。</remarks>
    public bool HasUnappliedChanges
        => Saved is not null
           && (LastApplied is null
               || LastApplied.Mode != FanControlMode.CustomCurve
               || !FanCurveComparison.AreEquivalent(Saved, LastApplied.Curves));

    /// <summary>给界面用的四态摘要，顺序固定，便于逐行展示。</summary>
    public IReadOnlyList<string> Describe()
    {
        var lines = new List<string>(4)
        {
            Editing is null
                ? "1. 编辑中的配置：无"
                : $"1. 编辑中的配置：{FanCurveComparison.Describe(Editing)}",
            Saved is null
                ? "2. 本地保存的配置：无"
                : $"2. 本地保存的配置：{FanCurveComparison.Describe(Saved)}"
        };

        lines.Add(LastApplied switch
        {
            null => "3. 最近成功应用的配置：无（尚未成功应用过，或应用未通过读回核对）",
            { Mode: FanControlMode.Automatic } record =>
                $"3. 最近成功应用的配置：固件自动策略（{record.AppliedAt:yyyy-MM-dd HH:mm:ss}），"
                + $"确认依据：{record.ConfirmationBasis}",
            { Curves: not null } record =>
                $"3. 最近成功应用的配置：{FanCurveComparison.Describe(record.Curves)}"
                + $"（{record.AppliedAt:yyyy-MM-dd HH:mm:ss}），确认依据：{record.ConfirmationBasis}",
            { } record =>
                $"3. 最近成功应用的配置：模式 {record.Mode}（{record.AppliedAt:yyyy-MM-dd HH:mm:ss}），"
                + $"确认依据：{record.ConfirmationBasis}"
        });

        lines.Add(Hardware switch
        {
            null => "4. 当前硬件状态：未读取",
            { Status: FanReadStatus.Verified } read =>
                $"4. 当前硬件状态：模式 {read.State.Mode}，"
                + $"CPU 曲线 {(read.State.CpuCurve is null ? "无（自动模式）" : $"{read.State.CpuCurve.NodeCount} 节点")}，"
                + $"GPU 曲线 {(read.State.GpuCurve is null ? "无（自动模式）" : $"{read.State.GpuCurve.NodeCount} 节点")}",
            { Status: FanReadStatus.TransportOnly } read =>
                $"4. 当前硬件状态：未知（读取通道可达但语义未确证）——{read.Message}",
            { } read => $"4. 当前硬件状态：不可用——{read.Message}"
        });

        return lines;
    }
}

/// <summary>
/// 一次「成功应用」的记录。
/// </summary>
/// <param name="Mode">应用后的控制模式。</param>
/// <param name="Curves">应用后读回确认的曲线。自动模式下为 <see langword="null"/>。</param>
/// <param name="AppliedAt">应用时刻（读回确认通过的时刻）。</param>
/// <param name="ConfirmationBasis">确认依据。**不得为空**：没有依据就不叫成功。</param>
public sealed record FanAppliedRecord(
    FanControlMode Mode,
    FanCurveSet? Curves,
    DateTimeOffset AppliedAt,
    string ConfirmationBasis)
{
    /// <summary>确认依据是否缺失（违规状态）。</summary>
    public bool IsUnsubstantiated => string.IsNullOrWhiteSpace(ConfirmationBasis);
}
