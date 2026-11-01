using MsiControl.Core.Models;

namespace MsiControl.Application.Control;

/// <summary>
/// 一次档位配置目标更新的结果。
/// </summary>
/// <param name="Ok">是否已接受并生效（软件配置层）。</param>
/// <param name="Problems">拒绝原因列表。<see cref="Ok"/> 为 <see langword="true"/> 时为空。</param>
/// <param name="Settings">接受时的最终配置（与提交值一致）。</param>
/// <remarks>
/// 更新只作用于<b>软件配置目标</b>，不触发任何硬件写入；
/// 硬件写入仍发生在档位切换时，并按能力门控逐项执行与如实报告。
/// </remarks>
public sealed record StageSettingsUpdateResult(
    bool Ok,
    IReadOnlyList<string> Problems,
    PerformanceSettings? Settings);
