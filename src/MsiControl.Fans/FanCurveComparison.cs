using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Fans;

/// <summary>
/// 风扇曲线/曲线对的**值语义**比较。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不能直接用 <c>==</c>：<see cref="FanCurve"/> 是普通类，默认引用相等；
/// <see cref="FanCurveSet"/> 虽然是 record，但其字段的相等性仍然落到引用比较，
/// 于是「内容完全相同的两条曲线」会被判为不同。
/// </para>
/// <para>
/// 这个区别在本模块是**功能性的**而非风格问题：
/// </para>
/// <list type="bullet">
/// <item>「编辑中的配置是否已保存」——引用比较会把刚保存的同一份对象判为相同，
///       把重建的等价对象判为不同，界面提示会反复抖动。</item>
/// <item>「写入后读回值是否与请求一致」——这是 <see cref="Core.Results.SettingResult.ConfirmationBasis"/>
///       的核心判据，判错就等于伪造成功。</item>
/// </list>
/// </remarks>
public static class FanCurveComparison
{
    /// <summary>数值比较允许的绝对误差（百分比与摄氏度共用）。</summary>
    public const double Tolerance = 1e-6;

    /// <summary>比较两条曲线是否等价（通道、节点数、每个节点的温度与输出）。</summary>
    /// <param name="left">左值。可为 <see langword="null"/>。</param>
    /// <param name="right">右值。可为 <see langword="null"/>。</param>
    /// <returns>两者均非空且内容等价时返回 <see langword="true"/>。</returns>
    /// <remarks>两个 <see langword="null"/> 视为等价；一个为 <see langword="null"/> 则不等价。</remarks>
    public static bool AreEquivalent(FanCurve? left, FanCurve? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left.Channel != right.Channel || left.NodeCount != right.NodeCount)
        {
            return false;
        }

        for (var i = 0; i < left.NodeCount; i++)
        {
            var a = left.Points[i];
            var b = right.Points[i];
            if (Math.Abs(a.TemperatureC - b.TemperatureC) > Tolerance
                || Math.Abs(a.OutputPercent - b.OutputPercent) > Tolerance)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>比较两组曲线对是否等价。</summary>
    /// <param name="left">左值。可为 <see langword="null"/>。</param>
    /// <param name="right">右值。可为 <see langword="null"/>。</param>
    /// <returns>两者均非空且两条通道均等价时返回 <see langword="true"/>。</returns>
    public static bool AreEquivalent(FanCurveSet? left, FanCurveSet? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left is not null
               && right is not null
               && AreEquivalent(left.Cpu, right.Cpu)
               && AreEquivalent(left.Gpu, right.Gpu);
    }

    /// <summary>给出一句话描述，供消息与日志使用。</summary>
    public static string Describe(FanCurve? curve)
        => curve is null
            ? "无曲线"
            : $"{curve.Channel} {curve.NodeCount} 节点 {curve.MinTemperatureC}–{curve.MaxTemperatureC} °C";

    /// <summary>给出一句话描述，供消息与日志使用。</summary>
    public static string Describe(FanCurveSet? set)
        => set is null ? "无曲线对" : $"CPU[{Describe(set.Cpu)}] GPU[{Describe(set.Gpu)}]";
}
