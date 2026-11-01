using MsiControl.Core.Models;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 监控数据源。
/// </summary>
/// <remarks>
/// <para>
/// 实现方负责选择数据来源、统一单位与判断有效期。
/// <b>不得修改功耗和风扇</b>（模块职责见任务书 §15 与首批源码框架 §2）。
/// </para>
/// <para>
/// 调用方约定：整个应用只有<b>一个</b>共享采样任务调用本接口，
/// 各窗口订阅结果，不各自创建计时器读取硬件（任务书 §16）。
/// </para>
/// </remarks>
public interface IMonitoringProvider
{
    /// <summary>
    /// 执行一次完整采样。
    /// </summary>
    /// <param name="cancellationToken">取消标记。实现方不得吞掉 <see cref="OperationCanceledException"/>。</param>
    /// <returns>本次采样快照。无法读取的项必须以对应的 <see cref="ReadingStatus"/> 表达，
    /// 不得沿用上一个有效值。</returns>
    Task<MonitoringSnapshot> ReadAsync(CancellationToken cancellationToken);
}
