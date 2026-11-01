using System.Runtime.ExceptionServices;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices;

namespace MsiControl.Fans;

/// <summary>
/// 风扇应用流程编排：校验 → 前置条件 → 下发 → 读回核对 → 记录。
/// </summary>
/// <remarks>
/// <para>
/// 本类型是任务书 §16「检查能力 → 检查条件 → 顺序写入 → 读取确认 → 发布实际状态与逐项结果」
/// 在风扇模块上的落地，也是「不显示虚假成功」原则的执行者：
/// </para>
/// <list type="bullet">
/// <item><b>不通过就不下发。</b>能力未达 <see cref="CapabilityLevel.Verified"/>、
///       曲线校验有错、硬件约束未取得证据、拿不到首次写入前快照——任一成立即拒绝，
///       且**不产生任何硬件结果对象**（<see cref="FanOperationResult.IsBlocked"/>）。</item>
/// <item><b>成功后必须有确认依据。</b>「固件接受了调用」不算成功；只有**读回值与请求一致**
///       才算，确认依据写入 <see cref="FanOperationResult.ConfirmationBasis"/>。</item>
/// <item><b>取消不跳过核对。</b>收到取消时先完成读回（并记录到
///       <see cref="LastReadBack"/>），再把取消异常原样抛出。</item>
/// </list>
/// <para>
/// <b>本类型不做串行排队。</b>任务书 §16 要求写操作由统一协调器串行执行，协调器属任务 07；
/// 本类型只保证单次调用内的顺序（先写、后读回）。
/// </para>
/// </remarks>
public sealed class FanApplicationService
{
    /// <summary>取消之后仍要完成的读回核对的时间上限。</summary>
    /// <remarks>
    /// 取消语义要求「不能跳过必要的状态核对」，但核对本身不能让退出流程无限等待，
    /// 因此给它一个上限；超时则如实记为「核对未完成」，而不是假装核对过。
    /// </remarks>
    public static readonly TimeSpan PostCancelReadBackTimeout = TimeSpan.FromSeconds(3);

    private readonly IFanHardware _hardware;
    private readonly DeviceCapabilities _capabilities;
    private FanHardwareState? _fullSpeedEntryState;

    /// <summary>构造服务。</summary>
    /// <param name="hardware">风扇硬件接口。</param>
    /// <param name="limits">硬件报告的风扇曲线约束。</param>
    /// <param name="capabilities">设备能力集合，用于写入门控。</param>
    /// <param name="bootSnapshot">首次写入前快照存储。省略时新建一个（会话内有效）。</param>
    /// <exception cref="ArgumentNullException">任一必需参数为 <see langword="null"/>。</exception>
    public FanApplicationService(
        IFanHardware hardware,
        FanCurveLimits limits,
        DeviceCapabilities capabilities,
        FanBootSnapshotStore? bootSnapshot = null)
    {
        _hardware = hardware ?? throw new ArgumentNullException(nameof(hardware));
        Limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        BootSnapshot = bootSnapshot ?? new FanBootSnapshotStore();
    }

    /// <summary>硬件报告的曲线约束。</summary>
    public FanCurveLimits Limits { get; }

    /// <summary>本次启动的首次写入前快照。</summary>
    public FanBootSnapshotStore BootSnapshot { get; }

    /// <summary>最近一次成功应用的记录。成功但无法读回时不更新。</summary>
    public FanAppliedRecord? LastApplied { get; private set; }

    /// <summary>最近一次读回结果（含被取消的那次，供界面展示）。</summary>
    public FanReadResult? LastReadBack { get; private set; }

    /// <summary>进入强冷前记录的状态。用于退出强冷后的核对。</summary>
    /// <remarks>
    /// 记录的是**当时的完整硬件状态**而非仅曲线：从固件自动模式进入强冷时曲线为空，
    /// 若只记录曲线，退出时就无法分辨「进入了强冷又回到自动」与「什么都没恢复」。
    /// </remarks>
    public FanHardwareState? FullSpeedEntryState => _fullSpeedEntryState;

    /// <summary>校验一条曲线（不接触硬件）。</summary>
    public FanCurveValidation Validate(FanCurve curve) => FanCurveValidator.ValidateCurve(curve, Limits);

    /// <summary>组装四态视图。编辑态与保存态由配置存储（任务 02）提供。</summary>
    public FanConfigurationState CreateConfigurationState(FanCurveSet? editing, FanCurveSet? saved)
        => new(editing, saved, LastApplied, LastReadBack);

    /// <summary>
    /// 读取硬件状态。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>读取结果，含可信程度与原始诊断。</returns>
    /// <remarks>
    /// 可信程度由**适配器自述**（<see cref="IFanStateReadability"/>）与原始读取结果共同决定：
    /// 适配器声明无法解读时，即使通道返回了数据，也只报
    /// <see cref="FanReadStatus.TransportOnly"/>，绝不据此判断控制模式。
    /// </remarks>
    public async Task<FanReadResult> ReadStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FanHardwareState state;
        try
        {
            state = await _hardware.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failed = FanReadResult.Unavailable(
                DateTimeOffset.Now,
                $"读取风扇硬件状态失败：{ex.GetType().Name}：{ex.Message}");
            LastReadBack = failed;
            return failed;
        }

        var readability = _hardware as IFanStateReadability;
        if (readability is { CanDecodeState: true })
        {
            var verified = FanReadResult.Verified(state, DateTimeOffset.Now, "适配器已解读硬件返回值。");
            LastReadBack = verified;
            return verified;
        }

        // 适配器不能解读：再取一次原始缓冲区，用于区分「通道故障」与「已知限制」。
        var raw = await TryReadRawAsync(cancellationToken).ConfigureAwait(false);
        var result = raw is { TransportSucceeded: true }
            ? FanReadResult.TransportOnly(
                DateTimeOffset.Now,
                "读取通道可达，但返回内容无法解读，因此无法判定当前控制模式或曲线。"
                + (readability?.DecodeLimitation ?? string.Empty),
                raw)
            : FanReadResult.Unavailable(
                DateTimeOffset.Now,
                raw?.Description ?? "硬件未返回可用的读取结果。");

        LastReadBack = result;
        return result;
    }

    /// <summary>
    /// 应用一条自定义曲线到指定通道。
    /// </summary>
    /// <param name="channel">目标通道。</param>
    /// <param name="curve">曲线。通道必须与 <paramref name="channel"/> 一致。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作结果。被前置条件拦下时 <see cref="FanOperationResult.IsBlocked"/> 为真。</returns>
    public async Task<FanOperationResult> ApplyCurveAsync(
        FanChannel channel,
        FanCurve curve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(curve);
        cancellationToken.ThrowIfCancellationRequested();

        if (curve.Channel != channel)
        {
            return FanOperationResult.Failed(
                FanOperationKind.ApplyCurve,
                $"调用方违约：曲线通道为 {curve.Channel}，目标通道为 {channel}。");
        }

        var blocked = CheckWritePreconditions(FanOperationKind.ApplyCurve);
        if (blocked is not null)
        {
            return blocked;
        }

        var validation = FanCurveValidator.ValidateCurve(curve, Limits);
        if (validation.Errors.Count > 0)
        {
            return FanOperationResult.Blocked(FanOperationKind.ApplyCurve, validation.Describe());
        }

        if (validation.HasUnverifiedConstraints)
        {
            return FanOperationResult.Blocked(FanOperationKind.ApplyCurve, validation.Describe());
        }

        var snapshotBlocker = await EnsureBootSnapshotAsync(FanOperationKind.ApplyCurve, cancellationToken)
            .ConfigureAwait(false);
        if (snapshotBlocker is not null)
        {
            return snapshotBlocker;
        }

        var result = await InvokeHardwareAsync(
                FanOperationKind.ApplyCurve,
                token => _hardware.ApplyCurveAsync(channel, curve, token),
                VerifyCurve(channel, curve),
                cancellationToken)
            .ConfigureAwait(false);

        RecordAppliedCurves(result, FanControlMode.CustomCurve);
        return result;
    }

    /// <summary>切回固件自动策略。这是固件策略入口，**不是** BIOS 出厂重置。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作结果。</returns>
    public async Task<FanOperationResult> SetAutomaticAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var blocked = CheckWritePreconditions(FanOperationKind.SetAutomatic);
        if (blocked is not null)
        {
            return blocked;
        }

        var result = await InvokeHardwareAsync(
                FanOperationKind.SetAutomatic,
                token => _hardware.SetAutomaticAsync(token),
                VerifyAutomatic(),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess && result.ConfirmationBasis is { } basis && result.ReadBack is { } readBack)
        {
            LastApplied = new FanAppliedRecord(FanControlMode.Automatic, null, readBack.SampledAt, basis);
        }

        return result;
    }

    /// <summary>
    /// 切换强冷（全速）。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作结果。</returns>
    /// <remarks>
    /// <para>启用前记录进入前状态；停用后与进入前状态核对。</para>
    /// <para>
    /// <b>强冷不自动与狂暴模式绑定</b>（任务书 §7.5）：本方法不会因为性能模式变化而被调用，
    /// 调用方也不得把两者绑定。
    /// </para>
    /// <para>
    /// <b>本方法不会替用户执行测试。</b>强冷属于需要用户配合的测试项（提示词 §四.9）；
    /// 本方法只提供开关，是否执行由用户显式决定。
    /// </para>
    /// </remarks>
    public async Task<FanOperationResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_capabilities.AllowsWrite(CapabilityKeys.FanFullSpeed))
        {
            var status = _capabilities.Get(CapabilityKeys.FanFullSpeed);
            return FanOperationResult.Blocked(
                FanOperationKind.SetFullSpeed,
                $"强冷（全速）不可用：能力键 {CapabilityKeys.FanFullSpeed} 当前为 {status.Level}，"
                + $"未达 {CapabilityLevel.Verified}。{status.DescribeUnavailable()}");
        }

        if (enabled)
        {
            var before = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
            if (before.Status != FanReadStatus.Verified)
            {
                return FanOperationResult.Blocked(
                    FanOperationKind.SetFullSpeed,
                    "进入强冷前未能读到可信状态，已放弃启用（否则退出时无法核对）：" + before.Message);
            }

            _fullSpeedEntryState = before.State;
        }

        var result = await InvokeHardwareAsync(
                FanOperationKind.SetFullSpeed,
                token => _hardware.SetFullSpeedAsync(enabled, token),
                VerifyFullSpeed(enabled),
                cancellationToken)
            .ConfigureAwait(false);

        if (!enabled && result.IsSuccess)
        {
            // 已退出强冷且核对通过：进入前状态使命完成，避免下次误用旧状态。
            _fullSpeedEntryState = null;
        }

        return result;
    }

    /// <summary>
    /// 恢复本次启动后、首次写入前读到的快照。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>操作结果。</returns>
    /// <remarks>
    /// <b>不是 BIOS 出厂重置。</b>没有可用快照时明确拒绝，而不是退回「随便下发一份默认曲线」。
    /// </remarks>
    public async Task<FanOperationResult> RestoreBootSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (BootSnapshot.DescribeRestoreBlocker() is { } blocker)
        {
            return FanOperationResult.Blocked(FanOperationKind.RestoreBootSnapshot, blocker);
        }

        var blocked = CheckWritePreconditions(FanOperationKind.RestoreBootSnapshot);
        if (blocked is not null)
        {
            return blocked;
        }

        var snapshot = BootSnapshot.Snapshot
            ?? throw new InvalidOperationException("快照判定与取值不一致，属于内部状态错误。");

        foreach (var validation in new[]
                 {
                     FanCurveValidator.ValidateCurve(snapshot.Cpu, Limits),
                     FanCurveValidator.ValidateCurve(snapshot.Gpu, Limits)
                 })
        {
            if (!validation.CanWrite)
            {
                return FanOperationResult.Blocked(
                    FanOperationKind.RestoreBootSnapshot,
                    "启动快照不满足当前硬件约束，已放弃恢复：" + validation.Describe());
            }
        }

        var result = await InvokeHardwareAsync(
                FanOperationKind.RestoreBootSnapshot,
                async token =>
                {
                    var cpu = await _hardware.ApplyCurveAsync(FanChannel.Cpu, snapshot.Cpu, token).ConfigureAwait(false);
                    var gpu = await _hardware.ApplyCurveAsync(FanChannel.Gpu, snapshot.Gpu, token).ConfigureAwait(false);
                    return ControlResult.From(cpu.Settings.Concat(gpu.Settings));
                },
                VerifyCurveSet(snapshot),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.IsSuccess)
        {
            RecordAppliedCurves(result, FanControlMode.CustomCurve);
        }

        return result;
    }

    /// <summary>
    /// 检查写入前置条件（能力 + 未验证约束）。
    /// </summary>
    /// <param name="kind">操作类型。</param>
    /// <returns>被拦下时返回结果，否则返回 <see langword="null"/>。</returns>
    /// <remarks>
    /// 这是两道独立闸门中的第一道：**能力等级**。它回答的是「本机有没有被证实的能力」；
    /// 第二道在 <see cref="FanCurveValidation.CanWrite"/>（曲线与硬件约束是否都已确认）。
    /// </remarks>
    private FanOperationResult? CheckWritePreconditions(FanOperationKind kind)
    {
        if (!_capabilities.AllowsWrite(CapabilityKeys.FanCurve))
        {
            var status = _capabilities.Get(CapabilityKeys.FanCurve);
            return FanOperationResult.Blocked(
                kind,
                $"风扇曲线写入不可用：能力键 {CapabilityKeys.FanCurve} 当前为 {status.Level}，"
                + $"未达 {CapabilityLevel.Verified}。{status.DescribeUnavailable()}");
        }

        if (!Limits.AreAllConstraintsVerified)
        {
            return FanOperationResult.Blocked(
                kind,
                $"曲线约束尚未全部取得实机证据：{string.Join("、", Limits.UnverifiedConstraints)}。"
                + $"来源：{Limits.Source}。未知不等于允许，因此拒绝写入。");
        }

        return null;
    }

    private async Task<FanOperationResult?> EnsureBootSnapshotAsync(
        FanOperationKind kind,
        CancellationToken cancellationToken)
    {
        if (BootSnapshot.HasSnapshot)
        {
            return null;
        }

        var read = await ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (BootSnapshot.TryCapture(read))
        {
            return null;
        }

        return FanOperationResult.Blocked(
            kind,
            $"未能取得首次写入前的有效快照，已放弃写入：{BootSnapshot.LastCaptureFailure}");
    }

    private async Task<FanRawReport?> TryReadRawAsync(CancellationToken cancellationToken)
    {
        if (_hardware is not IFanRawDiagnostics diagnostics)
        {
            return null;
        }

        try
        {
            return await diagnostics.ReadRawAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FanRawReport(
                DateTimeOffset.Now,
                "Get_Fan",
                TransportSucceeded: false,
                Payload: Array.Empty<byte>(),
                Description: $"原始读取失败：{ex.GetType().Name}：{ex.Message}");
        }
    }

    /// <summary>
    /// 下发 → 读回 → 核对 的公共流程。
    /// </summary>
    private async Task<FanOperationResult> InvokeHardwareAsync(
        FanOperationKind kind,
        Func<CancellationToken, Task<ControlResult>> write,
        Func<FanReadResult, WriteVerification> verify,
        CancellationToken cancellationToken)
    {
        ControlResult? control = null;
        OperationCanceledException? cancelled = null;

        try
        {
            control = await write(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            cancelled = ex;
        }

        var readBack = await ReadBackAsync(cancelled, cancellationToken).ConfigureAwait(false);
        LastReadBack = readBack;

        if (cancelled is not null)
        {
            // 状态核对已经完成（或已如实记为未完成），现在把取消原样抛出，不能吞掉。
            ExceptionDispatchInfo.Capture(cancelled).Throw();
        }

        if (control is null)
        {
            return FanOperationResult.Failed(kind, "硬件调用未返回结果。", null, readBack);
        }

        if (!control.IsSuccess)
        {
            return new FanOperationResult(kind, control.Status, DescribeControl(control), control, readBack);
        }

        var verification = verify(readBack);
        return new FanOperationResult(
            kind,
            verification.Status,
            verification.Message,
            control,
            readBack,
            verification.ConfirmationBasis);
    }

    private async Task<FanReadResult> ReadBackAsync(
        OperationCanceledException? cancelled,
        CancellationToken cancellationToken)
    {
        if (cancelled is null)
        {
            return await ReadStateAsync(CancellationToken.None).ConfigureAwait(false);
        }

        // 已取消：仍然完成核对，但加时间上限，避免退出流程被硬件调用拖住。
        using var settle = new CancellationTokenSource(PostCancelReadBackTimeout);
        try
        {
            return await ReadStateAsync(settle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return FanReadResult.Unavailable(
                DateTimeOffset.Now,
                "操作被取消后，状态核对未能在限定时间内完成，已如实记为未核对。");
        }
    }

    private void RecordAppliedCurves(FanOperationResult result, FanControlMode mode)
    {
        if (!result.IsSuccess
            || result.ConfirmationBasis is not { } basis
            || result.ReadBack is not { Status: FanReadStatus.Verified } readBack
            || readBack.State.CpuCurve is not { } cpu
            || readBack.State.GpuCurve is not { } gpu)
        {
            return;
        }

        LastApplied = new FanAppliedRecord(mode, new FanCurveSet(cpu, gpu), readBack.SampledAt, basis);
    }

    private static Func<FanReadResult, WriteVerification> VerifyCurve(FanChannel channel, FanCurve requested)
        => readBack =>
        {
            if (readBack.Status != FanReadStatus.Verified)
            {
                return new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"曲线已下发，但无法读回确认是否生效：{readBack.Message}",
                    null);
            }

            var actual = readBack.State.CurveFor(channel);
            if (actual is null)
            {
                return new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"{channel} 通道读回为空（固件可能仍处于自动模式），无法确认曲线已生效。",
                    null);
            }

            return FanCurveComparison.AreEquivalent(actual, requested)
                ? new WriteVerification(
                    OperationStatus.Succeeded,
                    $"已应用并读回确认：{FanCurveComparison.Describe(actual)}。",
                    $"写入后读回核对：{channel} 通道 {FanCurveComparison.Describe(actual)} 与请求一致")
                : new WriteVerification(
                    OperationStatus.Failed,
                    $"{channel} 通道读回与请求不一致：请求 {FanCurveComparison.Describe(requested)}，"
                    + $"读回 {FanCurveComparison.Describe(actual)}。",
                    null);
        };

    private static Func<FanReadResult, WriteVerification> VerifyCurveSet(FanCurveSet requested)
        => readBack =>
        {
            if (readBack.Status != FanReadStatus.Verified
                || readBack.State.CpuCurve is not { } cpu
                || readBack.State.GpuCurve is not { } gpu)
            {
                return new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"曲线已下发，但无法读回确认是否生效：{readBack.Message}",
                    null);
            }

            var actual = new FanCurveSet(cpu, gpu);
            return FanCurveComparison.AreEquivalent(actual, requested)
                ? new WriteVerification(
                    OperationStatus.Succeeded,
                    $"已恢复并读回确认：{FanCurveComparison.Describe(actual)}。",
                    $"恢复后读回核对：{FanCurveComparison.Describe(actual)} 与快照一致")
                : new WriteVerification(
                    OperationStatus.Failed,
                    $"读回与快照不一致：快照 {FanCurveComparison.Describe(requested)}，"
                    + $"读回 {FanCurveComparison.Describe(actual)}。",
                    null);
        };

    private static Func<FanReadResult, WriteVerification> VerifyAutomatic()
        => readBack =>
        {
            if (readBack.Status != FanReadStatus.Verified)
            {
                return new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"已请求切回固件自动策略，但无法读回确认：{readBack.Message}",
                    null);
            }

            return readBack.State.Mode == FanControlMode.Automatic
                ? new WriteVerification(
                    OperationStatus.Succeeded,
                    "已切回固件自动策略（读回模式为 Automatic）。",
                    "写入后读回：控制模式为 Automatic")
                : new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"已请求切回自动策略，但读回模式为 {readBack.State.Mode}，未确认生效。",
                    null);
        };

    private Func<FanReadResult, WriteVerification> VerifyFullSpeed(bool enabled)
        => readBack =>
        {
            if (readBack.Status != FanReadStatus.Verified)
            {
                return new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"强冷{(enabled ? "启用" : "停用")}请求已下发，但无法读回确认：{readBack.Message}",
                    null);
            }

            if (enabled)
            {
                return readBack.State.Mode == FanControlMode.FullSpeed
                    ? new WriteVerification(
                        OperationStatus.Succeeded,
                        "已进入强冷（读回模式为 FullSpeed）。",
                        "写入后读回：控制模式为 FullSpeed")
                    : new WriteVerification(
                        OperationStatus.PartiallySucceeded,
                        $"已下发启用强冷，但读回模式为 {readBack.State.Mode}，未确认生效。",
                        null);
            }

            if (_fullSpeedEntryState is { Mode: FanControlMode.Automatic })
            {
                return readBack.State.Mode == FanControlMode.Automatic
                    ? new WriteVerification(
                        OperationStatus.Succeeded,
                        "已退出强冷，读回模式为 Automatic，与进入前一致。",
                        "关闭强冷后读回：控制模式与进入前的 Automatic 一致")
                    : new WriteVerification(
                        OperationStatus.PartiallySucceeded,
                        $"已退出强冷，但读回模式为 {readBack.State.Mode}，与进入前的 Automatic 不一致。",
                        null);
            }

            if (_fullSpeedEntryState is { CpuCurve: { } entryCpu, GpuCurve: { } entryGpu }
                && readBack.State.CpuCurve is { } cpu
                && readBack.State.GpuCurve is { } gpu)
            {
                var entry = new FanCurveSet(entryCpu, entryGpu);
                var restored = new FanCurveSet(cpu, gpu);
                return FanCurveComparison.AreEquivalent(entry, restored)
                    ? new WriteVerification(
                        OperationStatus.Succeeded,
                        "已退出强冷，读回曲线与进入前一致。",
                        "关闭强冷后读回：曲线与进入前的快照一致")
                    : new WriteVerification(
                        OperationStatus.PartiallySucceeded,
                        $"已退出强冷，但读回曲线与进入前不一致（进入前 {FanCurveComparison.Describe(entry)}，"
                        + $"读回 {FanCurveComparison.Describe(restored)}）；请显式重新应用配置。",
                        null);
            }

            return readBack.State.Mode == FanControlMode.Automatic
                ? new WriteVerification(
                    OperationStatus.Succeeded,
                    "已退出强冷（读回模式为 Automatic）。",
                    "关闭强冷后读回：控制模式为 Automatic")
                : new WriteVerification(
                    OperationStatus.PartiallySucceeded,
                    $"已下发退出强冷，但读回模式为 {readBack.State.Mode}，未能与进入前状态核对。",
                    null);
        };

    private static string DescribeControl(ControlResult control)
    {
        var problems = control.Problems;
        if (problems.Count == 0)
        {
            return $"硬件未接受本次操作（{control.Status}），且未给出逐项说明。";
        }

        var details = problems.Select(problem =>
        {
            var message = string.IsNullOrWhiteSpace(problem.Message) ? string.Empty : $"（{problem.Message}）";
            return $"{problem.Setting}：{problem.Status}{message}";
        });

        return $"硬件未接受本次操作（{control.Status}）。{string.Join(" ", details)}";
    }

    /// <summary>下发后核对的结论。</summary>
    private readonly record struct WriteVerification(
        OperationStatus Status,
        string Message,
        string? ConfirmationBasis);
}
