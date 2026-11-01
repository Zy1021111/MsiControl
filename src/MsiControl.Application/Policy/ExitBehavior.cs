using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Application.Policy;

/// <summary>退出行为的完整说明。</summary>
/// <param name="FirmwarePersistent">退出后仍能继续生效的设置（由固件持续执行）。</param>
/// <param name="ProcessDependent">退出后不能继续生效的设置（依赖常驻进程）。</param>
/// <param name="Unverified">尚未取得证据、因此不作承诺的部分。</param>
/// <param name="HiddenIsNotExit">「隐藏窗口」与「停止进程」的区别说明。</param>
/// <param name="Summary">一段可直接展示给用户的结论。</param>
public sealed record ExitBehaviorSummary(
    IReadOnlyList<string> FirmwarePersistent,
    IReadOnlyList<string> ProcessDependent,
    IReadOnlyList<string> Unverified,
    string HiddenIsNotExit,
    string Summary);

/// <summary>
/// 退出行为说明。
/// </summary>
/// <remarks>
/// <para>对应任务书 §11「退出」与 design.md §9.3：</para>
/// <list type="bullet">
/// <item>目标是保留最近成功应用的硬件状态。</item>
/// <item>固件持久执行的设置可以在进程退出后继续生效。</item>
/// <item>依赖持续软件控制的设置不能承诺在全部进程退出后维持，要在界面上说明。</item>
/// <item>明确区分「退出界面、继续控制」与「停止全部进程」。</item>
/// <item><b>不以隐藏进程冒充完全退出</b>。</item>
/// </list>
/// <para>
/// 本类型是纯描述：它把「哪些设置属固件持久、哪些属进程依赖、哪些尚未验证」写成结构化结论，
/// 供界面、日志与文档共用同一份口径，避免各处文案不一致。
/// </para>
/// </remarks>
public static class ExitBehavior
{
    /// <summary>隐藏窗口不等于退出进程。</summary>
    public const string HiddenIsNotExitText =
        "隐藏窗口 ≠ 退出程序：隐藏后托盘驻留、采样与由本软件维持的调速继续运行；" +
        "「完全退出」是托盘与窗口上的独立入口，会停止采样与软件调速。";

    /// <summary>本机退出行为尚未实测的说明。</summary>
    public const string UnverifiedText =
        "本机「退出 / 重启 / 睡眠后设置是否保持」尚未取得实测证据（docs/hardware-evidence.md §7），" +
        "因此不对任何设置的退出后行为作承诺。";

    /// <summary>生成退出行为说明。</summary>
    /// <param name="capabilities">本机能力集合。用于判断相关写入是否已开放。</param>
    /// <param name="curvesExecutedByFirmware">
    /// 风扇曲线是否由固件自行执行（而非本软件持续调速）。
    /// 传 <see langword="null"/> 表示尚未实测，按「未验证」处理。
    /// </param>
    public static ExitBehaviorSummary Describe(
        DeviceCapabilities capabilities,
        bool? curvesExecutedByFirmware = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var firmware = new List<string>();
        var processDependent = new List<string>();
        var unverified = new List<string>();

        if (curvesExecutedByFirmware == true)
        {
            firmware.Add("风扇曲线：由固件持续执行，进程退出后仍然生效（前提：已写入固件并读回确认）。");
        }
        else if (curvesExecutedByFirmware == false)
        {
            processDependent.Add(
                "风扇曲线：当前由本软件持续调速执行，**停止全部进程后不再维持**；" +
                "退出前会保留固件侧最后一次写入的结果，但不再随温度变化调整。");
        }
        else
        {
            unverified.Add(
                "风扇曲线由固件执行还是软件调速：尚未实测（任务书 §7.6），" +
                "两种情形的退出行为不同，因此暂不承诺。");
        }

        // 功耗限制分 CPU / GPU 两条通道，退出行为不同，必须分开表述（HWE §26）。
        if (capabilities.AllowsWrite(CapabilityKeys.CpuPowerLimit))
        {
            firmware.Add(
                "CPU 功耗限制：写入 MSR 0x610 后由处理器持有，**退出程序不会自动撤销**，"
                + "直到下次重启（固件在开机时复位该寄存器）或再次修改；写入均以读回值为准。");
        }
        else
        {
            unverified.Add("CPU 功耗限制：写入通道尚未开放（未达「已确证」），退出后行为无从谈起。");
        }

        if (capabilities.AllowsWrite(CapabilityKeys.GpuPowerLimit))
        {
            firmware.Add("GPU 功耗限制：写入由驱动持有，进程退出后仍然生效（需读回确认）。");
        }
        else
        {
            unverified.Add(
                "GPU 功耗限制：本机无可用写入通道（NVML 拒绝写入，HWE §26），" +
                "因此不存在「退出后仍生效的 GPU 功耗限制」这一事实。");
        }

        if (capabilities.AllowsWrite(CapabilityKeys.CpuTurboLimit))
        {
            firmware.Add("CPU 睿频上限：写入由固件/驱动持有，进程退出后仍然生效（需读回确认）。");
        }
        else
        {
            unverified.Add("CPU 睿频上限：写入通道尚未开放，退出后行为无从谈起。");
        }

        if (capabilities.AllowsWrite(CapabilityKeys.GraphicsMode))
        {
            unverified.Add(
                "显卡模式：本程序不自动切换模式；切换成功后由你在询问框里选择是否立即重启"
                + "（选「立即重启」才会发起），否则等你自行重启或 S5 关机再开机，"
                + "生效结果由固件决定，本程序不作保证。");
        }
        else
        {
            unverified.Add("显卡模式：写入通道尚未开放，本机当前不支持由本程序切换。");
        }

        // 性能场景（0xD2）在本机是可写且固件持久的：
        // 退出时会被主动写回固件基准场景，「上次用户确认的档位」另存设置文件供下次启动恢复。
        if (capabilities.AllowsWrite(CapabilityKeys.PerformanceStage))
        {
            firmware.Add(
                "性能模式：运行期间不会自动改档，只在你确认后切换；主动退出时会把固件性能场景"
                + "写回基准档（平衡 0xC1，只写场景寄存器，不改功耗上限与风扇）；"
                + "下次启动按「上次用户确认的档位」恢复。");
        }
        else
        {
            unverified.Add("性能模式：场景写入通道尚未开放，退出与重启时的行为无从谈起。");
        }

        unverified.Add(UnverifiedText);

        var summary = BuildSummary(firmware, processDependent, unverified);
        return new ExitBehaviorSummary(firmware, processDependent, unverified, HiddenIsNotExitText, summary);
    }

    private static string BuildSummary(
        IReadOnlyList<string> firmware,
        IReadOnlyList<string> processDependent,
        IReadOnlyList<string> unverified)
    {
        var parts = new List<string>
        {
            $"完全退出后仍生效（固件持久）：{(firmware.Count == 0 ? "本机当前没有这一类设置" : string.Join("；", firmware))}。",
            $"完全退出后不再维持（依赖进程）：{(processDependent.Count == 0 ? "本机当前没有这一类设置" : string.Join("；", processDependent))}。",
            $"尚未验证：{string.Join("；", unverified)}",
            HiddenIsNotExitText
        };

        return string.Join(Environment.NewLine, parts);
    }
}
