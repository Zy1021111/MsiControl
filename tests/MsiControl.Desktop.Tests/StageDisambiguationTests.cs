using MsiControl.Core;
using MsiControl.Desktop;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 「安静/节能共用 0xC2」的显示消歧测试（HWE §21.3、§32.6）。
/// </summary>
/// <remarks>
/// 背景：切到安静后 EC 0xD2 读回仍是 0xC2（节能编码），若界面不消歧，
/// 用户看到的是「节能切不到安静，直接跳到平衡」——切到安静时显示不变，
/// 下一跳却直接前进。
/// </remarks>
public sealed class StageDisambiguationTests
{
    [Fact]
    public void DecodedEco_WithQuietConfirmed_ShowsQuiet()
    {
        var stage = DesktopApplicationContext.ApplyStageDisambiguation(
            PerformanceStage.Eco, PerformanceStage.Quiet);

        Assert.Equal(PerformanceStage.Quiet, stage);
    }

    [Fact]
    public void DecodedEco_WithEcoConfirmed_StaysEco()
    {
        // 上次确认的就是节能：无事可做，也不得翻转成安静。
        var stage = DesktopApplicationContext.ApplyStageDisambiguation(
            PerformanceStage.Eco, PerformanceStage.Eco);

        Assert.Equal(PerformanceStage.Eco, stage);
    }

    [Fact]
    public void DecodedBalanced_WithQuietConfirmed_ShowsBalanced()
    {
        // 消歧只对同编码的安静/节能生效；固件场景真的变了（平衡 0xC1）以读回为准。
        var stage = DesktopApplicationContext.ApplyStageDisambiguation(
            PerformanceStage.Balanced, PerformanceStage.Quiet);

        Assert.Equal(PerformanceStage.Balanced, stage);
    }

    [Fact]
    public void NoConfirmedStage_ShowsDecodedValue()
    {
        // 启动后尚无确认记录：宁可显示编码语义（节能），也不猜成安静。
        var stage = DesktopApplicationContext.ApplyStageDisambiguation(
            PerformanceStage.Eco, null);

        Assert.Equal(PerformanceStage.Eco, stage);
    }

    [Fact]
    public void DecodedNull_ReturnsNull()
        => Assert.Null(DesktopApplicationContext.ApplyStageDisambiguation(null, PerformanceStage.Quiet));
}
