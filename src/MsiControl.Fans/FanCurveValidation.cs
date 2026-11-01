using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices;

namespace MsiControl.Fans;

/// <summary>
/// 一次曲线校验的结果。
/// </summary>
/// <param name="Channel">被校验的通道。</param>
/// <param name="Errors">**阻断性**问题：存在则曲线不可用。</param>
/// <param name="UnverifiedConstraints">尚未取得实机证据的硬件约束项。</param>
/// <remarks>
/// <para>
/// <see cref="Errors"/> 与 <see cref="UnverifiedConstraints"/> 必须分开表达：
/// 前者是「这条曲线错了」，后者是「我们不知道硬件允许什么」。
/// 把两者混为一谈会让界面无法解释禁用原因（任务书 §1 原则 8）。
/// </para>
/// <para>
/// <b>未知不等于允许。</b><see cref="CanWrite"/> 同时要求「无错误」与「无未验证约束」，
/// 因此本机当前恒为 <see langword="false"/>——这是预期行为。
/// </para>
/// </remarks>
public sealed record FanCurveValidation(
    FanChannel Channel,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> UnverifiedConstraints)
{
    /// <summary>曲线是否通过全部已知校验。</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>是否存在尚未取得证据的硬件约束。</summary>
    public bool HasUnverifiedConstraints => UnverifiedConstraints.Count > 0;

    /// <summary>是否具备写入条件。任何未验证约束都会使写入被拒。</summary>
    public bool CanWrite => IsValid && !HasUnverifiedConstraints;

    /// <summary>给界面用的一句话摘要。</summary>
    public string Describe()
    {
        if (Errors.Count > 0)
        {
            return $"曲线校验未通过：{string.Join(" ", Errors)}";
        }

        return HasUnverifiedConstraints
            ? $"曲线本身合法，但以下硬件约束尚未取得实机证据，无法确认可以写入："
              + $"{string.Join("、", UnverifiedConstraints)}。"
            : "曲线校验通过，硬件约束已全部取得证据。";
    }
}

/// <summary>
/// 风扇曲线校验器。
/// </summary>
/// <remarks>
/// <para>
/// 校验放在业务模块而<b>不放在底层接口</b>（任务书 §7.1 / 提示词 §三.3）。
/// 校验分两遍：
/// </para>
/// <list type="number">
/// <item><b>通用遍</b>：用 <see cref="FanCurve.TryCreate"/> 做形状与范围校验，
///       并把**全部**错误一次列出（不要只报第一条）。</item>
/// <item><b>硬件遍</b>：用硬件报告的 <see cref="FanCurveLimits"/> 再校验一次
///       （节点上限、输出范围、温度可编辑性、步长）。未取得证据的约束不判为通过，
///       而是计入 <see cref="FanCurveValidation.UnverifiedConstraints"/>。</item>
/// </list>
/// <para>
/// <b>顺序语义提醒</b>：<see cref="FanCurve.TryCreate"/> 会先按温度升序规范化再校验，
/// 因此乱序输入会被接受；若要检查调用方给定的顺序本身是否合法，请直接用
/// <see cref="FanCurve.Validate"/>。本校验器采用前者（曲线以温度为自变量，升序是唯一规范顺序）。
/// </para>
/// </remarks>
public static class FanCurveValidator
{
    /// <summary>步长比对允许的绝对误差。</summary>
    private const double StepEpsilon = 1e-6;

    /// <summary>按「给定顺序」校验一组节点（不排序、不创建曲线）。</summary>
    /// <param name="channel">目标通道。</param>
    /// <param name="points">节点序列。</param>
    /// <param name="limits">硬件报告的限制。</param>
    /// <returns>校验结果。</returns>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/>。</exception>
    public static FanCurveValidation Validate(
        FanChannel channel,
        IEnumerable<FanPoint> points,
        FanCurveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(points);

        return ValidateCore(channel, points, limits);
    }

    /// <summary>校验一条已构造的曲线。</summary>
    /// <param name="curve">曲线。</param>
    /// <param name="limits">硬件报告的限制。</param>
    /// <returns>校验结果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> 为 <see langword="null"/>。</exception>
    public static FanCurveValidation ValidateCurve(FanCurve curve, FanCurveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(curve);

        return ValidateCore(curve.Channel, curve.Points, limits);
    }

    private static FanCurveValidation ValidateCore(
        FanChannel channel,
        IEnumerable<FanPoint> points,
        FanCurveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var errors = new List<string>();
        var unverified = new List<string>();

        // ── 通用遍：形状与范围。TryCreate 会先按温度升序规范化，再校验。 ──
        if (!FanCurve.TryCreate(channel, points, out var curve, out var coreErrors))
        {
            errors.AddRange(coreErrors);

            // 形状不合法时仍要列出未验证约束：界面需要一次性说明「哪里错了」与「哪些还不知道」。
            unverified.AddRange(limits.UnverifiedConstraints);
            return new FanCurveValidation(channel, errors, unverified);
        }

        var ordered = curve!.Points;

        // ── 硬件遍 1：节点上限 ──
        if (limits.MaxNodes is { } maxNodes)
        {
            if (ordered.Count > maxNodes)
            {
                errors.Add($"节点数 {ordered.Count} 超过硬件允许的 {maxNodes} 个。");
            }
        }

        // ── 硬件遍 2：温度区间与步长 ──
        for (var i = 0; i < ordered.Count; i++)
        {
            var temperature = ordered[i].TemperatureC;
            if (temperature < limits.MinTemperatureC || temperature > limits.MaxTemperatureC)
            {
                errors.Add(
                    $"第 {i + 1} 个节点温度 {temperature} °C 超出硬件允许的 " +
                    $"{limits.MinTemperatureC}–{limits.MaxTemperatureC} °C。");
            }
        }

        if (limits.TemperatureStepC is { } temperatureStep)
        {
            // 步长只校验相邻差值是否为步长的整数倍：不假设原点是 0 还是最低节点温度，
            // 因为「原点」这件事本身没有证据。
            for (var i = 1; i < ordered.Count; i++)
            {
                var delta = ordered[i].TemperatureC - ordered[i - 1].TemperatureC;
                if (!IsMultipleOf(delta, temperatureStep))
                {
                    errors.Add(
                        $"第 {i} 与第 {i + 1} 个节点的温度差 {delta} °C 不是步长 {temperatureStep} °C 的整数倍。");
                }
            }
        }

        // ── 硬件遍 3：输出区间与步长 ──
        for (var i = 0; i < ordered.Count; i++)
        {
            var output = ordered[i].OutputPercent;
            if (output < limits.MinOutputPercent || output > limits.MaxOutputPercent)
            {
                errors.Add(
                    $"第 {i + 1} 个节点输出 {output}% 超出硬件允许的 " +
                    $"{limits.MinOutputPercent}–{limits.MaxOutputPercent}%。");
            }
        }

        if (limits.OutputStepPercent is { } outputStep)
        {
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!IsMultipleOf(ordered[i].OutputPercent, outputStep))
                {
                    errors.Add(
                        $"第 {i + 1} 个节点输出 {ordered[i].OutputPercent}% 不是输出步长 {outputStep}% 的整数倍。");
                }
            }
        }

        // ── 硬件遍 4：温度是否可编辑 ──
        if (limits.TemperatureEditable == false)
        {
            errors.Add(
                "硬件报告温度阈值不可编辑，但本机节点可接受的固定温度取值尚无证据，无法构造可写入的曲线。");
        }

        // ── 未取得证据的约束：既不判通过，也不算错误，单独列出并阻断写入。 ──
        unverified.AddRange(limits.UnverifiedConstraints);

        return new FanCurveValidation(channel, errors, unverified);
    }

    private static bool IsMultipleOf(double value, double step)
    {
        if (step <= 0)
        {
            return true;
        }

        var quotient = value / step;
        return Math.Abs(quotient - Math.Round(quotient)) <= StepEpsilon;
    }
}
