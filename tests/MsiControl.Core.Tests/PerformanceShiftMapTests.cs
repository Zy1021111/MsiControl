using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 性能档位 ↔ EC Shift 编码映射表测试（HWE §20/§21 证据基线）。
/// </summary>
public sealed class PerformanceShiftMapTests
{
    [Theory]
    [InlineData(PerformanceStage.Eco, PerformanceShiftMap.EcoEncoding)]             // 0xC2
    [InlineData(PerformanceStage.Quiet, PerformanceShiftMap.EcoEncoding)]           // 组合档承载编码同为 0xC2
    [InlineData(PerformanceStage.Balanced, PerformanceShiftMap.BalancedEncoding)]   // 0xC1
    [InlineData(PerformanceStage.HighPerformance, PerformanceShiftMap.SportEncoding)] // 0xC0
    [InlineData(PerformanceStage.Extreme, PerformanceShiftMap.ExtremeEncoding)]     // 0xC4
    public void Encode_AllFiveStages_AreOpen(PerformanceStage stage, byte expected)
        => Assert.Equal(expected, PerformanceShiftMap.Encode(stage));

    [Fact]
    public void SupportedStages_CoversAllFive()
        => Assert.Equal(5, PerformanceShiftMap.SupportedStages.Count);

    [Theory]
    [InlineData((byte)0xC2, PerformanceStage.Eco)]
    [InlineData((byte)0xC1, PerformanceStage.Balanced)]
    [InlineData((byte)0xC0, PerformanceStage.HighPerformance)]
    [InlineData((byte)0xC4, PerformanceStage.Extreme)]
    public void Decode_KnownEncodings_MapToStages(byte encoding, PerformanceStage expected)
        => Assert.Equal(expected, PerformanceShiftMap.Decode(encoding));

    [Theory]
    [InlineData((byte)0xC3)]
    [InlineData((byte)0xC5)]
    [InlineData((byte)0xC6)]
    public void Decode_UnregisteredEncodings_ReturnNull_WithoutGuessing(byte encoding)
        => Assert.Null(PerformanceShiftMap.Decode(encoding));

    [Fact]
    public void Quiet_IsComposite_SilenceFanComposition()
    {
        Assert.True(PerformanceShiftMap.IsSilentFanComposite(PerformanceStage.Quiet));
        Assert.False(PerformanceShiftMap.IsSilentFanComposite(PerformanceStage.Eco));
        Assert.False(PerformanceShiftMap.IsSilentFanComposite(PerformanceStage.HighPerformance));
    }

    [Theory]
    [InlineData((byte)0xC0)]
    [InlineData((byte)0xC1)]
    [InlineData((byte)0xC2)]
    [InlineData((byte)0xC4)]
    public void Describe_RegisteredEncodings_HaveSemanticText(byte encoding)
    {
        var text = PerformanceShiftMap.Describe(encoding);
        Assert.DoesNotContain("未登记", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_UnregisteredEncoding_ShowsLiteral()
        => Assert.Contains("未登记", PerformanceShiftMap.Describe(0xC3), StringComparison.Ordinal);

    [Fact]
    public void Cycle_CoversAllFiveStages()
    {
        Assert.Equal(
            new[]
            {
                PerformanceStage.Eco,
                PerformanceStage.Quiet,
                PerformanceStage.Balanced,
                PerformanceStage.HighPerformance,
                PerformanceStage.Extreme
            },
            PerformanceShiftMap.Cycle);
    }

    [Theory]
    [InlineData(PerformanceStage.Eco, PerformanceStage.Quiet)]
    [InlineData(PerformanceStage.Quiet, PerformanceStage.Balanced)]
    [InlineData(PerformanceStage.Balanced, PerformanceStage.HighPerformance)]
    [InlineData(PerformanceStage.HighPerformance, PerformanceStage.Extreme)]
    [InlineData(PerformanceStage.Extreme, PerformanceStage.Eco)] // 末档回到首档，形成闭环
    public void NextStage_AdvancesAlongCycle(PerformanceStage current, PerformanceStage expected)
        => Assert.Equal(expected, PerformanceShiftMap.NextStage(current));

    [Fact]
    public void NextStage_UnknownCurrent_StartsFromFirstStage()
    {
        // 读不到当前档位时不猜一个「相近档」，而是回到循环首档：
        // 猜错会直接改变功耗行为，代价远高于多按几次。
        Assert.Equal(PerformanceStage.Eco, PerformanceShiftMap.NextStage(null));
    }
}
