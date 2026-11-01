using System.Globalization;

namespace MsiControl.Performance;

/// <summary>
/// 设备报告的可写范围。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §8.3 要求「目标值不受支持时显示支持范围」。本类型只表达**设备报告的**范围，
/// 不表达「我们认为应该支持的范围」——因此有两种状态：
/// <see cref="IsKnown"/> 为真（有报告值）与为假（尚未取得，附原因）。
/// </para>
/// <para>
/// <b>纪律（任务书 §四 纪律 3）：</b>范围值只能来自实机接口返回或已验证的协议文档，
/// 不得由设计文档中的目标值反推。取值来源必须经 <see cref="Source"/> 记录，
/// 便于在报告里说明「这个范围是凭什么得到的」。
/// </para>
/// <para>
/// <b>范围未知不等于范围受限。</b>两者在判定上的处理完全不同：
/// 未知时 <see cref="LimitSupportEvaluator"/> 不得假定目标受支持，也不得假定其不受支持。
/// </para>
/// </remarks>
public sealed record LimitRange
{
    private LimitRange(
        decimal? minimum,
        decimal? maximum,
        decimal? step,
        string source,
        string? unavailableReason)
    {
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Source = source;
        UnavailableReason = unavailableReason;
    }

    /// <summary>下界。未知时为 <see langword="null"/>。</summary>
    public decimal? Minimum { get; }

    /// <summary>上界。未知时为 <see langword="null"/>。</summary>
    public decimal? Maximum { get; }

    /// <summary>步长。设备未报告步长时为 <see langword="null"/>（不等于步长为 1）。</summary>
    public decimal? Step { get; }

    /// <summary>取值来源说明（证据追溯用），必定非空。</summary>
    public string Source { get; }

    /// <summary>范围未知时的原因。已知时为 <see langword="null"/>。</summary>
    public string? UnavailableReason { get; }

    /// <summary>是否已取得可写范围。</summary>
    public bool IsKnown => Minimum is not null && Maximum is not null;

    /// <summary>构造一个已知范围。</summary>
    /// <param name="minimum">下界，不得为负。</param>
    /// <param name="maximum">上界，不得小于下界。</param>
    /// <param name="step">步长。设备未报告时为 <see langword="null"/>；给定时必须为正。</param>
    /// <param name="source">取值来源，不得为空。</param>
    /// <returns>已知范围。</returns>
    /// <exception cref="ArgumentOutOfRangeException">数值不自洽。</exception>
    /// <exception cref="ArgumentException">来源为空。</exception>
    public static LimitRange Known(decimal minimum, decimal maximum, decimal? step, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        if (minimum < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "范围下界不得为负数。");
        }

        if (maximum < minimum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximum),
                maximum,
                $"范围上界 {maximum} 小于下界 {minimum}，该报告值不自洽。");
        }

        if (step is { } s && s <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "步长必须为正数。");
        }

        return new LimitRange(minimum, maximum, step, source, null);
    }

    /// <summary>构造一个尚未取得范围的对象。</summary>
    /// <param name="reason">未取得的原因，不得为空。</param>
    /// <returns>未知范围。</returns>
    /// <exception cref="ArgumentException">原因为空。</exception>
    /// <remarks>
    /// 原因必填：一个「不知道」如果连原因都说不出来，就无法与「没查」区分，
    /// 也就无法在报告里交代清楚（任务书 §8.5 要求逐项可查）。
    /// </remarks>
    public static LimitRange Unknown(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new LimitRange(null, null, null, "尚未取得", reason);
    }

    /// <summary>判断某个值是否落在已报告范围内。</summary>
    /// <param name="value">待判定的值。</param>
    /// <returns>范围已知且值落在闭区间内时为 <see langword="true"/>；范围未知时一律为 <see langword="false"/>。</returns>
    /// <remarks>
    /// 范围未知时返回 <see langword="false"/> 而不是「不适用」：
    /// 调用方不应据此认为「值不受支持」——请改用
    /// <see cref="LimitSupportEvaluator.Evaluate(PowerLimit, LimitRange)"/> 区分两种情况。
    /// </remarks>
    public bool Supports(decimal value)
        => IsKnown && value >= Minimum!.Value && value <= Maximum!.Value;

    /// <summary>
    /// 取最接近的受支持值。
    /// </summary>
    /// <param name="value">目标值。</param>
    /// <returns>范围内的最接近值；范围未知时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// <b>本方法的结果只能用作「建议」或显式标注的替代策略，不得静默替换用户目标值</b>（任务书 §8.3）。
    /// </remarks>
    public decimal? NearestSupported(decimal value)
    {
        if (!IsKnown)
        {
            return null;
        }

        if (value < Minimum!.Value)
        {
            return Minimum.Value;
        }

        return value > Maximum!.Value ? Maximum.Value : value;
    }

    /// <summary>生成面向用户的范围说明。</summary>
    /// <param name="unit">单位符号，例如 <c>W</c> 或 <c>MHz</c>。</param>
    /// <returns>可读说明。</returns>
    /// <exception cref="ArgumentException">单位为空。</exception>
    public string Describe(string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        if (!IsKnown)
        {
            return $"本机尚未取得可写范围（{UnavailableReason}）。未取得范围时不得假定目标值受支持。";
        }

        var min = Format(Minimum!.Value);
        var max = Format(Maximum!.Value);
        var stepText = Step is { } s ? $"，步长 {Format(s)} {unit}" : "，步长未报告";
        return $"本机报告的可写范围：{min}–{max} {unit}{stepText}（来源：{Source}）。";
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
