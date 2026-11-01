using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MsiControl.Persistence.Migration;
using MsiControl.Persistence.Settings;

namespace MsiControl.Persistence.Settings;

/// <summary>
/// 基于 JSON 文件的设置存储。
/// </summary>
/// <remarks>
/// <para>
/// 行为约定：
/// <list type="bullet">
/// <item><b>原子写入</b>：先写临时文件再整体替换，避免断电导致配置被截断。</item>
/// <item><b>保留备份</b>：覆盖前把上一个可用版本复制为 <c>settings.bak.json</c>。</item>
/// <item><b>损坏隔离</b>：解析失败时把原文件改名为 <c>corrupt-时间戳-…</c> 并回退默认值，
///       不静默丢弃证据。</item>
/// <item><b>版本管控</b>：文件版本高于本程序时拒绝加载并隔离，不尝试「尽力解析」。</item>
/// </list>
/// </para>
/// <para>
/// 本类型不记录、不写入任何设备序列号（任务书 §4、§22）。
/// </para>
/// </remarks>
public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        // 设置文件需要人手可读：中文不转义为 \uXXXX。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _backupFilePath;

    /// <summary>构造存储。</summary>
    /// <param name="filePath">设置文件路径。</param>
    /// <param name="backupFilePath">备份文件路径。</param>
    public JsonSettingsStore(string filePath, string backupFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupFilePath);
        FilePath = filePath;
        _backupFilePath = backupFilePath;
    }

    /// <summary>按 <see cref="AppPaths"/> 构造存储。</summary>
    public JsonSettingsStore(AppPaths paths)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).SettingsFilePath,
            paths.SettingsBackupFilePath)
    {
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public SettingsLoadResult Load()
    {
        var notes = new List<string>();

        if (!File.Exists(FilePath))
        {
            notes.Add("未找到设置文件，使用默认设置。");
            return new SettingsLoadResult(AppSettings.CreateDefault(), SettingsLoadSource.Defaults, notes);
        }

        string text;
        try
        {
            text = File.ReadAllText(FilePath);
        }
        catch (IOException ex)
        {
            notes.Add($"读取设置文件失败：{ex.Message}。已回退到默认设置，原文件保持不变。");
            return new SettingsLoadResult(AppSettings.CreateDefault(), SettingsLoadSource.Defaults, notes);
        }
        catch (UnauthorizedAccessException ex)
        {
            notes.Add($"读取设置文件被拒绝：{ex.Message}。已回退到默认设置，原文件保持不变。");
            return new SettingsLoadResult(AppSettings.CreateDefault(), SettingsLoadSource.Defaults, notes);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            notes.Add("设置文件为空，使用默认设置。");
            return new SettingsLoadResult(AppSettings.CreateDefault(), SettingsLoadSource.Defaults, notes);
        }

        int declaredVersion;
        AppSettings? candidate;
        bool isLegacyEnvelope;

        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Quarantine(text, notes, "设置文件根节点不是对象。");
            }

            if (TryFindProperty(root, "schemaVersion", out var versionElement)
                && versionElement.ValueKind == JsonValueKind.Number)
            {
                declaredVersion = versionElement.GetInt32();
                candidate = JsonSerializer.Deserialize<SettingsEnvelope>(text, SerializerOptions)?.Settings;
                isLegacyEnvelope = false;
            }
            else
            {
                // 无版本号的旧文件：缺少的是「信封」而不是字段，因此按首个版本处理。
                declaredVersion = AppSettings.CurrentSchemaVersion;
                candidate = JsonSerializer.Deserialize<AppSettings>(text, SerializerOptions);
                isLegacyEnvelope = true;
            }
        }
        catch (JsonException ex)
        {
            return Quarantine(text, notes, $"设置文件解析失败：{ex.Message}");
        }

        if (candidate is null)
        {
            return Quarantine(text, notes, "设置文件缺少 settings 节点或内容为空。");
        }

        if (isLegacyEnvelope)
        {
            notes.Add("设置文件缺少版本信封，已按当前版本重新解释并将在下次保存时补全。");
        }

        if (declaredVersion > AppSettings.CurrentSchemaVersion)
        {
            return Quarantine(
                text,
                notes,
                $"设置文件版本 {declaredVersion} 高于本程序支持的 {AppSettings.CurrentSchemaVersion}。");
        }

        if (declaredVersion < AppSettings.CurrentSchemaVersion)
        {
            notes.Add($"设置文件版本为 {declaredVersion}，开始迁移。");
            var migrated = SettingsMigrator.Migrate(
                candidate,
                AppSettings.CurrentSchemaVersion,
                SettingsMigrator.DefaultSteps,
                notes);

            if (migrated is null)
            {
                return Quarantine(text, notes, "缺少必要的迁移步骤，拒绝加载旧版本设置。");
            }

            candidate = migrated;
        }

        var normalization = candidate.Normalize();
        notes.AddRange(normalization.Notes);

        return new SettingsLoadResult(normalization.Settings, SettingsLoadSource.File, notes);
    }

    /// <inheritdoc />
    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = settings.Normalize().Settings with { SchemaVersion = AppSettings.CurrentSchemaVersion };
        var envelope = new SettingsEnvelope
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            SavedAt = DateTimeOffset.Now,
            Settings = normalized
        };

        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var payload = JsonSerializer.Serialize(envelope, SerializerOptions);
        var temporaryPath = FilePath + ".tmp";

        File.WriteAllText(temporaryPath, payload);

        if (File.Exists(FilePath))
        {
            // 保留上一个可用版本；备份失败不应阻断保存，否则用户会因为备份问题丢配置。
            try
            {
                File.Copy(FilePath, _backupFilePath, overwrite: true);
            }
            catch (IOException)
            {
                // 忽略：备份是尽力而为。
            }
            catch (UnauthorizedAccessException)
            {
                // 忽略：同上。
            }
        }

        File.Move(temporaryPath, FilePath, overwrite: true);
    }

    private SettingsLoadResult Quarantine(string originalText, List<string> notes, string reason)
    {
        notes.Add($"{reason} 已隔离原文件并回退到默认设置。");

        string? quarantined = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            quarantined = AppPaths.TimestampedPath(
                directory ?? ".",
                Path.GetFileName(FilePath),
                "corrupt",
                DateTimeOffset.Now);

            File.Move(FilePath, quarantined, overwrite: true);
        }
        catch (IOException ex)
        {
            notes.Add($"隔离原文件失败：{ex.Message}");
            quarantined = null;
        }
        catch (UnauthorizedAccessException ex)
        {
            notes.Add($"隔离原文件失败：{ex.Message}");
            quarantined = null;
        }

        var source = reason.Contains("高于本程序支持", StringComparison.Ordinal)
            ? SettingsLoadSource.RecoveredFromUnsupportedVersion
            : SettingsLoadSource.RecoveredFromCorrupt;

        return new SettingsLoadResult(AppSettings.CreateDefault(), source, notes, quarantined);
    }

    private static bool TryFindProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
