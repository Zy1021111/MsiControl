using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>测试用的假硬件实现。</summary>
internal sealed class FakePerformanceHardware : IPerformanceHardware
{
    public int ApplyCount { get; private set; }

    public PerformanceSettings? LastSettings { get; private set; }

    public ControlResult Response { get; set; } = ControlResult.From(new[]
    {
        SettingResult.Succeeded(
            PerformanceSettingKeys.CpuLongTerm,
            "60 W",
            "60 W",
            confirmationBasis: "写入后读回 60 W（测试桩）")
    });

    public bool ThrowOnApply { get; set; }

    public Task<PerformanceHardwareState> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(PerformanceHardwareState.Unknown);

    public Task<ControlResult> ApplyAsync(PerformanceSettings settings, CancellationToken cancellationToken)
    {
        ApplyCount++;
        LastSettings = settings;

        if (ThrowOnApply)
        {
            throw new OperationCanceledException("测试桩：模拟取消。");
        }

        return Task.FromResult(Response);
    }
}

/// <summary>
/// 应用用例的编排行为。
/// </summary>
public sealed class PerformanceApplicationTests
{
    [Fact]
    public async Task ApplyAsync_DelegatesToHardwareOnce()
    {
        var hardware = new FakePerformanceHardware();
        var application = new PerformanceApplication(hardware);

        var outcome = await application.ApplyAsync(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings);

        Assert.Equal(1, hardware.ApplyCount);
        Assert.Equal("平衡", outcome.StageDisplayName);
        Assert.NotEmpty(outcome.Plan);
        Assert.True(outcome.IsCompliant);
    }

    [Fact]
    public async Task ApplyAsync_ContradictoryConfiguration_NeverReachesHardware()
    {
        var hardware = new FakePerformanceHardware();
        var application = new PerformanceApplication(hardware);

        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings
            with
            {
                CpuLongTerm = PowerLimit.Custom(60m),
                CpuShortTerm = PowerLimit.Custom(40m)
            };

        var outcome = await application.ApplyAsync(settings);

        Assert.Equal(0, hardware.ApplyCount);
        Assert.True(outcome.ConfigurationRejected);
        Assert.NotEmpty(outcome.ConfigurationProblems);
        Assert.Empty(outcome.Result.Validate());
        Assert.Equal(OperationStatus.Failed, outcome.Result.Status);
    }

    [Fact]
    public async Task ApplyAsync_SurfacesUnsubstantiatedSuccessAsComplianceProblem()
    {
        var hardware = new FakePerformanceHardware
        {
            Response = ControlResult.From(new[]
            {
                new SettingResult(
                    PerformanceSettingKeys.CpuLongTerm,
                    Requested: "60 W",
                    ReadBack: null,
                    Status: OperationStatus.Succeeded,
                    Message: null,
                    ConfirmationBasis: null)
            })
        };

        var application = new PerformanceApplication(hardware);

        var outcome = await application.ApplyAsync(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings);

        Assert.False(outcome.IsCompliant);
        Assert.Contains(outcome.ResultProblems, p => p.Contains("缺少确认依据"));
    }

    [Fact]
    public async Task ApplyAsync_ReportsThirdPartyConflicts_WhenProbeIsProvided()
    {
        var application = new PerformanceApplication(
            new FakePerformanceHardware(),
            new FakeProcessPresenceProbe("XTU", "MSIAfterburner"));

        var outcome = await application.ApplyAsync(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Quiet).Settings);

        Assert.Equal(2, outcome.ThirdPartyConflicts.Count);
        Assert.NotNull(outcome.ThirdPartySummary);
    }

    [Fact]
    public async Task ApplyAsync_SkipsConflictDetection_WhenNoProbeIsInjected()
    {
        var application = new PerformanceApplication(new FakePerformanceHardware());

        var outcome = await application.ApplyAsync(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Quiet).Settings);

        Assert.Empty(outcome.ThirdPartyConflicts);
        Assert.Null(outcome.ThirdPartySummary);
    }

    [Fact]
    public async Task ApplyAsync_PlanAndResultShareTheSameSettingKeys()
    {
        // 用真实的 MS-15M3 实现（假只读通道）核对：硬件层逐项结果的标识
        // 必须与 PerformancePlanner 生成的计划逐项对应，否则界面无法把请求值与结果对齐。
        var hardware = new Ms15M3PerformanceHardware(
            new FakeMsiAcpiTransport(),
            Ms15M3Capabilities.Create());
        var application = new PerformanceApplication(hardware);

        var outcome = await application.ApplyAsync(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings);

        Assert.Equal(
            outcome.Plan.Select(p => p.Key).ToArray(),
            outcome.Result.Settings.Select(s => s.Setting).ToArray());
    }

    [Fact]
    public async Task ApplyAsync_PlanAndResultStayAligned_WhenFallbackIsPresent()
    {
        var hardware = new Ms15M3PerformanceHardware(
            new FakeMsiAcpiTransport(),
            Ms15M3Capabilities.Create());
        var application = new PerformanceApplication(hardware);

        var fallback = GpuEcoFallback.Create(
            "低于下界，改用下界。",
            PowerLimit.Custom(35m),
            "不保证精确达成目标瓦数。");

        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Eco).Settings
            with { GpuEcoFallback = fallback };

        var outcome = await application.ApplyAsync(settings);

        Assert.Contains(outcome.Plan, p => p.Key == PerformanceSettingKeys.GpuEco);
        Assert.Equal(
            outcome.Plan.Select(p => p.Key).ToArray(),
            outcome.Result.Settings.Select(s => s.Setting).ToArray());
        Assert.True(outcome.IsCompliant);
    }

    [Fact]
    public async Task ApplyAsync_ExposesPerItemResultLookup()
    {
        var application = new PerformanceApplication(new FakePerformanceHardware());

        var outcome = await application.ApplyAsync(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings);

        Assert.NotNull(outcome.ResultFor(PerformanceSettingKeys.CpuLongTerm));
        Assert.Null(outcome.ResultFor("not.a.key"));
    }

    [Fact]
    public async Task ApplyAsync_PropagatesCancellation()
    {
        var hardware = new FakePerformanceHardware { ThrowOnApply = true };
        var application = new PerformanceApplication(hardware);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => application.ApplyAsync(PerformanceProfileCatalog.GetDefault(PerformanceStage.Eco).Settings));
    }

    [Fact]
    public async Task ApplyAsync_RejectsNullSettings()
    {
        var application = new PerformanceApplication(new FakePerformanceHardware());

        await Assert.ThrowsAsync<ArgumentNullException>(() => application.ApplyAsync(null!));
    }
}
