namespace MsiControl.Persistence;

/// <summary>
/// 本地数据路径解析。
/// </summary>
/// <remarks>
/// 默认落在 <c>%LOCALAPPDATA%\MSI-Control</c>：不写注册表之外的系统位置，
/// 不使用漫游目录，避免配置跟随账号漫游到其他机器。
/// 构造函数允许注入基准目录，便于测试与后续「便携模式」扩展。
/// </remarks>
public sealed class AppPaths
{
    /// <summary>应用数据子目录名。</summary>
    public const string ApplicationFolderName = "MSI-Control";

    /// <summary>构造路径解析器。</summary>
    /// <param name="rootDirectory">
    /// 数据根目录。为 <see langword="null"/> 时使用 <c>%LOCALAPPDATA%\MSI-Control</c>。
    /// </param>
    public AppPaths(string? rootDirectory = null)
    {
        RootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ApplicationFolderName)
            : rootDirectory;
    }

    /// <summary>数据根目录。</summary>
    public string RootDirectory { get; }

    /// <summary>主设置文件路径。</summary>
    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>设置文件的上一个版本备份路径。</summary>
    public string SettingsBackupFilePath => Path.Combine(RootDirectory, "settings.bak.json");

    /// <summary>配置档（可导入/导出）目录。</summary>
    public string ProfilesDirectory => Path.Combine(RootDirectory, "profiles");

    /// <summary>日志目录。</summary>
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>当前日志文件路径。</summary>
    public string CurrentLogFilePath => Path.Combine(LogDirectory, "msi-control.log");

    /// <summary>创建所需目录。已存在时为空操作。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(ProfilesDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    /// <summary>
    /// 生成一个带时间戳的路径，用于隔离损坏文件或备份。
    /// </summary>
    /// <param name="directory">目标目录。</param>
    /// <param name="fileName">基础文件名（含扩展名）。</param>
    /// <param name="prefix">前缀，通常为 <c>corrupt</c> 或 <c>backup</c>。</param>
    /// <param name="timestamp">时间戳。</param>
    public static string TimestampedPath(
        string directory,
        string fileName,
        string prefix,
        DateTimeOffset timestamp)
        => Path.Combine(
            directory,
            $"{prefix}-{timestamp:yyyyMMdd-HHmmss}-{fileName}");
}
