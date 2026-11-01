using MsiControl.Desktop.Windows.Osd;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 桌面提示文案与语气映射的回归测试。
/// </summary>
/// <remarks>
/// 提示条只有「标题 + 徽标 + 语气」三段，任何一段写错都会让用户读到错误结论
/// （例如把已静音显示成「已开启」）。这些是纯数据工厂，因此可以严格断言。
/// </remarks>
public class OsdMessageTests
{
    [Theory]
    [InlineData(true, "已静音", (int)OsdTone.Warning)]
    [InlineData(false, "已恢复", (int)OsdTone.Success)]
    public void SpeakerMute_MapsStateToBadgeAndTone(bool muted, string badge, int tone)
    {
        var message = OsdMessage.SpeakerMute(muted);

        Assert.Equal("扬声器", message.Title);
        Assert.Equal(badge, message.Badge);
        Assert.Equal((OsdTone)tone, message.Tone);
    }

    [Theory]
    [InlineData(true, "已静音", (int)OsdTone.Warning)]
    [InlineData(false, "已开启", (int)OsdTone.Success)]
    public void Microphone_MapsStateToBadgeAndTone(bool muted, string badge, int tone)
    {
        var message = OsdMessage.Microphone(muted);

        Assert.Equal("麦克风", message.Title);
        Assert.Equal(badge, message.Badge);
        Assert.Equal((OsdTone)tone, message.Tone);
    }

    [Fact]
    public void Microphone_MutedIsWarningBecauseItIsTheStateUsersMiss()
    {
        // 静音是容易被忽略的状态，因此用警示色而不是成功色。
        Assert.Equal(OsdTone.Warning, OsdMessage.Microphone(muted: true).Tone);
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(23, "23%")]
    [InlineData(100, "100%")]
    public void Volume_ShowsPercentFromReadBack(int percent, string badge)
    {
        var message = OsdMessage.Volume(percent);

        Assert.Equal("音量", message.Title);
        Assert.Equal(badge, message.Badge);
        Assert.Equal(OsdTone.Neutral, message.Tone);
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(50, "50%")]
    public void Brightness_ShowsPercentFromReadBack(int percent, string badge)
    {
        var message = OsdMessage.Brightness(percent);

        Assert.Equal("亮度", message.Title);
        Assert.Equal(badge, message.Badge);
    }

    [Theory]
    [InlineData(true, "已启用", (int)OsdTone.Success)]
    [InlineData(false, "已禁用", (int)OsdTone.Warning)]
    public void Touchpad_MapsStateToBadgeAndTone(bool enabled, string badge, int tone)
    {
        var message = OsdMessage.Touchpad(enabled);

        Assert.Equal("触摸板", message.Title);
        Assert.Equal(badge, message.Badge);
        Assert.Equal((OsdTone)tone, message.Tone);
    }

    [Fact]
    public void PerformanceStage_UsesStageNameAsBadge()
    {
        var message = OsdMessage.PerformanceStage("平衡");

        Assert.Equal("性能模式", message.Title);
        Assert.Equal("平衡", message.Badge);
    }

    [Fact]
    public void BluetoothSettings_ReportsSettingsPageNotRadioState()
    {
        // 固件对 F6 的定义只是「打开设置页」，提示不得暗示蓝牙已被开关。
        var message = OsdMessage.BluetoothSettings();

        Assert.Equal("蓝牙", message.Title);
        Assert.Equal("已打开设置", message.Badge);
        Assert.DoesNotContain("已关闭", message.Badge);
        Assert.DoesNotContain("已开启", message.Badge);
    }

    [Fact]
    public void Failure_UsesDangerToneAndExplainsNothingFake()
    {
        var message = OsdMessage.Failure("触控板开关");

        Assert.Equal("触控板开关", message.Title);
        Assert.Equal("未完成", message.Badge);
        Assert.Equal(OsdTone.Danger, message.Tone);
    }

    [Fact]
    public void EveryFactory_ProducesNonEmptyTitleAndBadge()
    {
        var all = new[]
        {
            OsdMessage.SpeakerMute(true),
            OsdMessage.SpeakerMute(false),
            OsdMessage.Microphone(true),
            OsdMessage.Microphone(false),
            OsdMessage.Volume(42),
            OsdMessage.Brightness(42),
            OsdMessage.Touchpad(true),
            OsdMessage.Touchpad(false),
            OsdMessage.PerformanceStage("狂暴"),
            OsdMessage.BluetoothSettings(),
            OsdMessage.Failure("音量")
        };

        Assert.All(all, message =>
        {
            Assert.False(string.IsNullOrWhiteSpace(message.Title));
            Assert.False(string.IsNullOrWhiteSpace(message.Badge));
        });
    }

    [Fact]
    public void OsdTone_ValuesAreStable()
    {
        // 语气值进入配色分支，重排会静默改变颜色。
        Assert.Equal(0, (int)OsdTone.Neutral);
        Assert.Equal(1, (int)OsdTone.Success);
        Assert.Equal(2, (int)OsdTone.Warning);
        Assert.Equal(3, (int)OsdTone.Danger);
    }

    [Theory]
    [InlineData(true, "已开启", (int)OsdTone.Success)]
    [InlineData(false, "已关闭", (int)OsdTone.Warning)]
    public void Bluetooth_MapsRadioStateToBadgeAndTone(bool on, string badge, int tone)
    {
        var message = OsdMessage.Bluetooth(on);

        Assert.Equal("蓝牙", message.Title);
        Assert.Equal(badge, message.Badge);
        Assert.Equal((OsdTone)tone, message.Tone);
    }

    [Fact]
    public void Bluetooth_DiffersFromBluetoothSettings()
    {
        // 射频真开关与「打开设置页」是两件事，文案不得混用。
        var toggle = OsdMessage.Bluetooth(true);
        var settings = OsdMessage.BluetoothSettings();

        Assert.NotEqual(toggle.Badge, settings.Badge);
    }
}
