using MsiControl.Application.Control;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 把应用层的控制协调器适配为桌面壳的控制出口。
/// </summary>
/// <remarks>
/// <para>
/// 这是任务 07（整合负责人）在桌面壳侧添加的**唯一接缝**：桌面壳不引用
/// <c>MsiControl.Hardware.Msi</c>，也不直接持有任何硬件适配层，
/// 只把用户操作转成
/// <see cref="ControlRequestOrigin.Manual"/> 的请求交给协调器
/// （任务 02 的规则 11：窗口只发请求、只订阅状态）。
/// </para>
/// <para>
/// <b>为什么需要这一层适配：</b><c>IControlRequestSink</c> 定义在桌面壳中
/// （<c>MsiControl.Desktop.Shell</c>），而应用层不得反向引用桌面壳；
/// 因此由桌面壳提供这一份薄适配，把 <see cref="ControlOutcome"/> 中的逐项结果
/// 交给界面渲染，处理方式（是否执行 / 被取代 / 被能力门控）以结果消息形式呈现。
/// </para>
/// </remarks>
public sealed class ApplicationControlSink : IControlRequestSink
{
    private readonly ControlCoordinator _coordinator;

    /// <summary>构造适配器。</summary>
    /// <param name="coordinator">应用层控制协调器。</param>
    /// <exception cref="ArgumentNullException">协调器为空。</exception>
    public ApplicationControlSink(ControlCoordinator coordinator)
        => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    /// <summary>
    /// 用户手动切换性能档位且**取得读回确认**后发布。
    /// </summary>
    /// <remarks>
    /// 只在本入口（即用户显式操作）发布，自动策略与启动恢复走宿主，不经过这里——
    /// 否则软件写入的值会被当成「用户确认过的档位」持久化。
    /// </remarks>
    public event EventHandler<PerformanceStage>? StageConfirmed;

    /// <inheritdoc />
    public async Task<ControlResult> RequestPerformanceStageAsync(
        PerformanceStage stage,
        CancellationToken cancellationToken)
    {
        var outcome = await _coordinator
            .RequestPerformanceStageAsync(stage, ControlRequestOrigin.Manual, cancellationToken)
            .ConfigureAwait(true);

        var result = outcome.Result;

        // 判据只看场景项本身的读回确认：整体可能是 PartiallySucceeded
        // （GPU 功耗等无写入通道的项如实报 Unsupported），那不属于失败。
        if (result.Settings.Any(s =>
                s.Setting.EndsWith("stage", StringComparison.Ordinal)
                && s.Status == OperationStatus.Succeeded))
        {
            StageConfirmed?.Invoke(this, stage);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<ControlResult> RestoreDefaultStageAsync(CancellationToken cancellationToken)
    {
        var outcome = await _coordinator
            .RestoreDefaultStageAsync(ControlRequestOrigin.Manual, cancellationToken)
            .ConfigureAwait(true);

        return outcome.Result;
    }

    /// <inheritdoc />
    public async Task<ControlResult> RequestGraphicsModeAsync(
        GraphicsMode mode,
        CancellationToken cancellationToken)
    {
        var outcome = await _coordinator
            .RequestGraphicsModeAsync(mode, ControlRequestOrigin.Manual, cancellationToken)
            .ConfigureAwait(true);

        return outcome.Result;
    }

    /// <inheritdoc />
    public async Task<ControlResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken)
    {
        var outcome = await _coordinator
            .SetFullSpeedAsync(enabled, ControlRequestOrigin.Manual, cancellationToken)
            .ConfigureAwait(true);

        return outcome.Result;
    }

    /// <inheritdoc />
    public async Task<ControlResult> RequestFanModeAsync(
        FanControlMode mode,
        CancellationToken cancellationToken)
    {
        var outcome = await _coordinator
            .RequestFanModeAsync(mode, ControlRequestOrigin.Manual, cancellationToken)
            .ConfigureAwait(true);

        return outcome.Result;
    }

    /// <inheritdoc />
    public PerformanceSettings GetStageSettings(PerformanceStage stage)
        => _coordinator.SettingsFor(stage);

    /// <inheritdoc />
    public StageSettingsUpdateResult UpdateStageSettings(PerformanceSettings settings)
        => _coordinator.UpdateStageSettings(settings);

    /// <inheritdoc />
    /// <remarks>
    /// 直接透传协调器的事实：无论请求来自哪个入口（窗口按钮、托盘、等效热键、
    /// Fn 事件、启动恢复），写入都汇聚到协调器，判据统一为「场景项读完确认」。
    /// </remarks>
    public PerformanceStage? LastConfirmedStage => _coordinator.LastConfirmedStage;
}
