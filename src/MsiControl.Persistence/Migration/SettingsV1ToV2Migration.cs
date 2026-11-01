using MsiControl.Persistence.Settings;

namespace MsiControl.Persistence.Migration;

/// <summary>
/// 设置结构 v1 → v2：新增性能档位 CPU 功耗目标（<see cref="AppSettings.StageTargets"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 纯新增字段，**无需搬运历史数据**：v1 从未持久化过档位目标
/// （编辑只写内存协调器，重启即回默认），因此这里只给空表，语义为「全部沿用默认配置表」。
/// </para>
/// <para>
/// 本步骤存在的意义不是改数据，而是让 v1 设置文件能被正常升级：
/// <see cref="SettingsMigrator.Migrate"/> 缺步时**拒绝升级并回退默认设置**，
/// 那会连带丢掉用户的窗口位置、快捷键与开机启动选择。
/// </para>
/// </remarks>
public sealed class SettingsV1ToV2Migration : ISettingsMigration
{
    /// <inheritdoc />
    public int FromVersion => 1;

    /// <inheritdoc />
    public int ToVersion => 2;

    /// <inheritdoc />
    public AppSettings Apply(AppSettings source)
        => source with { StageTargets = Array.Empty<StagePowerTarget>() };
}
