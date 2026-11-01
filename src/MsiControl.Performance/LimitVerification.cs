using System.Globalization;

namespace MsiControl.Performance;

/// <summary>验证限制生效时设备所处的负载条件。</summary>
public enum LoadCondition
{
    /// <summary>负载条件未声明。</summary>
    Unknown = 0,

    /// <summary>空闲或轻负载。</summary>
    Idle = 1,

    /// <summary>已构造有效负载（足以逼出功耗上限的持续负载）。</summary>
    EffectiveLoad = 2
}

/// <summary>限制生效性的判定结论。</summary>
public enum LimitVerificationVerdict
{
    /// <summary>目标为原生上限，没有自定义值需要核对。</summary>
    NotAttempted,

    /// <summary>读回值与请求值一致（在容差内），限制已按请求生效。</summary>
    ConfirmedByReadBack,

    /// <summary>读回值与请求值不一致。</summary>
    ReadBackMismatch,

    /// <summary>有效负载下实测功耗持续超过请求值。</summary>
    ObservedExceedsRequested,

    /// <summary>无法读回限制值，因此不能确认，也不得宣称生效。</summary>
    UnverifiableByReadBack,

    /// <summary>证据不足：低负载读数不能证明限制生效。</summary>
    InsufficientEvidence
}

/// <summary>一次限制生效性判定的输入。</summary>
/// <param name="SettingKey">设置项标识，取值见 <see cref="PerformanceSettingKeys"/>。</param>
/// <param name="RequestedValue">请求值。原生上限时为 <see langword="null"/>。</param>
/// <param name="ReadBackValue">读回值。无法读回时为 <see langword="null"/>。</param>
/// <param name="Load">判定时的负载条件。</param>
/// <param name="ObservedPackageWatts">同期的实测封装功耗。无来源时为 <see langword="null"/>。</param>
/// <param name="ObservationWindow">观测时长。未声明时为 <see langword="null"/>。</param>
public sealed record LimitVerificationInput(
    string SettingKey,
    decimal? RequestedValue,
    decimal? ReadBackValue,
    LoadCondition Load,
    decimal? ObservedPackageWatts = null,
    TimeSpan? ObservationWindow = null);

/// <summary>一次限制生效性判定的结果。</summary>
/// <param name="Verdict">判定结论。</param>
/// <param name="Basis">判定依据，面向用户，必含具体数值或缺失原因。</param>
/// <param name="Notes">补充说明，例如时间窗口偏差与证据强度限制。</param>
public sealed record LimitVerificationResult(
    LimitVerificationVerdict Verdict,
    string Basis,
    IReadOnlyList<string> Notes)
{
    /// <summary>是否已确认生效。</summary>
    public bool IsConfirmed => Verdict == LimitVerificationVerdict.ConfirmedByReadBack;

    /// <summary>是否已确认为「未按请求生效」。</summary>
    public bool IsViolated
        => Verdict is LimitVerificationVerdict.ReadBackMismatch or LimitVerificationVerdict.ObservedExceedsRequested;

    /// <summary>是否无法得出结论。</summary>
    public bool IsInconclusive
        => Verdict is LimitVerificationVerdict.UnverifiableByReadBack or LimitVerificationVerdict.InsufficientEvidence;
}

/// <summary>
/// 限制生效性的判定规则。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §8.5 的关键约束：<b>「实际低负载功耗低于目标，不是限制成功的证据。」</b>
/// 本类型把这条要求落成可判定的规则：
/// <list type="number">
/// <item>只有<b>读回值</b>与请求值一致，才算确认生效。</item>
/// <item>没有读回值时，无论实测功耗高低，一律不得判为成功；低负载情形单独归为「证据不足」。</item>
/// <item>有效负载下实测功耗持续超过请求值（含容许的瞬时偏差比例）时，判为未生效并说明时间窗口。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么必须如此：</b>CPU 功耗限制基于时间窗口（PL1/PL2 类比），瞬时功耗可以超过窗口平均值；
/// 反过来，空闲时功耗天然低于目标。两个方向的读数都不能单独证明限制机制在工作，
/// 因此只能用「读回的限制值」作为主证据，用负载下的实测值作为旁证或反证。
/// </para>
/// </remarks>
public static class LimitVerification
{
    /// <summary>默认容差（与判定量同单位，取 0.5 以容纳整数取整）。</summary>
    public const decimal DefaultTolerance = 0.5m;

    /// <summary>有效负载下允许的瞬时超出比例（5%），超出该比例视为未生效。</summary>
    public const decimal DefaultOvershootRatio = 0.05m;

    /// <summary>
    /// 判定一项限制是否生效。
    /// </summary>
    /// <param name="input">判定输入。</param>
    /// <param name="tolerance">读回值比对容差，不得为负。</param>
    /// <param name="overshootRatio">允许的瞬时超出比例，不得为负。</param>
    /// <returns>判定结果。</returns>
    /// <exception cref="ArgumentNullException">输入为空。</exception>
    /// <exception cref="ArgumentException">设置项标识为空。</exception>
    /// <exception cref="ArgumentOutOfRangeException">容差或比例非法、观测时长为负。</exception>
    public static LimitVerificationResult Verify(
        LimitVerificationInput input,
        decimal tolerance = DefaultTolerance,
        decimal overshootRatio = DefaultOvershootRatio)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.SettingKey);

        if (tolerance < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "容差不得为负数。");
        }

        if (overshootRatio < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(overshootRatio), overshootRatio, "超出比例不得为负数。");
        }

        if (input.ObservationWindow is { } window && window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(input), window, "观测时长不得为负数。");
        }

        var notes = new List<string>();

        if (input.RequestedValue is not { } requested)
        {
            return new LimitVerificationResult(
                LimitVerificationVerdict.NotAttempted,
                "目标为原生上限：本软件未下发任何数值，因此不存在「请求值 vs 读回值」需要核对，" +
                "也不能据此声称限制生效。",
                notes);
        }

        if (input.ObservationWindow is not null && input.Load == LoadCondition.EffectiveLoad)
        {
            notes.Add($"观测时长 {FormatSeconds(input.ObservationWindow.Value)}。");
        }
        else if (input.Load == LoadCondition.EffectiveLoad)
        {
            notes.Add("未声明观测时长，负载证据的强度有限。");
        }

        if (input.ReadBackValue is { } readBack)
        {
            if (Math.Abs(readBack - requested) <= tolerance)
            {
                AppendObservedNote(notes, input, requested, overshootRatio);
                return new LimitVerificationResult(
                    LimitVerificationVerdict.ConfirmedByReadBack,
                    $"写入后读回 {Format(readBack)}，与请求 {Format(requested)} 的差值在容差 " +
                    $"{Format(tolerance)} 之内，确认限制值已按请求生效。",
                    notes);
            }

            return new LimitVerificationResult(
                LimitVerificationVerdict.ReadBackMismatch,
                $"读回 {Format(readBack)} 与请求 {Format(requested)} 不一致（超出容差 {Format(tolerance)}）：" +
                "限制未按请求生效，需报告实际读回值并说明被拒绝或被固件覆盖的原因。",
                notes);
        }

        // 无读回值：这是本机的现实情况（MSI_ACPI 的 OutParameters 不含 ReturnValue）。
        if (input.Load == LoadCondition.Idle)
        {
            notes.Add("低负载下实际功耗低于目标，不构成限制生效的证据（任务书 §8.5）。");
            return new LimitVerificationResult(
                LimitVerificationVerdict.InsufficientEvidence,
                "在空闲或轻负载条件下无法判定：本机无法读回限制值，" +
                "且低负载功耗天然低于目标，两个方向都不能证明限制机制在工作。",
                notes);
        }

        if (input.ObservedPackageWatts is { } observed
            && input.Load == LoadCondition.EffectiveLoad
            && observed > requested * (1m + overshootRatio))
        {
            notes.Add(
                $"容许的瞬时超出比例为 {Format(overshootRatio * 100m)}%，" +
                "用于容纳时间窗口内的瞬时偏差；持续超出说明限制未生效。");
            return new LimitVerificationResult(
                LimitVerificationVerdict.ObservedExceedsRequested,
                $"有效负载下实测功耗 {Format(observed)} W 持续超过请求 {Format(requested)} W：" +
                "限制未生效，或该目标值未被固件接受。",
                notes);
        }

        notes.Add("需要以读回的限制值作为主证据；负载下的实测功耗只能作为旁证。");
        return new LimitVerificationResult(
            LimitVerificationVerdict.UnverifiableByReadBack,
            input.Load == LoadCondition.EffectiveLoad
                ? "已施加有效负载，但本机无法读回限制值（provider 未暴露 ReturnValue，且 Data 协议尚未验证），" +
                  "因此只能判定为「未确认」，不得宣称限制已生效。"
                : "负载条件未声明且无法读回限制值：既不能确认生效，也不能排除未生效。",
            notes);
    }

    private static void AppendObservedNote(
        ICollection<string> notes,
        LimitVerificationInput input,
        decimal requested,
        decimal overshootRatio)
    {
        if (input.ObservedPackageWatts is not { } observed)
        {
            return;
        }

        var limit = requested * (1m + overshootRatio);
        notes.Add(observed > limit
            ? $"同期实测封装功耗 {Format(observed)} W 高于请求值 {Format(requested)} W 的容许上限 " +
              $"{Format(limit)} W；读回值一致说明限制值已写入，这一现象属于时间窗口内的瞬时偏差，" +
              "不应据此判为失败，但也不能承诺每次采样都低于设定值。"
            : $"同期实测封装功耗 {Format(observed)} W 未超过请求值 {Format(requested)} W。");
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatSeconds(TimeSpan value)
        => value.TotalSeconds.ToString("0.##", CultureInfo.InvariantCulture) + " 秒";
}
