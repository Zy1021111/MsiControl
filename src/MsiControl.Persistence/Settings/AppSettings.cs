using System.Globalization;
using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Persistence.Settings;

/// <summary>
/// 应用设置（对应任务书 §12「精简设置」，只保留三组）。
/// </summary>
/// <remarks>
/// <para>
/// 本类型承载**界面与进程行为**，以及性能档位的 CPU 功耗目标（<see cref="StageTargets"/>）——
/// 后者是用户在主窗口「详细编辑」里直接编辑的数值，必须随进程退出保留，
/// 因此从 v2 起并入设置文件（v1 沿任务书 §7.3、§8.2 把它划归配置档，而配置档只有
/// 手工导入导出、且导出的是默认表，导致编辑结果重启即丢失）。
/// 风扇曲线仍按档位单独保存在 <c>fan-curves.json</c>。
/// </para>
/// <para>
/// 与设计定稿的对应关系：
/// <list type="bullet">
/// <item>启动与窗口：<see cref="LaunchOnStartup"/>、<see cref="CloseToTray"/>、
///       <see cref="StartupWindow"/>、<see cref="ShowHideWindowHotkey"/>、<see cref="FullSpeedHotkey"/>。</item>
/// <item>数据与配置：<see cref="RefreshIntervalSeconds"/>、<see cref="FanDisplay"/>、
///       <see cref="StageTargets"/>。</item>
/// <item>设备与诊断：不在此保存，改为运行时读取后在界面展示（避免把硬件信息当配置固化）。</item>
/// </list>
/// </para>
/// </remarks>
public sealed record AppSettings
{
    /// <summary>当前设置架构版本。</summary>
    /// <remarks>
    /// 每次字段语义变更都需递增，并在 <c>SettingsMigrator</c> 注册对应迁移步骤。
    /// v1 为首个版本；v2 增加 <see cref="StageTargets"/>（迁移步骤 v1→v2 只补空表，
    /// 因为 v1 从未持久化过档位目标，不存在需要搬运的历史数据）；
    /// v3 增加 <see cref="LastConfirmedStage"/>（迁移步骤 v2→v3 置空，
    /// 语义为「升级前从未记录过用户确认的档位」，启动时因此不写入任何档位）；
    /// v4 增加 <see cref="StartMinimizedOnStartup"/>（迁移步骤 v3→v4 置 false，
    /// 语义为「升级前没有这个偏好」——默认仍按 <see cref="StartupWindow"/> 显示窗口）。
    /// </remarks>
    public const int CurrentSchemaVersion = 4;

    /// <summary>刷新间隔允许的最小值（秒）。</summary>
    public const int MinRefreshIntervalSeconds = 1;

    /// <summary>刷新间隔允许的最大值（秒）。</summary>
    public const int MaxRefreshIntervalSeconds = 60;

    /// <summary>刷新间隔越界时回退到的值（秒）。</summary>
    public const int DefaultRefreshIntervalSeconds = 1;

    /// <summary>设置架构版本。</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>是否开机启动。默认开启（任务书 §11）。</summary>
    public bool LaunchOnStartup { get; init; } = true;

    /// <summary>关闭窗口时是否进入托盘。默认开启（任务书 §10）。</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>启动时显示哪种窗口。默认正常窗口（任务书 §10）。</summary>
    public WindowMode StartupWindow { get; init; } = WindowMode.Normal;

    /// <summary>
    /// 开机启动后是否直接静默驻留后台（不显示窗口、不打断桌面）。
    /// </summary>
    /// <remarks>
    /// 只影响「开机自启」这一次启动；用户手动启动仍然显示窗口
    /// （否则双击图标没反应会被当成程序未启动）。关掉开机自启时该偏好保留但不再生效。
    /// </remarks>
    public bool StartMinimizedOnStartup { get; init; }

    /// <summary>刷新间隔，秒。默认 1 秒（任务书 §6.4）。</summary>
    public int RefreshIntervalSeconds { get; init; } = DefaultRefreshIntervalSeconds;

    /// <summary>风扇数值显示偏好。默认百分比，无有效校准时界面回落到 RPM。</summary>
    public FanDisplayMode FanDisplay { get; init; } = FanDisplayMode.Percent;

    /// <summary>
    /// 各性能档位的 CPU 功耗配置目标。
    /// </summary>
    /// <remarks>
    /// 空表表示全部沿用 <see cref="PerformanceProfileCatalog.Defaults"/>；
    /// 有值时按 <see cref="StagePowerTarget.Stage"/> 逐档覆盖默认表的 CPU 持续/短时两项。
    /// 保存时写全五档快照，缺档的档位在加载后退回默认值（语义单一，不做「部分继承」猜测）。
    /// </remarks>
    public IReadOnlyList<StagePowerTarget> StageTargets { get; init; } = Array.Empty<StagePowerTarget>();

    /// <summary>
    /// 上一次**用户手动确认**并且写入成功的性能档位。未确认过时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只由「用户点档位按钮 / 托盘 / 快捷键并取得读回确认」这一条路径写入；
    /// 自动策略、启动恢复与退出恢复都**不改**本字段（否则会把软件写过的值
    /// 冒充成用户的选择）。
    /// </para>
    /// <para>
    /// 用途：再次启动时把机器恢复到用户上次确认的档位。为空时启动不改动性能模式。
    /// </para>
    /// </remarks>
    public PerformanceStage? LastConfirmedStage { get; init; }

    /// <summary>显示/隐藏窗口全局快捷键。</summary>
    public HotkeyBinding ShowHideWindowHotkey { get; init; } =
        new(Enabled: true, Control: true, Alt: true, Shift: false, Windows: false, KeyName: "M");

    /// <summary>强冷切换全局快捷键。</summary>
    /// <remarks>
    /// 默认关闭：强冷能力当前为 <c>Unknown</c>（接口未定位），
    /// 给一个不可用功能绑定默认全局快捷键会误导用户（docs/接口与工程约定.md §1.3）。
    /// </remarks>
    public HotkeyBinding FullSpeedHotkey { get; init; } = HotkeyBinding.Disabled("F");

    /// <summary>正常窗口位置与尺寸。未记录时为 <see langword="null"/>。</summary>
    public WindowPlacement? NormalWindowPlacement { get; init; }

    /// <summary>迷你窗口位置与尺寸。未记录时为 <see langword="null"/>。</summary>
    public WindowPlacement? MiniWindowPlacement { get; init; }

    /// <summary>创建默认设置。</summary>
    public static AppSettings CreateDefault() => new();

    /// <summary>
    /// 规范化设置：把越界或自相矛盾的值修正到合法范围。
    /// </summary>
    /// <returns>规范化结果，附修正说明。</returns>
    /// <remarks>
    /// 迁移只负责「字段结构」的变化，取值范围与枚举合法性由本方法负责。
    /// 两者分开的原因：迁移是一次性的历史修补，规范化是每次加载都必须执行的防御，
    /// 手工编辑过的配置文件同样必须能安全加载。
    /// </remarks>
    public SettingsNormalizationResult Normalize()
    {
        var notes = new List<string>();
        var result = this;

        if (result.SchemaVersion != CurrentSchemaVersion)
        {
            notes.Add($"设置版本 {result.SchemaVersion} 与当前版本 {CurrentSchemaVersion} 不一致，已按当前版本处理。");
            result = result with { SchemaVersion = CurrentSchemaVersion };
        }

        if (result.RefreshIntervalSeconds is < MinRefreshIntervalSeconds or > MaxRefreshIntervalSeconds)
        {
            notes.Add(
                $"刷新间隔 {result.RefreshIntervalSeconds} 秒超出 {MinRefreshIntervalSeconds}–" +
                $"{MaxRefreshIntervalSeconds} 秒范围，已修正为 {DefaultRefreshIntervalSeconds} 秒。");
            result = result with { RefreshIntervalSeconds = DefaultRefreshIntervalSeconds };
        }

        if (!Enum.IsDefined(result.StartupWindow))
        {
            notes.Add($"启动窗口取值 {result.StartupWindow} 无效，已回退为正常窗口。");
            result = result with { StartupWindow = WindowMode.Normal };
        }

        if (!Enum.IsDefined(result.FanDisplay))
        {
            notes.Add($"风扇显示方式取值 {result.FanDisplay} 无效，已回退为百分比。");
            result = result with { FanDisplay = FanDisplayMode.Percent };
        }

        result = result with
        {
            ShowHideWindowHotkey = NormalizeHotkey(result.ShowHideWindowHotkey, "显示/隐藏窗口", notes),
            FullSpeedHotkey = NormalizeHotkey(result.FullSpeedHotkey, "强冷切换", notes)
        };

        if (result.NormalWindowPlacement is { IsUsable: false })
        {
            notes.Add("已记录的正常窗口尺寸过小，已丢弃，改为使用默认尺寸。");
            result = result with { NormalWindowPlacement = null };
        }

        if (result.MiniWindowPlacement is { IsUsable: false })
        {
            notes.Add("已记录的迷你窗口尺寸过小，已丢弃，改为使用默认尺寸。");
            result = result with { MiniWindowPlacement = null };
        }

        if (NormalizeStageTargets(result.StageTargets, notes) is { } targets)
        {
            result = result with { StageTargets = targets };
        }

        // 未登记档位（含手工编辑成非法枚举值）一律丢弃：宁可不恢复，也不猜一个档位写进硬件。
        if (result.LastConfirmedStage is { } confirmed
            && (!Enum.IsDefined(confirmed) || !PerformanceProfileCatalog.TryGetDefault(confirmed, out _)))
        {
            notes.Add($"记录的上次确认档位 {(int)confirmed} 未登记，已丢弃（启动时不恢复性能模式）。");
            result = result with { LastConfirmedStage = null };
        }

        return new SettingsNormalizationResult(result, notes);
    }

    /// <summary>
    /// 规范化档位功耗目标：丢弃未知档位、越界瓦数与重复项，并按默认表顺序重排。
    /// </summary>
    /// <returns>规范化后的列表；无需改动时为 <see langword="null"/>。</returns>
    /// <remarks>
    /// 手工编辑过的设置文件同样必须能安全加载，因此越界值一律**丢弃并回退该档默认值**，
    /// 而不是夹紧到边界——夹紧会把「用户写错了」变成「用户想限制到某个值」，属于猜测。
    /// </remarks>
    private static IReadOnlyList<StagePowerTarget>? NormalizeStageTargets(
        IReadOnlyList<StagePowerTarget>? targets,
        ICollection<string> notes)
    {
        if (targets is null || targets.Count == 0)
        {
            return targets is null ? Array.Empty<StagePowerTarget>() : null;
        }

        var accepted = new Dictionary<PerformanceStage, StagePowerTarget>();
        var rejected = new HashSet<PerformanceStage>();
        var seen = new HashSet<PerformanceStage>();

        foreach (var target in targets)
        {
            if (target is null)
            {
                notes.Add("档位功耗目标中存在空项，已丢弃。");
                continue;
            }

            if (!Enum.IsDefined(target.Stage) || !PerformanceProfileCatalog.TryGetDefault(target.Stage, out _))
            {
                notes.Add($"档位功耗目标中的档位取值 {(int)target.Stage} 未登记，已丢弃。");
                continue;
            }

            if (!seen.Add(target.Stage))
            {
                notes.Add($"档位 {target.Stage} 的功耗目标重复出现，已保留最后一项。");
            }

            if (target.CpuWatts is { } watts
                && !PowerLimit.TryCustom(watts, out _, out var error))
            {
                notes.Add(
                    $"档位 {target.Stage} 的功耗目标 {watts.ToString("0.##", CultureInfo.InvariantCulture)} W 无效"
                    + $"（{error}），已丢弃并改用该档默认值。");
                accepted.Remove(target.Stage);
                rejected.Add(target.Stage);
                continue;
            }

            accepted[target.Stage] = target;
        }

        var ordered = new List<StagePowerTarget>(accepted.Count);
        foreach (var profile in PerformanceProfileCatalog.Defaults)
        {
            if (!rejected.Contains(profile.Stage)
                && accepted.TryGetValue(profile.Stage, out var kept))
            {
                ordered.Add(kept);
            }
        }

        if (ordered.Count == targets.Count
            && ordered.SequenceEqual(targets))
        {
            return null;
        }

        return ordered;
    }

    private static HotkeyBinding NormalizeHotkey(
        HotkeyBinding? binding,
        string displayName,
        ICollection<string> notes)
    {
        var normalized = (binding ?? HotkeyBinding.Disabled()).Normalized();
        var problems = normalized.Validate();
        if (problems.Count == 0)
        {
            return normalized;
        }

        notes.Add($"{displayName}快捷键配置无效（{string.Join(' ', problems)}），已关闭该快捷键。");
        return normalized with { Enabled = false };
    }
}

/// <summary>设置规范化的结果。</summary>
/// <param name="Settings">规范化后的设置。</param>
/// <param name="Notes">修正说明，供日志与界面提示。</param>
public sealed record SettingsNormalizationResult(AppSettings Settings, IReadOnlyList<string> Notes);
