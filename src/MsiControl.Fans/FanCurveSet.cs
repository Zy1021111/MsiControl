using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Fans;

/// <summary>
/// 一个性能档位下的 CPU / GPU 双风扇曲线。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §7.3 要求「每个性能模式保存独立 CPU/GPU 风扇曲线」。
/// 本类型是该组合的**唯一规范形状**：曲线由本模块校验，配置存储由任务 02 负责，
/// 档位编排由 05/07 负责，三方不得各建一份（提示词 §三.5）。
/// </para>
/// <para>
/// 构造不失败，但**必须显式调用 <see cref="Validate"/>**——与
/// <c>MsiControl.Core.Models.PerformanceSettings</c> 同一约定：
/// 单条曲线的不变量由 <see cref="FanCurve"/> 保证，字段之间的组合关系由本方法负责。
/// </para>
/// </remarks>
/// <param name="Cpu">CPU 通道曲线。</param>
/// <param name="Gpu">GPU 通道曲线。</param>
public sealed record FanCurveSet(FanCurve Cpu, FanCurve Gpu)
{
    /// <summary>按通道取曲线。</summary>
    public FanCurve For(FanChannel channel) => channel == FanChannel.Cpu ? Cpu : Gpu;

    /// <summary>
    /// 组合校验：两条曲线的通道必须与所处位置一致。
    /// </summary>
    /// <returns>问题列表。为空表示组合合法。</returns>
    /// <remarks>
    /// 把 CPU 曲线放到 GPU 位置（或反之）在类型上完全合法，却是最常见的配置错误；
    /// 底层接口按通道写入，一旦错位就会把 CPU 的曲线烧到 GPU 通道上。
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (Cpu.Channel != FanChannel.Cpu)
        {
            problems.Add($"CPU 位置上的曲线通道是 {Cpu.Channel}，与位置不一致。");
        }

        if (Gpu.Channel != FanChannel.Gpu)
        {
            problems.Add($"GPU 位置上的曲线通道是 {Gpu.Channel}，与位置不一致。");
        }

        return problems;
    }

    /// <summary>
    /// 用节点序列构造曲线对。全部错误会被一次性列出。
    /// </summary>
    /// <param name="cpuPoints">CPU 通道节点。</param>
    /// <param name="gpuPoints">GPU 通道节点。</param>
    /// <param name="set">成功时为曲线对，否则为 <see langword="null"/>。</param>
    /// <param name="errors">校验问题列表，带通道前缀。</param>
    /// <returns>两条曲线均创建成功时返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException">参数为 <see langword="null"/>。</exception>
    public static bool TryCreate(
        IEnumerable<FanPoint> cpuPoints,
        IEnumerable<FanPoint> gpuPoints,
        out FanCurveSet? set,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(cpuPoints);
        ArgumentNullException.ThrowIfNull(gpuPoints);

        var problems = new List<string>();

        var cpuOk = FanCurve.TryCreate(FanChannel.Cpu, cpuPoints, out var cpu, out var cpuErrors);
        problems.AddRange(cpuErrors.Select(e => $"CPU 通道：{e}"));

        var gpuOk = FanCurve.TryCreate(FanChannel.Gpu, gpuPoints, out var gpu, out var gpuErrors);
        problems.AddRange(gpuErrors.Select(e => $"GPU 通道：{e}"));

        if (!cpuOk || !gpuOk)
        {
            set = null;
            errors = problems;
            return false;
        }

        set = new FanCurveSet(cpu!, gpu!);
        errors = problems;
        return true;
    }
}
