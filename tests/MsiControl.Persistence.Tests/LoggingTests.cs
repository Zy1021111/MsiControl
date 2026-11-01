using MsiControl.Persistence.Logging;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 日志脱敏与有界轮转测试。
/// </summary>
/// <remarks>
/// 覆盖重点：脱敏在写入时完成（磁盘上不留原始信息）、总量有硬上限（不会无限增长）。
/// </remarks>
public sealed class LoggingTests
{
    [Theory]
    [InlineData(@"打开 C:\Users\yzy\AppData\Local\MSI-Control", "yzy")]
    [InlineData(@"路径 C:\Users\Some User\file.txt 无效", "Some User")]
    [InlineData(@"旧路径 C:\Documents and Settings\OldUser\x", "OldUser")]
    public void Redact_RemovesUserDirectoryPaths(string input, string secret)
    {
        var redactor = new LogRedactor(@"C:\Users\yzy");

        var output = redactor.Redact(input);

        Assert.DoesNotContain(secret, output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("序列号: ABC123456")]
    [InlineData("SerialNumber=XYZ7890")]
    [InlineData("SN: 0000ABCD")]
    public void Redact_MasksSerialNumberLikeAssignments(string input)
    {
        var redactor = new LogRedactor();

        var output = redactor.Redact(input);

        Assert.Contains("=***", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC123456", output, StringComparison.Ordinal);
        Assert.DoesNotContain("XYZ7890", output, StringComparison.Ordinal);
        Assert.DoesNotContain("0000ABCD", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Redact_KeepsOrdinaryTextIntact()
    {
        var redactor = new LogRedactor(@"C:\Users\yzy");
        const string message = "采样失败：MSI_ACPI 返回 WBEM_E_NOT_SUPPORTED。";

        Assert.Equal(message, redactor.Redact(message));
    }

    [Fact]
    public void Redact_HandlesNullAndEmpty()
    {
        var redactor = new LogRedactor();

        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
        Assert.Equal(string.Empty, redactor.Redact(null!));
    }

    [Fact]
    public void Write_ProducesReadableLineWithLevelAndCategory()
    {
        using var temp = new TempDirectory();
        using var log = new RotatingLogWriter(temp.File("msi-control.log"), new LogRedactor(@"C:\Users\yzy"));

        log.Write(LogLevel.Warning, "Settings", "设置文件版本 2 高于本程序支持的 1。");

        var text = File.ReadAllText(temp.File("msi-control.log"));
        Assert.Contains("[WARN ]", text, StringComparison.Ordinal);
        Assert.Contains("Settings:", text, StringComparison.Ordinal);
        Assert.Contains("高于本程序支持", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_RotatesAndKeepsFileCountBounded()
    {
        using var temp = new TempDirectory();
        using var log = new RotatingLogWriter(
            temp.File("msi-control.log"),
            new LogRedactor(),
            maxFileBytes: 1024,
            maxFiles: 3);

        for (var index = 0; index < 400; index++)
        {
            log.Write(LogLevel.Info, "Loop", $"第 {index} 行日志，用于触发轮转。");
        }

        Assert.True(log.Files.Count <= 3, "文件数不得超过上限。");
        Assert.True(log.Files.Count > 1, "写入量超过单文件上限后应当发生轮转。");
        Assert.Equal(3, log.MaxFiles);
        Assert.Equal(3072, log.MaxTotalBytes);
    }

    [Fact]
    public void Write_RedactsBeforePersisting()
    {
        using var temp = new TempDirectory();
        using var log = new RotatingLogWriter(temp.File("msi-control.log"), new LogRedactor(@"C:\Users\yzy"));

        log.Write(LogLevel.Error, "Device", @"读取 C:\Users\yzy\secret\dump.bin 失败");

        var text = File.ReadAllText(temp.File("msi-control.log"));
        Assert.DoesNotContain("yzy", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Export_ConcatenatesOldestFirstAndAddsDisclaimer()
    {
        using var temp = new TempDirectory();
        using var log = new RotatingLogWriter(
            temp.File("msi-control.log"),
            new LogRedactor(),
            maxFileBytes: 1024,
            maxFiles: 3);

        // 行数刻意控制在保留窗口之内（3 文件 × 1 KiB），
        // 否则最旧的内容会随轮转被删除，无法用于断言「顺序正确」。
        for (var index = 0; index < 30; index++)
        {
            log.Write(LogLevel.Info, "Loop", $"第 {index} 行。");
        }

        var exportPath = temp.File("export.txt");
        log.Export(exportPath);

        var text = File.ReadAllText(exportPath);
        Assert.Contains("MSI-Control 日志导出", text, StringComparison.Ordinal);
        Assert.Contains("不含设备序列号", text, StringComparison.Ordinal);
        Assert.Contains("第 0 行。", text, StringComparison.Ordinal);
        Assert.Contains("第 29 行。", text, StringComparison.Ordinal);

        // 顺序必须是从旧到新，便于人工按时间阅读。
        Assert.True(
            text.IndexOf("第 0 行。", StringComparison.Ordinal) < text.IndexOf("第 29 行。", StringComparison.Ordinal),
            "导出内容应按从旧到新排列。");
    }

    [Fact]
    public void Constructor_RejectsUnreasonableLimits()
    {
        using var temp = new TempDirectory();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new RotatingLogWriter(temp.File("a.log"), null, maxFileBytes: 10, maxFiles: 2);
        });

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = new RotatingLogWriter(temp.File("a.log"), null, maxFileBytes: 4096, maxFiles: 0);
        });

        Assert.Throws<ArgumentException>(() =>
        {
            _ = new RotatingLogWriter("  ");
        });
    }

    [Fact]
    public void Write_AfterDispose_IsIgnoredInsteadOfThrowing()
    {
        using var temp = new TempDirectory();
        var log = new RotatingLogWriter(temp.File("msi-control.log"));
        log.Dispose();

        // 退出流程中可能出现「已释放仍收到日志」的竞态，必须静默而非抛异常。
        log.Write(LogLevel.Info, "Shell", "释放后的写入。");

        Assert.False(File.Exists(temp.File("msi-control.log")));
    }
}
