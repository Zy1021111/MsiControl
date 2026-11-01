using Microsoft.Win32;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 开机启动项注册。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §11 / design.md §9.1：开机启动默认开启、可关闭。
/// 实现方式为当前用户的 <c>Run</c> 键，<b>不写系统级 Run</b>，不请求管理员权限，
/// 不创建计划任务或服务（design.md §13.1：不预设额外 Windows 服务）。
/// </para>
/// <para>
/// 安全约束：<b>只有用户在设置界面显式操作时才写入或删除</b>，
/// 程序启动流程本身绝不修改注册表；卸载语义上等价于删除该值。
/// </para>
/// </remarks>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MSI-Control";

    /// <summary>
    /// 开机自启时附加的命令行参数。
    /// </summary>
    /// <remarks>
    /// 用来区分「开机自启」与「用户手动双击」：只有前者才可能按
    /// <c>StartMinimizedOnStartup</c> 静默驻留，否则双击图标没反应会被当成程序没启动。
    /// </remarks>
    internal const string AutostartArgument = "--autostart";
    private const string ExpectedProcessName = "MsiControl.Desktop";

    /// <summary>
    /// 当前是否具备写入开机启动项的条件。
    /// </summary>
    /// <remarks>
    /// 开发期常以 <c>dotnet run</c> 或测试宿主启动，此时 <see cref="Environment.ProcessPath"/>
    /// 指向 <c>dotnet.exe</c>。若照此写入，会得到一个「开机启动 dotnet」的错误项，
    /// 因此明确拒绝并在界面说明原因，而不是写入一个无效项。
    /// </remarks>
    internal static bool CanRegister => Environment.ProcessPath is { } path
        && Path.GetFileNameWithoutExtension(path).Equals(ExpectedProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>读取当前是否已注册。</summary>
    internal static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// 写入或删除开机启动项。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <returns>失败原因；成功或无需操作时为 <see langword="null"/>。</returns>
    internal static string? Apply(bool enabled)
    {
        if (!CanRegister)
        {
            return "当前程序不是以自身可执行文件启动（开发主机或测试宿主），未修改开机启动项。";
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

            if (key is null)
            {
                return "无法打开当前用户的启动项注册表键。";
            }

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return "无法确定程序路径，未写入开机启动项。";
                }

                key.SetValue(ValueName, "\"" + path + "\" " + AutostartArgument, RegistryValueKind.String);
                return null;
            }

            if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return $"修改开机启动项失败：{ex.Message}";
        }
    }
}
