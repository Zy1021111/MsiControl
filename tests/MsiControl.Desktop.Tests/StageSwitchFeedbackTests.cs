using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Desktop.Presentation;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 档位切换「一句话反馈」的判定测试。
/// </summary>
/// <remarks>
/// 背景（叶哥 2026-10-31 反馈）：一次切档会连带尝试 CPU 持续/短时功耗与 GPU 功耗上限，
/// 而 GPU 功耗上限在本机永久不受支持，整组恒为 PartiallySucceeded。
/// 把逐项结果原样拼进界面，用户看到的就是一条成功提示后面拖着四行明细，
/// 其中还夹着一段「本机不支持」的长原因。
/// </remarks>
public sealed class StageSwitchFeedbackTests
{
    private static SettingResult StageOk()
        => SettingResult.Succeeded(
            PerformanceShiftMap.StageSettingKey,
            "平衡",
            "0xC1",
            "写入 0xD2=0xC1 后立即读回一致");

    private static SettingResult CpuOk(string key)
        => SettingResult.Succeeded(key, "60 W", "60 W", "写入 MSR 0x610 后立即读回一致");

    private static SettingResult GpuUnsupported()
        => SettingResult.Unsupported("gpu.limit", "nvmlDeviceSetPowerManagementLimit 在本机返回 NOT_SUPPORTED。");

    [Fact]
    public void StageApplied_WithOnlyKnownUnsupportedItem_ReturnsSingleLine()
    {
        // 实际形态：stage 成功 + 两个 CPU 功耗成功 + GPU 功耗上限不支持。
        var result = ControlResult.From(new[]
        {
            StageOk(), CpuOk("cpu.longterm"), CpuOk("cpu.shortterm"), GpuUnsupported()
        });

        var text = StageSwitchFeedback.SuccessText(result, PerformanceStage.Balanced);

        Assert.Equal("已切换到「平衡」", text);
        Assert.DoesNotContain("cpu.longterm", text, StringComparison.Ordinal);
        Assert.DoesNotContain("gpu.limit", text, StringComparison.Ordinal);
    }

    [Fact]
    public void StageSucceeded_AllItemsSucceeded_StillOneLine()
    {
        var result = ControlResult.From(new[] { StageOk(), CpuOk("cpu.longterm") });

        Assert.Equal("已切换到「平衡」", StageSwitchFeedback.SuccessText(result, PerformanceStage.Balanced));
    }

    [Fact]
    public void StageNotConfirmed_ReturnsNull()
    {
        // 档位项没有读回确认：不能给「已切换」，必须回落到逐项明细。
        var result = ControlResult.From(new[]
        {
            SettingResult.Failed(PerformanceShiftMap.StageSettingKey, "平衡", "写入失败"),
            CpuOk("cpu.longterm")
        });

        Assert.Null(StageSwitchFeedback.SuccessText(result, PerformanceStage.Balanced));
    }

    [Fact]
    public void StageApplied_ButAnotherItemFailed_ReturnsNull()
    {
        // 档位切过去了，但风扇模式写入失败：属于真的有问题，明细要摆到用户面前。
        var result = ControlResult.From(new[]
        {
            StageOk(),
            SettingResult.Failed("fan.mode", "0x1D", "读回 0x0D，与请求不一致")
        });

        Assert.Null(StageSwitchFeedback.SuccessText(result, PerformanceStage.Quiet));
    }

    [Fact]
    public void UnsupportedIsNotTreatedAsFailure()
    {
        // 「本机不支持」是已知限制，不是本次操作失败——否则本机永远拿不到成功短句。
        var result = ControlResult.From(new[] { StageOk(), GpuUnsupported() });

        Assert.True(StageSwitchFeedback.Applied(result));
        Assert.NotNull(StageSwitchFeedback.SuccessText(result, PerformanceStage.Balanced));
    }
}
