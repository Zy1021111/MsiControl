using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Desktop.Presentation;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 能力门控与文案测试。
/// </summary>
/// <remarks>
/// 这是任务 02 最核心的规则：能力未达 <c>Verified</c> 不得开放写入，
/// 且不可用入口必须给出原因文案而不是仅置灰。
/// </remarks>
public sealed class CapabilityPresenterTests
{
    private static DeviceCapabilities CreateCapabilities() => new(new[]
    {
        CapabilityStatus.Verified(CapabilityKeys.ThermalFallback, "MSAcpi_ThermalZoneTemperature 已返回有效数据"),
        CapabilityStatus.Detected(CapabilityKeys.FanCurve, "Get_Fan/Set_Fan 存在，未调用验证"),
        CapabilityStatus.Detected(CapabilityKeys.FirmwareScenario, "Get_Device/Set_Device 存在，未验证"),
        CapabilityStatus.Unknown(CapabilityKeys.FanFullSpeed, "接口未定位"),
        CapabilityStatus.Unsupported(CapabilityKeys.GpuEcoFallback, "本机无候选接口且已确认不可用")
    });

    [Fact]
    public void Verified_AllowsWriteAndReportsNoReason()
    {
        var entry = CapabilityPresenter.Describe(
            CreateCapabilities(),
            CapabilityKeys.ThermalFallback,
            requiresWrite: true);

        Assert.True(entry.Enabled);
        Assert.Equal(CapabilityLevel.Verified, entry.Level);
        Assert.Equal("通用热区温度（兜底）", entry.Title);
        Assert.Equal(string.Empty, entry.Reason);
    }

    [Fact]
    public void Detected_ReadOnlyIsAllowedButWriteIsBlockedWithReason()
    {
        var capabilities = CreateCapabilities();

        var read = CapabilityPresenter.Describe(capabilities, CapabilityKeys.FanCurve, requiresWrite: false);
        var write = CapabilityPresenter.Describe(capabilities, CapabilityKeys.FanCurve, requiresWrite: true);

        Assert.True(read.Enabled);
        Assert.False(write.Enabled);
        Assert.Contains("线索", write.Reason, StringComparison.Ordinal);
        Assert.Contains("仅允许只读", write.Reason, StringComparison.Ordinal);
        Assert.Contains("Get_Fan/Set_Fan", write.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_IsDisabledForBothReadAndWrite()
    {
        var capabilities = CreateCapabilities();

        var read = CapabilityPresenter.Describe(capabilities, CapabilityKeys.FanFullSpeed, requiresWrite: false);
        var write = CapabilityPresenter.Describe(capabilities, CapabilityKeys.FanFullSpeed, requiresWrite: true);

        Assert.False(read.Enabled);
        Assert.False(write.Enabled);
        Assert.Contains("待验证", write.Reason, StringComparison.Ordinal);
        Assert.Contains("接口未定位", write.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_ExplainsTheConfirmedBasis()
    {
        var entry = CapabilityPresenter.Describe(
            CreateCapabilities(),
            CapabilityKeys.GpuEcoFallback,
            requiresWrite: true);

        Assert.False(entry.Enabled);
        Assert.Contains("已确认不可用", entry.Reason, StringComparison.Ordinal);
        Assert.Contains("无候选接口", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void UnregisteredKeyDefaultsToUnknownAndDisabled()
    {
        var entry = CapabilityPresenter.Describe(
            DeviceCapabilities.Empty,
            CapabilityKeys.GraphicsMode,
            requiresWrite: false);

        Assert.False(entry.Enabled);
        Assert.Equal(CapabilityLevel.Unknown, entry.Level);
        Assert.NotEqual(string.Empty, entry.Reason);
    }

    [Fact]
    public void EmptyCapabilitySetDisablesEverything()
    {
        foreach (var key in CapabilityKeys.All)
        {
            var entry = CapabilityPresenter.Describe(DeviceCapabilities.Empty, key, requiresWrite: false);
            Assert.False(entry.Enabled);
            Assert.NotEqual(string.Empty, entry.Reason);
        }
    }

    [Fact]
    public void EveryCapabilityKeyHasAHumanTitle()
    {
        // 防止新增能力键后界面直接显示英文键名。
        foreach (var key in CapabilityKeys.All)
        {
            var title = CapabilityPresenter.TitleOf(key);
            Assert.NotEqual(key, title);
            Assert.False(string.IsNullOrWhiteSpace(title));
        }
    }

    [Fact]
    public void LevelTextMapsAllLevels()
    {
        Assert.Equal("已确证", CapabilityPresenter.LevelText(CapabilityLevel.Verified));
        Assert.Equal("线索", CapabilityPresenter.LevelText(CapabilityLevel.Detected));
        Assert.Equal("待验证", CapabilityPresenter.LevelText(CapabilityLevel.Unknown));
        Assert.Equal("已确认不可用", CapabilityPresenter.LevelText(CapabilityLevel.Unsupported));
    }

    [Fact]
    public void DescribeAllCoversRegisteredKeysInStableOrder()
    {
        var capabilities = CreateCapabilities();

        var entries = CapabilityPresenter.DescribeAll(capabilities);

        Assert.Equal(capabilities.Count, entries.Count);
        Assert.Equal(entries.Select(e => e.Key).OrderBy(k => k, StringComparer.Ordinal), entries.Select(e => e.Key));
    }

    [Fact]
    public void DescribeRejectsBlankKey()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            _ = CapabilityPresenter.Describe(DeviceCapabilities.Empty, "   ", requiresWrite: true);
        });
    }
}
