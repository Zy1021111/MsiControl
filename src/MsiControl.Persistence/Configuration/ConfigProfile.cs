using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Persistence.Configuration;

/// <summary>
/// 可导入/导出的硬件配置档。
/// </summary>
/// <remarks>
/// <para>
/// 一个配置档包含若干性能档位的完整配置（功耗、睿频、双风扇曲线），
/// 对应任务书 §7.3「保存 / 重命名 / 删除 / 导入 / 导出 / 标签 / 排序」中的可迁移部分。
/// </para>
/// <para>
/// <b>必须记录 <see cref="Device"/>：</b>导入时用
/// <see cref="DeviceIdentity.IsIdentified"/> 与 <see cref="DeviceIdentity.IsSameBoardAs"/>
/// 双重校验机型。未识别机型一律拒绝导入——未识别 ≠ 同机型（docs/接口与工程约定.md §3.10）。
/// </para>
/// <para>
/// 本类型只描述配置，不代表任何硬件写入。导入后须由用户**显式应用**（任务书 §7.3）。
/// </para>
/// </remarks>
/// <param name="SchemaVersion">配置档架构版本。</param>
/// <param name="Device">导出时的设备标识。</param>
/// <param name="Name">配置档名称。</param>
/// <param name="Tags">标签。</param>
/// <param name="SortOrder">排序键，值小的排在前面。</param>
/// <param name="Stages">各性能档位的配置。</param>
public sealed record ConfigProfile(
    int SchemaVersion,
    DeviceIdentity Device,
    string Name,
    IReadOnlyList<string> Tags,
    int SortOrder,
    IReadOnlyList<PerformanceSettings> Stages)
{
    /// <summary>当前配置档架构版本。</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>取得指定档位的配置。未包含该档位时返回 <see langword="null"/>。</summary>
    public PerformanceSettings? ForStage(PerformanceStage stage)
        => Stages.FirstOrDefault(s => s.Stage == stage);

    /// <summary>取得全部档位。</summary>
    public IReadOnlyList<PerformanceStage> CoveredStages
        => Stages.Select(s => s.Stage).ToArray();
}
