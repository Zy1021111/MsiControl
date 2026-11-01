using MsiControl.Persistence.Settings;

namespace MsiControl.Persistence.Migration;

/// <summary>
/// 设置结构 v3 → v4：新增「开机启动后静默驻留后台」（<see cref="AppSettings.StartMinimizedOnStartup"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 纯新增字段，无需搬运历史数据：v3 及更早版本没有这个偏好，
/// 置 <see langword="false"/> 的语义是「升级前不这么做」，即仍然按
/// <see cref="AppSettings.StartupWindow"/> 显示窗口，不改变既有行为。
/// </para>
/// <para>
/// 本步骤必须存在：<see cref="SettingsMigrator.Migrate"/> 缺步时拒绝升级并回退默认设置，
/// 会连带丢掉用户的窗口位置、快捷键、开机启动选择、档位功耗目标与上次确认档位。
/// </para>
/// </remarks>
public sealed class SettingsV3ToV4Migration : ISettingsMigration
{
    /// <inheritdoc />
    public int FromVersion => 3;

    /// <inheritdoc />
    public int ToVersion => 4;

    /// <inheritdoc />
    public AppSettings Apply(AppSettings source) => source with { StartMinimizedOnStartup = false };
}
