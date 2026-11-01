using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>
/// 设置项标识与五档默认值的交叉一致性。
/// </summary>
/// <remarks>
/// 硬件层为保持「Hardware 只依赖 Core」的依赖方向，用字面量登记设置项标识，
/// 因此必须由测试锁定它与 <see cref="PerformanceSettingKeys"/> 完全一致——
/// 否则界面按计划渲染的行会与硬件返回的结果错位。
/// </remarks>
public sealed class SettingKeyConsistencyTests
{
    [Fact]
    public void HardwareSettingKeys_MatchPerformanceModuleKeys()
    {
        // 用分隔符拼接后比较：既能发现缺项/多项，也能发现顺序错位。
        Assert.Equal(
            string.Join('|', PerformanceSettingKeys.All),
            string.Join('|', Ms15M3PerformanceHardware.SettingKeys));
    }

    [Fact]
    public void CapabilityKeyFor_CoversEveryRegisteredSetting()
    {
        foreach (var key in PerformanceSettingKeys.All)
        {
            Assert.True(PerformanceSettingKeys.IsKnown(key));
            Assert.False(string.IsNullOrWhiteSpace(PerformanceSettingKeys.CapabilityKeyFor(key)));
        }
    }

    [Fact]
    public void CapabilityKeyFor_RejectsUnknownSetting()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PerformanceSettingKeys.CapabilityKeyFor("unknown.key"));

    [Fact]
    public void ConfigurationCheckKey_IsSharedByBothLayers()
    {
        Assert.Equal(
            PerformanceApplication.ConfigurationCheckKey,
            Ms15M3PerformanceHardware.ConfigurationCheckKey);
    }

    [Fact]
    public void ConfigurationCheckKey_IsNotACapabilityGatedSetting()
    {
        Assert.False(PerformanceSettingKeys.IsKnown(PerformanceApplication.ConfigurationCheckKey));
        Assert.DoesNotContain(PerformanceApplication.ConfigurationCheckKey, Ms15M3PerformanceHardware.SettingKeys);
    }

    [Fact]
    public void Defaults_MatchTaskBookSection81()
    {
        Assert.Equal(10m, Watts(PerformanceStage.Eco, s => s.CpuLongTerm));
        Assert.Equal(20m, Watts(PerformanceStage.Eco, s => s.GpuLimit));
        Assert.Equal(50m, Watts(PerformanceStage.Quiet, s => s.CpuLongTerm));
        Assert.Equal(80m, Watts(PerformanceStage.Quiet, s => s.GpuLimit));
        Assert.Equal(60m, Watts(PerformanceStage.Balanced, s => s.CpuLongTerm));
        Assert.Equal(90m, Watts(PerformanceStage.Balanced, s => s.GpuLimit));
    }

    [Fact]
    public void NativeStages_UseNativeModeForEveryLimit()
    {
        foreach (var stage in new[] { PerformanceStage.HighPerformance, PerformanceStage.Extreme })
        {
            var settings = PerformanceProfileCatalog.GetDefault(stage).Settings;

            Assert.True(settings.IsFullyNative);
            Assert.True(settings.CpuLongTerm.IsNative);
            Assert.True(settings.CpuShortTerm.IsNative);
            Assert.True(settings.CpuTurbo.IsNative);
            Assert.True(settings.GpuLimit.IsNative);
            Assert.Null(settings.CpuLongTerm.Watts);
        }
    }

    [Fact]
    public void HighPerformanceAndExtreme_DifferOnlyByThermalConfiguration()
    {
        var high = PerformanceProfileCatalog.GetDefault(PerformanceStage.HighPerformance).Settings;
        var extreme = PerformanceProfileCatalog.GetDefault(PerformanceStage.Extreme).Settings;

        // 本机未取得「两档功耗能力存在差异」的证据，因此两者功耗配置完全一致：
        // 差异由各自独立的风扇曲线（散热配置）表达，不虚构增强能力（任务书 §8.4）。
        Assert.Equal(high.CpuLongTerm, extreme.CpuLongTerm);
        Assert.Equal(high.CpuShortTerm, extreme.CpuShortTerm);
        Assert.Equal(high.GpuLimit, extreme.GpuLimit);
        Assert.Equal(high.CpuTurbo, extreme.CpuTurbo);
    }

    [Fact]
    public void LimitedStages_ConstrainShortTermEqualToLongTerm()
    {
        foreach (var stage in new[] { PerformanceStage.Eco, PerformanceStage.Quiet, PerformanceStage.Balanced })
        {
            var settings = PerformanceProfileCatalog.GetDefault(stage).Settings;

            Assert.False(settings.IsFullyNative);
            Assert.Equal(settings.CpuLongTerm.Watts, settings.CpuShortTerm.Watts);
            Assert.Null(settings.GpuEcoFallback);
        }
    }

    [Fact]
    public void EveryStageAppearsExactlyOnce()
    {
        var stages = PerformanceProfileCatalog.Defaults.Select(p => p.Stage).ToArray();

        Assert.Equal(Enum.GetValues<PerformanceStage>().Length, stages.Length);
        Assert.Equal(stages.Distinct().Count(), stages.Length);
    }

    private static decimal? Watts(PerformanceStage stage, Func<PerformanceSettings, PowerLimit> selector)
        => selector(PerformanceProfileCatalog.GetDefault(stage).Settings).Watts;
}
