using MsiControl.Application.Control;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Shell;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 「性能模式只由用户确认」的桌面接缝测试。
/// </summary>
/// <remarks>
/// <para>
/// 这里锁定两条不可回退的行为：
/// <list type="number">
/// <item>只有<b>手动切换且场景项读回成功</b>才发布 <see cref="ApplicationControlSink.StageConfirmed"/>——
///       该事件是「把档位写进设置文件」的唯一触发源，误报会让软件写入的值被当成用户确认。</item>
/// <item>退出恢复走 <see cref="IScenarioRestoreChannel"/>，<b>不</b>调用整档应用，
///       因此不会连带改写 CPU 功耗上限与风扇设置。</item>
/// </list>
/// </para>
/// <para>不构造窗体、不触硬件：适配层为本地替身。</para>
/// </remarks>
public sealed class PerformanceStageRecordingTests
{
    [Fact]
    public async Task ManualStageSwitch_WithReadBackConfirmation_RaisesStageConfirmed()
    {
        var hardware = new SwitchablePerformanceHardware();
        var sink = new ApplicationControlSink(CreateCoordinator(hardware));
        PerformanceStage? confirmed = null;
        sink.StageConfirmed += (_, stage) => confirmed = stage;

        var result = await sink.RequestPerformanceStageAsync(PerformanceStage.HighPerformance, CancellationToken.None);

        Assert.Contains(result.Settings, s => s.Status == OperationStatus.Succeeded);
        Assert.Equal(PerformanceStage.HighPerformance, confirmed);
    }

    [Fact]
    public async Task ManualStageSwitch_WhenWriteFails_DoesNotRaiseStageConfirmed()
    {
        var hardware = new SwitchablePerformanceHardware
        {
            ApplyHandler = (_, _) => Task.FromResult(ControlResult.Single(
                SettingResult.Failed("stage", "狂暴", "写入后读回不一致（替身）")))
        };

        var sink = new ApplicationControlSink(CreateCoordinator(hardware));
        var raised = false;
        sink.StageConfirmed += (_, _) => raised = true;

        await sink.RequestPerformanceStageAsync(PerformanceStage.Extreme, CancellationToken.None);

        Assert.False(raised);
    }

    [Fact]
    public async Task RestoreDefaultStage_WritesScenarioOnly()
    {
        var hardware = new SwitchablePerformanceHardware();
        var sink = new ApplicationControlSink(CreateCoordinator(hardware));

        var result = await sink.RestoreDefaultStageAsync(CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, hardware.RestoreCalls);
        Assert.Equal(0, hardware.ApplyCalls);
        Assert.Equal(PerformanceShiftMap.FirmwareDefaultStage, hardware.LastRestoredStage);
    }

    private static ControlCoordinator CreateCoordinator(SwitchablePerformanceHardware hardware)
    {
        var capabilities = new DeviceCapabilities(new[]
        {
            CapabilityStatus.Verified(CapabilityKeys.PerformanceStage, "桌面接缝测试替身")
        });

        return new ControlCoordinator(capabilities, hardware);
    }

    /// <summary>场景型适配层替身：同时实现「仅写场景」的恢复通道。</summary>
    private sealed class SwitchablePerformanceHardware
        : IPerformanceHardware, IScenarioPerformanceHardware, IScenarioRestoreChannel
    {
        public Func<PerformanceSettings, CancellationToken, Task<ControlResult>> ApplyHandler { get; set; }
            = (settings, _) => Task.FromResult(ControlResult.Single(
                SettingResult.Succeeded(
                    "stage",
                    settings.Stage.ToString(),
                    "读回一致",
                    "写入 0xD2 后立即读回一致（替身）")));

        public int ApplyCalls { get; private set; }

        public int RestoreCalls { get; private set; }

        public PerformanceStage? LastRestoredStage { get; private set; }

        public Task<PerformanceHardwareState> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(new PerformanceHardwareState(PerformanceShiftMap.FirmwareDefaultStage));

        public Task<ControlResult> ApplyAsync(PerformanceSettings settings, CancellationToken cancellationToken)
        {
            ApplyCalls++;
            return ApplyHandler(settings, cancellationToken);
        }

        public Task<ControlResult> ApplyScenarioOnlyAsync(
            PerformanceStage stage,
            CancellationToken cancellationToken)
        {
            RestoreCalls++;
            LastRestoredStage = stage;
            return Task.FromResult(ControlResult.Single(
                SettingResult.Succeeded(
                    "stage",
                    stage.ToString(),
                    "读回一致",
                    "写入 0xD2 后立即读回一致（替身）")));
        }
    }
}
