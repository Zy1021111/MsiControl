using System.Globalization;
using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Application.Control;

/// <summary>控制请求的种类。</summary>
/// <remarks>请求合并与取代只在**同种类**之间发生；不同种类的请求顺序执行。</remarks>
public enum ControlIntentKind
{
    /// <summary>切换性能档位。</summary>
    PerformanceStage = 0,

    /// <summary>切换显卡模式。</summary>
    GraphicsMode = 1,

    /// <summary>切换强冷（全速）。</summary>
    FullSpeed = 2,

    /// <summary>切换风扇模式（自动/静音/高级）。</summary>
    FanMode = 3,

    /// <summary>把固件性能场景恢复为默认档位（只写场景寄存器，不应用配置表中的瓦数目标）。</summary>
    DefaultStageRestore = 4
}

/// <summary>
/// 请求来源。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §11 与 design.md §13.4 要求「启动恢复与供电变化不得覆盖更新的手动选择」。
/// 该规则需要一个可判定的来源标记：自动来源**不得**取代仍在排队的手动请求，
/// 而手动来源总是取代更早的请求。
/// </para>
/// </remarks>
public enum ControlRequestOrigin
{
    /// <summary>自动策略发起：启动恢复、供电变化、唤醒恢复。</summary>
    Automatic = 0,

    /// <summary>用户显式操作。</summary>
    Manual = 1
}

/// <summary>
/// 一次控制意图。
/// </summary>
/// <remarks>
/// 只表达「想要什么」与「谁提出」，不表达「怎么写」。写入细节由协调器按能力门控决定。
/// </remarks>
public sealed record ControlIntent
{
    private ControlIntent(
        ControlIntentKind kind,
        PerformanceStage? stage,
        GraphicsMode? graphicsMode,
        bool? fullSpeed,
        FanControlMode? fanMode,
        ControlRequestOrigin origin)
    {
        Kind = kind;
        Stage = stage;
        GraphicsMode = graphicsMode;
        FullSpeed = fullSpeed;
        FanMode = fanMode;
        Origin = origin;
    }

    /// <summary>请求种类。</summary>
    public ControlIntentKind Kind { get; }

    /// <summary>目标性能档位。仅 <see cref="ControlIntentKind.PerformanceStage"/> 时非空。</summary>
    public PerformanceStage? Stage { get; }

    /// <summary>目标显卡模式。仅 <see cref="ControlIntentKind.GraphicsMode"/> 时非空。</summary>
    public GraphicsMode? GraphicsMode { get; }

    /// <summary>强冷目标状态。仅 <see cref="ControlIntentKind.FullSpeed"/> 时非空。</summary>
    public bool? FullSpeed { get; }

    /// <summary>目标风扇模式。仅 <see cref="ControlIntentKind.FanMode"/> 时非空。</summary>
    public FanControlMode? FanMode { get; }

    /// <summary>请求来源。</summary>
    public ControlRequestOrigin Origin { get; }

    /// <summary>构造性能档位切换意图。</summary>
    /// <exception cref="ArgumentOutOfRangeException">档位取值非法。</exception>
    public static ControlIntent ForPerformanceStage(PerformanceStage stage, ControlRequestOrigin origin)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "档位取值非法。");
        }

        return new ControlIntent(ControlIntentKind.PerformanceStage, stage, null, null, null, origin);
    }

    /// <summary>
    /// 构造「恢复固件默认性能场景」意图。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ForPerformanceStage"/> 的区别只在执行路径：本意图**不应用档位配置表**
    /// （不写 MSR 功耗墙、不动风扇曲线），只把 EC 0xD2 退回固件基准场景。
    /// </remarks>
    public static ControlIntent ForDefaultStageRestore(ControlRequestOrigin origin)
        => new(
            ControlIntentKind.DefaultStageRestore,
            PerformanceShiftMap.FirmwareDefaultStage,
            null,
            null,
            null,
            origin);

    /// <summary>构造显卡模式切换意图。</summary>    /// <exception cref="ArgumentOutOfRangeException">模式取值非法。</exception>
    public static ControlIntent ForGraphicsMode(GraphicsMode mode, ControlRequestOrigin origin)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "显卡模式取值非法。");
        }

        return new ControlIntent(ControlIntentKind.GraphicsMode, null, mode, null, null, origin);
    }

    /// <summary>构造强冷切换意图。</summary>
    public static ControlIntent ForFullSpeed(bool enabled, ControlRequestOrigin origin)
        => new(ControlIntentKind.FullSpeed, null, null, enabled, null, origin);

    /// <summary>
    /// 构造风扇模式切换意图。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 模式为 <see cref="FanControlMode.FullSpeed"/>（强冷走
    /// <see cref="ForFullSpeed"/>）或取值非法。
    /// </exception>
    public static ControlIntent ForFanMode(FanControlMode mode, ControlRequestOrigin origin)
    {
        if (mode is FanControlMode.FullSpeed || !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "风扇模式请求不支持该取值（强冷请使用 ForFullSpeed）。");
        }

        return new ControlIntent(ControlIntentKind.FanMode, null, null, null, mode, origin);
    }

    /// <summary>人类可读的请求描述，用于结果与日志。</summary>
    public string Describe() => Kind switch
    {
        ControlIntentKind.PerformanceStage => $"性能档位切换 → {StageText(Stage!.Value)}",
        ControlIntentKind.GraphicsMode => $"显卡模式切换 → {GraphicsText(GraphicsMode!.Value)}",
        ControlIntentKind.FullSpeed => FullSpeed!.Value ? "开启强冷（全速）" : "关闭强冷（全速）",
        ControlIntentKind.FanMode => $"风扇模式切换 → {FanModeText(FanMode!.Value)}",
        ControlIntentKind.DefaultStageRestore => "恢复固件默认性能场景",
        _ => Kind.ToString()
    };

    /// <summary>风扇模式显示名。</summary>
    public static string FanModeText(FanControlMode mode) => mode switch
    {
        FanControlMode.Automatic => "固件自动",
        FanControlMode.Silent => "静音",
        FanControlMode.CustomCurve => "高级（advanced）",
        FanControlMode.FullSpeed => "强冷",
        _ => mode.ToString()
    };

    /// <summary>档位显示名。与桌面层的呈现保持一致，避免两处文案漂移。</summary>
    public static string StageText(PerformanceStage stage) => stage switch
    {
        PerformanceStage.Eco => "节能",
        PerformanceStage.Quiet => "安静",
        PerformanceStage.Balanced => "平衡",
        PerformanceStage.HighPerformance => "高性能",
        PerformanceStage.Extreme => "狂暴",
        _ => stage.ToString()
    };

    /// <summary>显卡模式显示名。</summary>
    /// <remarks>
    /// 用 <c>Core.GraphicsMode</c> 限定：本类型上有同名属性 <see cref="GraphicsMode"/>，
    /// 不限定会解析到属性而不是枚举类型。
    /// </remarks>
    public static string GraphicsText(GraphicsMode mode) => mode switch
    {
        Core.GraphicsMode.Integrated => "核显",
        Core.GraphicsMode.Hybrid => "混合",
        Core.GraphicsMode.Discrete => "独显直连",
        _ => mode.ToString()
    };

    /// <inheritdoc />
    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Describe()}（来源：{(Origin == ControlRequestOrigin.Manual ? "手动" : "自动策略")}）");
}
