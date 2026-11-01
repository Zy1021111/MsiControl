using MsiControl.Core;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 单位换算测试。
/// </summary>
/// <remarks>
/// ACPI 热区温度的「十分之一开尔文」是冷却类工具最常见的错误来源，
/// 因此换算逻辑必须有回归保护。
/// </remarks>
public class MeasurementTests
{
    [Theory]
    [InlineData(3332, 60.05)]   // 本机实测值 ACPI\ThermalZone\THRM_0
    [InlineData(3832, 110.05)]  // 本机实测 CriticalTripPoint
    [InlineData(2732, 0.05)]    // 冰点附近
    [InlineData(3000, 26.85)]   // 常温
    public void DeciKelvinToCelsius_ConvertsCorrectly(double deciKelvin, double expectedCelsius)
    {
        var actual = Measurement.DeciKelvinToCelsius(deciKelvin);

        Assert.Equal(expectedCelsius, actual, 2);
    }

    [Fact]
    public void CelsiusToDeciKelvin_IsInverseOfDeciKelvinToCelsius()
    {
        const double original = 3332;

        var celsius = Measurement.DeciKelvinToCelsius(original);
        var roundTrip = Measurement.CelsiusToDeciKelvin(celsius);

        Assert.Equal((long)original, roundTrip);
    }

    [Fact]
    public void DeciKelvinToCelsius_DoesNotTreatRawValueAsCelsius()
    {
        // 防回归：3332 绝不等于 33.32 °C，也不等于 3332 °C。
        var actual = Measurement.DeciKelvinToCelsius(3332);

        Assert.NotEqual(33.32, actual, 2);
        Assert.NotEqual(3332.0, actual, 2);
    }

    [Theory]
    [InlineData(5400, 5.4)]
    [InlineData(1000, 1.0)]
    [InlineData(0, 0.0)]
    public void MegahertzToGigahertz_ConvertsCorrectly(double megahertz, double expectedGigahertz)
    {
        Assert.Equal(expectedGigahertz, Measurement.MegahertzToGigahertz(megahertz), 6);
    }

    [Fact]
    public void GigahertzToMegahertz_IsInverseOfMegahertzToGigahertz()
    {
        Assert.Equal(5400.0, Measurement.GigahertzToMegahertz(Measurement.MegahertzToGigahertz(5400)), 6);
    }

    [Theory]
    [InlineData(-10.0, 0.0, 100.0, 0.0)]
    [InlineData(150.0, 0.0, 100.0, 100.0)]
    [InlineData(50.0, 0.0, 100.0, 50.0)]
    public void Clamp_ConstrainsDoubleWithinRange(double value, double min, double max, double expected)
    {
        Assert.Equal(expected, Measurement.Clamp(value, min, max), 6);
    }

    [Theory]
    [InlineData(-1.0, 0.0, 10.0, 0.0)]
    [InlineData(99.0, 0.0, 10.0, 10.0)]
    [InlineData(5.0, 0.0, 10.0, 5.0)]
    public void Clamp_ConstrainsDecimalWithinRange(decimal value, decimal min, decimal max, decimal expected)
    {
        Assert.Equal(expected, Measurement.Clamp(value, min, max));
    }

    [Theory]
    [InlineData(25.0, true)]
    [InlineData(-20.0, true)]
    [InlineData(130.0, true)]
    [InlineData(-20.1, false)]
    [InlineData(130.1, false)]
    public void IsValidCurveTemperature_EnforcesBounds(double celsius, bool expected)
    {
        Assert.Equal(expected, Measurement.IsValidCurveTemperature(celsius));
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(100.0, true)]
    [InlineData(-0.1, false)]
    [InlineData(100.1, false)]
    public void IsValidCurveOutput_EnforcesBounds(double percent, bool expected)
    {
        Assert.Equal(expected, Measurement.IsValidCurveOutput(percent));
    }

    /// <summary>
    /// 风扇占空比 → 转速的两点折线标定。45% = 3000 RPM 是叶哥 2026-10-31 给出的基准，
    /// 曾按「满速/100 = 90 转每百分比」的线性模型算成 4050 RPM（偏高 35%）。
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(20.0, 1333.333)]  // 低段：20/45 × 3000
    [InlineData(45.0, 3000.0)]   // 标准工况锚点（本次修复的判据）
    [InlineData(60.0, 4636.364)] // 高段：(60−45) × 109.09 + 3000
    [InlineData(100.0, 9000.0)]  // 满速锚点
    public void FanDutyToEstimatedRpm_UsesTwoPointCalibration(double percent, double expectedRpm)
    {
        Assert.Equal(expectedRpm, Measurement.FanDutyToEstimatedRpm(percent), 3);
    }

    [Fact]
    public void FanDutyToEstimatedRpm_ClampsOutOfRangeInput()
    {
        Assert.Equal(0, Measurement.FanDutyToEstimatedRpm(-10));
        Assert.Equal(9000, Measurement.FanDutyToEstimatedRpm(150));
        Assert.Equal(0, Measurement.FanDutyToEstimatedRpm(double.NaN));
    }

    [Fact]
    public void FanDutyToEstimatedRpm_IsStrictlyMonotonic()
    {
        var previous = -1.0;
        for (var percent = 0; percent <= 100; percent++)
        {
            var rpm = Measurement.FanDutyToEstimatedRpm(percent);
            Assert.True(rpm > previous, $"{percent}% 处的估算转速 {rpm} 未严格大于前一档 {previous}。");
            previous = rpm;
        }
    }

    [Theory]
    [InlineData(3000.0, 45.0)]
    [InlineData(9000.0, 100.0)]
    [InlineData(0.0, 0.0)]
    public void FanRpmToEstimatedDuty_IsInverseOfDutyToRpm(double rpm, double expectedPercent)
    {
        Assert.Equal(expectedPercent, Measurement.FanRpmToEstimatedDuty(rpm), 3);
    }

    [Fact]
    public void FanRpmToEstimatedDuty_ClampsAboveFullSpeed()
    {
        Assert.Equal(100, Measurement.FanRpmToEstimatedDuty(12000));
    }
}
