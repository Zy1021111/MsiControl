using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Desktop.Presentation;

/// <summary>
/// 档位切换的「一句话」反馈。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它：</b>一次档位切换会连带尝试 CPU 持续/短时功耗与 GPU 功耗上限，
/// 逐项结果动辄四五条，其中 GPU 功耗上限在本机<b>永久不受支持</b>（HWE §26）。
/// 把这些逐项结果原样拼进界面提示，用户看到的就是「切换成功」旁边拖着
/// 「cpu.longterm＝成功…；cpu.shortterm＝成功…；gpu.limit＝本机不支持（…长段原因…）」，
/// 一条成功提示被摊成好几条、还带警告色（叶哥 2026-10-31 反馈「重复出现多条提示信息」）。
/// </para>
/// <para>
/// <b>判据仍然严格：</b>只有「档位项读回确认」且「没有任何一项真的失败」才给成功短句；
/// 一旦有失败项，调用方必须回落到逐项明细，不掩盖问题。
/// 逐项明细不会消失——它已经全部写进日志（<c>LogControlResult</c>），
/// 需要证据时查日志即可，界面上只留一句人能读的话。
/// </para>
/// </remarks>
internal static class StageSwitchFeedback
{
    /// <summary>档位项是否已按读回确认生效。</summary>
    /// <remarks>
    /// 不看整组状态：整组恒为 <see cref="OperationStatus.PartiallySucceeded"/>，
    /// 用它判断会让一次成功的切换显示成「部分成功」。
    /// </remarks>
    public static bool Applied(ControlResult result)
        => result.IsSettingSucceeded(PerformanceShiftMap.StageSettingKey);

    /// <summary>
    /// 成功时的单行文案；档位未确认或存在失败项时返回 <see langword="null"/>。
    /// </summary>
    public static string? SuccessText(ControlResult result, PerformanceStage stage)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!Applied(result))
        {
            return null;
        }

        // 「本机不支持」是已知限制，不算失败；只有 Failed 才需要把明细摆到用户面前。
        return result.Settings.Any(s => s.Status == OperationStatus.Failed)
            ? null
            : $"已切换到「{ModeFormatter.StageText(stage)}」";
    }
}
