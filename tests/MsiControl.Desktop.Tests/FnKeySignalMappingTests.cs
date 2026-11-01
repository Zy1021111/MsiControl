using MsiControl.Core.Models;
using MsiControl.Desktop;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// Fn 键原始信号 → 动作的映射测试。
/// </summary>
/// <remarks>
/// 这是「按 F9 却去开蓝牙」这类错误的唯一防线：WMI 的 120 与 EC 的 120 语义不同，
/// 因此映射必须按通道分开，未登记码一律返回 false（只记日志），不得兜底猜一个动作。
/// </remarks>
public sealed class FnKeySignalMappingTests
{
    [Theory]
    [InlineData(112, "扬声器静音")]
    [InlineData(113, "音量降低")]
    [InlineData(114, "音量提高")]
    [InlineData(8, "触控板开关")]
    [InlineData(131, "触控板开关")]
    [InlineData(190, "触控板开关")]
    [InlineData(116, "麦克风静音")]
    [InlineData(117, "蓝牙开关")]
    [InlineData(118, "性能模式切换")]
    [InlineData(120, "亮度降低")]
    [InlineData(121, "亮度提高")]
    public void EcChannel_KnownCodes_MapToExpectedDisplayName(int code, string expected)
    {
        var signal = FnKeySignal.Create(FnKeySignal.Channels.Ec, code, $"0x{code:X2}");

        Assert.True(DesktopApplicationContext.TryMapFnSignal(signal, out _, out var displayName));
        Assert.Equal(expected, displayName);
    }

    [Theory]
    [InlineData(0xAD, "扬声器静音")]
    [InlineData(0xAE, "音量降低")]
    [InlineData(0xAF, "音量提高")]
    [InlineData(0x83, "触控板开关")]
    public void KeyboardChannel_KnownVirtualKeys_MapToExpectedDisplayName(int virtualKey, string expected)
    {
        var signal = FnKeySignal.Create(FnKeySignal.Channels.Keyboard, virtualKey, "vk");

        Assert.True(DesktopApplicationContext.TryMapFnSignal(signal, out _, out var displayName));
        Assert.Equal(expected, displayName);
    }

    [Theory]
    [InlineData(FnKeySignal.Channels.Ec, 0xFE)]
    [InlineData(FnKeySignal.Channels.Keyboard, 0xB3)] // VK_MEDIA_PLAY：采集得到但没有对应动作
    [InlineData("unknown-channel", 112)]
    public void UnknownSignal_IsNotMapped(string channel, int code)
    {
        var signal = FnKeySignal.Create(channel, code, "?");

        Assert.False(DesktopApplicationContext.TryMapFnSignal(signal, out _, out _));
    }

    [Fact]
    public void SameCode_DifferentChannel_AreResolvedSeparately()
    {
        // EC 的 120 是「亮度降低」，WMI 的 120 是「蓝牙」——同码不同义，绝不能串。
        var ec = FnKeySignal.Create(FnKeySignal.Channels.Ec, 120, "ec");
        var keyboard = FnKeySignal.Create(FnKeySignal.Channels.Keyboard, 120, "vk");

        Assert.True(DesktopApplicationContext.TryMapFnSignal(ec, out _, out var ecName));
        Assert.Equal("亮度降低", ecName);

        // 键盘通道 0x78 不在关注集合内，也不该被映射成任何动作。
        Assert.False(DesktopApplicationContext.TryMapFnSignal(keyboard, out _, out _));
    }
}
