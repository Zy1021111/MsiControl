using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Presentation;

/// <summary>
/// 模式枚举到界面文本的映射。
/// </summary>
/// <remarks>
/// <para>
/// 集中管理术语，保证「实际生效」与「待生效」在所有窗口中使用同一套说法
/// （任务书 §6.1、§9.1 要求待生效状态不得覆盖实际状态）。
/// </para>
/// <para>
/// 方法名统一加 <c>Text</c> 后缀：避免方法名与 <see cref="GraphicsMode"/>、
/// <see cref="WindowMode"/> 等类型名同名而在成员查找上产生歧义。
/// </para>
/// </remarks>
public static class ModeFormatter
{
    /// <summary>性能档位的中文名。</summary>
    public static string StageText(PerformanceStage stage) => stage switch
    {
        PerformanceStage.Eco => "节能",
        PerformanceStage.Quiet => "安静",
        PerformanceStage.Balanced => "平衡",
        PerformanceStage.HighPerformance => "高性能",
        PerformanceStage.Extreme => "狂暴",
        _ => "未知档位"
    };

    /// <summary>可空的档位文本。未知时返回「未适配」。</summary>
    public static string StageText(PerformanceStage? stage)
        => stage.HasValue ? StageText(stage.Value) : "未适配";

    /// <summary>显卡模式的中文名。</summary>
    public static string GraphicsText(GraphicsMode mode) => mode switch
    {
        GraphicsMode.Integrated => "核显",
        GraphicsMode.Hybrid => "混合",
        GraphicsMode.Discrete => "独显直连",
        _ => "未知模式"
    };

    /// <summary>可空的显卡模式文本。未知时返回「未适配」。</summary>
    public static string GraphicsText(GraphicsMode? mode)
        => mode.HasValue ? GraphicsText(mode.Value) : "未适配";

    /// <summary>风扇控制模式的中文名。</summary>
    public static string FanModeText(FanControlMode mode) => mode switch
    {
        FanControlMode.Automatic => "固件自动",
        FanControlMode.CustomCurve => "自定义曲线",
        FanControlMode.FullSpeed => "全速（强冷）",
        FanControlMode.Silent => "静音模式",
        _ => "未知模式"
    };

    /// <summary>
    /// 风扇模式<b>读回</b>文本。与 <see cref="FanModeText"/> 的区别：
    /// EC advanced 模式（0x8D）下固件仍在自动调速（HWE §17.3），
    /// 读回展示不得表述为「自定义曲线已生效」。
    /// </summary>
    public static string FanModeReadbackText(FanControlMode mode) => mode switch
    {
        FanControlMode.Automatic => "固件自动",
        FanControlMode.CustomCurve => "高级（advanced；固件仍在自动调速）",
        FanControlMode.FullSpeed => "全速（强冷）",
        FanControlMode.Silent => "静音模式",
        _ => "未知模式"
    };

    /// <summary>窗口形态的中文名。</summary>
    public static string WindowModeText(WindowMode mode)
        => mode == WindowMode.Mini ? "迷你窗口" : "正常窗口";

    /// <summary>
    /// 生成「实际状态 + 待生效」的显示文本。
    /// </summary>
    /// <param name="current">实际生效模式。</param>
    /// <param name="pending">待生效模式。</param>
    /// <param name="requiresRestart">待生效是否需要重启。</param>
    /// <remarks>
    /// 关键约束：<b>待生效必须以附加信息出现，绝不覆盖实际状态</b>（任务书 §6.1、§9.1）。
    /// </remarks>
    public static string GraphicsTextWithPending(
        GraphicsMode? current,
        GraphicsMode? pending,
        bool requiresRestart)
    {
        var text = GraphicsText(current);
        if (!pending.HasValue || pending == current)
        {
            return text;
        }

        var restart = requiresRestart ? "重启后生效" : "待生效";
        return $"{text}（待生效：{GraphicsText(pending)}，{restart}）";
    }

    /// <summary>采样统计口径的中文说明。</summary>
    public static string FrequencyBasisText(FrequencyAggregation basis) => basis switch
    {
        FrequencyAggregation.AverageActive => "活跃核心均值",
        FrequencyAggregation.MaximumActive => "活跃核心最大值",
        _ => "统计口径未注明"
    };
}
