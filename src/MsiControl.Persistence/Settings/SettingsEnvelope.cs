using MsiControl.Persistence.Settings;

namespace MsiControl.Persistence.Settings;

/// <summary>
/// 设置文件的外层信封。
/// </summary>
/// <remarks>
/// 单独包一层的目的是让「版本」与「保存时间」独立于设置字段本身：
/// 即使设置结构发生字段级变化，版本号仍可被稳定读取，从而驱动迁移。
/// </remarks>
public sealed class SettingsEnvelope
{
    /// <summary>设置架构版本。</summary>
    public int SchemaVersion { get; set; } = AppSettings.CurrentSchemaVersion;

    /// <summary>保存时刻。</summary>
    public DateTimeOffset SavedAt { get; set; }

    /// <summary>设置内容。</summary>
    public AppSettings? Settings { get; set; }
}

/// <summary>设置加载来源。</summary>
public enum SettingsLoadSource
{
    /// <summary>未找到文件，使用默认设置。</summary>
    Defaults = 0,

    /// <summary>正常从文件加载。</summary>
    File = 1,

    /// <summary>文件损坏，已隔离并回退到默认设置。</summary>
    RecoveredFromCorrupt = 2,

    /// <summary>文件版本高于本程序支持，已隔离并回退到默认设置。</summary>
    RecoveredFromUnsupportedVersion = 3
}

/// <summary>
/// 设置加载结果。
/// </summary>
/// <param name="Settings">加载并规范化后的设置。</param>
/// <param name="Source">来源。</param>
/// <param name="Notes">诊断说明，含迁移与修正记录。</param>
/// <param name="QuarantinedFilePath">被隔离的原始文件路径。未隔离时为 <see langword="null"/>。</param>
public sealed record SettingsLoadResult(
    AppSettings Settings,
    SettingsLoadSource Source,
    IReadOnlyList<string> Notes,
    string? QuarantinedFilePath = null)
{
    /// <summary>是否发生了需要告知用户的降级。</summary>
    public bool IsDegraded => Source is SettingsLoadSource.RecoveredFromCorrupt
        or SettingsLoadSource.RecoveredFromUnsupportedVersion;
}

/// <summary>
/// 设置存储。
/// </summary>
/// <remarks>
/// 实现方必须保证：写入是**原子**的（先写临时文件再替换），
/// 且保留上一个可用版本作为备份，避免断电或崩溃导致配置文件被截断。
/// </remarks>
public interface ISettingsStore
{
    /// <summary>存储文件路径。</summary>
    string FilePath { get; }

    /// <summary>加载设置。实现方不得因文件损坏而抛出异常，必须降级到默认值并报告。</summary>
    SettingsLoadResult Load();

    /// <summary>保存设置。</summary>
    /// <param name="settings">要保存的设置。实现方应在写入前执行规范化。</param>
    void Save(AppSettings settings);
}
