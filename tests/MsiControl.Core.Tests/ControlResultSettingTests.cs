using MsiControl.Core.Models;
using MsiControl.Core.Results;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 「主目标已达成、附带项不支持」的判定测试。
/// </summary>
/// <remarks>
/// 背景（HWE §32.5）：档位切换会连带尝试 GPU 功耗上限，本机 vBIOS 未开放该旋钮，
/// 整组恒为 <see cref="OperationStatus.PartiallySucceeded"/>。若用整组状态判断主目标，
/// 会出现「档位明明切了，界面却报未完成」且循环永远停在同一档。
/// </remarks>
public sealed class ControlResultSettingTests
{
    [Fact]
    public void IsSettingSucceeded_TrueWhenThatSettingSucceeded_EvenIfOverallIsPartial()
    {
        var result = ControlResult.From(new[]
        {
            SettingResult.Succeeded(
                PerformanceShiftMap.StageSettingKey,
                "安静（0xC2，eco/节能）",
                "0xC2（eco/节能）",
                "写入后读回一致。"),
            SettingResult.Unsupported(
                PerformanceSettingAliases.GpuLimit,
                "GPU 功耗上限：nvmlDeviceSetPowerManagementLimit 返回 NOT_SUPPORTED。")
        });

        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
        Assert.False(result.IsSuccess);
        Assert.True(result.IsSettingSucceeded(PerformanceShiftMap.StageSettingKey));
        Assert.False(result.IsSettingSucceeded(PerformanceSettingAliases.GpuLimit));
    }

    [Fact]
    public void IsSettingSucceeded_FalseWhenSettingFailed()
    {
        var result = ControlResult.From(new[]
        {
            SettingResult.Failed(
                PerformanceShiftMap.StageSettingKey,
                "高性能（0xC0，sport）",
                "写入后读回 0xC1，与请求不一致，场景可能未被固件接受。")
        });

        Assert.False(result.IsSettingSucceeded(PerformanceShiftMap.StageSettingKey));
    }

    [Fact]
    public void StageSettingKey_MatchesPerformanceSettingKeysConvention()
    {
        // Core 与 Performance 各持一份常量；若某天改了其中一份而没改另一份，
        // 档位判定会静默失效，所以用这个测试把它钉住。
        Assert.Equal("stage", PerformanceShiftMap.StageSettingKey);
    }

    /// <summary>
    /// 避免测试工程引用 Performance 的本地别名（值必须与 <c>PerformanceSettingKeys</c> 一致）。
    /// </summary>
    private static class PerformanceSettingAliases
    {
        public const string GpuLimit = "gpu.limit";
    }
}
