using System.Globalization;
using System.Text.Json.Serialization;
using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Persistence.Settings;

/// <summary>
/// 某个性能档位的 CPU 功耗配置目标（持久化形态）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="PowerLimit"/> 的区别：本类型**允许**用 <see langword="null"/> 表达
/// 「原生上限」，以便直接作为 JSON 字段保存；进入领域模型前必须经
/// <see cref="StageTargetResolver"/> 转成 <see cref="PowerLimit.Native"/>，
/// 不允许把 <c>null</c> 或 0 带进硬件层（接口与工程约定 §3.9）。
/// </para>
/// <para>
/// 只承载 CPU 持续/短时上限：界面同一数值同写两者，GPU 功耗在本机不可写（HWE §26），
/// 睿频上限无写入通道（HWE §7），二者不进入持久化目标，避免「存了却永远不下发」的假配置。
/// </para>
/// </remarks>
/// <param name="Stage">性能档位。</param>
/// <param name="CpuWatts">CPU 功耗目标（瓦）。<see langword="null"/> 表示原生上限（不限制）。</param>
public sealed record StagePowerTarget(PerformanceStage Stage, decimal? CpuWatts)
{
    /// <summary>是否使用原生上限。</summary>
    /// <remarks>
    /// 派生属性，必须排除在序列化之外：写进文件会让人误以为改它就能生效
    /// （桌面框架阶段已有同款踩坑，见 <see cref="AppSettings"/> 相关测试）。
    /// </remarks>
    [JsonIgnore]
    public bool IsNative => CpuWatts is null;

    /// <inheritdoc />
    public override string ToString()
        => IsNative
            ? $"{Stage}=原生上限"
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}={1:0.##} W",
                Stage,
                CpuWatts!.Value);
}

/// <summary>
/// 在「设置的持久化形态」与「领域配置目标」之间双向转换。
/// </summary>
/// <remarks>
/// 职责边界：本类型**不做**取值范围校验（那是 <see cref="AppSettings.Normalize"/> 的职责），
/// 也**不触碰硬件**。装配时用它把已保存的目标叠加到默认配置表上；界面提交后用它抓快照。
/// </remarks>
public static class StageTargetResolver
{
    /// <summary>
    /// 把已保存的档位目标叠加到默认配置表上。
    /// </summary>
    /// <param name="settings">应用设置。</param>
    /// <param name="defaults">默认配置表（按 <see cref="PerformanceProfileCatalog"/> 顺序）。</param>
    /// <returns>每个档位一份配置；未保存目标的档位保持默认值。</returns>
    /// <remarks>
    /// 只覆盖 CPU 持续/短时两项，睿频、GPU、风扇曲线仍取默认值——
    /// 界面不提供这些目标的编辑入口，覆盖它们等于凭空造配置。
    /// </remarks>
    public static IReadOnlyList<PerformanceSettings> Resolve(
        AppSettings settings,
        IReadOnlyList<PerformanceProfile> defaults)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(defaults);

        var targets = settings.StageTargets ?? Array.Empty<StagePowerTarget>();
        var result = new List<PerformanceSettings>(defaults.Count);

        foreach (var profile in defaults)
        {
            var saved = targets.LastOrDefault(t => t.Stage == profile.Stage);
            if (saved is null)
            {
                result.Add(profile.Settings);
                continue;
            }

            var limit = saved.CpuWatts is { } watts
                ? PowerLimit.Custom(watts)
                : PowerLimit.Native;

            // 同一数值同写持续与短时：界面的合并语义，也保证 Validate 不会因短时偏低而拒绝。
            result.Add(profile.Settings with { CpuLongTerm = limit, CpuShortTerm = limit });
        }

        return result;
    }

    /// <summary>
    /// 从当前各档配置抓取持久化快照。
    /// </summary>
    /// <param name="settings">各档配置。</param>
    /// <returns>按传入顺序排列的目标列表。</returns>
    /// <remarks>
    /// 持续与短时不一致时（理论上只有手工改配置才会出现）取持续值：
    /// 界面本就只有一个数字框，写回后二者会重新一致。
    /// </remarks>
    public static IReadOnlyList<StagePowerTarget> Capture(IEnumerable<PerformanceSettings> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var result = new List<StagePowerTarget>();
        foreach (var item in settings)
        {
            var watts = item.CpuLongTerm.IsNative
                ? item.CpuShortTerm.Watts
                : item.CpuLongTerm.Watts;
            result.Add(new StagePowerTarget(item.Stage, watts));
        }

        return result;
    }
}
