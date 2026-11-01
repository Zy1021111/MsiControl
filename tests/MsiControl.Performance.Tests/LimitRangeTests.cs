using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>可写范围模型的不变量与行为。</summary>
public sealed class LimitRangeTests
{
    [Fact]
    public void Known_RejectsNegativeMinimum()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LimitRange.Known(-1m, 90m, null, "测试"));

    [Fact]
    public void Known_RejectsMaximumBelowMinimum()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LimitRange.Known(90m, 10m, null, "测试"));

    [Fact]
    public void Known_RejectsNonPositiveStep()
        => Assert.Throws<ArgumentOutOfRangeException>(() => LimitRange.Known(10m, 90m, 0m, "测试"));

    [Fact]
    public void Known_RejectsEmptySource()
        => Assert.Throws<ArgumentException>(() => LimitRange.Known(10m, 90m, null, "   "));

    [Fact]
    public void Unknown_RequiresReason()
        => Assert.Throws<ArgumentException>(() => LimitRange.Unknown(" "));

    [Fact]
    public void Unknown_IsNotTreatedAsKnown()
    {
        var range = LimitRange.Unknown("Get_Range 尚未调用验证。");

        Assert.False(range.IsKnown);
        Assert.False(range.Supports(60m));
        Assert.Null(range.NearestSupported(60m));
        Assert.Contains("Get_Range", range.Describe("W"));
    }

    [Fact]
    public void Supports_IsInclusiveOnBothBounds()
    {
        var range = LimitRange.Known(10m, 90m, 5m, "测试");

        Assert.True(range.Supports(10m));
        Assert.True(range.Supports(90m));
        Assert.False(range.Supports(9.9m));
        Assert.False(range.Supports(90.1m));
    }

    [Fact]
    public void NearestSupported_ClampsToBounds()
    {
        var range = LimitRange.Known(35m, 90m, null, "测试");

        Assert.Equal(35m, range.NearestSupported(20m));
        Assert.Equal(90m, range.NearestSupported(200m));
        Assert.Equal(60m, range.NearestSupported(60m));
    }

    [Fact]
    public void Describe_IncludesStepWhenReported()
    {
        var range = LimitRange.Known(10m, 90m, 5m, "Get_Range 返回值");

        var text = range.Describe("W");

        Assert.Contains("10", text);
        Assert.Contains("90 W", text);
        Assert.Contains("步长 5 W", text);
        Assert.Contains("Get_Range 返回值", text);
    }

    [Fact]
    public void Describe_StatesStepUnknown_RatherThanAssumingOne()
    {
        var range = LimitRange.Known(10m, 90m, null, "测试");

        Assert.Contains("步长未报告", range.Describe("W"));
    }
}
