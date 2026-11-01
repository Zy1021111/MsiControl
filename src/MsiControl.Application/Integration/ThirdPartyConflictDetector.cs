using System.Diagnostics;

namespace MsiControl.Application.Integration;

/// <summary>
/// 进程枚举的来源。
/// </summary>
/// <remarks>
/// 抽象出来以便在无实际进程的测试环境下验证冲突判定逻辑。
/// <b>实现方不得启动任何外部命令进程</b>（任务书 §20「不周期性启动外部命令进程」）。
/// </remarks>
public interface IProcessProbe
{
    /// <summary>枚举当前进程名（不含扩展名）。读取失败时返回空列表。</summary>
    IReadOnlyList<string> GetProcessNames();
}

/// <summary>
/// 基于 <see cref="Process"/> 的进程枚举。
/// </summary>
/// <remarks>
/// <see cref="Process.GetProcesses()"/> 是只读枚举，不启动新进程、不需要管理员权限。
/// 进程信息中的用户与路径均不采集，只取进程名。
/// </remarks>
public sealed class SystemProcessProbe : IProcessProbe
{
    /// <inheritdoc />
    public IReadOnlyList<string> GetProcessNames()
    {
        var names = new List<string>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            return names;
        }

        foreach (var process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 进程在枚举期间退出：跳过即可，不影响整体扫描。
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }
}

/// <summary>一个已知会与本软件控制领域重叠的第三方产品。</summary>
/// <param name="Id">稳定标识。</param>
/// <param name="DisplayName">产品显示名。</param>
/// <param name="ProcessNamePatterns">候选进程名片段（大小写不敏感，按包含匹配）。</param>
/// <param name="OverlappingDomains">与本软件重叠的控制领域。</param>
/// <param name="Note">说明，包括「候选名未经实机核对」这类限制。</param>
public sealed record ThirdPartyProduct(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ProcessNamePatterns,
    IReadOnlyList<string> OverlappingDomains,
    string Note);

/// <summary>一次冲突检测的结果项。</summary>
/// <param name="Product">命中的产品。</param>
/// <param name="MatchedProcessName">命中的进程名。</param>
public sealed record ThirdPartyConflict(ThirdPartyProduct Product, string MatchedProcessName);

/// <summary>冲突检测报告。</summary>
/// <param name="Detected">命中的冲突项。为空表示未检出。</param>
/// <param name="PolicyNote">处理策略说明，直接展示给用户。</param>
/// <param name="KnownProducts">本次检测涉及的产品清单（含未命中的），便于用户理解检测范围。</param>
public sealed record ThirdPartyConflictReport(
    IReadOnlyList<ThirdPartyConflict> Detected,
    IReadOnlyList<ThirdPartyProduct> KnownProducts,
    string PolicyNote)
{
    /// <summary>是否检出任何冲突。</summary>
    public bool AnyDetected => Detected.Count > 0;

    /// <summary>一句话摘要。</summary>
    public string Summary => AnyDetected
        ? "检测到可能与本软件重叠的第三方控制工具：" +
          string.Join(
              "、",
              Detected.Select(c => $"{c.Product.DisplayName}（进程 {c.MatchedProcessName}）")) +
          "。" + PolicyNote
        : "未检测到已知的第三方控制工具进程。" + PolicyNote;
}

/// <summary>
/// 第三方控制工具共存检测。
/// </summary>
/// <remarks>
/// <para>对应任务书 §11「XTU / Afterburner」与 design.md §10：</para>
/// <list type="bullet">
/// <item>检测可识别的冲突来源。</item>
/// <item>显示设置被其他程序覆盖的状态。</item>
/// <item><b>不通过无休止重写争夺控制</b>；不修改或删除其配置；不自动停止或卸载。</item>
/// </list>
/// <para>
/// <b>本类型只检测与报告，不产生任何写入或进程操作。</b>
/// 它是一个纯函数：给定进程名单，得出冲突结论。
/// </para>
/// <para>
/// <b>检测时机：</b>启动时一次，以及用户显式请求时。刻意<b>不做周期轮询</b>——
/// 轮询既增加开销，也不解决任何问题：冲突是否存在不取决于轮询频率。
/// </para>
/// </remarks>
public sealed class ThirdPartyConflictDetector
{
    /// <summary>处理策略说明。所有报告共用同一份文案，避免各处口径不一致。</summary>
    public const string PolicyText =
        "本软件只在用户显式操作时写入设置，并检测这些设置是否被其他程序改写；" +
        "检测到重叠时只报告，不修改或删除第三方配置、不自动停止或卸载它们，" +
        "也不会通过重复写入争夺控制权（任务书 §11）。";

    /// <summary>已知的第三方产品清单。</summary>
    /// <remarks>
    /// 进程名按**片段**匹配，因为同一产品的不同版本可执行文件名不同。
    /// 清单标注了哪些是「本机已确证安装」，哪些只是「候选名」——
    /// 未在实机核对过的名称不得当作已确证结论使用。
    /// </remarks>
    public static IReadOnlyList<ThirdPartyProduct> KnownProducts { get; } = new[]
    {
        new ThirdPartyProduct(
            "intel-xtu",
            "Intel Extreme Tuning Utility (XTU)",
            new[] { "XtuService", "XTU", "PerfTune", "XtuUiLauncher", "IntelExtremeTuningUtility" },
            new[] { "CPU 功耗限制", "CPU 睿频上限" },
            "本机已确证安装（10.0.1.188X），且 XtuService 正在运行。" +
            "进程名取自安装目录中的可执行文件（PerfTune.exe / XtuService.exe / XtuUiLauncher.exe）。"),

        new ThirdPartyProduct(
            "msi-afterburner",
            "MSI Afterburner（含 RivaTuner Statistics Server）",
            new[] { "MSIAfterburner", "Afterburner", "RTSS" },
            new[] { "GPU 功耗 / 功耗上限", "风扇转速控制" },
            "任务书记录为本机已安装，但 2026-10-16 只读核对未发现安装痕迹" +
            "（无卸载项、无注册表键、无服务、无默认安装目录）。" +
            "进程名为候选名，未在本机核对；待用户确认后再更新此条。"),

        new ThirdPartyProduct(
            "msi-center",
            "MSI Center / Dragon Center（含 NB Foundation Service）",
            new[] { "MSICenter", "MSI Center", "MSI_Center", "MSIAPService", "OneDC_Updater", "AIOC" },
            new[] { "性能场景", "风扇策略", "显卡模式", "功耗限制" },
            "本机磁盘上存在 MSI OEM 组件目录（C:\\Program Files (x86)\\MSI\\...），" +
            "但未注册为已安装程序、无相关服务在运行。" +
            "该组件的日志显示 MSI 自身也在调用同一套 ACPI WMI 接口，属高度重叠来源。" +
            "进程名为候选名，未在本机核对。")
    };

    private readonly IProcessProbe _probe;

    /// <summary>构造检测器。</summary>
    /// <param name="probe">进程枚举来源。为空时使用系统实现。</param>
    public ThirdPartyConflictDetector(IProcessProbe? probe = null)
        => _probe = probe ?? new SystemProcessProbe();

    /// <summary>执行一次检测。</summary>
    /// <returns>报告。检测本身不抛异常。</returns>
    public ThirdPartyConflictReport Scan()
    {
        IReadOnlyList<string> processNames;
        try
        {
            processNames = _probe.GetProcessNames();
        }
        catch (Exception)
        {
            processNames = Array.Empty<string>();
        }

        var detected = new List<ThirdPartyConflict>();
        foreach (var product in KnownProducts)
        {
            var hit = processNames.FirstOrDefault(name =>
                !string.IsNullOrWhiteSpace(name)
                && product.ProcessNamePatterns.Any(pattern =>
                    name.Contains(pattern, StringComparison.OrdinalIgnoreCase)));

            if (hit is not null)
            {
                detected.Add(new ThirdPartyConflict(product, hit));
            }
        }

        return new ThirdPartyConflictReport(detected, KnownProducts, PolicyText);
    }

    /// <summary>生成重叠领域的去重清单，供界面提示与文档使用。</summary>
    public static IReadOnlyList<string> OverlappingDomains(ThirdPartyConflictReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return report.Detected
            .SelectMany(c => c.Product.OverlappingDomains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToArray();
    }
}
