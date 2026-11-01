using MsiControl.Application.Control;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 控制请求出口。
/// </summary>
/// <remarks>
/// <para>
/// 任务 02 特有规则第 11 条：<b>窗口只发请求、只订阅状态，不得绕过协调层直接写硬件。</b>
/// 因此界面层不接触 <c>IFanHardware</c> / <c>IPerformanceHardware</c> / <c>IGraphicsHardware</c>，
/// 只调用本接口，由任务 07 的协调器负责串行写入、超时与读回确认。
/// </para>
/// <para>
/// 所有方法返回 <see cref="ControlResult"/>：成功项必须带可追溯的确认依据，
/// 界面据此展示「请求值 / 接口结果 / 读回值 / 确认依据」（任务书 §8.5）。
/// </para>
/// </remarks>
public interface IControlRequestSink
{
    /// <summary>请求切换到指定性能档位。</summary>
    Task<ControlResult> RequestPerformanceStageAsync(PerformanceStage stage, CancellationToken cancellationToken);

    /// <summary>请求切换到指定显卡模式。</summary>
    /// <remarks>
    /// 本方法自身不重启：结果可能为 <see cref="OperationStatus.PendingRestart"/>（任务书 §9.1）。
    /// 是否立即重启由界面在拿到该结果后询问用户决定，重启请求只从那一处发出。
    /// </remarks>
    Task<ControlResult> RequestGraphicsModeAsync(GraphicsMode mode, CancellationToken cancellationToken);

    /// <summary>
    /// 把固件性能场景恢复为默认档位（只写场景寄存器，不应用配置表）。
    /// </summary>
    /// <remarks>
    /// 仅用于「用户主动退出程序」这一步：目的是不让机器停留在狂暴/高性能等高功耗档位，
    /// 因此实现方**不得**顺带写入 CPU 功耗上限或改动风扇曲线。
    /// 本方法与 <see cref="RequestPerformanceStageAsync"/> 语义不同，不可互相顶替。
    /// </remarks>
    Task<ControlResult> RestoreDefaultStageAsync(CancellationToken cancellationToken);

    /// <summary>切换强冷（全速）。</summary>
    Task<ControlResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken);

    /// <summary>请求切换到指定风扇模式（自动/静音/高级；强冷走 <see cref="SetFullSpeedAsync"/>）。</summary>
    Task<ControlResult> RequestFanModeAsync(FanControlMode mode, CancellationToken cancellationToken);

    /// <summary>取得指定档位的软件配置目标。</summary>
    /// <remarks>用于界面表格的初始显示；未适配实现返回默认配置表，不虚构用户修改。</remarks>
    PerformanceSettings GetStageSettings(PerformanceStage stage);

    /// <summary>更新指定档位的软件配置目标。</summary>
    /// <remarks>
    /// 纯软件层操作，<b>不触发任何硬件写入</b>；硬件写入仍发生在档位切换时，
    /// 并按能力门控逐项执行与如实报告。非法配置整体拒绝并说明原因。
    /// </remarks>
    StageSettingsUpdateResult UpdateStageSettings(PerformanceSettings settings);

    /// <summary>
    /// 最近一次经本出口**取得读回确认**的性能档位；尚无确认时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 存在理由：本机固件组里「安静」与「节能」共用场景编码 0xC2（HWE §21.3），
    /// 单看 EC 0xD2 无法分辨。界面显示与档位循环都靠「上次实际应用的档位」消歧，
    /// 该事实只能由协调层持有（所有写入入口都汇聚到它），桌面壳不得自建副本——
    /// 否则界面按钮切到安静后，循环热键不知道，会把安静当节能再切一次。
    /// </para>
    /// <para>
    /// 这是**本软件自己写入并确认过**的值，不是硬件读数；硬件实际状态仍以
    /// <c>ReadPerformanceStateAsync</c> 的读回为准，两者在消歧时配合使用。
    /// </para>
    /// </remarks>
    PerformanceStage? LastConfirmedStage { get; }
}

/// <summary>
/// 尚未接入协调器时的请求出口。
/// </summary>
/// <remarks>
/// <para>
/// 一律返回 <see cref="OperationStatus.Unsupported"/> 并附依据，
/// <b>不返回假的成功</b>——任务书提示词第 6、8 条。
/// </para>
/// <para>
/// 注意：<see cref="ControlResult.From"/> 的整体归并规则不会产生
/// <c>Unsupported</c>（见 docs/接口说明.md §4）；因此界面必须读取
/// <see cref="ControlResult.Settings"/> 的逐项状态，而不能只看整体状态。
/// </para>
/// </remarks>
public sealed class NotAdaptedControlRequestSink : IControlRequestSink
{
    private readonly string _reason;

    /// <summary>构造请求出口。</summary>
    /// <param name="reason">不可用原因。</param>
    public NotAdaptedControlRequestSink(string reason = "硬件协调层（任务 07）尚未接入，本版本不执行任何硬件写入")
    {
        _reason = reason;
    }

    /// <inheritdoc />
    public Task<ControlResult> RequestPerformanceStageAsync(PerformanceStage stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ControlResult.Single(
            SettingResult.Unsupported("performance.stage", $"{_reason}；请求的档位为 {stage}。")));
    }

    /// <inheritdoc />
    public Task<ControlResult> RequestGraphicsModeAsync(GraphicsMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ControlResult.Single(
            SettingResult.Unsupported("graphics.mode", $"{_reason}；请求的模式为 {mode}。")));
    }

    /// <inheritdoc />
    public Task<ControlResult> RestoreDefaultStageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ControlResult.Single(
            SettingResult.Unsupported(
                "performance.stage",
                $"{_reason}；退出时未能把固件性能场景恢复为默认档位（保持当前场景不变）。")));
    }

    /// <inheritdoc />
    public Task<ControlResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ControlResult.Single(
            SettingResult.Unsupported("fan.fullspeed", $"{_reason}；请求值为 {enabled}。")));
    }

    /// <inheritdoc />
    public Task<ControlResult> RequestFanModeAsync(FanControlMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ControlResult.Single(
            SettingResult.Unsupported("fan.mode", $"{_reason}；请求的模式为 {mode}。")));
    }

    /// <inheritdoc />
    public PerformanceSettings GetStageSettings(PerformanceStage stage)
        // 未适配时如实返回默认配置表：没有协调器，就不存在「用户已修改」的配置。
        => PerformanceProfileCatalog.GetDefault(stage).Settings;

    /// <inheritdoc />
    public StageSettingsUpdateResult UpdateStageSettings(PerformanceSettings settings)
        => new(
            false,
            new[] { $"{_reason}：配置目标无处生效，已保持默认配置表。" },
            null);

    /// <inheritdoc />
    /// <remarks>未接入协调器意味着从未写过硬件，自然不存在「已确认档位」。</remarks>
    public PerformanceStage? LastConfirmedStage => null;
}
