using System.Text;

namespace MsiControl.Persistence.Logging;

/// <summary>
/// 有界轮转日志写入器。
/// </summary>
/// <remarks>
/// <para>
/// 文件命名：<c>msi-control.log</c> 为当前文件，<c>msi-control.1.log</c> …
/// <c>msi-control.N.log</c> 为历史文件，编号越大越旧。
/// </para>
/// <para>线程安全：内部加锁，可由采样线程与 UI 线程并发调用。</para>
/// </remarks>
public sealed class RotatingLogWriter : IRotatingLog, IDisposable
{
    /// <summary>单文件默认上限（256 KiB）。</summary>
    public const long DefaultMaxFileBytes = 256 * 1024;

    /// <summary>默认保留文件数（含当前文件）。</summary>
    public const int DefaultMaxFiles = 4;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _baseName;
    private readonly string _extension;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly LogRedactor _redactor;
    private bool _disposed;

    /// <summary>构造写入器。</summary>
    /// <param name="currentFilePath">当前日志文件路径。</param>
    /// <param name="redactor">脱敏器。为 <see langword="null"/> 时使用默认脱敏规则。</param>
    /// <param name="maxFileBytes">单文件字节上限。</param>
    /// <param name="maxFiles">保留文件数，含当前文件。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数超出合理范围。</exception>
    public RotatingLogWriter(
        string currentFilePath,
        LogRedactor? redactor = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFiles = DefaultMaxFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFilePath);

        if (maxFileBytes < 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes), maxFileBytes, "单文件上限不得小于 1 KiB。");
        }

        if (maxFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles), maxFiles, "至少保留一个文件。");
        }

        CurrentFilePath = currentFilePath;
        _directory = Path.GetDirectoryName(currentFilePath) ?? ".";
        _baseName = Path.GetFileNameWithoutExtension(currentFilePath);
        _extension = Path.GetExtension(currentFilePath);
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        _redactor = redactor ?? new LogRedactor();
    }

    /// <inheritdoc />
    public string CurrentFilePath { get; }

    /// <summary>总字节上限。</summary>
    public long MaxTotalBytes => _maxFileBytes * _maxFiles;

    /// <summary>单文件字节上限。</summary>
    public long MaxFileBytes => _maxFileBytes;

    /// <summary>保留文件数，含当前文件。</summary>
    public int MaxFiles => _maxFiles;

    /// <inheritdoc />
    public IReadOnlyList<string> Files
    {
        get
        {
            lock (_gate)
            {
                return EnumerateFiles();
            }
        }
    }

    /// <inheritdoc />
    public void Write(LogLevel level, string category, string message)
    {
        if (_disposed)
        {
            return;
        }

        var safeCategory = _redactor.Redact(category ?? string.Empty);
        var safeMessage = _redactor.Redact(message ?? string.Empty);
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:ss.fffzzz} [{LevelText(level)}] {safeCategory}: {safeMessage}{Environment.NewLine}");

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                var existingLength = File.Exists(CurrentFilePath) ? new FileInfo(CurrentFilePath).Length : 0;
                if (existingLength + line.Length > _maxFileBytes)
                {
                    Rotate();
                }

                File.AppendAllText(CurrentFilePath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 日志写入失败不得影响主流程，也不得递归记录日志。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    /// <inheritdoc />
    public void Export(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        lock (_gate)
        {
            using var writer = new StreamWriter(destinationPath, append: false, Encoding.UTF8);
            writer.WriteLine($"# MSI-Control 日志导出 {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            writer.WriteLine("# 说明：日志在写入时已完成脱敏，不含设备序列号与用户目录路径。");
            writer.WriteLine();

            // 从最旧到最新，保证时间顺序可读。
            foreach (var file in EnumerateFiles())
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                writer.WriteLine($"===== {Path.GetFileName(file)} =====");
                try
                {
                    writer.Write(File.ReadAllText(file));
                }
                catch (IOException)
                {
                    writer.WriteLine("(该文件读取失败)");
                }

                writer.WriteLine();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }

    private void Rotate()
    {
        // 删除最旧的一份，再把编号整体后移，最后清空当前文件位置。
        var oldest = PathForIndex(_maxFiles - 1);
        if (File.Exists(oldest))
        {
            TryDelete(oldest);
        }

        for (var index = _maxFiles - 2; index >= 1; index--)
        {
            var source = PathForIndex(index);
            if (!File.Exists(source))
            {
                continue;
            }

            TryMove(source, PathForIndex(index + 1), overwrite: true);
        }

        if (File.Exists(CurrentFilePath))
        {
            TryMove(CurrentFilePath, PathForIndex(1), overwrite: true);
        }
    }

    private IReadOnlyList<string> EnumerateFiles()
    {
        var result = new List<string>(_maxFiles);

        // 最旧在前：索引大的先加入。
        for (var index = _maxFiles - 1; index >= 1; index--)
        {
            var path = PathForIndex(index);
            if (File.Exists(path))
            {
                result.Add(path);
            }
        }

        if (File.Exists(CurrentFilePath))
        {
            result.Add(CurrentFilePath);
        }

        return result;
    }

    private string PathForIndex(int index) => Path.Combine(_directory, $"{_baseName}.{index}{_extension}");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryMove(string source, string destination, bool overwrite)
    {
        try
        {
            File.Move(source, destination, overwrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO "
    };
}
