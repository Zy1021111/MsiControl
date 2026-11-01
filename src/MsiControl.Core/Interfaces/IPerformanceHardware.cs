using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 性能与功耗硬件访问。
/// </summary>
/// <remarks>
/// <para>
/// 本接口不自己定时争抢硬件设置；写入一律经统一协调器串行执行（任务书 §16）。
/// </para>
/// <para>
/// 目标值不受支持时，实现<b>必须报告实际支持范围，不得静默替换目标</b>；
/// 如需降级，应通过 <see cref="PerformanceSettings.GpuEcoFallback"/> 表达，
/// 并在结果中明确标注其为替代策略（任务书 §8.3）。
/// </para>
/// </remarks>
public interface IPerformanceHardware
{
    /// <summary>读取当前实际生效的档位与各限制项的读回值。</summary>
    Task<PerformanceHardwareState> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 应用一档性能配置。
    /// </summary>
    /// <param name="settings">目标配置。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>逐项结果。每一项都必须单独报告请求值、接口结果与读回值。</returns>
    Task<ControlResult> ApplyAsync(PerformanceSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// 标记：本性能适配层是「固件场景切换」型（EC Shift 模式），不承载自定义瓦数目标。
/// </summary>
/// <remarks>
/// <para>
/// 协调器据此调整能力门控：场景型通道只要求
/// <see cref="CapabilityKeys.PerformanceStage"/> 达 <see cref="CapabilityLevel.Verified"/>，
/// 不因瓦数类能力键（cpu.power 等）未验证而整组拒绝——瓦数目标由适配层
/// 在逐项结果中如实报告「不受支持」，而不是静默替换或隐瞒。
/// </para>
/// <para>
/// WMI（MSI_ACPI）实现不实现本接口：它承载瓦数目标语义，继续按原门控执行。
/// </para>
/// </remarks>
public interface IScenarioPerformanceHardware
{
}

/// <summary>
/// 「只写固件性能场景」的恢复通道：把 EC Shift 场景恢复到指定档位，
/// <b>不触碰瓦数目标、睿频上限与风扇曲线</b>。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由：用户主动退出程序时要把性能模式恢复到固件默认场景（<c>0xC1</c> 平衡），
/// 这一动作的语义是「撤销本软件对场景寄存器的改动」，<b>不是</b>「重新应用某个档位的
/// 配置表」。若走 <see cref="IPerformanceHardware.ApplyAsync"/>，会连带把该档位的
/// CPU 功耗目标写进 MSR 0x610，等于在退出时又制造一次用户没有要求的配置变更。
/// </para>
/// <para>
/// 本接口<b>不含</b>「读回」以外的任何推断：实现方仍须写入后立即读回并逐项报告，
/// 未实现本接口的适配层在恢复请求中一律如实返回 <see cref="OperationStatus.Unsupported"/>，
/// 不得降级成「用 ApplyAsync 顶替」。
/// </para>
/// </remarks>
public interface IScenarioRestoreChannel
{
    /// <summary>仅写入固件性能场景编码并读回核对。</summary>
    /// <param name="stage">目标档位。须为已开放档位。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>单项结果（键为 <c>stage</c>）。</returns>
    Task<ControlResult> ApplyScenarioOnlyAsync(PerformanceStage stage, CancellationToken cancellationToken);
}
