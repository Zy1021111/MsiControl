using System.Globalization;
using MsiControl.Core.Models;

namespace MsiControl.Performance;

/// <summary>
/// 目标限制值与「本机报告的可写范围」对照后的判定结果。
/// </summary>
public enum LimitSupportVerdict
{
    /// <summary>目标为原生上限：取消本软件额外限制，恢复设备原生策略。</summary>
    RestoreNativePolicy,

    /// <summary>目标落在本机报告的范围内，可原样下发。</summary>
    WithinReportedRange,

    /// <summary>目标低于本机报告的下界。</summary>
    BelowReportedMinimum,

    /// <summary>目标高于本机报告的上界。</summary>
    AboveReportedMaximum,

    /// <summary>本机尚未取得可写范围，无法判定目标是否受支持。</summary>
    RangeUnknown
}

/// <summary>
/// 一次「目标是否受支持」判定的结果。
/// </summary>
/// <param name="Verdict">判定结论。</param>
/// <param name="Explanation">面向用户的说明，必含依据。</param>
/// <param name="NearestSupportedValue">
/// 范围内的最接近值。<b>仅作建议或显式替代策略</b>，范围未知时为 <see langword="null"/>。
/// </param>
public sealed record LimitSupportResult(
    LimitSupportVerdict Verdict,
    string Explanation,
    decimal? NearestSupportedValue = null)
{
    /// <summary>是否可以按用户目标值原样下发。</summary>
    public bool CanApplyRequestedValue
        => Verdict is LimitSupportVerdict.RestoreNativePolicy or LimitSupportVerdict.WithinReportedRange;

    /// <summary>是否需要替代策略（目标不受支持）。</summary>
    public bool RequiresFallback
        => Verdict is LimitSupportVerdict.BelowReportedMinimum or LimitSupportVerdict.AboveReportedMaximum;

    /// <summary>是否因范围未知而无法判定。</summary>
    public bool IsIndeterminate => Verdict == LimitSupportVerdict.RangeUnknown;

    /// <summary>是否为原生上限（无需下发任何数值）。</summary>
    public bool IsNativePolicy => Verdict == LimitSupportVerdict.RestoreNativePolicy;
}

/// <summary>
/// 目标限制值与可写范围的对照判定。
/// </summary>
/// <remarks>
/// <para>
/// 这是任务书 §8.3「显示支持范围和未应用原因，<b>不静默替换参数</b>」的落点：
/// 判定只回答「受支持 / 低于下界 / 高于上界 / 范围未知」，
/// 是否降级、降级成什么，由调用方显式决定并标注（见 <see cref="GpuEcoFallbackPlanner"/>）。
/// </para>
/// <para>
/// <b>本类型不做任何硬件访问，也不修改传入的目标值。</b>
/// </para>
/// </remarks>
public static class LimitSupportEvaluator
{
    /// <summary>判定功耗限制目标是否受支持。</summary>
    /// <param name="target">目标功耗限制。</param>
    /// <param name="range">本机报告的可写范围。</param>
    /// <param name="quantity">量的中文名，用于生成说明。</param>
    /// <returns>判定结果。</returns>
    public static LimitSupportResult Evaluate(
        PowerLimit target,
        LimitRange range,
        string quantity = "功耗")
    {
        ArgumentNullException.ThrowIfNull(target);
        return EvaluateCore(target.IsNative, target.Watts, range, quantity, "W");
    }

    /// <summary>判定 CPU 睿频上限目标是否受支持。</summary>
    /// <param name="target">目标睿频上限。</param>
    /// <param name="range">本机报告的可写范围（单位 MHz）。</param>
    /// <param name="quantity">量的中文名，用于生成说明。</param>
    /// <returns>判定结果。</returns>
    public static LimitSupportResult Evaluate(
        CpuTurboLimit target,
        LimitRange range,
        string quantity = "CPU 睿频上限")
    {
        ArgumentNullException.ThrowIfNull(target);
        return EvaluateCore(target.IsNative, target.Megahertz, range, quantity, "MHz");
    }

    private static LimitSupportResult EvaluateCore(
        bool isNative,
        decimal? value,
        LimitRange range,
        string quantity,
        string unit)
    {
        ArgumentNullException.ThrowIfNull(range);
        ArgumentException.ThrowIfNullOrWhiteSpace(quantity);

        if (isNative)
        {
            return new LimitSupportResult(
                LimitSupportVerdict.RestoreNativePolicy,
                $"{quantity}请求原生上限：取消本软件额外限制、恢复设备允许的原生策略，" +
                "不绕过硬件保护（不使用 0、-1 或极大数字代表无限）。");
        }

        // 到这里 value 必然有值：PowerLimit / CpuTurboLimit 的自定义模式由构造期不变量保证。
        var target = value!.Value;

        if (!range.IsKnown)
        {
            return new LimitSupportResult(
                LimitSupportVerdict.RangeUnknown,
                $"{quantity}目标 {Format(target)} {unit}：{range.UnavailableReason} " +
                "因此无法判定该目标是否受支持；不假定其受支持，也不假定其不受支持。");
        }

        if (target < range.Minimum!.Value)
        {
            return new LimitSupportResult(
                LimitSupportVerdict.BelowReportedMinimum,
                $"{quantity}目标 {Format(target)} {unit} 低于本机报告的下界 " +
                $"{Format(range.Minimum.Value)} {unit}，不会按该值下发，也不会静默替换。" +
                $"如需降级，可显式采用下界 {Format(range.Minimum.Value)} {unit} 作为替代策略并标注其不保证精确达成目标。",
                range.Minimum.Value);
        }

        if (target > range.Maximum!.Value)
        {
            return new LimitSupportResult(
                LimitSupportVerdict.AboveReportedMaximum,
                $"{quantity}目标 {Format(target)} {unit} 高于本机报告的上界 " +
                $"{Format(range.Maximum.Value)} {unit}，不会按该值下发，也不会静默截断为上限。" +
                $"如需降级，可显式采用上界 {Format(range.Maximum.Value)} {unit}，或改用原生上限表达「不额外限制」。",
                range.Maximum.Value);
        }

        return new LimitSupportResult(
            LimitSupportVerdict.WithinReportedRange,
            $"{quantity}目标 {Format(target)} {unit} 落在本机报告的范围内" +
            $"（{Format(range.Minimum.Value)}–{Format(range.Maximum.Value)} {unit}，来源：{range.Source}）。");
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
