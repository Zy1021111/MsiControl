using System.Text.RegularExpressions;

namespace MsiControl.Persistence.Logging;

/// <summary>日志级别。</summary>
public enum LogLevel
{
    /// <summary>调试信息。默认不写盘，仅在诊断模式启用。</summary>
    Debug = 0,

    /// <summary>一般信息。</summary>
    Info = 1,

    /// <summary>警告：功能降级但仍可继续。</summary>
    Warning = 2,

    /// <summary>错误：操作失败。</summary>
    Error = 3
}

/// <summary>
/// 日志脱敏。
/// </summary>
/// <remarks>
/// 任务书 §12、§22：导出日志不得包含设备序列号与不必要的个人路径。
/// 本类型在**写入时**就完成脱敏，而不是等到导出时——
/// 写入时脱敏能保证落盘文件本身就不含敏感信息，避免磁盘上留下原始副本。
/// </remarks>
public sealed class LogRedactor
{
    private static readonly Regex UserPathPattern = new(
        @"[A-Za-z]:\\Users\\[^\\\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LegacyUserPathPattern = new(
        @"[A-Za-z]:\\Documents and Settings\\[^\\\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SerialAssignmentPattern = new(
        @"(?<key>(?i:serial(?:\s*number)?|序列号|sn))\s*[:=]\s*(?<value>\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string? _userProfilePath;

    /// <summary>构造脱敏器。</summary>
    /// <param name="userProfilePath">
    /// 当前用户目录。提供后会替换为 <c>%USERPROFILE%</c>。
    /// 即使不提供，通用路径正则仍会覆盖 <c>C:\Users\&lt;name&gt;</c> 形式。
    /// </param>
    public LogRedactor(string? userProfilePath = null)
    {
        _userProfilePath = string.IsNullOrWhiteSpace(userProfilePath) ? null : userProfilePath.TrimEnd('\\');
    }

    /// <summary>对一行日志文本脱敏。</summary>
    public string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var result = message;

        if (_userProfilePath is not null)
        {
            result = result.Replace(_userProfilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        result = UserPathPattern.Replace(result, "%USERPROFILE%");
        result = LegacyUserPathPattern.Replace(result, "%USERPROFILE%");
        // 序列号类赋值一律替换为掩码：本项目不收集序列号，出现即视为异常输入。
        result = SerialAssignmentPattern.Replace(result, "${key}=***");

        return result;
    }
}

/// <summary>
/// 有界、可轮转、可导出的本地日志。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §12「日志限制总量并轮转」、§20「有界日志与缓存」、
/// design.md §14.1「日志轮转并限制总量」。
/// </para>
/// <para>
/// 总量上限 = 单文件上限 × 文件数，是**硬上限**而非「尽力而为」：
/// 一旦超出就删除最旧的一份，因此磁盘占用不会随时间增长。
/// </para>
/// </remarks>
public interface IRotatingLog
{
    /// <summary>当前写入的文件路径。</summary>
    string CurrentFilePath { get; }

    /// <summary>现有日志文件，按从旧到新排列。</summary>
    IReadOnlyList<string> Files { get; }

    /// <summary>写入一行日志。实现方负责脱敏与轮转。</summary>
    void Write(LogLevel level, string category, string message);

    /// <summary>把全部日志合并导出到目标文件。</summary>
    /// <param name="destinationPath">目标路径。已存在时被覆盖。</param>
    void Export(string destinationPath);
}
