using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Performance;

/// <summary>单个设置项要执行的动作。</summary>
public enum PerformanceSettingAction
{
    /// <summary>下发一个自定义数值。</summary>
    ApplyCustomValue,

    /// <summary>恢复设备原生策略（不下发任何数值，也不下发 0 / -1 / 极大值）。</summary>
    RestoreNativePolicy
}

/// <summary>
/// 应用计划中的一项。
/// </summary>
/// <param name="Key">设置项标识，取值见 <see cref="PerformanceSettingKeys"/>。</param>
/// <param name="CapabilityKey">对应的能力键，写入门控的依据。</param>
/// <param name="Requested">请求值的可读表达。原生上限时为「原生上限」文字而非数字。</param>
/// <param name="Action">要执行的动作。</param>
/// <param name="Note">补充说明，例如替代策略的能力边界。</param>
public sealed record PlannedPerformanceSetting(
    string Key,
    string CapabilityKey,
    string? Requested,
    PerformanceSettingAction Action,
    string? Note = null)
{
    /// <summary>本项是否为「恢复原生策略」。</summary>
    public bool IsNativeRestore => Action == PerformanceSettingAction.RestoreNativePolicy;
}

/// <summary>
/// 由性能档配置生成「逐项应用计划」。
/// </summary>
/// <remarks>
/// <para>
/// 计划的唯一作用是**把一档配置摊平成逐项请求**，使每一项都能独立被门控、独立下发、
/// 独立读回、独立报告状态（任务书 §8.5）。本类型不做硬件访问，也不决定是否真的下发。
/// </para>
/// <para>
/// 默认数值一律取自 <see cref="PerformanceProfileCatalog"/>，本模块**不另建一份数值表**。
/// </para>
/// <para>
/// <b>风扇曲线引用不进入本计划</b>：<see cref="PerformanceSettings.CpuCurve"/> /
/// <see cref="PerformanceSettings.GpuCurve"/> 只表达「该档绑定哪两条曲线」，
/// 曲线的校验与下发属任务 04（<c>IFanHardware</c>）。这是 04 与 05 的分界，
/// 避免两个任务各建一套档位配置（提示词 §备注）。
/// </para>
/// </remarks>
public static class PerformancePlanner
{
    /// <summary>生成一档配置的应用计划，顺序即建议的下发顺序。</summary>
    /// <param name="settings">档位配置。</param>
    /// <returns>逐项计划。</returns>
    /// <exception cref="ArgumentNullException">配置为空。</exception>
    public static IReadOnlyList<PlannedPerformanceSetting> BuildPlan(PerformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var plan = new List<PlannedPerformanceSetting>
        {
            new(
                PerformanceSettingKeys.Stage,
                PerformanceSettingKeys.CapabilityKeyFor(PerformanceSettingKeys.Stage),
                DisplayNameOf(settings.Stage),
                PerformanceSettingAction.ApplyCustomValue,
                "写入 MSI 固件性能场景（候选方法 Get_Device / Set_Device、Get_AP / Set_AP）；" +
                "是否可写由能力门控决定，本模块不预设结果。"),

            For(PerformanceSettingKeys.CpuLongTerm, settings.CpuLongTerm, "CPU 持续功耗上限"),
            For(PerformanceSettingKeys.CpuShortTerm, settings.CpuShortTerm, "CPU 短时功耗上限")
        };

        plan.Add(new PlannedPerformanceSetting(
            PerformanceSettingKeys.CpuTurbo,
            PerformanceSettingKeys.CapabilityKeyFor(PerformanceSettingKeys.CpuTurbo),
            settings.CpuTurbo.ToString(),
            settings.CpuTurbo.IsNative
                ? PerformanceSettingAction.RestoreNativePolicy
                : PerformanceSettingAction.ApplyCustomValue,
            settings.CpuTurbo.IsNative
                ? "CPU 睿频上限：恢复原生策略。"
                : "CPU 睿频上限以频率（MHz）表达，不使用「禁用睿频」冒充可调上限。"));

        plan.Add(new PlannedPerformanceSetting(
            PerformanceSettingKeys.GpuLimit,
            PerformanceSettingKeys.CapabilityKeyFor(PerformanceSettingKeys.GpuLimit),
            settings.GpuLimit.ToString(),
            settings.GpuLimit.IsNative
                ? PerformanceSettingAction.RestoreNativePolicy
                : PerformanceSettingAction.ApplyCustomValue,
            settings.GpuEcoFallback is null
                ? null
                : $"目标值不受支持，改用替代策略：{settings.GpuEcoFallback.Description}" +
                  $" 能力边界：{settings.GpuEcoFallback.Limitation}"));

        if (settings.GpuEcoFallback is { } fallback)
        {
            plan.Add(new PlannedPerformanceSetting(
                PerformanceSettingKeys.GpuEco,
                PerformanceSettingKeys.CapabilityKeyFor(PerformanceSettingKeys.GpuEco),
                fallback.Applied.ToString(),
                PerformanceSettingAction.ApplyCustomValue,
                $"显式替代策略（不是用户原始目标值）。{fallback.Limitation}"));
        }

        return plan;
    }

    /// <summary>
    /// 下发前的配置自检。
    /// </summary>
    /// <param name="settings">档位配置。</param>
    /// <returns>问题列表。为空表示可以下发。</returns>
    /// <remarks>
    /// 在 <see cref="PerformanceSettings.Validate"/>（短时不得低于持续、曲线必须绑定正确通道）之上，
    /// 追加本模块负责的检查：替代策略只能用于「目标不受支持」的情形，
    /// 与原生上限同时出现属于自相矛盾的配置。
    /// </remarks>
    public static IReadOnlyList<string> ValidateBeforeApply(PerformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>(settings.Validate());

        if (settings.GpuEcoFallback is not null && settings.GpuLimit.IsNative)
        {
            problems.Add(
                "GPU 功耗上限为原生上限，却同时携带替代节能策略：替代策略只在目标值不受支持时使用，" +
                "该组合自相矛盾，请先明确 GPU 目标值。");
        }

        return problems;
    }

    /// <summary>取得档位的中文显示名。取自默认配置表，不另建映射。</summary>
    /// <param name="stage">档位。</param>
    /// <returns>中文显示名。默认表中未登记该档位时退回枚举名。</returns>
    public static string DisplayNameOf(PerformanceStage stage)
        => PerformanceProfileCatalog.TryGetDefault(stage, out var profile)
            ? profile!.DisplayName
            : stage.ToString();

    private static PlannedPerformanceSetting For(
        string key,
        PowerLimit limit,
        string quantity)
        => new(
            key,
            PerformanceSettingKeys.CapabilityKeyFor(key),
            limit.ToString(),
            limit.IsNative
                ? PerformanceSettingAction.RestoreNativePolicy
                : PerformanceSettingAction.ApplyCustomValue,
            limit.IsNative
                ? $"{quantity}：恢复设备原生策略，不下发数值。"
                : $"{quantity}：目标值不代表硬件持续耗电；功耗限制的时间窗口可能允许瞬时偏差。");
}
