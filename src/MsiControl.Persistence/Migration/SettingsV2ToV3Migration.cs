using MsiControl.Persistence.Settings;

namespace MsiControl.Persistence.Migration;

/// <summary>
/// 设置结构 v2 → v3：新增「上次用户确认的性能档位」（<see cref="AppSettings.LastConfirmedStage"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 纯新增字段，**无需搬运历史数据**：v2 及更早版本从未记录过用户确认的档位，
/// 因此这里一律置空，语义为「从未确认过」——启动时不改动性能模式，比猜一个档位写进硬件诚实。
/// </para>
/// <para>
/// 本步骤必须存在：<see cref="SettingsMigrator.Migrate"/> 缺步时**拒绝升级并回退默认设置**，
/// 会连带丢掉用户的窗口位置、快捷键、开机启动选择与档位功耗目标。
/// </para>
/// </remarks>
public sealed class SettingsV2ToV3Migration : ISettingsMigration
{
    /// <inheritdoc />
    public int FromVersion => 2;

    /// <inheritdoc />
    public int ToVersion => 3;

    /// <inheritdoc />
    public AppSettings Apply(AppSettings source) => source with { LastConfirmedStage = null };
}
