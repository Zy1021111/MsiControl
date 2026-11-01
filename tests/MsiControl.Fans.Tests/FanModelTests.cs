using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Devices;
using Xunit;

namespace MsiControl.Fans.Tests;

public sealed class FanCurveSetTests
{
    [Fact]
    public void TryCreate_BuildsBothChannels()
    {
        var ok = FanCurveSet.TryCreate(
            new[] { new FanPoint(40, 20), new FanPoint(80, 80) },
            new[] { new FanPoint(40, 30), new FanPoint(80, 90) },
            out var set,
            out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.NotNull(set);
        Assert.Equal(FanChannel.Cpu, set!.Cpu.Channel);
        Assert.Equal(FanChannel.Gpu, set.Gpu.Channel);
    }

    [Fact]
    public void TryCreate_PrefixesErrorsWithChannel()
    {
        var ok = FanCurveSet.TryCreate(
            new[] { new FanPoint(40, 20) },
            new[] { new FanPoint(40, 20), new FanPoint(80, 80) },
            out var set,
            out var errors);

        Assert.False(ok);
        Assert.Null(set);
        Assert.Contains(errors, e => e.StartsWith("CPU 通道：", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DetectsSwappedChannels()
    {
        var set = new FanCurveSet(
            TestData.Curve(FanChannel.Gpu, (40, 20), (80, 80)),
            TestData.Curve(FanChannel.Cpu, (40, 20), (80, 80)));

        var problems = set.Validate();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("CPU 位置", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("GPU 位置", StringComparison.Ordinal));
    }

    [Fact]
    public void For_ReturnsMatchingChannel()
    {
        var set = TestData.CurveSet();

        Assert.Same(set.Cpu, set.For(FanChannel.Cpu));
        Assert.Same(set.Gpu, set.For(FanChannel.Gpu));
    }
}

public sealed class FanStageCurveSetTests
{
    [Fact]
    public void Empty_ReportsAllFiveStagesMissing()
    {
        var problems = FanStageCurveSet.Empty.Validate();

        Assert.Equal(5, problems.Count);
        Assert.Equal(5, FanStageCurveSet.Empty.MissingStages().Count);
    }

    [Fact]
    public void ConfiguredStages_FollowEnumOrder()
    {
        var set = FanStageCurveSet.Empty
            .WithStage(PerformanceStage.Extreme, TestData.CurveSet())
            .WithStage(PerformanceStage.Eco, TestData.CurveSet());

        Assert.Equal(new[] { PerformanceStage.Eco, PerformanceStage.Extreme }, set.ConfiguredStages);
    }

    [Fact]
    public void Validate_ReportsChannelMismatchWithStageName()
    {
        var swapped = new FanCurveSet(
            TestData.Curve(FanChannel.Gpu, (40, 20), (80, 80)),
            TestData.Curve(FanChannel.Cpu, (40, 20), (80, 80)));

        var set = FanStageCurveSet.Empty.WithStage(PerformanceStage.Balanced, swapped);
        var problems = set.Validate();

        Assert.Contains(problems, p => p.StartsWith("Balanced：", StringComparison.Ordinal));
    }

    [Fact]
    public void WithStage_DoesNotMutateOriginal()
    {
        var original = FanStageCurveSet.Empty;
        var updated = original.WithStage(PerformanceStage.Balanced, TestData.CurveSet());

        Assert.Empty(original.ConfiguredStages);
        Assert.Single(updated.ConfiguredStages);
    }

    [Fact]
    public void ForStage_ReturnsNullWhenMissing()
    {
        Assert.Null(FanStageCurveSet.Empty.ForStage(PerformanceStage.Quiet));
    }
}

public sealed class FanCurveComparisonTests
{
    [Fact]
    public void EquivalentCurves_AreEqualAcrossInstances()
    {
        var left = TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40));
        var right = TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40));

        Assert.False(ReferenceEquals(left, right));
        Assert.True(FanCurveComparison.AreEquivalent(left, right));
    }

    [Fact]
    public void DifferentOutput_IsNotEquivalent()
    {
        var left = TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40));
        var right = TestData.Curve(FanChannel.Cpu, (40, 20), (60, 41));

        Assert.False(FanCurveComparison.AreEquivalent(left, right));
    }

    [Fact]
    public void DifferentChannelOrNodeCount_IsNotEquivalent()
    {
        Assert.False(FanCurveComparison.AreEquivalent(
            TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40)),
            TestData.Curve(FanChannel.Gpu, (40, 20), (60, 40))));

        Assert.False(FanCurveComparison.AreEquivalent(
            TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40)),
            TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40), (80, 80))));
    }

    [Fact]
    public void Nulls_AreHandledExplicitly()
    {
        Assert.True(FanCurveComparison.AreEquivalent((FanCurve?)null, null));
        Assert.False(FanCurveComparison.AreEquivalent(TestData.Curve(FanChannel.Cpu, (40, 20), (60, 40)), null));
        Assert.False(FanCurveComparison.AreEquivalent((FanCurveSet?)null, TestData.CurveSet()));
    }
}

public sealed class FanOperationResultTests
{
    [Fact]
    public void Blocked_ProducesUnsupportedWithoutHardwareResult()
    {
        var result = FanOperationResult.Blocked(FanOperationKind.ApplyCurve, "能力未达 Verified。");

        Assert.True(result.IsBlocked);
        Assert.Null(result.Control);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public void Success_WithoutConfirmationBasis_IsDetected()
    {
        var result = new FanOperationResult(
            FanOperationKind.ApplyCurve,
            OperationStatus.Succeeded,
            "看起来成功了。");

        Assert.True(result.IsUnsubstantiatedSuccess);
        Assert.Contains(result.Validate(), p => p.Contains("确认依据", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocked_WithHardwareResult_IsDetected()
    {
        var result = new FanOperationResult(
            FanOperationKind.ApplyCurve,
            OperationStatus.Unsupported,
            "被拦下但带了硬件结果。",
            ControlResult.Single(SettingResult.Unsupported("fan.curve.cpu", "原因")));

        Assert.Contains(result.Validate(), p => p.Contains("不应产生硬件结果对象", StringComparison.Ordinal));
    }

    [Fact]
    public void Blocked_WithEmptyMessage_Throws()
    {
        Assert.Throws<ArgumentException>(() => FanOperationResult.Blocked(FanOperationKind.ApplyCurve, "  "));
        Assert.Throws<ArgumentException>(() => FanOperationResult.Failed(FanOperationKind.ApplyCurve, string.Empty));
    }
}

public sealed class FanReadResultTests
{
    [Fact]
    public void TransportOnly_AlwaysReportsUnknownState()
    {
        var result = FanReadResult.TransportOnly(DateTimeOffset.Now, "语义未确证。");

        Assert.Equal(FanReadStatus.TransportOnly, result.Status);
        Assert.Equal(FanHardwareState.Unknown, result.State);
        Assert.False(result.CanSnapshot);
    }

    [Fact]
    public void Verified_RequiresBothCurvesForSnapshot()
    {
        var single = new FanHardwareState(FanControlMode.CustomCurve, TestData.CurveSet().Cpu, null);
        var partial = FanReadResult.Verified(single, DateTimeOffset.Now, "只有 CPU。");
        Assert.False(partial.CanSnapshot);

        var full = FanReadResult.Verified(TestData.CustomState(), DateTimeOffset.Now, "双通道。");
        Assert.True(full.CanSnapshot);
    }
}

public sealed class FanBootSnapshotStoreTests
{
    [Fact]
    public void VerifiedRead_WithBothCurves_IsCaptured()
    {
        var store = new FanBootSnapshotStore();
        var read = FanReadResult.Verified(TestData.CustomState(), DateTimeOffset.Now, "双通道。");

        Assert.True(store.TryCapture(read));
        Assert.True(store.HasSnapshot);
        Assert.NotNull(store.CapturedAt);
        Assert.Null(store.LastCaptureFailure);
    }

    [Fact]
    public void TransportOnlyRead_IsRejected()
    {
        var store = new FanBootSnapshotStore();

        Assert.False(store.TryCapture(FanReadResult.TransportOnly(DateTimeOffset.Now, "语义未确证。")));
        Assert.False(store.HasSnapshot);
        Assert.NotNull(store.LastCaptureFailure);
        Assert.Contains("不可信", store.LastCaptureFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableRead_IsRejected()
    {
        var store = new FanBootSnapshotStore();

        Assert.False(store.TryCapture(FanReadResult.Unavailable(DateTimeOffset.Now, "通道故障。")));
        Assert.Contains("不可信", store.LastCaptureFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedRead_WithoutCurves_IsRejected()
    {
        var store = new FanBootSnapshotStore();
        var read = FanReadResult.Verified(
            new FanHardwareState(FanControlMode.Automatic, null, null),
            DateTimeOffset.Now,
            "自动模式，无曲线。");

        Assert.False(store.TryCapture(read));
        Assert.Contains("自动模式下曲线为空", store.LastCaptureFailure!, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondCapture_DoesNotOverwriteFirst()
    {
        var store = new FanBootSnapshotStore();
        Assert.True(store.TryCapture(FanReadResult.Verified(TestData.CustomState(), DateTimeOffset.Now, "第一次。")));

        var first = store.Snapshot;
        var other = TestData.CurveSet();
        Assert.True(store.TryCapture(FanReadResult.Verified(
            new FanHardwareState(FanControlMode.CustomCurve, other.Cpu, other.Gpu),
            DateTimeOffset.Now,
            "第二次。")));

        Assert.Same(first, store.Snapshot);
    }

    [Fact]
    public void DescribeRestoreBlocker_ExplainsWhyRestoreIsImpossible()
    {
        var store = new FanBootSnapshotStore();
        var blocker = store.DescribeRestoreBlocker();

        Assert.NotNull(blocker);
        Assert.Contains("BIOS", blocker!, StringComparison.Ordinal);
        Assert.Contains(FanBootSnapshotStore.RestoreSemanticsNotice, store.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Clear_RemovesSnapshot()
    {
        var store = new FanBootSnapshotStore();
        store.TryCapture(FanReadResult.Verified(TestData.CustomState(), DateTimeOffset.Now, "双通道。"));
        store.Clear();

        Assert.False(store.HasSnapshot);
        Assert.NotNull(store.DescribeRestoreBlocker());
    }
}

public sealed class FanConfigurationStateTests
{
    [Fact]
    public void Describe_AlwaysProducesFourLines()
    {
        var state = new FanConfigurationState(
            TestData.CurveSet(),
            TestData.CurveSet(),
            new FanAppliedRecord(FanControlMode.CustomCurve, TestData.CurveSet(), DateTimeOffset.Now, "读回一致"),
            FanReadResult.TransportOnly(DateTimeOffset.Now, "语义未确证。"));

        var lines = state.Describe();

        Assert.Equal(4, lines.Count);
        Assert.StartsWith("1. 编辑中的配置", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("2. 本地保存的配置", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("3. 最近成功应用的配置", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("4. 当前硬件状态", lines[3], StringComparison.Ordinal);
        Assert.Contains("未知", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_ReportsNothingConfigured()
    {
        var lines = FanConfigurationState.Empty.Describe();

        Assert.Contains("无", lines[0], StringComparison.Ordinal);
        Assert.Contains("未读取", lines[3], StringComparison.Ordinal);
        Assert.False(FanConfigurationState.Empty.HasUnsavedChanges);
        Assert.False(FanConfigurationState.Empty.HasUnappliedChanges);
    }

    [Fact]
    public void HasUnappliedChanges_WhenAppliedIsAutomatic()
    {
        var state = new FanConfigurationState(
            null,
            TestData.CurveSet(),
            new FanAppliedRecord(FanControlMode.Automatic, null, DateTimeOffset.Now, "读回模式 Automatic"),
            null);

        Assert.True(state.HasUnappliedChanges);
        Assert.Contains("固件自动策略", state.Describe()[2], StringComparison.Ordinal);
    }

    [Fact]
    public void HasUnsavedChanges_ComparesByValueNotReference()
    {
        var editing = TestData.CurveSet();
        var saved = TestData.CurveSet();

        var state = new FanConfigurationState(editing, saved, null, null);

        Assert.False(state.HasUnsavedChanges);
    }
}

public sealed class FanCapabilitySummaryTests
{
    [Fact]
    public void Describe_ListsThreeFanCapabilities()
    {
        var lines = FanCapabilitySummary.Describe(Ms15M3Capabilities.Create());

        Assert.Equal(FanCapabilitySummary.Keys.Count, lines.Count);
        Assert.Contains(lines, l => l.Contains(CapabilityKeys.FanCurve, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(CapabilityKeys.FanFullSpeed, StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains(CapabilityKeys.FanCalibration, StringComparison.Ordinal));
    }

    [Fact]
    public void Ms15M3Baseline_WritePolicy_MatchesEvidence()
    {
        var capabilities = Ms15M3Capabilities.Create();

        // 2026-10-17（HWE §17/§20/§22）：fan.mode 已凭 EC 写入实验升为 Verified；
        // 强冷（fan.fullspeed）写入已实证，叶哥下午决定启用（Verified，写开放）；
        // 曲线 fan.curve 已凭占空比实验（0x71/0x89，HWE §23）升为 Verified（软件曲线引擎保持）；
        // 占空比 fan.pwm、校准仍禁用。
        Assert.True(FanCapabilitySummary.AnyWriteAllowed(capabilities));
        Assert.False(FanCapabilitySummary.AllWritesBlocked(capabilities));

        Assert.True(capabilities.AllowsWrite(CapabilityKeys.FanMode));
        Assert.True(capabilities.AllowsWrite(CapabilityKeys.FanFullSpeed));
        Assert.True(capabilities.AllowsWrite(CapabilityKeys.FanCurve));
        Assert.False(capabilities.AllowsWrite(CapabilityKeys.FanPwm));
        Assert.False(capabilities.AllowsWrite(CapabilityKeys.FanCalibration));

        // 逐项对齐「是否可写」与「可读写」文案：按键索引配对，不用子串匹配——
        // 其他键的禁用原因文案里可能提到 fan.curve（如 fan.calibration）。
        var lines = FanCapabilitySummary.Describe(capabilities);
        Assert.Equal(lines.Count, FanCapabilitySummary.Keys.Count);
        for (var index = 0; index < lines.Count; index++)
        {
            var key = FanCapabilitySummary.Keys[index];
            var isAllowed = key is CapabilityKeys.FanCurve
                or CapabilityKeys.FanFullSpeed
                or CapabilityKeys.FanMode;
            Assert.Equal(
                isAllowed,
                lines[index].Contains("可读写", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void UnregisteredDevice_IsAlsoBlocked()
    {
        Assert.True(FanCapabilitySummary.AllWritesBlocked(DeviceCapabilities.Empty));
    }
}
