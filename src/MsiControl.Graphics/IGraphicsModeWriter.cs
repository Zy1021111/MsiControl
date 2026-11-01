using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices.Ms15M3;

namespace MsiControl.Graphics;

/// <summary>
/// 显卡模式的实际写入方。
/// </summary>
/// <remarks>
/// <para>
/// 把「该不该切」（<see cref="GraphicsSwitchPolicy"/>）与「怎么切」（本接口）分开：
/// 策略可脱离硬件完整测试，写入方则封装与固件/驱动交互的细节。
/// </para>
/// <para>
/// <b>当前实现为 <see cref="UnsupportedGraphicsModeWriter"/>：</b>
/// 任务 06 阶段一未能定位任何可验证的切换接口，因此不存在可用的写入路径。
/// 该接口是为「将来取得证据后接入写入」预留的**唯一**扩展点，
/// 在此之前不允许任何实现声称成功（任务书 §1 原则 6）。
/// </para>
/// <para>
/// <b>实现约束：</b>写入方不得自动重启设备，不得在启动或供电变化时被调用（任务书 §9.1）；
/// 收到取消请求时不能跳过必要的状态核对（约定 §6）。
/// </para>
/// </remarks>
public interface IGraphicsModeWriter
{
    /// <summary>
    /// 把目标模式写入硬件。
    /// </summary>
    /// <param name="mode">目标模式。</param>
    /// <param name="currentState">写入前的实际状态，供实现核对与读回比较。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>
    /// 逐项操作结果。成功必须携带可追溯的确认依据；需重启生效时返回
    /// <see cref="OperationStatus.PendingRestart"/>。
    /// </returns>
    Task<ControlResult> WriteAsync(
        GraphicsMode mode,
        GraphicsHardwareState currentState,
        CancellationToken cancellationToken);
}

/// <summary>
/// 默认写入方：本机不存在可用的切换路径，一律返回「不支持」并附依据。
/// </summary>
/// <remarks>
/// <para>
/// 依据来自 <see cref="GraphicsInterfaceEvidence.SwitchInterfaceNotLocatedReason"/>，
/// 即任务 06 阶段一的只读调查结论。
/// </para>
/// <para>
/// 本实现**不做任何硬件访问**，因此也不会产生虚假成功。
/// 当且仅当将来定位到可验证的切换接口、且 <c>graphics.mode</c> 能力达到
/// <see cref="CapabilityLevel.Verified"/> 后，才应替换为真实实现。
/// </para>
/// </remarks>
public sealed class UnsupportedGraphicsModeWriter : IGraphicsModeWriter
{
    /// <summary>共享实例。本类型无状态。</summary>
    public static UnsupportedGraphicsModeWriter Instance { get; } = new();

    /// <inheritdoc />
    public Task<ControlResult> WriteAsync(
        GraphicsMode mode,
        GraphicsHardwareState currentState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentState);
        cancellationToken.ThrowIfCancellationRequested();

        var result = ControlResult.Single(SettingResult.Unsupported(
            GraphicsSwitchPolicy.GraphicsModeSettingKey,
            GraphicsInterfaceEvidence.SwitchInterfaceNotLocatedReason));

        return Task.FromResult(result);
    }
}
