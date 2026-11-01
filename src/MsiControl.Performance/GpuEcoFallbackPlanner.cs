using System.Globalization;
using MsiControl.Core.Models;

namespace MsiControl.Performance;

/// <summary>
/// GPU 替代节能策略的规划结果。
/// </summary>
/// <param name="Fallback">替代策略。无需替代时为 <see langword="null"/>。</param>
/// <param name="Explanation">结论说明，必含依据。</param>
public sealed record GpuEcoFallbackPlan(GpuEcoFallback? Fallback, string Explanation)
{
    /// <summary>是否存在替代策略。</summary>
    public bool HasFallback => Fallback is not null;

    /// <summary>构造一个「无需替代」的结果。</summary>
    /// <param name="explanation">说明。</param>
    public static GpuEcoFallbackPlan None(string explanation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        return new GpuEcoFallbackPlan(null, explanation);
    }
}

/// <summary>
/// 在 GPU 目标值不受支持时规划显式替代策略。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §8.3 的原文要求：显示支持范围和未应用原因、<b>不静默替换参数</b>、
/// 提供经过验证的兼容节能策略、<b>明确替代策略不保证精确目标瓦数</b>、
/// 不扩展为未授权的超频或 VF 曲线编辑。本类型逐条落实这五点：
/// 没有范围就**不给**替代值（不猜下界）；给出替代值时强制携带能力边界说明。
/// </para>
/// <para>
/// <b>本类型不保证替代策略一定能被硬件接受</b>——本机写入通道尚未开放，
/// 因此规划出的策略在应用时同样要经能力门控（见 <c>PerformanceCapabilityGate</c>）。
/// </para>
/// </remarks>
public static class GpuEcoFallbackPlanner
{
    /// <summary>
    /// 规划 GPU 替代节能策略。
    /// </summary>
    /// <param name="requestedWatts">用户目标瓦数。</param>
    /// <param name="range">本机报告的 GPU 可写范围。</param>
    /// <returns>规划结果。</returns>
    public static GpuEcoFallbackPlan Plan(decimal requestedWatts, LimitRange range)
    {
        ArgumentNullException.ThrowIfNull(range);

        if (requestedWatts <= 0m)
        {
            return GpuEcoFallbackPlan.None(
                $"GPU 目标 {Format(requestedWatts)} W 不是有效瓦数：功耗限制必须为正数，" +
                "不要用 0 或负数表达「不限制」，原生上限请使用 LimitMode.Native。");
        }

        if (!range.IsKnown)
        {
            return GpuEcoFallbackPlan.None(
                $"无法为 GPU 目标 {Format(requestedWatts)} W 规划替代策略：{range.UnavailableReason} " +
                "缺少可写范围时不猜测下界，也不静默替换目标值。");
        }

        var minimum = range.Minimum!.Value;
        if (requestedWatts >= minimum)
        {
            return GpuEcoFallbackPlan.None(
                $"GPU 目标 {Format(requestedWatts)} W 不低于本机报告的下界 {Format(minimum)} W，无需替代策略。");
        }

        if (!PowerLimit.TryCustom(minimum, out var applied, out var error))
        {
            return GpuEcoFallbackPlan.None(
                $"下界 {Format(minimum)} W 无法构成合法的功耗限制值（{error}），因此不给出替代策略。");
        }

        var description =
            $"GPU 目标 {Format(requestedWatts)} W 低于本机报告的下界 {Format(minimum)} W；" +
            $"按显式确认的替代策略改用下界 {Format(minimum)} W，原目标值不予下发，也不做静默替换。";

        var limitation =
            "该策略不保证精确达到用户原始目标瓦数，也不代表 GPU 实际耗电等于该值；" +
            $"下界来自本机范围报告（来源：{range.Source}）；" +
            "策略范围不含超频与 VF 曲线编辑，也不会绕过硬件保护。";

        return new GpuEcoFallbackPlan(GpuEcoFallback.Create(description, applied!, limitation), description);
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
