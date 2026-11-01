using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices;
using Xunit;

namespace MsiControl.Fans.Tests;

public sealed class FanCurveValidationTests
{
    private static FanPoint[] Points(params (double T, double P)[] pairs)
        => pairs.Select(p => new FanPoint(p.T, p.P)).ToArray();

    [Fact]
    public void ValidCurve_WithFullyVerifiedLimits_CanWrite()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((40, 20), (60, 40), (80, 80)),
            TestData.VerifiedLimits());

        Assert.True(result.IsValid);
        Assert.False(result.HasUnverifiedConstraints);
        Assert.True(result.CanWrite);
    }

    [Fact]
    public void SingleNode_ReportsCoreError()
    {
        var result = FanCurveValidator.Validate(FanChannel.Cpu, Points((60, 40)), TestData.VerifiedLimits());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("至少需要 2 个节点", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateTemperature_IsRejected()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((60, 30), (60, 40)),
            TestData.VerifiedLimits());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("严格递增", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfRangeOutput_IsReported()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((40, 20), (60, 120)),
            TestData.VerifiedLimits());

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("0–100", StringComparison.Ordinal));
    }

    [Fact]
    public void AllErrors_AreListed_NotJustTheFirst()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((200, 20), (300, 150)),
            TestData.VerifiedLimits());

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 3, $"应一次列出全部问题，实际 {result.Errors.Count} 条。");
    }

    [Fact]
    public void NodeCountAboveHardwareLimit_IsReported()
    {
        var points = new[]
        {
            new FanPoint(30, 10),
            new FanPoint(35, 15),
            new FanPoint(40, 20),
            new FanPoint(45, 25)
        };

        var result = FanCurveValidator.Validate(FanChannel.Cpu, points, TestData.VerifiedLimits(maxNodes: 3));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("超过硬件允许的 3", StringComparison.Ordinal));
    }

    [Fact]
    public void TemperatureOutsideHardwareRange_IsReported()
    {
        var limits = new FanCurveLimits(
            "测试替身：窄温度区间",
            CapabilityLevel.Verified,
            40,
            90,
            0,
            100,
            maxNodes: 8,
            temperatureEditable: true,
            temperatureStepC: 1.0,
            outputStepPercent: 1.0);

        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((30, 20), (60, 40)),
            limits);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("超出硬件允许的", StringComparison.Ordinal));
    }

    [Fact]
    public void OutputNotMultipleOfStep_IsReported()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((40, 20), (60, 45)),
            TestData.VerifiedLimits(outputStepPercent: 10.0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("输出步长", StringComparison.Ordinal));
    }

    [Fact]
    public void TemperatureDeltaNotMultipleOfStep_IsReported()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((40, 20), (50, 40), (62, 60)),
            TestData.VerifiedLimits(temperatureStepC: 5.0));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("不是步长", StringComparison.Ordinal));
    }

    [Fact]
    public void TemperatureNotEditable_IsReported()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((40, 20), (60, 40)),
            TestData.VerifiedLimits(temperatureEditable: false));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("温度阈值不可编辑", StringComparison.Ordinal));
    }

    [Fact]
    public void Ms15M3Limits_AlwaysBlockWrite_BecauseConstraintsAreUnverified()
    {
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((40, 20), (60, 40)),
            FanCurveLimits.Ms15M3);

        Assert.True(result.IsValid);
        Assert.True(result.HasUnverifiedConstraints);
        Assert.False(result.CanWrite);
        Assert.Contains("曲线节点上限", result.UnverifiedConstraints);
        Assert.Contains("输出步长", result.UnverifiedConstraints);
        Assert.Contains("无法确认可以写入", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void UnverifiedConstraints_AreListedEvenWhenShapeIsInvalid()
    {
        var result = FanCurveValidator.Validate(FanChannel.Cpu, Points((60, 40)), FanCurveLimits.Ms15M3);

        Assert.False(result.IsValid);
        Assert.True(result.HasUnverifiedConstraints);
    }

    [Fact]
    public void UnorderedInput_IsNormalizedAndAccepted()
    {
        // FanCurve.TryCreate 先按温度排序再校验：乱序输入会被接受（与 Validate 的语义不同）。
        var result = FanCurveValidator.Validate(
            FanChannel.Cpu,
            Points((80, 80), (40, 20), (60, 40)),
            TestData.VerifiedLimits());

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidateCurve_UsesCurveChannel()
    {
        var curve = TestData.Curve(FanChannel.Gpu, (40, 20), (60, 40));
        var result = FanCurveValidator.ValidateCurve(curve, FanCurveLimits.Ms15M3);

        Assert.Equal(FanChannel.Gpu, result.Channel);
    }

    [Fact]
    public void NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => FanCurveValidator.Validate(FanChannel.Cpu, null!, TestData.VerifiedLimits()));
        Assert.Throws<ArgumentNullException>(() => FanCurveValidator.Validate(FanChannel.Cpu, Points((40, 20)), null!));
        Assert.Throws<ArgumentNullException>(() => FanCurveValidator.ValidateCurve(null!, TestData.VerifiedLimits()));
    }
}
