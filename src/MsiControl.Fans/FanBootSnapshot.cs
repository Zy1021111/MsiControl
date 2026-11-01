namespace MsiControl.Fans;

/// <summary>
/// 本次启动的「首次写入前快照」。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §7.4：应用曲线前必须**先读有效快照**；「恢复启动曲线」恢复到本次启动后
/// 首次写入前的快照，**不是** BIOS 出厂重置。任务书 §7.4 同时要求提供
/// 「切回固件自动策略」入口——两者是不同能力，本类型只负责前者。
/// </para>
/// <para>
/// <b>只在进程内存中保留，不落盘。</b>原因：跨重启保留一份「上次的快照」会造成
/// 语义漂移——用户以为恢复到出厂，实际恢复的是几天前某个会话的状态。
/// 进程重启后没有快照，界面必须如实显示「本次启动尚未取得快照」。
/// </para>
/// </remarks>
public sealed class FanBootSnapshotStore
{
    /// <summary>给用户看的语义说明，避免与 BIOS 出厂重置混淆。</summary>
    public const string RestoreSemanticsNotice =
        "「恢复启动曲线」恢复的是本次启动后、首次写入前读到的快照；它不是 BIOS 出厂重置，"
        + "也不会清除固件自身的默认策略。";

    private FanCurveSet? _snapshot;

    /// <summary>已捕获的快照。未捕获时为 <see langword="null"/>。</summary>
    public FanCurveSet? Snapshot => _snapshot;

    /// <summary>捕获时刻。</summary>
    public DateTimeOffset? CapturedAt { get; private set; }

    /// <summary>是否已取得快照。</summary>
    public bool HasSnapshot => _snapshot is not null;

    /// <summary>最近一次捕获失败的原因。成功时为 <see langword="null"/>。</summary>
    public string? LastCaptureFailure { get; private set; }

    /// <summary>
    /// 尝试用一次硬件读取结果捕获快照。
    /// </summary>
    /// <param name="read">硬件读取结果。</param>
    /// <returns>捕获成功返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>只捕获可信的读取结果。</b><see cref="FanReadStatus.TransportOnly"/> 与
    /// <see cref="FanReadStatus.Unavailable"/> 都会被拒绝——用一段无法解读的返回值
    /// 充当「快照」，会让「恢复」变成一个凭空下发的动作。
    /// </para>
    /// <para>
    /// <b>已捕获后不再覆盖。</b>快照语义是「首次写入之前」，第二次读取会返回写入后的状态，
    /// 覆盖它等于把还原点搬到了修改之后。
    /// </para>
    /// </remarks>
    public bool TryCapture(FanReadResult read)
    {
        if (read is null)
        {
            LastCaptureFailure = "未提供硬件读取结果。";
            return false;
        }

        if (HasSnapshot)
        {
            return true;
        }

        if (read.Status != FanReadStatus.Verified)
        {
            LastCaptureFailure =
                $"读取结果不可信（{read.Status}），不能作为快照：{read.Message}";
            return false;
        }

        if (read.State.CpuCurve is null || read.State.GpuCurve is null)
        {
            LastCaptureFailure =
                "读取结果缺少双通道曲线，无法作为快照：自动模式下曲线为空是正常的，"
                + "但此时也没有可恢复的自定义曲线。";
            return false;
        }

        _snapshot = new FanCurveSet(read.State.CpuCurve, read.State.GpuCurve);
        CapturedAt = read.SampledAt;
        LastCaptureFailure = null;
        return true;
    }

    /// <summary>清除快照（例如用户显式放弃还原点）。</summary>
    public void Clear()
    {
        _snapshot = null;
        CapturedAt = null;
        LastCaptureFailure = null;
    }

    /// <summary>描述当前还原点状态。</summary>
    /// <remarks>两个分支都带上语义说明：没有快照时最容易被误读为「没有出厂重置能力」。</remarks>
    public string Describe()
        => HasSnapshot
            ? $"本次启动已取得首次写入前快照（{CapturedAt:yyyy-MM-dd HH:mm:ss}）："
              + $"{FanCurveComparison.Describe(_snapshot)}。{RestoreSemanticsNotice}"
            : $"本次启动尚未取得可用快照：{LastCaptureFailure ?? "尚未读取硬件状态。"} {RestoreSemanticsNotice}";

    /// <summary>
    /// 返回不能恢复的原因；可以恢复时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>附带语义说明，避免把「恢复启动曲线」误解为 BIOS 出厂重置。</remarks>
    public string? DescribeRestoreBlocker()
        => HasSnapshot
            ? null
            : $"没有可恢复的启动快照：{LastCaptureFailure ?? "尚未读取硬件状态。"} {RestoreSemanticsNotice}";
}
