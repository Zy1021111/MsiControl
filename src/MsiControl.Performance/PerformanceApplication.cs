using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Performance;

/// <summary>
/// 一次「应用性能档配置」的完整结果。
/// </summary>
/// <param name="StageDisplayName">档位中文名。</param>
/// <param name="Plan">逐项应用计划，与逐项结果按键对齐。</param>
/// <param name="Result">逐项结果与归并后的整体状态。</param>
/// <param name="ConfigurationProblems">下发前的配置自检问题。非空表示未向硬件下发任何内容。</param>
/// <param name="ResultProblems">结果合规自检问题（例如「成功但无确认依据」）。</param>
/// <param name="ThirdPartyConflicts">检测到的第三方重叠程序。</param>
public sealed record PerformanceApplyOutcome(
    string StageDisplayName,
    IReadOnlyList<PlannedPerformanceSetting> Plan,
    ControlResult Result,
    IReadOnlyList<string> ConfigurationProblems,
    IReadOnlyList<string> ResultProblems,
    IReadOnlyList<ThirdPartyConflict> ThirdPartyConflicts)
{
    /// <summary>配置自检是否拦下了本次下发。</summary>
    public bool ConfigurationRejected => ConfigurationProblems.Count > 0;

    /// <summary>结果是否通过合规自检（无「无依据的成功」等违规）。</summary>
    public bool IsCompliant => ResultProblems.Count == 0;

    /// <summary>第三方冲突的可展示摘要。无冲突时为 <see langword="null"/>。</summary>
    public string? ThirdPartySummary => ThirdPartyConflictDetector.Describe(ThirdPartyConflicts);

    /// <summary>取得指定设置项的逐项结果。不存在时为 <see langword="null"/>。</summary>
    public SettingResult? ResultFor(string settingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingKey);
        return Result.Settings.FirstOrDefault(s => string.Equals(s.Setting, settingKey, StringComparison.Ordinal));
    }
}

/// <summary>
/// 性能档的应用用例。
/// </summary>
/// <remarks>
/// <para>
/// 本类型只做**用例编排**：配置自检 → 生成计划 → 交硬件实现执行 → 结果合规自检 → 附带第三方冲突提示。
/// 它<b>不做调度</b>——不排队、不重试、不定时、不与他人争抢：串行写入与请求优先级属统一协调器
/// （任务 07，见任务书 §16），本类型只被协调器调用。
/// </para>
/// <para>
/// <b>取消语义</b>：取消标记原样传给硬件实现，本类型不吞 <see cref="OperationCanceledException"/>。
/// 若硬件实现已完成写入，其内部仍须完成读回核对（docs/接口与工程约定.md §6）。
/// </para>
/// </remarks>
public sealed class PerformanceApplication
{
    /// <summary>配置自检汇总项的结果标识。</summary>
    /// <remarks>
    /// 这是一个<b>汇总项</b>，不参与能力门控（<see cref="PerformanceSettingKeys"/> 中不含它），
    /// 仅用于在配置被拒绝时也能产出「逐项结果非空」的合规 <see cref="ControlResult"/>。
    /// </remarks>
    public const string ConfigurationCheckKey = "performance.config";

    private readonly IPerformanceHardware _hardware;
    private readonly IProcessPresenceProbe? _processProbe;

    /// <summary>构造用例。</summary>
    /// <param name="hardware">性能硬件访问实现。</param>
    /// <param name="processProbe">进程存在性探测。为 <see langword="null"/> 时跳过第三方冲突检测。</param>
    public PerformanceApplication(IPerformanceHardware hardware, IProcessPresenceProbe? processProbe = null)
    {
        ArgumentNullException.ThrowIfNull(hardware);
        _hardware = hardware;
        _processProbe = processProbe;
    }

    /// <summary>应用一档性能配置。</summary>
    /// <param name="settings">档位配置。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>完整结果。</returns>
    /// <exception cref="ArgumentNullException">配置为空。</exception>
    public async Task<PerformanceApplyOutcome> ApplyAsync(
        PerformanceSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var stageName = PerformancePlanner.DisplayNameOf(settings.Stage);
        var plan = PerformancePlanner.BuildPlan(settings);
        var conflicts = _processProbe is null
            ? Array.Empty<ThirdPartyConflict>()
            : ThirdPartyConflictDetector.Detect(_processProbe);

        var configurationProblems = PerformancePlanner.ValidateBeforeApply(settings);
        if (configurationProblems.Count > 0)
        {
            // 配置自相矛盾时不下发：先修正配置，再谈硬件（docs/接口说明.md §3.9）。
            var refused = ControlResult.From(new[]
            {
                SettingResult.Failed(
                    ConfigurationCheckKey,
                    stageName,
                    "配置自检未通过，未向硬件下发任何内容：" + string.Join(" ", configurationProblems))
            });

            return new PerformanceApplyOutcome(
                stageName,
                plan,
                refused,
                configurationProblems,
                refused.Validate(),
                conflicts);
        }

        var result = await _hardware.ApplyAsync(settings, cancellationToken).ConfigureAwait(false);

        return new PerformanceApplyOutcome(
            stageName,
            plan,
            result,
            configurationProblems,
            result.Validate(),
            conflicts);
    }
}
