namespace MsiControl.Persistence.Tests;

/// <summary>
/// 测试用临时目录。
/// </summary>
/// <remarks>
/// 所有持久化测试都在临时目录内进行，避免污染真实数据目录
/// （<c>%LOCALAPPDATA%\MSI-Control</c>）。
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "msi-control-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }

    internal string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不影响断言结论。
        }
    }
}
