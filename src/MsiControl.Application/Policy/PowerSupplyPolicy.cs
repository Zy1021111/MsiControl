using MsiControl.Core;

namespace MsiControl.Application.Policy;

/// <summary>自动策略决定要执行的动作。</summary>
public enum PowerSupplyActionKind
{
    /// <summary>不做任何自动动作。</summary>
    None = 0,

    /// <summary>切换到指定档位（拔电自动节能）。</summary>
    SwitchToStage = 1,

    /// <summary>恢复拔电前记录的档位。</summary>
    RestoreRecordedStage = 2
}

/// <summary>供电变化后的处理决定。</summary>
/// <param name="Kind">动作种类。</param>
/// <param name="Stage">目标档位。仅在前两种动作时有值。</param>
/// <param name="Reason">可追溯的判断依据，直接写入日志与界面提示。</param>
/// <remarks>
/// <b>本类型刻意不提供任何显卡模式动作。</b>任务书 §11 与 §9.1 要求启动与供电变化
/// <b>不自动切换显卡模式</b>；用「类型里没有这个字段」来保证，比写在注释里更可靠。
/// </remarks>
public sealed record PowerSupplyDecision(PowerSupplyActionKind Kind, PerformanceStage? Stage, string Reason)
{
    /// <summary>不动作。</summary>
    public static PowerSupplyDecision None(string reason) => new(PowerSupplyActionKind.None, null, reason);

    /// <summary>切换到指定档位。</summary>
    /// <exception cref="ArgumentOutOfRangeException">档位非法。</exception>
    public static PowerSupplyDecision SwitchTo(PerformanceStage stage, string reason)
    {
        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "档位取值非法。");
        }

        return new PowerSupplyDecision(PowerSupplyActionKind.SwitchToStage, stage, reason);
    }

    /// <summary>恢复拔电前档位。</summary>
    public static PowerSupplyDecision Restore(PerformanceStage stage, string reason)
        => new(PowerSupplyActionKind.RestoreRecordedStage, stage, reason);
}

/// <summary>供电变化策略参数。</summary>
public sealed record PowerSupplyPolicyOptions
{
    /// <summary>默认参数。</summary>
    public static PowerSupplyPolicyOptions Default { get; } = new();

    /// <summary>拔电后自动切换到的档位。默认节能（任务书 §11）。</summary>
    public PerformanceStage OnBatteryStage { get; init; } = PerformanceStage.Eco;
}

/// <summary>
/// 供电变化自动策略。
/// </summary>
/// <remarks>
/// <para>对应任务书 §11「自动行为」与 design.md §9.2：</para>
/// <list type="bullet">
/// <item>拔电：记录原性能档位，自动切换到节能。</item>
/// <item>插电：若拔电后<b>未</b>发生手动选择，恢复拔电前档位；
///       若<b>已</b>发生手动选择，保留手动选择。</item>
/// <item>全程<b>不自动切换显卡模式</b>。</item>
/// </list>
/// <para>
/// 本类型是<b>纯逻辑</b>：不读硬件、不写硬件、不订阅系统事件。
/// 它只把「供电状态 + 当前档位 + 是否发生过手动选择」映射为「要不要做什么」，
/// 因此可以被完整单元测试，包括那些在真机上难以复现的时序。
/// </para>
/// <para>
/// 状态机取向：<b>只在供电状态真正发生跳变时动作</b>，重复事件是空操作，
/// 避免「同一个事件被反复处理」导致的重复写入（任务书 §11「不无限重写争夺控制」）。
/// </para>
/// </remarks>
public sealed class PowerSupplyPolicy
{
    private readonly PowerSupplyPolicyOptions _options;

    private PowerSupplyKind _lastKnown = PowerSupplyKind.Unknown;
    private PerformanceStage? _stageBeforeUnplug;
    private bool _manualSelectionSinceUnplug;

    /// <summary>构造策略。</summary>
    /// <param name="options">参数。为空时使用默认值。</param>
    public PowerSupplyPolicy(PowerSupplyPolicyOptions? options = null)
        => _options = options ?? PowerSupplyPolicyOptions.Default;

    /// <summary>最近一次已知的供电状态。</summary>
    public PowerSupplyKind LastKnownSupply => _lastKnown;

    /// <summary>拔电前记录的档位。未记录时为 <see langword="null"/>。</summary>
    public PerformanceStage? StageBeforeUnplug => _stageBeforeUnplug;

    /// <summary>本次拔电周期内是否发生过手动档位选择。</summary>
    public bool HasManualSelectionSinceUnplug => _manualSelectionSinceUnplug;

    /// <summary>当前是否处于电池供电。</summary>
    public bool IsOnBattery => _lastKnown == PowerSupplyKind.Battery;

    /// <summary>
    /// 记录一次**手动**档位选择。
    /// </summary>
    /// <remarks>
    /// 只在电池供电期间生效：拔电周期的语义是「拔电 → 可能手动改档 → 插电」。
    /// 在交流供电下手动选档属于普通操作，不应影响下一次拔电/插电的判定。
    /// </remarks>
    public void NotifyManualPerformanceSelection()
    {
        if (IsOnBattery)
        {
            _manualSelectionSinceUnplug = true;
        }
    }

    /// <summary>处理一次供电状态变化。</summary>
    /// <param name="supply">新的供电状态。未知时不做任何动作。</param>
    /// <param name="currentStage">当前实际档位，用于记录拔电前状态。未知时传 <see langword="null"/>。</param>
    /// <returns>要执行的动作与依据。</returns>
    public PowerSupplyDecision OnSupplyChanged(PowerSupplyKind supply, PerformanceStage? currentStage)
    {
        if (supply == PowerSupplyKind.Unknown)
        {
            return PowerSupplyDecision.None("供电状态未知：不做任何自动切换，避免在无法判定时改动硬件状态。");
        }

        if (supply == _lastKnown)
        {
            return PowerSupplyDecision.None($"供电状态未变化（{TextOf(supply)}）：重复事件按空操作处理。");
        }

        var previous = _lastKnown;
        _lastKnown = supply;

        if (supply == PowerSupplyKind.Battery)
        {
            _stageBeforeUnplug = currentStage;
            _manualSelectionSinceUnplug = false;

            var recorded = currentStage is { } stage
                ? $"已记录拔电前档位={stage}"
                : "拔电前档位未能读取，未记录（插电时不会自动恢复）";

            return PowerSupplyDecision.SwitchTo(
                _options.OnBatteryStage,
                $"已切换到电池供电（{TextOf(previous)} → 电池）：{recorded}；" +
                $"按任务书 §11 自动切换到节能档。");
        }

        // 插电：unless 期间发生过手动选择，否则恢复拔电前档位。
        if (_manualSelectionSinceUnplug)
        {
            _stageBeforeUnplug = null;
            _manualSelectionSinceUnplug = false;
            return PowerSupplyDecision.None(
                "已插电，但拔电期间发生过手动档位选择：保留用户的手动选择，不自动恢复" +
                "（任务书 §11「已发生手动选择时保留手动选择」）。");
        }

        if (_stageBeforeUnplug is { } recordedStage)
        {
            _stageBeforeUnplug = null;
            return PowerSupplyDecision.Restore(
                recordedStage,
                "已插电且拔电期间未发生手动选择：恢复拔电前档位" +
                $"（{TextOf(recordedStage)}，任务书 §11）。");
        }

        return PowerSupplyDecision.None("已插电，但拔电时未能读取到档位：不自动切换，避免猜测目标状态。");
    }

    /// <summary>
    /// 处理一次系统唤醒。
    /// </summary>
    /// <returns>要执行的动作与依据。</returns>
    /// <remarks>
    /// 本机「睡眠/唤醒后硬件状态是否保持」<b>尚未取得证据</b>（docs/hardware-evidence.md §7），
    /// 因此这里**不做任何自动重放**：重放会对固件策略产生无依据的覆盖，
    /// 属于在未验证的前提下改动硬件。依赖持续进程的软件调速必须先实测（任务 04）。
    /// 唤醒后重新读取实时状态由监控模块负责，与本策略无关。
    /// </remarks>
    public static PowerSupplyDecision OnResumed()
        => PowerSupplyDecision.None(
            "唤醒后不自动重放任何硬件写入：本机睡眠/唤醒后的实际保持能力尚未实测" +
            "（证据清单 §7），在取得证据前不猜测目标状态（任务书 §19）。");

    /// <summary>把策略状态复位为「未知供电」，用于重新装配或测试。</summary>
    public void Reset()
    {
        _lastKnown = PowerSupplyKind.Unknown;
        _stageBeforeUnplug = null;
        _manualSelectionSinceUnplug = false;
    }

    private static string TextOf(PowerSupplyKind kind) => kind switch
    {
        PowerSupplyKind.Battery => "电池",
        PowerSupplyKind.Ac => "交流电",
        _ => "未知"
    };

    private static string TextOf(PerformanceStage stage) => Control.ControlIntent.StageText(stage);
}
