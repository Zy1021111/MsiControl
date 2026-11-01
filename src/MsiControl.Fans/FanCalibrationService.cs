using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Fans;

/// <summary>校准流程的结局。</summary>
public enum FanCalibrationOutcome
{
    /// <summary>前置条件不满足，未开始。</summary>
    Blocked = 0,

    /// <summary>已开始（已进入满速并等待采样）。</summary>
    Started = 1,

    /// <summary>已完成并取得基准。</summary>
    Completed = 2,

    /// <summary>已由用户取消，并已尝试恢复原策略。</summary>
    Cancelled = 3,

    /// <summary>失败。</summary>
    Failed = 4
}

/// <summary>一次校准请求的结果。</summary>
/// <param name="Outcome">结局。</param>
/// <param name="Message">面向用户的说明。</param>
/// <param name="Session">会话。未开始时为 <see langword="null"/>。</param>
/// <param name="HardwareOperation">涉及的硬件操作结果。未下发时为 <see langword="null"/>。</param>
/// <param name="Baselines">取得的基准。未取得时为空集合。</param>
public sealed record FanCalibrationResult(
    FanCalibrationOutcome Outcome,
    string Message,
    FanCalibrationSession? Session = null,
    FanOperationResult? HardwareOperation = null,
    FanCalibrationBaselineSet? Baselines = null)
{
    /// <summary>是否被前置条件拦下。</summary>
    public bool IsBlocked => Outcome == FanCalibrationOutcome.Blocked;
}

/// <summary>
/// 风扇校准编排。
/// </summary>
/// <remarks>
/// <para>
/// 流程固定为：<b>检查前置条件 → 记录进入前状态 → 进入满速 → 采样与进度 → 退出满速 → 恢复并核对 → 结算基准</b>。
/// 每一步失败都单独呈现，不合并成「校准失败」四个字。
/// </para>
/// <para>
/// <b>纪律（提示词 §四.9）：本模块不会自行执行风扇全速测试。</b>
/// 校准涉及风扇全速，必须由用户配合安排；因此
/// <see cref="FanCalibrationPlan.UserAcknowledgedFullSpeed"/> 是**硬前置条件**，
/// 调用方必须先把下列内容告知用户并取得同意：
/// </para>
/// <list type="number">
/// <item>步骤：退出自动策略 → 进入满速 → 稳定若干秒 → 连续采样 → 退出满速 → 恢复并核对。</item>
/// <item>预期现象：风扇噪音显著上升、转速升至上限、机身出风量变大；期间 CPU/GPU 温度会下降。</item>
/// <item>恢复入口：随时可点「取消」，取消后会退出满速并恢复进入前状态；若恢复核对失败，
///       界面会给出手动恢复入口（重新应用配置 / 切回固件自动策略）。</item>
/// </list>
/// </remarks>
public sealed class FanCalibrationService
{
    /// <summary>满速测试的告知文案（步骤、预期现象、恢复入口）。</summary>
    public const string FullSpeedTestNotice =
        "校准需要让风扇进入满速：① 退出当前策略并进入满速；② 稳定若干秒后连续采样；"
        + "③ 退出满速并恢复进入前的状态，然后核对。"
        + "预期现象：风扇噪音明显上升、转速升至上限、出风量变大。"
        + "恢复入口：过程中可随时取消，取消后会退出满速并恢复进入前状态；"
        + "若恢复核对未通过，界面会提供「重新应用配置」与「切回固件自动策略」两个手动入口。";

    private readonly FanApplicationService _application;
    private readonly DeviceCapabilities _capabilities;

    /// <summary>构造校准服务。</summary>
    /// <param name="application">应用流程服务。</param>
    /// <param name="capabilities">设备能力集合。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public FanCalibrationService(FanApplicationService application, DeviceCapabilities capabilities)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    /// <summary>当前进行中的会话。没有则为 <see langword="null"/>。</summary>
    public FanCalibrationSession? Current { get; private set; }

    /// <summary>最近一次成功取得的基准集合。</summary>
    public FanCalibrationBaselineSet Baselines { get; private set; } = FanCalibrationBaselineSet.Empty;

    /// <summary>
    /// 检查能否开始校准。
    /// </summary>
    /// <param name="plan">校准计划。</param>
    /// <returns>阻断原因列表。为空表示可以开始。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> 为 <see langword="null"/>。</exception>
    public IReadOnlyList<string> CheckPreconditions(FanCalibrationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var reasons = new List<string>();
        reasons.AddRange(plan.Validate());

        if (!plan.UserAcknowledgedFullSpeed)
        {
            reasons.Add($"尚未取得用户对满速测试的明确同意。{FullSpeedTestNotice}");
        }

        var calibration = _capabilities.Get(CapabilityKeys.FanCalibration);
        if (!calibration.AllowsWrite)
        {
            reasons.Add(
                $"能力键 {CapabilityKeys.FanCalibration} 当前为 {calibration.Level}，"
                + $"未达 {CapabilityLevel.Verified}。{calibration.DescribeUnavailable()}");
        }

        var fullSpeed = _capabilities.Get(CapabilityKeys.FanFullSpeed);
        if (!fullSpeed.AllowsWrite)
        {
            reasons.Add(
                $"强冷（全速）是校准的必要条件，但能力键 {CapabilityKeys.FanFullSpeed} 当前为 "
                + $"{fullSpeed.Level}。{fullSpeed.DescribeUnavailable()}");
        }

        if (!_application.BootSnapshot.HasSnapshot)
        {
            reasons.Add(
                "尚未取得首次写入前的快照，无法保证校准结束后可恢复原策略"
                + $"（{_application.BootSnapshot.LastCaptureFailure ?? "尚未读取硬件状态"}）。");
        }

        if (Current is not null)
        {
            reasons.Add("已有校准会话正在进行。");
        }

        return reasons;
    }

    /// <summary>
    /// 开始校准。
    /// </summary>
    /// <param name="plan">校准计划。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果。前置条件不满足时 <see cref="FanCalibrationResult.IsBlocked"/> 为真，且不接触硬件。</returns>
    public async Task<FanCalibrationResult> StartAsync(
        FanCalibrationPlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var blockers = CheckPreconditions(plan);
        if (blockers.Count > 0)
        {
            return new FanCalibrationResult(
                FanCalibrationOutcome.Blocked,
                "校准未开始：" + string.Join(" ", blockers));
        }

        var entered = await _application.SetFullSpeedAsync(true, cancellationToken).ConfigureAwait(false);
        if (!entered.IsSuccess)
        {
            return new FanCalibrationResult(
                FanCalibrationOutcome.Failed,
                "校准未开始：无法进入满速。" + entered.Message,
                HardwareOperation: entered);
        }

        Current = new FanCalibrationSession(plan);
        return new FanCalibrationResult(
            FanCalibrationOutcome.Started,
            "校准已开始：已进入满速，等待采样。" + entered.Message,
            Current,
            entered);
    }

    /// <summary>提交一次采样。进度与样本数由会话维护。</summary>
    /// <param name="fans">双风扇快照。</param>
    /// <returns>是否已达到计划样本数。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fans"/> 为 <see langword="null"/>。</exception>
    public bool Report(FanSnapshot fans)
    {
        ArgumentNullException.ThrowIfNull(fans);
        return Current?.Report(fans) ?? false;
    }

    /// <summary>
    /// 完成校准：结算基准 → 退出满速 → 恢复并核对。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果。</returns>
    public Task<FanCalibrationResult> CompleteAsync(CancellationToken cancellationToken)
        => FinishAsync(cancel: false, cancellationToken);

    /// <summary>
    /// 取消校准：同样要退出满速并恢复核对（取消不等于可以跳过恢复）。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>结果。</returns>
    public Task<FanCalibrationResult> CancelAsync(CancellationToken cancellationToken)
        => FinishAsync(cancel: true, cancellationToken);

    private async Task<FanCalibrationResult> FinishAsync(bool cancel, CancellationToken cancellationToken)
    {
        if (Current is not { } session)
        {
            return new FanCalibrationResult(
                FanCalibrationOutcome.Failed,
                "没有进行中的校准会话。");
        }

        FanCalibrationBaselineSet baselines;
        if (cancel)
        {
            session.Cancel();
            baselines = FanCalibrationBaselineSet.Empty;
        }
        else
        {
            baselines = session.Complete();
        }

        // 无论完成还是取消，都必须退出满速并核对（取消不能跳过恢复）。
        var restore = await _application.SetFullSpeedAsync(false, cancellationToken).ConfigureAwait(false);
        Current = null;

        if (baselines.IsUsable)
        {
            Baselines = baselines;
        }

        if (!restore.IsSuccess)
        {
            return new FanCalibrationResult(
                FanCalibrationOutcome.Failed,
                $"校准会话已结束，但退出满速 / 恢复未确认：{restore.Message}",
                session,
                restore,
                baselines);
        }

        if (cancel)
        {
            return new FanCalibrationResult(
                FanCalibrationOutcome.Cancelled,
                $"校准已取消，并已退出满速：{restore.Message}",
                session,
                restore,
                baselines);
        }

        return baselines.IsUsable
            ? new FanCalibrationResult(
                FanCalibrationOutcome.Completed,
                $"{session.Describe()} {baselines.Describe()} {restore.Message}",
                session,
                restore,
                baselines)
            : new FanCalibrationResult(
                FanCalibrationOutcome.Failed,
                $"校准结束时未能得到有效基准：{session.TerminationReason ?? "样本不足或不稳定"}",
                session,
                restore,
                baselines);
    }
}
