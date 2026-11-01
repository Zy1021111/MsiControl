using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Fans;

/// <summary>软件调速的决策动作。</summary>
public enum FanPolicyAction
{
    /// <summary>无需动作。</summary>
    None = 0,

    /// <summary>保持当前输出。</summary>
    Hold = 1,

    /// <summary>下发新的输出百分比。</summary>
    SetOutput = 2,

    /// <summary>放弃软件调速，交还固件自动策略。</summary>
    RelinquishToFirmware = 3
}

/// <summary>
/// 软件调速策略的参数。
/// </summary>
/// <remarks>
/// 这些参数只在**实测证明固件无法持续执行曲线**（任务书 §7.6）时才会用到；
/// 默认值取自冷却控制的常见做法（升速快、降速慢、带滞回），但都必须经实机验证后调整，
/// 不得直接把默认值当成「已验证的调参结果」。
/// </remarks>
public sealed record FanPolicySettings
{
    /// <summary>构造参数集合。</summary>
    /// <param name="HysteresisC">滞回带宽度（摄氏度）。温度变化未超出该带宽时不改变输出。</param>
    /// <param name="MaxIncreaseStepPercent">单次允许的最大升速幅度（百分点）。</param>
    /// <param name="MaxDecreaseStepPercent">单次允许的最大降速幅度（百分点）。降速比升速更保守。</param>
    /// <param name="MinimumChangeInterval">两次调速之间的最小间隔。</param>
    /// <param name="SensorTimeout">温度读数多久没有新鲜样本即视为超时。</param>
    /// <param name="MinimumOutputDelta">小于该变化量的调整直接忽略，避免抖动。</param>
    public FanPolicySettings(
        double HysteresisC = 3.0,
        double MaxIncreaseStepPercent = 5.0,
        double MaxDecreaseStepPercent = 2.0,
        TimeSpan? MinimumChangeInterval = null,
        TimeSpan? SensorTimeout = null,
        double MinimumOutputDelta = 1.0)
    {
        if (!double.IsFinite(HysteresisC) || HysteresisC < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(HysteresisC), HysteresisC, "滞回带不得为负数。");
        }

        if (!(MaxIncreaseStepPercent > 0) || !(MaxDecreaseStepPercent > 0))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxIncreaseStepPercent),
                "升降速幅度必须为正数。");
        }

        if (!(MinimumOutputDelta > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(MinimumOutputDelta), MinimumOutputDelta, "最小变化量必须为正数。");
        }

        var minimumChangeInterval = MinimumChangeInterval ?? TimeSpan.FromSeconds(5);
        var sensorTimeout = SensorTimeout ?? TimeSpan.FromSeconds(10);

        if (minimumChangeInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumChangeInterval),
                minimumChangeInterval,
                "调速间隔必须为正数。");
        }

        if (sensorTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(SensorTimeout), sensorTimeout, "传感器超时必须为正数。");
        }

        this.HysteresisC = HysteresisC;
        this.MaxIncreaseStepPercent = MaxIncreaseStepPercent;
        this.MaxDecreaseStepPercent = MaxDecreaseStepPercent;
        this.MinimumChangeInterval = minimumChangeInterval;
        this.SensorTimeout = sensorTimeout;
        this.MinimumOutputDelta = MinimumOutputDelta;
    }

    /// <summary>滞回带宽度（摄氏度）。</summary>
    public double HysteresisC { get; }

    /// <summary>单次允许的最大升速幅度（百分点）。</summary>
    public double MaxIncreaseStepPercent { get; }

    /// <summary>单次允许的最大降速幅度（百分点）。</summary>
    public double MaxDecreaseStepPercent { get; }

    /// <summary>两次调速之间的最小间隔。</summary>
    public TimeSpan MinimumChangeInterval { get; }

    /// <summary>温度读数超时阈值。</summary>
    public TimeSpan SensorTimeout { get; }

    /// <summary>最小有效变化量。</summary>
    public double MinimumOutputDelta { get; }
}

/// <summary>
/// 一次策略评估的输入。
/// </summary>
/// <param name="TemperatureC">当前温度（摄氏度）。不可用时为 <see langword="null"/>。</param>
/// <param name="SampledAt">温度采样时刻。不可用时为 <see langword="null"/>。</param>
/// <param name="Now">本次评估时刻。</param>
/// <param name="ResumedFromSleep">本次评估是否紧跟在睡眠唤醒之后。</param>
public sealed record FanPolicyInput(
    double? TemperatureC,
    DateTimeOffset? SampledAt,
    DateTimeOffset Now,
    bool ResumedFromSleep = false);

/// <summary>
/// 一次策略评估的结论。
/// </summary>
/// <param name="Action">动作。</param>
/// <param name="TargetPercent">建议下发的输出百分比。非 <see cref="FanPolicyAction.SetOutput"/> 时为 <see langword="null"/>。</param>
/// <param name="CurveTargetPercent">曲线在该温度下的目标输出（未经节流）。</param>
/// <param name="Reason">决策理由，可直接展示。</param>
public sealed record FanPolicyDecision(
    FanPolicyAction Action,
    double? TargetPercent,
    double? CurveTargetPercent,
    string Reason)
{
    /// <summary>是否需要下发。</summary>
    public bool RequiresWrite => Action == FanPolicyAction.SetOutput;
}

/// <summary>
/// 软件调速策略（纯逻辑，不接触硬件）。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §7.6：优先让固件执行曲线；**若实测必须软件持续调速**，则必须补齐
/// 滞回、升降速节流、传感器超时处理、睡眠唤醒处理与异常退出行为。本类型实现前四项的决策部分。
/// </para>
/// <list type="bullet">
/// <item><b>滞回</b>：温度与上次决策温度之差不超过 <see cref="FanPolicySettings.HysteresisC"/> 时不改变输出，
///       避免在临界点反复升降。</item>
/// <item><b>升降速节流</b>：升速允许每周期最多 +<c>MaxIncreaseStepPercent</c>，
///       降速最多 −<c>MaxDecreaseStepPercent</c>，且两次调速间隔不小于 <c>MinimumChangeInterval</c>。</item>
/// <item><b>传感器超时</b>：读数不可用且超过 <c>SensorTimeout</c> 时，
///       <b>交还固件自动策略</b>而不是「猜一个安全值」——擅自把风扇拉到全速属于
///       需要用户配合的测试行为（提示词 §四.9）。</item>
/// <item><b>睡眠唤醒</b>：唤醒后第一个周期先等新鲜样本，并重置节流基准，
///       避免拿休眠前的旧温度立刻调整。</item>
/// </list>
/// <para>
/// <b>异常退出行为</b>见 <see cref="SoftwareFanControlLoop.DependencyNotice"/>：
/// 本策略不提供「异常退出后自动回滚」的保证（任务书 §7.6 明确不承诺未验证的自动回滚）。
/// </para>
/// </remarks>
public sealed class SoftwareFanPolicy
{
    private readonly FanPolicySettings _settings;
    private double? _lastAppliedPercent;
    private DateTimeOffset? _lastChangeAt;
    private double? _lastDecisionTemperatureC;
    private DateTimeOffset? _lastFreshSampleAt;

    /// <summary>构造策略。</summary>
    /// <param name="settings">参数。省略时使用默认参数。</param>
    public SoftwareFanPolicy(FanPolicySettings? settings = null)
        => _settings = settings ?? new FanPolicySettings();

    /// <summary>当前策略参数。</summary>
    public FanPolicySettings Settings => _settings;

    /// <summary>最近一次实际下发的输出百分比。</summary>
    public double? LastAppliedPercent => _lastAppliedPercent;

    /// <summary>清空全部历史状态（例如用户切换回固件策略后重新开始）。</summary>
    public void Reset()
    {
        _lastAppliedPercent = null;
        _lastChangeAt = null;
        _lastDecisionTemperatureC = null;
        _lastFreshSampleAt = null;
    }

    /// <summary>
    /// 评估一次。
    /// </summary>
    /// <param name="curve">当前生效的曲线。曲线插值由 <see cref="FanCurve.Interpolate"/> 提供，本类型不重复实现。</param>
    /// <param name="input">评估输入。</param>
    /// <returns>决策。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> 或 <paramref name="input"/> 为 <see langword="null"/>。</exception>
    public FanPolicyDecision Evaluate(FanCurve curve, FanPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(curve);
        ArgumentNullException.ThrowIfNull(input);

        if (input.ResumedFromSleep)
        {
            // 唤醒后节流基准作废：休眠前的时刻已经不能与当前时刻比较。
            _lastChangeAt = null;
            if (input.TemperatureC is null)
            {
                return new FanPolicyDecision(
                    FanPolicyAction.Hold,
                    null,
                    null,
                    "刚从睡眠唤醒，等待第一个新的有效温度样本后再调速。");
            }
        }

        if (input.TemperatureC is not { } temperature || input.SampledAt is null)
        {
            var age = _lastFreshSampleAt.HasValue ? input.Now - _lastFreshSampleAt.Value : (TimeSpan?)null;
            return age is null || age > _settings.SensorTimeout
                ? new FanPolicyDecision(
                    FanPolicyAction.RelinquishToFirmware,
                    null,
                    null,
                    $"温度读数不可用，且距最后一次新鲜样本已 "
                    + $"{(age is null ? "超过一个完整会话" : $"{age.Value.TotalSeconds:F0} 秒")}"
                    + $"，超过传感器超时 {_settings.SensorTimeout.TotalSeconds:F0} 秒：交还固件自动策略。"
                    + "不以「拉满转速」代替失去反馈时的决策。")
                : new FanPolicyDecision(
                    FanPolicyAction.Hold,
                    null,
                    null,
                    "温度读数暂时不可用，保持当前输出并等待恢复。");
        }

        _lastFreshSampleAt = input.Now;

        var curveTarget = Measurement.Clamp(
            curve.Interpolate(temperature),
            Measurement.MinCurveOutputPercent,
            Measurement.MaxCurveOutputPercent);

        if (_lastAppliedPercent is not { } last)
        {
            _lastAppliedPercent = curveTarget;
            _lastChangeAt = input.Now;
            _lastDecisionTemperatureC = temperature;
            return new FanPolicyDecision(
                FanPolicyAction.SetOutput,
                curveTarget,
                curveTarget,
                $"首次按曲线取目标输出 {curveTarget:F0}%（{temperature:F1} °C）。");
        }

        if (_lastDecisionTemperatureC is { } lastTemperature
            && Math.Abs(temperature - lastTemperature) <= _settings.HysteresisC)
        {
            return new FanPolicyDecision(
                FanPolicyAction.Hold,
                null,
                curveTarget,
                $"温度变化 {Math.Abs(temperature - lastTemperature):F1} °C 未超出滞回带 "
                + $"{_settings.HysteresisC:F1} °C，保持当前输出 {last:F0}%。");
        }

        var delta = curveTarget - last;
        if (Math.Abs(delta) < _settings.MinimumOutputDelta)
        {
            return new FanPolicyDecision(
                FanPolicyAction.Hold,
                null,
                curveTarget,
                $"曲线目标 {curveTarget:F0}% 与当前输出 {last:F0}% 的差异小于最小变化量 "
                + $"{_settings.MinimumOutputDelta:F1}%，保持当前输出。");
        }

        // 升降速节流：升速允许更快，降速更保守。
        var limited = delta > 0
            ? last + Math.Min(delta, _settings.MaxIncreaseStepPercent)
            : last + Math.Max(delta, -_settings.MaxDecreaseStepPercent);
        limited = Measurement.Clamp(limited, Measurement.MinCurveOutputPercent, Measurement.MaxCurveOutputPercent);

        if (Math.Abs(limited - last) < _settings.MinimumOutputDelta)
        {
            return new FanPolicyDecision(
                FanPolicyAction.Hold,
                null,
                curveTarget,
                $"目标 {curveTarget:F0}% 与当前 {last:F0}% 的差距未达到升降速幅度的可执行范围，本次不改变输出。");
        }

        if (_lastChangeAt is { } lastChange
            && input.Now - lastChange < _settings.MinimumChangeInterval)
        {
            return new FanPolicyDecision(
                FanPolicyAction.Hold,
                null,
                curveTarget,
                $"距上次调速不足 {_settings.MinimumChangeInterval.TotalSeconds:F0} 秒，本次不改变输出。");
        }

        _lastAppliedPercent = limited;
        _lastChangeAt = input.Now;
        _lastDecisionTemperatureC = temperature;

        return new FanPolicyDecision(
            FanPolicyAction.SetOutput,
            limited,
            curveTarget,
            $"按曲线目标 {curveTarget:F0}% 调整到 {limited:F0}%"
            + $"（{temperature:F1} °C，受升降速节流限制：升速 ≤ {_settings.MaxIncreaseStepPercent:F0}% / "
            + $"降速 ≤ {_settings.MaxDecreaseStepPercent:F0}% / 间隔 ≥ {_settings.MinimumChangeInterval.TotalSeconds:F0} 秒）。");
    }
}

/// <summary>
/// 单通道软件调速回路：绑定一条曲线，逐周期产出决策并保留有限历史。
/// </summary>
/// <remarks>
/// <para>
/// <b>依赖说明（必须同时展示给用户）：</b><see cref="DependencyNotice"/>。
/// </para>
/// <para>
/// <b>当前无法闭环（重要限制，须上报任务 01）：</b><see cref="WritePathLimitation"/>。
/// </para>
/// </remarks>
public sealed class SoftwareFanControlLoop
{
    /// <summary>软件调速对进程存活性的依赖说明。</summary>
    public const string DependencyNotice =
        "软件持续调速依赖本程序的控制进程存活：程序退出或未运行时该策略不再生效；"
        + "固件自身持久化的设置不受影响。异常退出时本策略不提供自动回滚保证"
        + "（任务书 §7.6：不保证未验证的自动回滚一定成功）。";

    /// <summary>当前契约无法下发软件调速的说明。</summary>
    public const string WritePathLimitation =
        "共享接口 IFanHardware 只提供「应用曲线 / 切回自动 / 强冷开关」三种写操作，"
        + "没有「按周期下发输出百分比」的方法，因此软件调速目前只能产出决策、无法下发。"
        + "闭环需要任务 01 扩展契约（属于共享接口变更，按约定 §8 提出请求）。";

    /// <summary>决策历史上限。</summary>
    public const int HistoryLimit = 32;

    private readonly List<FanPolicyDecision> _history = new();

    /// <summary>构造回路。</summary>
    /// <param name="channel">通道。</param>
    /// <param name="curve">该通道的曲线。</param>
    /// <param name="settings">策略参数。</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentException">曲线通道与 <paramref name="channel"/> 不一致。</exception>
    public SoftwareFanControlLoop(
        FanChannel channel,
        FanCurve curve,
        FanPolicySettings? settings = null)
    {
        ArgumentNullException.ThrowIfNull(curve);

        if (curve.Channel != channel)
        {
            throw new ArgumentException(
                $"曲线通道 {curve.Channel} 与回路通道 {channel} 不一致。",
                nameof(curve));
        }

        Channel = channel;
        Curve = curve;
        Policy = new SoftwareFanPolicy(settings);
    }

    /// <summary>通道。</summary>
    public FanChannel Channel { get; }

    /// <summary>当前曲线。</summary>
    public FanCurve Curve { get; }

    /// <summary>策略实例。</summary>
    public SoftwareFanPolicy Policy { get; }

    /// <summary>决策历史（最多 <see cref="HistoryLimit"/> 条，最新在后）。</summary>
    public IReadOnlyList<FanPolicyDecision> History => _history;

    /// <summary>是否能够真正下发决策。当前恒为 <see langword="false"/>，见 <see cref="WritePathLimitation"/>。</summary>
    public bool CanApplyDecision => false;

    /// <summary>评估一次并记录决策。</summary>
    /// <param name="input">评估输入。</param>
    /// <returns>决策。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="input"/> 为 <see langword="null"/>。</exception>
    public FanPolicyDecision Tick(FanPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var decision = Policy.Evaluate(Curve, input);
        _history.Add(decision);
        if (_history.Count > HistoryLimit)
        {
            _history.RemoveRange(0, _history.Count - HistoryLimit);
        }

        return decision;
    }

    /// <summary>给界面用的状态说明。</summary>
    public string Describe()
        => CanApplyDecision
            ? $"{Channel} 通道软件调速回路运行中，最近决策：{_history.LastOrDefault()?.Reason ?? "尚未评估"}。{DependencyNotice}"
            : $"{Channel} 通道软件调速回路**未闭环**：{WritePathLimitation} {DependencyNotice}";
}
