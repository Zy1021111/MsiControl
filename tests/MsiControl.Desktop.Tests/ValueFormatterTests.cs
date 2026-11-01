using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Desktop.Presentation;
using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 读数格式化测试。
/// </summary>
/// <remarks>
/// 覆盖任务书 §6.4 的四态展示要求，以及 §6.5 的频率与风扇口径约定。
/// </remarks>
public sealed class ValueFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 19, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public void Describe_ValidReadingShowsValueWithUnit()
    {
        var reading = SensorReading.Valid(62.44, Units.Celsius, "MSI_ACPI", Now);

        Assert.Equal("62.4 °C", ValueFormatter.Describe(reading));
    }

    [Fact]
    public void Describe_StaleReadingIsExplicitlyMarked()
    {
        var reading = SensorReading.Stale(62.44, Units.Celsius, "MSI_ACPI", Now, "采样超时");

        var text = ValueFormatter.Describe(reading);

        Assert.Contains("62.4 °C", text, StringComparison.Ordinal);
        Assert.Contains("过期", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_StaleReadingCanOmitSuffixWhenCallerMarksIt()
    {
        var reading = SensorReading.Stale(62.44, Units.Celsius, "MSI_ACPI", Now);

        Assert.Equal("62.4 °C", ValueFormatter.Describe(reading, suffixStale: false));
    }

    [Fact]
    public void Describe_SleepingAndUnavailableUseDedicatedWords()
    {
        Assert.Equal("休眠", ValueFormatter.Describe(SensorReading.Sleeping(Units.Celsius, "NVML")));
        Assert.Equal("不可用", ValueFormatter.Describe(SensorReading.Unavailable(Units.Celsius, "MSI_ACPI")));
    }

    [Fact]
    public void FrequencyGigahertz_ConvertsFromMegahertzViaSharedMeasurement()
    {
        var reading = SensorReading.Valid(4120, Units.Megahertz, "MSI_ACPI", Now);

        Assert.Equal("4.12 GHz", ValueFormatter.FrequencyGigahertz(reading));
        Assert.Equal(
            reading.Value!.Value / 1000.0,
            Measurement.MegahertzToGigahertz(reading.Value.Value));
    }

    [Fact]
    public void FrequencyGigahertz_SleepingGpuShowsSleepingNotZero()
    {
        var reading = SensorReading.Sleeping(Units.Megahertz, "NVML");

        Assert.Equal("休眠", ValueFormatter.FrequencyGigahertz(reading));
    }

    [Fact]
    public void Fan_WithoutCalibrationFallsBackToRpmEvenWhenPercentPreferred()
    {
        var fan = FanReading.Valid(3200, "MSI_ACPI", Now, actualPercent: null, targetPercent: 60);

        Assert.Equal("3200 RPM", ValueFormatter.Fan(fan, FanDisplayMode.Percent));
        Assert.NotNull(ValueFormatter.FanFallbackNote(fan, FanDisplayMode.Percent));
        Assert.Equal("目标 60 %", ValueFormatter.TargetPercent(fan));
    }

    [Fact]
    public void Fan_WithCalibrationShowsActualPercentButKeepsTargetSeparate()
    {
        var fan = FanReading.Valid(3900, "MSI_ACPI", Now, actualPercent: 78, targetPercent: 60);

        Assert.Equal("78 %", ValueFormatter.Fan(fan, FanDisplayMode.Percent));
        Assert.Equal("目标 60 %", ValueFormatter.TargetPercent(fan));
        Assert.Null(ValueFormatter.FanFallbackNote(fan, FanDisplayMode.Percent));
    }

    [Fact]
    public void Fan_RpmModeIgnoresCalibration()
    {
        var fan = FanReading.Valid(3900, "MSI_ACPI", Now, actualPercent: 78);

        Assert.Equal("3900 RPM", ValueFormatter.Fan(fan, FanDisplayMode.Rpm));
    }

    [Fact]
    public void Fan_UnavailableAndSleepingAreDistinguished()
    {
        Assert.Equal("不可用", ValueFormatter.Fan(FanReading.Unavailable("MSI_ACPI"), FanDisplayMode.Percent));
        Assert.Equal("休眠", ValueFormatter.Fan(FanReading.Sleeping("NVML"), FanDisplayMode.Percent));
    }

    [Fact]
    public void SystemPower_UnavailableNeverFallsBackToCpuPlusGpu()
    {
        var power = SystemPowerReading.Unavailable("本机无适配器侧读数");

        Assert.Equal("不可用", ValueFormatter.SystemPower(power));
        Assert.False(power.IsAvailable);
    }

    [Fact]
    public void SystemPower_ShowsKindSoScopesAreNotConfused()
    {
        var reading = SensorReading.Valid(45.2, Units.Watt, "Win32_Battery", Now);
        var power = new SystemPowerReading(PowerSourceKind.BatterySide, reading);

        var text = ValueFormatter.SystemPower(power);

        Assert.Contains("45.2 W", text, StringComparison.Ordinal);
        Assert.Contains("电池侧", text, StringComparison.Ordinal);
        Assert.Equal("插座", ValueFormatter.PowerSourceText(PowerSourceKind.WallSocket));
        Assert.Equal("来源未确定", ValueFormatter.PowerSourceText(PowerSourceKind.Unknown));
    }

    [Fact]
    public void SampledAt_MissingTimestampShowsPlaceholder()
    {
        Assert.Equal(ValueFormatter.Placeholder, ValueFormatter.SampledAt(null));
        Assert.Equal(
            Now.ToLocalTime().ToString("HH:mm:ss"),
            ValueFormatter.SampledAt(Now));
    }

    [Fact]
    public void StatusText_CoversAllReadingStates()
    {
        Assert.Equal(string.Empty, ValueFormatter.StatusText(ReadingStatus.Valid));
        Assert.Equal("休眠", ValueFormatter.StatusText(ReadingStatus.Sleeping));
        Assert.Equal("不可用", ValueFormatter.StatusText(ReadingStatus.Unavailable));
        Assert.Equal("过期", ValueFormatter.StatusText(ReadingStatus.Stale));
    }
}
