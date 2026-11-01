using MsiControl.Persistence.Settings;

namespace MsiControl.Persistence.Migration;

/// <summary>
/// 一次设置结构迁移。
/// </summary>
/// <remarks>
/// 实现方只负责把 <see cref="FromVersion"/> 的结构转换为 <c>FromVersion + 1</c> 的结构，
/// 不负责取值范围校正（那是 <c>AppSettings.Normalize</c> 的职责）。
/// </remarks>
public interface ISettingsMigration
{
    /// <summary>源版本号。</summary>
    int FromVersion { get; }

    /// <summary>目标版本号，恒为 <see cref="FromVersion"/> + 1。</summary>
    int ToVersion { get; }

    /// <summary>执行迁移。</summary>
    /// <param name="source">源设置的原始反序列化结果。</param>
    /// <returns>迁移后的设置。</returns>
    AppSettings Apply(AppSettings source);
}

/// <summary>
/// 设置迁移链。
/// </summary>
/// <remarks>
/// <para>
/// 迁移表必须覆盖 <c>1 → AppSettings.CurrentSchemaVersion</c> 的每一步：
/// <see cref="Migrate"/> 缺步时拒绝升级（回退默认设置），会连带丢掉窗口位置、快捷键等
/// 与版本改动无关的用户选择，所以纯新增字段也必须登记一步。
/// </para>
/// <para>
/// 「无版本号的旧文件」不通过迁移表处理，而由 <see cref="JsonSettingsStore"/> 在解析阶段
/// 直接视为当前版本（见其 <c>Load</c> 的兼容分支），因为它缺少的是**信封**而不是字段。
/// </para>
/// </remarks>
public static class SettingsMigrator
{
    /// <summary>默认迁移表。v1 是首个版本，因此表从 v1→v2 起。</summary>
    public static IReadOnlyList<ISettingsMigration> DefaultSteps { get; } =
        new ISettingsMigration[]
        {
            new SettingsV1ToV2Migration(),
            new SettingsV2ToV3Migration(),
            new SettingsV3ToV4Migration()
        };

    /// <summary>
    /// 把设置沿迁移链升级到目标版本。
    /// </summary>
    /// <param name="settings">已反序列化的设置。</param>
    /// <param name="targetVersion">目标版本。</param>
    /// <param name="steps">迁移步骤集合。</param>
    /// <param name="notes">迁移说明输出。</param>
    /// <returns>升级后的设置。缺失步骤时为 <see langword="null"/>，表示无法安全升级。</returns>
    /// <remarks>
    /// 缺失中间步骤时**拒绝升级**而不是「尽力而为」：
    /// 部分迁移会让用户拿到一个字段混搭的配置，比回退默认值更难排查。
    /// </remarks>
    public static AppSettings? Migrate(
        AppSettings settings,
        int targetVersion,
        IReadOnlyList<ISettingsMigration> steps,
        ICollection<string> notes)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(notes);

        var current = settings;
        var guard = 0;

        while (current.SchemaVersion < targetVersion)
        {
            if (++guard > 64)
            {
                notes.Add("迁移链出现循环，已中止。");
                return null;
            }

            var step = steps.FirstOrDefault(s => s.FromVersion == current.SchemaVersion);
            if (step is null)
            {
                notes.Add(
                    $"缺少从版本 {current.SchemaVersion} 到 {current.SchemaVersion + 1} 的迁移步骤，" +
                    "无法安全升级，将回退到默认设置。");
                return null;
            }

            current = step.Apply(current) with { SchemaVersion = step.ToVersion };
            notes.Add($"设置已从版本 {step.FromVersion} 迁移到 {step.ToVersion}。");
        }

        return current;
    }
}
