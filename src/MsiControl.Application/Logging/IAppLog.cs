namespace MsiControl.Application.Logging;

/// <summary>应用层日志级别。</summary>
/// <remarks>
/// 刻意不复用持久化层的 <c>LogLevel</c>：应用层不应为写一行日志而依赖具体的落盘实现。
/// 这是 <c>MsiControl.Application</c> 保持「零持久化依赖」的一部分。
/// </remarks>
public enum AppLogLevel
{
    /// <summary>调试信息。</summary>
    Debug = 0,

    /// <summary>一般信息。</summary>
    Info = 1,

    /// <summary>警告：功能降级但仍可继续。</summary>
    Warning = 2,

    /// <summary>错误：操作失败。</summary>
    Error = 3
}

/// <summary>应用层日志出口。</summary>
/// <remarks>
/// 由宿主（桌面壳）适配到真实日志实现。应用层只写、不读、不管理文件。
/// </remarks>
public interface IAppLog
{
    /// <summary>写入一行日志。实现方负责脱敏与落盘。</summary>
    void Write(AppLogLevel level, string category, string message);
}

/// <summary>不写任何日志的实现。用于测试与无宿主的装配。</summary>
public sealed class NullAppLog : IAppLog
{
    /// <summary>共享实例。</summary>
    public static NullAppLog Instance { get; } = new();

    private NullAppLog()
    {
    }

    /// <inheritdoc />
    public void Write(AppLogLevel level, string category, string message)
    {
    }
}

/// <summary>把委托适配为 <see cref="IAppLog"/>。便于宿主一行接入既有日志。</summary>
public sealed class DelegateAppLog : IAppLog
{
    private readonly Action<AppLogLevel, string, string> _write;

    /// <summary>构造适配器。</summary>
    /// <param name="write">写入委托，参数依次为级别、分类、消息。</param>
    public DelegateAppLog(Action<AppLogLevel, string, string> write)
    {
        _write = write ?? throw new ArgumentNullException(nameof(write));
    }

    /// <inheritdoc />
    public void Write(AppLogLevel level, string category, string message) => _write(level, category, message);
}
