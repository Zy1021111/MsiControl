using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Hotkeys;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests;

/// <summary>
/// Fn 键输入通道的纯逻辑部分测试（不发真实按键、不碰 EC）。
/// </summary>
public sealed class FnKeyChannelTests
{
    [Theory]
    [InlineData(0xAD, "VK_VOLUME_MUTE")]
    [InlineData(0xAE, "VK_VOLUME_DOWN")]
    [InlineData(0xAF, "VK_VOLUME_UP")]
    [InlineData(0x83, "VK_F20")]
    [InlineData(0x87, "VK_F24")]
    [InlineData(0x7C, "VK_F13")]
    public void DescribeKey_ProducesReadableName(int virtualKey, string expected)
        => Assert.Equal(expected, SystemKeyHookChannel.DescribeKey(virtualKey));

    [Fact]
    public void DescribeKey_UnknownKey_FallsBackToHex()
        => Assert.Equal("VK_0x41", SystemKeyHookChannel.DescribeKey(0x41));

    [Fact]
    public void EcChannel_ConstantsMatchReversedEvidence()
    {
        // 0xC0 是热键码寄存器、0xC1 bit7 是上报使能位——两者取自 M-Control 的采样实现，
        // 写死在常量里是为了让后续读代码的人不必再翻逆向资料。
        Assert.Equal(0xC0, EcHotkeyChannel.HotkeyCodeAddress);
        Assert.Equal(0xC1, EcHotkeyChannel.HotkeyEnableAddress);
        Assert.Equal(0x80, EcHotkeyChannel.HotkeyEnableBit);
    }

    [Fact]
    public void EcChannel_WithoutStartedTimer_ReportsChannelName()
    {
        using var channel = new EcHotkeyChannel(new StubEcAccess());
        Assert.Equal(FnKeySignal.Channels.Ec, channel.Channel);
    }

    [Fact]
    public void EcChannel_EnableBitClear_DoesNotStartAndExplainsWhy()
    {
        // 0xC1 bit7 未置位时**绝不写入**：只如实报告不可用。
        var access = new StubEcAccess { EnableValue = 0x10 };
        using var channel = new EcHotkeyChannel(access);

        Assert.False(channel.Start());
        Assert.False(string.IsNullOrWhiteSpace(channel.StartupError));
        Assert.Equal(0, access.WriteCount);
    }

    [Fact]
    public void EcChannel_AvailabilityError_DoesNotStart()
    {
        using var channel = new EcHotkeyChannel(new StubEcAccess { Error = "驱动未安装" });

        Assert.False(channel.Start());
        Assert.Contains("驱动未安装", channel.StartupError ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class StubEcAccess : Transport.PawnIo.IEcRegisterAccess
    {
        public string? Error { get; init; }

        public byte EnableValue { get; init; } = 0x90;

        public int WriteCount { get; private set; }

        public string? AvailabilityError => Error;

        public byte ReadRegister(byte address) => address == EcHotkeyChannel.HotkeyEnableAddress ? EnableValue : (byte)0;

        public void WriteRegister(byte address, byte value) => WriteCount++;
    }
}
