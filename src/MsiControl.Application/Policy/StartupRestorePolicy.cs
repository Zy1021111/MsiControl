using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Application.Policy;

/// <summary>
/// 一次「最近成功应用」的配置记录。
/// </summary>
/// <para>
/// 记录 <see cref="Device"/> 是必需的：启动恢复属于**自动写入**，
/// 只有在确认「这份配置就是为这台主板保存的」之后才允许下发（任务书 §11）。
/// </para>
/// <param name="Settings">该档位的完整配置（功耗、睿频、双风扇曲线）。</param>
/// <param name="Device">保存时所在设备的标识。</param>
/// <param name="AppliedAt">确认成功应用的时刻。</param>
public sealed record LastAppliedConfig(PerformanceSettings Settings, DeviceIdentity Device, DateTimeOffset AppliedAt);

/// <summary>
/// 「最近成功应用配置」的来源。
/// </summary>
/// <remarks>
/// <para>
/// 配置档的落盘归任务 04/05（曲线与性能参数编辑完成后才能产生「成功应用」记录）。
/// 任务 07 定义接口并消费；在落盘实现接入之前，
/// 装配使用 <see cref="NoLastAppliedConfigSource"/>，启动恢复因此**不会**发生，
/// 并给出明确原因——这比用默认值假装恢复过要诚实。
/// </para>
/// </remarks>
public interface ILastAppliedConfigSource
{
    /// <summary>读取最近一次确认成功应用的配置。没有记录时返回 <see langword="null"/>。</summary>
    LastAppliedConfig? Read();
}

/// <summary>
/// 暂无「最近成功应用配置」来源时的占位实现。
/// </summary>
/// <remarks>
/// 一律返回 <see langword="null"/>，**不返回构造的默认值**：
/// 用出厂默认配置冒充「用户上次应用过的配置」会在启动时产生一次无据可依的硬件写入。
/// </remarks>
public sealed class NoLastAppliedConfigSource : ILastAppliedConfigSource
{
    /// <summary>不可用原因。</summary>
    public const string Reason =
        "尚无「最近成功应用配置」的持久化来源（配置档落盘属任务 04/05，尚未接入）；" +
        "启动恢复因此不会下发任何写入。";

    /// <inheritdoc />
    public LastAppliedConfig? Read() => null;
}

/// <summary>启动恢复的一项前置校验。</summary>
/// <param name="Name">校验项名称。</param>
/// <param name="Passed">是否通过。</param>
/// <param name="Detail">依据说明。</param>
public sealed record StartupRestoreCheck(string Name, bool Passed, string Detail);

/// <summary>启动恢复的决定。</summary>
/// <param name="ShouldRestore">是否应当下发恢复写入。</param>
/// <param name="Stage">要恢复的档位。不恢复时为 <see langword="null"/>。</param>
/// <param name="Checks">逐项校验记录，供日志与诊断展示。</param>
/// <param name="Reason">结论说明。</param>
/// <remarks>
/// <b>本决定不包含任何显卡模式动作</b>：任务书 §11 与 §9.1 明确要求启动时不自动切换显卡模式。
/// </remarks>
public sealed record StartupRestoreDecision(
    bool ShouldRestore,
    PerformanceStage? Stage,
    IReadOnlyList<StartupRestoreCheck> Checks,
    string Reason);

/// <summary>启动恢复的输入。</summary>
/// <param name="CurrentDevice">本机设备标识。</param>
/// <param name="Supply">当前供电状态。</param>
public sealed record StartupRestoreInput(DeviceIdentity CurrentDevice, PowerSupplyKind Supply);

/// <summary>
/// 启动恢复策略。
/// </summary>
/// <remarks>
/// <para>对应任务书 §11「启动」与 design.md §9.1：</para>
/// <list type="bullet">
/// <item>恢复最近成功应用的性能与风扇配置。</item>
/// <item>恢复前校验设备、配置适用性与当前供电状态。</item>
/// <item><b>不自动改变显卡模式。</b></item>
/// <item>不覆盖更新的手动选择。</item>
/// </list>
/// <para>
/// 「不覆盖更新的手动选择」在两处落实：本策略只负责**决定**；
/// 真正下发时由 <see cref="Control.ControlCoordinator"/> 以
/// <see cref="Control.ControlRequestOrigin.Automatic"/> 提交，
/// 该来源的请求永远不会取代仍在排队的手动请求。
/// </para>
/// <para>
/// 本类型是纯逻辑，不读硬件、不写硬件。
/// </para>
/// </remarks>
public static class StartupRestorePolicy
{
    /// <summary>显卡模式不在启动恢复范围内。该结论写入每一次决定的校验记录，便于审计。</summary>
    public const string GraphicsExclusionNote =
        "显卡模式不在启动恢复范围内：启动与供电变化均不自动切换显卡模式（任务书 §9.1、§11）。";

    /// <summary>评估是否应当执行启动恢复。</summary>
    /// <param name="input">本机设备与供电状态。</param>
    /// <param name="source">最近成功应用配置的来源。</param>
    /// <returns>决定与逐项依据。</returns>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    public static StartupRestoreDecision Evaluate(StartupRestoreInput input, ILastAppliedConfigSource source)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(source);

        var checks = new List<StartupRestoreCheck>
        {
            new(
                "显卡模式排除",
                true,
                GraphicsExclusionNote)
        };

        // 1) 本机设备必须已识别，否则无法判断配置适用性。
        var deviceIdentified = input.CurrentDevice.IsIdentified;
        checks.Add(new StartupRestoreCheck(
            "本机设备已识别",
            deviceIdentified,
            deviceIdentified
                ? $"主板码 {input.CurrentDevice.BoardCode}。"
                : "本机主板码未读出：无法判断配置适用性，不执行自动写入。"));

        var saved = source.Read();
        var hasRecord = saved is not null;
        checks.Add(new StartupRestoreCheck(
            "存在最近成功应用记录",
            hasRecord,
            hasRecord
                ? $"记录时间 {saved!.AppliedAt:yyyy-MM-dd HH:mm:ss zzz}。"
                : NoLastAppliedConfigSource.Reason));

        var sameBoard = saved is not null && input.CurrentDevice.IsSameBoardAs(saved.Device);
        checks.Add(new StartupRestoreCheck(
            "配置与本机同主板",
            sameBoard,
            saved is null
                ? "无记录可校验。"
                : sameBoard
                    ? $"记录主板码 {saved.Device.BoardCode}，与本机一致。"
                    : $"记录主板码 {saved.Device.BoardCode}，与本机 {input.CurrentDevice.BoardCode} 不一致" +
                      "（未识别机型一律视为不一致，任务书 §2.2）。"));

        var supplyKnown = input.Supply != PowerSupplyKind.Unknown;
        checks.Add(new StartupRestoreCheck(
            "供电状态已知",
            supplyKnown,
            supplyKnown
                ? $"当前供电：{(input.Supply == PowerSupplyKind.Ac ? "交流电" : "电池")}。"
                : "供电状态未知：不在无法判定供电的情况下改动功耗限制。"));

        var configValid = saved is null ? true : saved.Settings.Validate().Count == 0;
        checks.Add(new StartupRestoreCheck(
            "配置自洽",
            configValid,
            saved is null
                ? "无记录可校验。"
                : configValid
                    ? "功耗与曲线的组合关系校验通过。"
                    : "记录中的配置自相矛盾：" + string.Join(" ", saved.Settings.Validate())));

        var failed = checks.FirstOrDefault(c => !c.Passed);
        if (failed is not null)
        {
            return new StartupRestoreDecision(
                ShouldRestore: false,
                Stage: null,
                checks,
                Reason: $"不执行启动恢复：{failed.Name} 未通过 —— {failed.Detail}");
        }

        return new StartupRestoreDecision(
            ShouldRestore: true,
            Stage: saved!.Settings.Stage,
            checks,
            Reason:
                $"执行启动恢复：恢复到 {Control.ControlIntent.StageText(saved.Settings.Stage)}；" +
                "该写入以「自动策略」来源提交，不会覆盖用户更新的手动选择。");
    }
}
