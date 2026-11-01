using System.Diagnostics;

namespace MsiControl.Performance;

/// <summary>
/// 探测指定进程名是否有实例在运行。
/// </summary>
/// <remarks>
/// 抽象成接口的唯一目的是让冲突检测可以脱离真实环境测试。
/// 实现方<b>只允许查询是否存在</b>，不得结束、暂停或修改任何第三方进程。
/// </remarks>
public interface IProcessPresenceProbe
{
    /// <summary>
    /// 查询哪些进程名当前有实例在运行。
    /// </summary>
    /// <param name="processNames">不带扩展名的进程名。</param>
    /// <returns>实际在运行的进程名子集。顺序与传入顺序一致。</returns>
    IReadOnlyList<string> FindRunning(IReadOnlyList<string> processNames);
}

/// <summary>
/// 基于 <see cref="Process"/> 的进程存在性探测（只读）。
/// </summary>
/// <remarks>
/// 查询失败（权限、进程已退出等）时按「未运行」处理并静默跳过：
/// 冲突检测是辅助信息，<b>不得因探测失败而阻断性能设置的正常流程</b>。
/// </remarks>
public sealed class WindowsProcessPresenceProbe : IProcessPresenceProbe
{
    /// <inheritdoc />
    public IReadOnlyList<string> FindRunning(IReadOnlyList<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);

        var running = new List<string>();
        foreach (var name in processNames)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            try
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0)
                    {
                        running.Add(name);
                    }
                }
                finally
                {
                    foreach (var process in processes)
                    {
                        process.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // 查询失败不等于「在运行」，也不等于「不在运行」：这里选择不计入冲突，
                // 因为它只是辅助信息，不能据此判定第三方软件一定在覆盖设置。
            }
        }

        return running;
    }
}

/// <summary>
/// 一个可识别的、可能与 MSI-Control 争抢同一硬件参数的第三方程序。
/// </summary>
/// <param name="DisplayName">面向用户的名称。</param>
/// <param name="ProcessNames">不带扩展名的进程名。</param>
/// <param name="OverlappingSettings">重叠的设置项标识，取值见 <see cref="PerformanceSettingKeys"/>。</param>
public sealed record ThirdPartyConflictSource(
    string DisplayName,
    IReadOnlyList<string> ProcessNames,
    IReadOnlyList<string> OverlappingSettings);

/// <summary>
/// 一次冲突检测的结论。
/// </summary>
/// <param name="DisplayName">面向用户的名称。</param>
/// <param name="OverlappingSettings">重叠的设置项标识。</param>
/// <param name="Guidance">处置建议，必含「不修改、不停止、不卸载、不无限重写」的边界说明。</param>
public sealed record ThirdPartyConflict(
    string DisplayName,
    IReadOnlyList<string> OverlappingSettings,
    string Guidance);

/// <summary>
/// 第三方控制工具共存检测。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §11 与 §4.1 A8：XTU 与 Afterburner 已安装在本机，重叠参数可能互相覆盖。
/// 本软件的处理边界是：<b>检测并报告、显示被覆盖状态、不通过无休止重写争夺控制、
/// 不修改或删除其配置、不自动停止或卸载它们</b>。
/// </para>
/// <para>
/// 检测结果只是「该程序当前在运行」这一事实，<b>不等于「设置已被覆盖」</b>。
/// 是否真的被覆盖，仍须以读回值与请求值不一致为证据（见 <see cref="LimitVerification"/>）。
/// 这条区分很重要：把「装了第三方软件」直接说成「参数被覆盖」属于无依据的断言。
/// </para>
/// </remarks>
public static class ThirdPartyConflictDetector
{
    /// <summary>本机已安装、且与本任务负责参数重叠的可识别程序。</summary>
    /// <remarks>
    /// 清单只登记<b>已确证安装</b>的程序（任务书 §4 控制工具行），不凭猜测扩充。
    /// 其他未识别的冲突来源需要用户配合确认。
    /// </remarks>
    public static IReadOnlyList<ThirdPartyConflictSource> KnownSources { get; } = new[]
    {
        new ThirdPartyConflictSource(
            "Intel XTU",
            new[] { "XTU", "XTU3SERVICE" },
            new[] { PerformanceSettingKeys.CpuLongTerm, PerformanceSettingKeys.CpuShortTerm, PerformanceSettingKeys.CpuTurbo }),
        new ThirdPartyConflictSource(
            "MSI Afterburner",
            new[] { "MSIAfterburner" },
            new[] { PerformanceSettingKeys.GpuLimit })
    };

    private const string Boundary =
        "MSI-Control 不会修改、停止或卸载该程序，也不会通过无限循环写入争夺控制；" +
        "建议在该程序的设置中关闭上述参数的开机自动应用。";

    /// <summary>
    /// 检测当前正在运行的重叠程序。
    /// </summary>
    /// <param name="probe">进程存在性探测。</param>
    /// <returns>检测到的冲突项。无冲突时为空列表。</returns>
    /// <exception cref="ArgumentNullException">探测为空。</exception>
    public static IReadOnlyList<ThirdPartyConflict> Detect(IProcessPresenceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var conflicts = new List<ThirdPartyConflict>();
        foreach (var source in KnownSources)
        {
            var running = probe.FindRunning(source.ProcessNames);
            if (running.Count == 0)
            {
                continue;
            }

            var settings = string.Join('、', source.OverlappingSettings);
            conflicts.Add(new ThirdPartyConflict(
                source.DisplayName,
                source.OverlappingSettings,
                $"检测到 {source.DisplayName} 正在运行（进程：{string.Join('、', running)}），" +
                $"与 MSI-Control 重叠的设置项：{settings}。该事实不代表设置已被覆盖；" +
                "若读回值与请求值不一致，才判定为被覆盖。" + Boundary));
        }

        return conflicts;
    }

    /// <summary>生成一段可展示的冲突摘要。无冲突时返回 <see langword="null"/>。</summary>
    /// <param name="conflicts">冲突项。</param>
    public static string? Describe(IReadOnlyList<ThirdPartyConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        return conflicts.Count == 0
            ? null
            : string.Join(Environment.NewLine, conflicts.Select(c => c.Guidance));
    }
}
