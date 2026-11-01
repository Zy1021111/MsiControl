using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Fans;
/// <summary>
/// 软件曲线引擎：按周期把「曲线目标占空比」下发到 EC，对抗固件的周期性曲线回写。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要本引擎：</b>MS-15M3 固件在 advanced 模式下约每 5 秒把
/// EC 0x71/0x89（占空比）改回固件曲线值（HWE §17.3）。要让用户编辑的自定义曲线
/// 真正生效，唯一途径是以更高频率重新下发目标占空比（NBFC 同款方案，叶哥 2026-10-17 授权启用）。
/// </para>
/// <para>
/// <b>每个周期的动作（每通道）：</b>读温度 → 曲线插值 → 升降速限幅 → 下发占空比。
/// 温度读取失败时保持上次输出并重写下发（防止固件回写后无人纠正）；
/// 连续失败达到上限后<b>自动停机并交还固件</b>（<see cref="AutoStopReason"/>），
/// 绝不在失去温度依据的情况下继续输出。
/// </para>
/// <para>
/// <b>安全边界：</b>引擎停止（手动或自动）后<b>不主动恢复任何曲线</b>——固件会在数秒内
/// 用自己的曲线接管（这是本机固件的实测行为，反而是自愈保障）；
/// 「切回自动」由调用方显式执行（0xD4=0x0D）。
/// </para>
/// </remarks>
public sealed class FanCurveEngine : IDisposable
{
    /// <summary>默认下发周期。</summary>
    /// <remarks>
    /// 实测（HWE §23）固件在 ≤2 秒内把占空比回写为自身曲线值；
    /// 1 秒周期可让自定义占空比在绝大多数时间保持生效（写入事务本身亚毫秒到数毫秒级）。
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1);

    /// <summary>温度连续读取失败的自动停机阈值。</summary>
    public const int MaxConsecutiveSensorFailures = 5;

    /// <summary>
    /// 占空比连续下发失败的自动停机阈值。
    /// </summary>
    /// <remarks>
    /// 与温度失败分开取值：温度丢失意味着<b>失去调速依据</b>，必须立刻停机（5 拍约 5 秒）；
    /// 写失败不会失去依据，固件也会在数秒内用自身曲线接管，属于可自愈的瞬时争用。
    /// 20 拍（约 20 秒）既能跨过短暂争用，又能在通道真的失效时及时停机。
    /// 首版与温度共用 5 拍，2026-10-29 实机日志 20:10:41 出现过一次「连续 5 次失败后停机」，
    /// 当时界面无操作、曲线运行约 30 秒，**具体诱因尚未定位**（同版本另一次 70 秒静置实验无失败），
    /// 因此本改动只按「写失败可自愈」放宽阈值，不宣称已修复某个具体原因。
    /// </remarks>
    public const int MaxConsecutiveWriteFailures = 20;

    /// <summary>占空比写入的设置项键。</summary>
    public const string DutySettingKey = "fan.curve";

    /// <summary>
    /// 可维持稳定转动的最低占空比（百分比）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 实测依据：本机风扇占空比低于约 20% 时只「点一下」而不成转（EC 读数是占空比，
    /// 物理转速在低占空比区间存在启动死区，HWE §24.1 的线性换算在低区间不成立）。
    /// 曲线给出 1–19% 时按本值下发，避免「半转不转」把用户听成故障。
    /// </para>
    /// <para>本值是**软件侧保守下限**，不是硬件规格；如实测更低可稳定转动，可下调。</para>
    /// </remarks>
    public const double MinimumControlledDuty = 20.0;

    /// <summary>
    /// 曲线求值所用温度的下行迟滞带宽（摄氏度）。
    /// </summary>
    /// <remarks>
    /// 升温立即响应（安全优先），降温时要求真实温度比上次求值温度低出本值才下移。
    /// 作用：吸收 ±1–2°C 的读数抖动，避免占空比在节点边界左右反复跳变。
    /// </remarks>
    public const double TemperatureHysteresisC = 2.0;

    private readonly IFanOutputControl _output;
    private readonly Func<FanChannel, double?> _readTemperatureC;
    private readonly object _gate = new();
    private readonly Dictionary<FanChannel, ChannelState> _channels = new();
    private readonly Action<string>? _log;
    private System.Threading.Timer? _timer;
    private TimeSpan _interval = DefaultInterval;
    private bool _disposed;

    /// <summary>构造软件曲线引擎。</summary>
    /// <param name="output">占空比下发通道。</param>
    /// <param name="readTemperatureC">按通道读取温度（摄氏度）；不可用时返回 <see langword="null"/>。</param>
    /// <param name="log">日志回调（文本已含上下文），可为空。Fans 工程不依赖 Application，故用委托而非 IAppLog。</param>
    public FanCurveEngine(
        IFanOutputControl output,
        Func<FanChannel, double?> readTemperatureC,
        Action<string>? log = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _readTemperatureC = readTemperatureC ?? throw new ArgumentNullException(nameof(readTemperatureC));
        _log = log;
    }

    /// <summary>占空比下发周期。</summary>
    public TimeSpan Interval
    {
        get
        {
            lock (_gate)
            {
                return _interval;
            }
        }
    }

    /// <summary>引擎是否正在运行。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _timer is not null;
            }
        }
    }

    /// <summary>自动停机原因；运行中或手动停止后为 <see langword="null"/>。</summary>
    public string? AutoStopReason { get; private set; }

    /// <summary>当前运行的曲线集合（只读视图）；未运行为 <see langword="null"/>。</summary>
    public FanCurveSet? RunningCurveSet { get; private set; }

    /// <summary>各通道最近一次下发结果（供界面展示；未下发过为 <see langword="null"/>）。</summary>
    public IReadOnlyDictionary<FanChannel, ChannelSnapshot> LastSnapshots
    {
        get
        {
            lock (_gate)
            {
                return _channels.ToDictionary(kv => kv.Key, kv => kv.Value.Snapshot);
            }
        }
    }

    /// <summary>单通道最近一次下发的快照。</summary>
    /// <param name="Percent">实际下发的百分比。</param>
    /// <param name="TemperatureC">下发时的温度；读取失败为 <see langword="null"/>。</param>
    /// <param name="At">下发时刻。</param>
    /// <param name="Note">补充说明（如温度读取失败时的保持语义）。</param>
    /// <param name="ReleasedToFirmware">本通道当前是否已交还固件调速（本拍未写入）。</param>
    public sealed record ChannelSnapshot(
        double Percent,
        double? TemperatureC,
        DateTimeOffset At,
        string? Note,
        bool ReleasedToFirmware = false);

    /// <summary>
    /// 启动引擎（替换已运行的曲线集）。
    /// </summary>
    /// <param name="curveSet">CPU/GPU 两条曲线。</param>
    /// <param name="interval">下发周期；省略用 <see cref="DefaultInterval"/>。首拍同样按该周期延迟。</param>
    /// <exception cref="ArgumentNullException"><paramref name="curveSet"/> 为 <see langword="null"/>.</exception>
    /// <returns>是否成功启动；通道不具备下发条件时返回 <see langword="false"/> 并填 <see cref="AutoStopReason"/>。</returns>
    /// <remarks>
    /// 首拍刻意与后续周期同延迟（而非立即执行）：「应用曲线」的调用方会先切换 EC 模式，
    /// 引擎在其后一个周期内开始下发即可；同步首拍会与调用方的模式切换请求产生竞态。
    /// </remarks>
    public bool Start(FanCurveSet curveSet, TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(curveSet);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (!_output.IsDutyWriteEnabled)
            {
                AutoStopReason = "占空比下发通道不可用（能力或写白名单未就绪），引擎未启动。";
                return false;
            }

            StopCore();
            AutoStopReason = null;
            RunningCurveSet = curveSet;
            _interval = interval ?? DefaultInterval;
            _channels[FanChannel.Cpu] = new ChannelState(curveSet.Cpu);
            _channels[FanChannel.Gpu] = new ChannelState(curveSet.Gpu);
            _timer = new Timer(
                _ => TickSafe(),
                null,
                _interval,
                _interval);
            return true;
        }
    }

    /// <summary>
    /// 停止引擎。停止后不做任何恢复动作——固件会在数秒内用自身曲线接管（实测自愈行为）。
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    /// <summary>
    /// 执行一次周期（供测试注入；生产由定时器调用）。
    /// </summary>
    /// <param name="now">本次周期时刻。</param>
    public void TickOnce(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_timer is null || RunningCurveSet is null)
            {
                return;
            }

            foreach (var (channel, state) in _channels)
            {
                TickChannel(channel, state, now);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StopCore();
            _disposed = true;
        }
    }

    private void StopCore()
    {
        _timer?.Dispose();
        _timer = null;
        _channels.Clear();
        RunningCurveSet = null;
    }

    private void TickSafe()
    {
        try
        {
            TickOnce(DateTimeOffset.Now);
        }
        catch (Exception exception)
        {
            // 周期回调里不允许异常逃逸（会拖垮定时器）：记录并继续，下一次周期重试。
            _log?.Invoke($"[FanCurveEngine] 周期执行异常（下一周期重试）：{exception.Message}");
        }
    }

    private void TickChannel(FanChannel channel, ChannelState state, DateTimeOffset now)
    {
        var temperature = SafeReadTemperature(channel);
        if (temperature is null)
        {
            state.ConsecutiveSensorFailures++;
            if (state.ConsecutiveSensorFailures >= MaxConsecutiveSensorFailures)
            {
                AutoStop($"温度连续 {state.ConsecutiveSensorFailures} 次读取失败，已自动停机并交还固件（{channel}）。");
                return;
            }
        }
        else
        {
            state.ConsecutiveSensorFailures = 0;
        }

        // 求值温度带下行迟滞：升温立即跟随，降温需越过 2°C 才下移。
        // 否则温度在节点边界抖动 ±1°C 会让占空比 0%↔8% 反复跳变，风扇一停一转。
        var evaluated = temperature is not null
            ? ApplyDownwardHysteresis(state, temperature.Value)
            : state.EffectiveTemperatureC;

        // 已交还固件的通道：曲线目标重新为正数才接管。
        // 接管后一律按「可维持下限」以上下发，因此不会在临界点制造启停抖动。
        if (state.ReleasedToFirmware)
        {
            if (evaluated is null || state.Curve.Interpolate(evaluated.Value) <= 0)
            {
                state.Snapshot = state.Snapshot with { TemperatureC = temperature };
                return;
            }

            state.ReleasedToFirmware = false;
            state.LastWrittenPercent = null;
            _log?.Invoke(
                $"[FanCurveEngine] {channel} 曲线目标重新为正，重新接管占空比（按不低于 "
                + $"{MinimumControlledDuty:0}% 下发）。");
        }

        // 温度不可用时保持上次输出（并重写下发以对抗固件回写）；
        // 从未成功读取过温度且没有上次输出时，不盲目输出，等待下一个周期。
        var target = evaluated is not null
            ? state.Curve.Interpolate(evaluated.Value)
            : state.LastWrittenPercent;

        if (target is null)
        {
            return;
        }

        // 曲线要求 0%（停转）：**不再写 0**。
        // 实测固件不接受 0%：写 0 后立即读回的是固件自身曲线值（日志实录「写入后读回 24%，与请求 0% 不一致」），
        // 引擎每秒写 0、固件每秒回写非零值，风扇就被驱动成「转一下停一下」。
        // 正确做法是把该通道交还固件（停止写入），由固件自己的低转速曲线稳定维持。
        if (target.Value <= 0)
        {
            ReleaseToFirmware(channel, state, temperature, now);
            return;
        }

        // 1–19% 属启动死区：按可维持下限下发，避免「点一下不转」。
        if (target.Value < MinimumControlledDuty)
        {
            if (!state.BelowMinimumNoted)
            {
                state.BelowMinimumNoted = true;
                _log?.Invoke(
                    $"[FanCurveEngine] {channel} 曲线目标 {target.Value:0}% 低于可维持转速，"
                    + $"已按 {MinimumControlledDuty:0}% 下发（低于该值风扇只点动、不成转）。");
            }

            target = MinimumControlledDuty;
        }
        else
        {
            state.BelowMinimumNoted = false;
        }

        // 升快降慢的限幅（与 SoftwareFanPolicy 同方向的保守取值）。
        var limited = state.LastWrittenPercent is { } last
            ? Math.Clamp(
                target.Value,
                last - 10.0,
                last + 20.0)
            : target.Value;
        limited = Math.Clamp(limited, 0, 100);

        var result = _output.SetDutyPercentAsync(channel, limited, CancellationToken.None)
            .GetAwaiter().GetResult();
        var setting = result.Settings.FirstOrDefault();
        if (setting?.Status is OperationStatus.Succeeded or OperationStatus.PendingRestart)
        {
            state.LastWrittenPercent = limited;
            state.Snapshot = new ChannelSnapshot(limited, temperature, now, null);
        }
        else
        {
            state.ConsecutiveWriteFailures++;
            var failureMessage = setting?.Message ?? result.Status.ToString();
            state.Snapshot = new ChannelSnapshot(
                state.LastWrittenPercent ?? 0,
                temperature,
                now,
                $"下发失败：{failureMessage}");

            // 首失败与临近停机的失败各记一条：既有可追溯的底层原因，又不会 1 秒一条刷屏。
            if (state.ConsecutiveWriteFailures == 1
                || state.ConsecutiveWriteFailures == MaxConsecutiveWriteFailures)
            {
                _log?.Invoke(
                    $"[FanCurveEngine] {channel} 占空比下发失败（连续 {state.ConsecutiveWriteFailures} 次）：{failureMessage}");
            }

            if (state.ConsecutiveWriteFailures >= MaxConsecutiveWriteFailures)
            {
                AutoStop($"占空比连续 {state.ConsecutiveWriteFailures} 次下发失败，已自动停机（{channel}）。");
            }
        }
    }

    /// <summary>下行迟滞：升温立即跟随，降温需超过 <see cref="TemperatureHysteresisC"/> 才下移。</summary>
    private static double ApplyDownwardHysteresis(ChannelState state, double raw)
    {
        if (state.EffectiveTemperatureC is not { } last)
        {
            state.EffectiveTemperatureC = raw;
            return raw;
        }

        if (raw >= last)
        {
            state.EffectiveTemperatureC = raw;
            return raw;
        }

        if (last - raw <= TemperatureHysteresisC)
        {
            // 小幅回落不改变求值温度：抑制边界抖动，读数仍按实际值展示。
            return last;
        }

        state.EffectiveTemperatureC = raw;
        return raw;
    }

    /// <summary>把通道交还固件：停止写 0%，由固件曲线维持低转速。</summary>
    private void ReleaseToFirmware(
        FanChannel channel,
        ChannelState state,
        double? temperature,
        DateTimeOffset now)
    {
        if (!state.ReleasedToFirmware)
        {
            state.ReleasedToFirmware = true;
            state.LastWrittenPercent = null;
            _log?.Invoke(
                $"[FanCurveEngine] {channel} 曲线目标为 0%，已停止对该通道写入并交还固件曲线："
                + "本机固件不接受 0% 占空比（写入后被回写为更高值），每秒写 0 会让风扇转一下停一下。");
        }

        state.Snapshot = new ChannelSnapshot(
            0,
            temperature,
            now,
            "曲线目标 0%：已交还固件调速（不写 0%，避免风扇反复启停）",
            ReleasedToFirmware: true);
    }

    private double? SafeReadTemperature(FanChannel channel)
    {
        try
        {
            var value = _readTemperatureC(channel);
            return value is >= 0 and <= 120 ? value : null;
        }
        catch (Exception exception)
        {
            _log?.Invoke($"[FanCurveEngine] 读取 {channel} 温度失败：{exception.Message}");
            return null;
        }
    }

    private void AutoStop(string reason)
    {
        _log?.Invoke($"[FanCurveEngine] {reason}");
        StopCore();
        AutoStopReason = reason;
    }

    private sealed class ChannelState(FanCurve curve)
    {
        public FanCurve Curve { get; } = curve;

        public double? LastWrittenPercent { get; set; }

        /// <summary>本通道当前用于曲线求值的温度（含下行迟滞）。</summary>
        public double? EffectiveTemperatureC { get; set; }

        /// <summary>是否已交还固件调速（曲线目标 0% 时置位）。</summary>
        public bool ReleasedToFirmware { get; set; }

        /// <summary>是否已就「低于可维持转速」记过一次日志（避免每秒刷屏）。</summary>
        public bool BelowMinimumNoted { get; set; }

        public int ConsecutiveSensorFailures { get; set; }

        public int ConsecutiveWriteFailures { get; set; }

        public ChannelSnapshot Snapshot { get; set; } = new(0, null, DateTimeOffset.MinValue, null);
    }
}
