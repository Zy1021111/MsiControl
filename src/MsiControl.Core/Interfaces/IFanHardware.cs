using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 风扇硬件访问。
/// </summary>
/// <remarks>
/// <para>
/// 本接口只负责<b>真实访问</b>。曲线校验、配置应用流程、校准流程与恢复策略放在业务模块
/// （首批源码框架 §4）。
/// </para>
/// <para>
/// <b>写入开始后，取消请求不能让实现跳过必要的状态核对</b>（任务书 §16）。
/// 实现应在收到取消时仍完成读回，再抛出取消异常。
/// </para>
/// <para>
/// 能力等级未达到 <see cref="CapabilityLevel.Verified"/> 前，调用方不得调用写入方法
/// （任务书 §16.1）。具体可用性以 <see cref="Models.DeviceCapabilities"/> 为准。
/// </para>
/// </remarks>
public interface IFanHardware
{
    /// <summary>读取当前控制模式与设备上已生效的曲线。</summary>
    Task<FanHardwareState> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 应用一条自定义曲线到指定通道。
    /// </summary>
    /// <param name="channel">目标通道。</param>
    /// <param name="curve">曲线。调用方须保证 <paramref name="curve"/> 的通道与 <paramref name="channel"/> 一致。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>逐项结果连同可追溯的确认依据。</returns>
    Task<ControlResult> ApplyCurveAsync(FanChannel channel, FanCurve curve, CancellationToken cancellationToken);

    /// <summary>切回固件自动策略。</summary>
    /// <remarks>对应任务书中「切回自动入口」。恢复的是固件策略，不是 BIOS 出厂重置。</remarks>
    Task<ControlResult> SetAutomaticAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 切换强冷（全速）。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <remarks>
    /// 实现方应在启用前记录有效策略，并在关闭后恢复并核对。
    /// 强冷<b>不与狂暴模式强制绑定</b>（任务书 §7.5）。
    /// </remarks>
    Task<ControlResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// 可选扩展：支持静音 / advanced 档的风扇模式切换（EC 0xD4 三编码）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IFanHardware"/> 只保证「切回自动」；本接口由具备三编码写入证据的
/// 适配层（<c>FanHardwareMs15M3Ec</c>）额外实现。协调器以模式匹配探测，
/// 未实现时如实返回 <see cref="OperationStatus.Unsupported"/>，不猜测。
/// </para>
/// </remarks>
public interface IFanModeSwitch
{
    /// <summary>切换到固件静音模式（EC 0xD4=0x1D）。</summary>
    Task<ControlResult> SetSilentAsync(CancellationToken cancellationToken);

    /// <summary>切换到 advanced 模式（EC 0xD4=0x8D）。</summary>
    /// <remarks>实测该模式下固件仍自动调速（HWE §17.3）；与 auto 的差异由固件曲线决定。</remarks>
    Task<ControlResult> SetAdvancedAsync(CancellationToken cancellationToken);
}
