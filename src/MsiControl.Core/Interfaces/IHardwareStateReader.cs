using MsiControl.Core.Models;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 硬件状态读回源：由宿主装配并注入桌面壳，供界面展示「固件里实际生效的配置状态」。
/// </summary>
/// <remarks>
/// <para>
/// 与监控快照（<c>IMonitoringProvider</c>）的分工：监控描述<b>实时读数</b>
/// （温度、转速、功耗），本接口描述<b>配置状态</b>（当前风扇控制模式、
/// 实际性能档位、实际显卡模式）。二者不可互相替代（见 <see cref="Models.FanHardwareState"/> 与
/// <see cref="Models.FanSnapshot"/> 的区别说明）。
/// </para>
/// <para>
/// <b>诚实性约束：</b>实现必须只基于已确证的只读寄存器（本机为 EC 0xD4 / 0xD2，
/// 显卡为 EC 0x2E / 0xD1 + UEFI 目标变量，HWE §17/§20/§21）。读取失败或超时时返回
/// <see langword="null"/>，由界面保持上一次状态并说明；<b>不得编造状态填充显示</b>（任务书 §1 原则 6）。
/// </para>
/// </remarks>
public interface IHardwareStateReader
{
    /// <summary>读回当前风扇控制状态（EC 0xD4）。不可用时为 <see langword="null"/>。</summary>
    Task<FanHardwareState?> ReadFanStateAsync(CancellationToken cancellationToken);

    /// <summary>读回当前实际性能档位（EC 0xD2）。不可用时为 <see langword="null"/>。</summary>
    Task<PerformanceHardwareState?> ReadPerformanceStateAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 读回当前实际显卡模式（EC 0x2E bit6 登记态 + EC 0xD1 触发位 + UEFI 目标变量，HWE §21）。
    /// 不可用时为 <see langword="null"/>。
    /// </summary>
    Task<GraphicsHardwareState?> ReadGraphicsStateAsync(CancellationToken cancellationToken);
}
