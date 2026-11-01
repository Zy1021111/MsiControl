using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 可选扩展：支持按占空比百分比直接下发风扇输出（软件调速回路的写通道）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IFanHardware"/> 只提供「应用曲线 / 切回自动 / 强冷」三种写操作，
/// 没有「下发输出百分比」的方法——这正是软件调速回路（<c>SoftwareFanControlLoop</c>）
/// 此前无法闭环的原因（其 <c>WritePathLimitation</c>）。本接口补上该缺口：
/// 由具备占空比写证据的适配层（<c>FanHardwareMs15M3Ec</c>，EC 0x71/0x89）额外实现，
/// 协调方以模式匹配探测，未实现时不得假装可调速。
/// </para>
/// <para>
/// <b>语义边界（必须向用户展示）：</b>MS-15M3 固件在 advanced 模式下约每 5 秒把
/// 占空比寄存器改回固件曲线值（HWE §17.3），因此本接口只能「周期性重写下发」，
/// 属<b>软件持续调速</b>：依赖本程序存活，两次写入之间固件可能短暂接管；
/// 程序退出后固件在数秒内自行恢复其曲线（无需担心残留）。
/// </para>
/// </remarks>
public interface IFanOutputControl
{
    /// <summary>
    /// 把指定通道的风扇占空比下发为给定百分比。
    /// </summary>
    /// <param name="channel">目标通道。</param>
    /// <param name="percent">占空比百分比（0–100，四舍五入到整数）。</param>
    /// <param name="cancellationToken">取消标记。取消不得跳过读回核对。</param>
    /// <returns>逐项结果；成功必须携带「写入+读回一致」的确认依据。</returns>
    Task<ControlResult> SetDutyPercentAsync(
        FanChannel channel,
        double percent,
        CancellationToken cancellationToken);

    /// <summary>
    /// 是否具备受理占空比下发的条件（能力 Verified + 写白名单含 0x71/0x89）。
    /// </summary>
    bool IsDutyWriteEnabled { get; }
}
