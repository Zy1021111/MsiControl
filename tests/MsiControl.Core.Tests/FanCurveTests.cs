using MsiControl.Core;
using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 风扇曲线校验与插值测试。
/// </summary>
public class FanCurveTests
{
    private static readonly FanPoint[] ValidPoints =
    {
        new(30, 0),
        new(60, 40),
        new(90, 100)
    };

    [Fact]
    public void Validate_AcceptsValidCurve()
    {
        Assert.Empty(FanCurve.Validate(ValidPoints));
    }

    [Fact]
    public void Validate_RejectsFewerThanTwoPoints()
    {
        var errors = FanCurve.Validate(new[] { new FanPoint(50, 50) });

        Assert.Single(errors);
        Assert.Contains("至少需要 2 个节点", errors[0]);
    }

    [Fact]
    public void Validate_RejectsNonIncreasingTemperature()
    {
        var errors = FanCurve.Validate(new[]
        {
            new FanPoint(60, 40),
            new FanPoint(60, 80)
        });

        Assert.Contains(errors, e => e.Contains("严格递增"));
    }

    [Fact]
    public void Validate_RejectsDecreasingTemperature()
    {
        var errors = FanCurve.Validate(new[]
        {
            new FanPoint(80, 40),
            new FanPoint(60, 80)
        });

        Assert.Contains(errors, e => e.Contains("严格递增"));
    }

    [Theory]
    [InlineData(-21.0)]
    [InlineData(131.0)]
    public void Validate_RejectsOutOfRangeTemperature(double temperature)
    {
        var errors = FanCurve.Validate(new[]
        {
            new FanPoint(temperature, 30),
            new FanPoint(100, 80)
        });

        Assert.Contains(errors, e => e.Contains("超出允许范围"));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(101.0)]
    public void Validate_RejectsOutOfRangeOutput(double output)
    {
        var errors = FanCurve.Validate(new[]
        {
            new FanPoint(30, output),
            new FanPoint(100, 80)
        });

        Assert.Contains(errors, e => e.Contains("超出允许范围"));
    }

    [Fact]
    public void TryCreate_OrdersPointsAscending()
    {
        var ok = FanCurve.TryCreate(
            FanChannel.Cpu,
            new[]
            {
                new FanPoint(90, 100),
                new FanPoint(30, 0),
                new FanPoint(60, 40)
            },
            out var curve,
            out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.NotNull(curve);
        Assert.Equal(new double[] { 30, 60, 90 }, curve!.Points.Select(p => p.TemperatureC).ToArray());
        Assert.Equal(FanChannel.Cpu, curve.Channel);
    }

    [Fact]
    public void TryCreate_ReturnsNullCurveOnInvalidInput()
    {
        var ok = FanCurve.TryCreate(
            FanChannel.Gpu,
            new[] { new FanPoint(50, 50) },
            out var curve,
            out var errors);

        Assert.False(ok);
        Assert.Null(curve);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Create_ThrowsOnInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => FanCurve.Create(FanChannel.Cpu, new[] { new FanPoint(50, 50) }));
    }

    [Theory]
    [InlineData(20.0, 0.0)]     // 低于首节点 → 取首节点输出
    [InlineData(30.0, 0.0)]     // 恰在首节点
    [InlineData(45.0, 20.0)]    // 30→60 区间的中点
    [InlineData(60.0, 40.0)]    // 恰在中间节点
    [InlineData(75.0, 70.0)]    // 60→90 区间的中点
    [InlineData(90.0, 100.0)]   // 恰在末节点
    [InlineData(200.0, 100.0)]  // 高于末节点 → 取末节点输出
    public void Interpolate_ReturnsExpectedOutput(double temperature, double expected)
    {
        var curve = FanCurve.Create(FanChannel.Cpu, ValidPoints);

        Assert.Equal(expected, curve.Interpolate(temperature), 6);
    }

    [Fact]
    public void Interpolate_IsClampedAtBothEnds()
    {
        var curve = FanCurve.Create(FanChannel.Gpu, ValidPoints);

        Assert.InRange(curve.Interpolate(-100), 0, 100);
        Assert.InRange(curve.Interpolate(1000), 0, 100);
    }

    [Fact]
    public void NodeCount_MinAndMaxTemperatureAreExposed()
    {
        var curve = FanCurve.Create(FanChannel.Cpu, ValidPoints);

        Assert.Equal(3, curve.NodeCount);
        Assert.Equal(30, curve.MinTemperatureC);
        Assert.Equal(90, curve.MaxTemperatureC);
    }

    [Fact]
    public void WithChannel_KeepsPointsAndSwitchesChannel()
    {
        var cpu = FanCurve.Create(FanChannel.Cpu, ValidPoints);

        var gpu = cpu.WithChannel(FanChannel.Gpu);

        Assert.Equal(FanChannel.Gpu, gpu.Channel);
        Assert.Equal(cpu.Points, gpu.Points);
        Assert.Equal(FanChannel.Cpu, cpu.Channel); // 原对象不被修改
    }
}
