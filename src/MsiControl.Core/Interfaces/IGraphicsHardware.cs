using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 显卡模式硬件访问。
/// </summary>
/// <remarks>
/// <para>
/// <b>本接口不自动重启设备</b>，也不在启动或供电变化时被自动调用（任务书 §9）。
/// 是否重启、何时重启由上层与用户决定。
/// </para>
/// <para>
/// 禁止以「设备管理器禁用独显」冒充 <see cref="GraphicsMode.Integrated"/>，
/// 禁止以「应用 GPU 偏好」冒充 MUX 切换（任务书 §9.3）。
/// </para>
/// </remarks>
public interface IGraphicsHardware
{
    /// <summary>读取当前实际模式、待生效模式与能力信息。</summary>
    Task<GraphicsHardwareState> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 请求切换到目标模式。
    /// </summary>
    /// <param name="mode">目标模式。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>
    /// 结果状态可能为 <see cref="OperationStatus.PendingRestart"/>，表示已写入但需重启或 S5 关机再开机生效。
    /// </returns>
    Task<ControlResult> RequestModeAsync(GraphicsMode mode, CancellationToken cancellationToken);
}
