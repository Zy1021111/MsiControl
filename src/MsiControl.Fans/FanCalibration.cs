using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Fans;

/// <summary>
/// 满速 RPM 基准。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §7.5：校准「保存有效 RPM 基准」，并且**不能被解释为**完整的输出百分比到 RPM
/// 精确映射。本类型只承载前者：
/// </para>
/// <list type="bullet">
/// <item><see cref="ToPercent"/> 用「满速基准」把读到的 RPM 换算成读数比例，
///       供仪表盘显示「实际转速百分比」。</item>
/// <item>它**不能**反向用于预测「设定 60% 输出会得到多少 RPM」——固件对输出的解释、
///       最低启动转速、电压曲线都会影响结果。</item>
/// </list>
/// <para>
/// <b>稳定性要求：</b>满速读数在采样窗口内必须收敛（离散度不超过
/// <see cref="MaxSpreadRatio"/>），否则该基准不可用——把抖动的读数当基准，
/// 会让仪表盘的百分比变成噪声。
/// </para>
/// </remarks>
public sealed record FanCalibrationBaseline
{
    /// <summary>允许的样本离散度上限（(最大−最小)/最大）。</summary>
    public const double MaxSpreadRatio = 0.20;

    /// <summary>判定基准可用所需的最少样本数。</summary>
    public const int MinimumSamples = 3;

    private FanCalibrationBaseline(
        FanChannel channel,
        int fullSpeedRpm,
        IReadOnlyList<int> samples,
        DateTimeOffset capturedAt,
        string source)
    {
        Channel = channel;
        FullSpeedRpm = fullSpeedRpm;
        Samples = samples;
        CapturedAt = capturedAt;
        Source = source;
    }

    /// <summary>通道。</summary>
    public FanChannel Channel { get; }

    /// <summary>满速基准转速（样本均值，四舍五入）。</summary>
    public int FullSpeedRpm { get; }

    /// <summary>用于建立基准的原始样本。</summary>
    public IReadOnlyList<int> Samples { get; }

    /// <summary>采集时刻。</summary>
    public DateTimeOffset CapturedAt { get; }

    /// <summary>数据来源说明。</summary>
    public string Source { get; }

    /// <summary>样本离散度（(最大−最小)/最大）。</summary>
    public double SpreadRatio
    {
        get
        {
            if (Samples.Count == 0)
            {
                return 0;
            }

            var max = Samples.Max();
            return max <= 0 ? 0 : (double)(max - Samples.Min()) / max;
        }
    }

    /// <summary>
    /// 用本基准把读到的 RPM 换算成读数比例（0–100）。
    /// </summary>
    /// <param name="rpm">实际转速。</param>
    /// <returns>占满速基准的比例，按 0–100 截断。</returns>
    /// <remarks>
    /// 这是**由读数反推比例**，不是「按比例设定输出」的映射。两者不可互相替代（任务书 §6.4）。
    /// </remarks>
    public double ToPercent(int rpm)
        => FullSpeedRpm <= 0 ? 0 : Measurement.Clamp(100.0 * rpm / FullSpeedRpm, 0.0, 100.0);

    /// <summary>给用户看的说明，含上述限制。</summary>
    public string Describe()
        => $"{Channel} 风扇满速基准 {FullSpeedRpm} RPM（{Samples.Count} 个样本，离散度 "
           + $"{SpreadRatio:P0}，采集于 {CapturedAt:yyyy-MM-dd HH:mm:ss}）。"
           + "该基准只用于把读到的转速换算成比例，不代表设定任意输出都能得到对应转速。";

    /// <summary>
    /// 由样本创建基准。样本不足或不稳定时失败。
    /// </summary>
    /// <param name="channel">通道。</param>
    /// <param name="samples">RPM 样本。至少 <see cref="MinimumSamples"/> 个。</param>
    /// <param name="capturedAt">采集时刻。</param>
    /// <param name="source">来源说明。</param>
    /// <param name="baseline">成功时为基准，否则为 <see langword="null"/>。</param>
    /// <param name="errors">失败原因。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> 或 <paramref name="source"/> 为 <see langword="null"/>。</exception>
    public static bool TryCreate(
        FanChannel channel,
        IEnumerable<int> samples,
        DateTimeOffset capturedAt,
        string source,
        out FanCalibrationBaseline? baseline,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(source);

        var list = samples.ToList();
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(source))
        {
            problems.Add("来源说明不能为空：无法追溯的基准不允许进入模型。");
        }

        if (list.Count < MinimumSamples)
        {
            problems.Add($"样本数量 {list.Count} 少于判定基准所需的最少 {MinimumSamples} 个。");
        }

        if (list.Any(rpm => rpm <= 0))
        {
            problems.Add("样本中存在非正的转速读数，视为无效样本。");
        }

        if (problems.Count > 0)
        {
            baseline = null;
            errors = problems;
            return false;
        }

        var max = list.Max();
        var spread = (double)(max - list.Min()) / max;
        if (spread > MaxSpreadRatio)
        {
            problems.Add(
                $"满速读数不稳定：离散度 {spread:P0} 超过允许的 {MaxSpreadRatio:P0}（样本 {string.Join("、", list)} RPM）。");
            baseline = null;
            errors = problems;
            return false;
        }

        baseline = new FanCalibrationBaseline(
            channel,
            (int)Math.Round(list.Average(), MidpointRounding.AwayFromZero),
            list,
            capturedAt,
            source);
        errors = problems;
        return true;
    }
}

/// <summary>
/// 双通道满速基准集合。
/// </summary>
/// <param name="Cpu">CPU 通道基准。</param>
/// <param name="Gpu">GPU 通道基准。</param>
public sealed record FanCalibrationBaselineSet(FanCalibrationBaseline? Cpu, FanCalibrationBaseline? Gpu)
{
    /// <summary>全部为空的集合。</summary>
    public static FanCalibrationBaselineSet Empty { get; } = new(null, null);

    /// <summary>两个通道是否都已取得有效基准。未全部取得时，界面必须回落显示 RPM。</summary>
    public bool IsUsable => Cpu is not null && Gpu is not null;

    /// <summary>按通道取基准。</summary>
    public FanCalibrationBaseline? For(FanChannel channel)
        => channel == FanChannel.Cpu ? Cpu : Gpu;

    /// <summary>给用户看的说明。</summary>
    public string Describe()
        => IsUsable
            ? $"CPU {Cpu!.FullSpeedRpm} RPM / GPU {Gpu!.FullSpeedRpm} RPM。"
            : $"基准不完整（CPU {(Cpu is null ? "缺失" : "已有")}，GPU {(Gpu is null ? "缺失" : "已有")}）："
              + "界面必须回落显示 RPM，不得使用不完整的百分比。";
}

/// <summary>校准会话状态。</summary>
public enum FanCalibrationState
{
    /// <summary>未开始。</summary>
    Idle = 0,

    /// <summary>进行中。</summary>
    Running = 1,

    /// <summary>已完成并取得基准。</summary>
    Completed = 2,

    /// <summary>已由用户取消。</summary>
    Cancelled = 3,

    /// <summary>因样本无效或硬件失败而终止。</summary>
    Failed = 4
}

/// <summary>
/// 校准计划：把「需要用户配合」这件事写进参数，而不是靠调用方自觉。
/// </summary>
/// <param name="SampleCount">目标样本数量。</param>
/// <param name="SettleTime">进入满速后等待稳定的时长。</param>
/// <param name="UserAcknowledgedFullSpeed">
/// 用户是否已知悉并同意执行满速测试（含步骤、预期现象与恢复入口）。
/// <b>必须为 <see langword="true"/> 才允许开始。</b>
/// </param>
public sealed record FanCalibrationPlan(
    int SampleCount = 10,
    TimeSpan? SettleTime = null,
    bool UserAcknowledgedFullSpeed = false)
{
    /// <summary>默认计划：10 个样本、稳定 30 秒、**未**获得用户同意。</summary>
    public static FanCalibrationPlan Default { get; } = new();

    /// <summary>实际使用的稳定时长。</summary>
    public TimeSpan EffectiveSettleTime => SettleTime ?? TimeSpan.FromSeconds(30);

    /// <summary>计划本身是否合法（与能力、用户同意无关）。</summary>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (SampleCount < FanCalibrationBaseline.MinimumSamples)
        {
            problems.Add($"样本数量 {SampleCount} 少于判定基准所需的最少 {FanCalibrationBaseline.MinimumSamples} 个。");
        }

        if (EffectiveSettleTime <= TimeSpan.Zero)
        {
            problems.Add("稳定时长必须为正数。");
        }

        return problems;
    }
}

/// <summary>
/// 校准会话：进度、样本收集与取消。
/// </summary>
/// <remarks>
/// 会话只负责**收集与判定**，不下发任何硬件命令——下发由
/// <see cref="FanCalibrationService"/> 统一编排，便于把「进入满速 → 采样 → 退出满速 → 核对」
/// 的每一步失败都单独呈现。
/// </remarks>
public sealed class FanCalibrationSession
{
    private readonly List<int> _cpuSamples = new();
    private readonly List<int> _gpuSamples = new();

    /// <summary>构造会话。</summary>
    /// <param name="plan">校准计划。</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> 为 <see langword="null"/>。</exception>
    public FanCalibrationSession(FanCalibrationPlan plan)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        StartedAt = DateTimeOffset.Now;
    }

    /// <summary>计划。</summary>
    public FanCalibrationPlan Plan { get; }

    /// <summary>开始时刻。</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>当前状态。</summary>
    public FanCalibrationState State { get; private set; } = FanCalibrationState.Running;

    /// <summary>已收集的 CPU 样本。</summary>
    public IReadOnlyList<int> CpuSamples => _cpuSamples;

    /// <summary>已收集的 GPU 样本。</summary>
    public IReadOnlyList<int> GpuSamples => _gpuSamples;

    /// <summary>失败或取消的原因。</summary>
    public string? TerminationReason { get; private set; }

    /// <summary>进度（0–100），按已收集的双通道样本对数估算。</summary>
    public int ProgressPercent
    {
        get
        {
            if (Plan.SampleCount <= 0)
            {
                return 0;
            }

            var pairs = Math.Min(_cpuSamples.Count, _gpuSamples.Count);
            return (int)Math.Round(Math.Min(100.0, 100.0 * pairs / Plan.SampleCount));
        }
    }

    /// <summary>是否仍在进行。</summary>
    public bool IsRunning => State == FanCalibrationState.Running;

    /// <summary>
    /// 提交一次采样。
    /// </summary>
    /// <param name="fans">双风扇快照。</param>
    /// <returns>是否已收集满计划要求的样本数。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fans"/> 为 <see langword="null"/>。</exception>
    /// <remarks>
    /// 只接受**有效**读数：<c>Stale</c> 与不可用读数一律跳过，避免把旧值算进基准
    /// （任务书 §6.4：过期数据不得冒充当前读数）。
    /// </remarks>
    public bool Report(FanSnapshot fans)
    {
        ArgumentNullException.ThrowIfNull(fans);

        if (!IsRunning)
        {
            return false;
        }

        var cpu = ReadUsableRpm(fans.Cpu);
        var gpu = ReadUsableRpm(fans.Gpu);
        if (cpu is null || gpu is null)
        {
            return false;
        }

        _cpuSamples.Add(cpu.Value);
        _gpuSamples.Add(gpu.Value);
        return _cpuSamples.Count >= Plan.SampleCount && _gpuSamples.Count >= Plan.SampleCount;
    }

    /// <summary>请求取消。实际退出满速与恢复由服务完成。</summary>
    public void Cancel()
    {
        if (IsRunning)
        {
            State = FanCalibrationState.Cancelled;
            TerminationReason = "用户取消。";
        }
    }

    /// <summary>标记失败。</summary>
    /// <param name="reason">原因，不能为空。</param>
    /// <exception cref="ArgumentException"><paramref name="reason"/> 为空。</exception>
    public void Fail(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (IsRunning)
        {
            State = FanCalibrationState.Failed;
            TerminationReason = reason;
        }
    }

    /// <summary>
    /// 结算：按已收集样本建立双通道基准。
    /// </summary>
    /// <returns>基准集合。样本不足或状态不允许时为 <see cref="FanCalibrationBaselineSet.Empty"/>。</returns>
    public FanCalibrationBaselineSet Complete()
    {
        if (!IsRunning)
        {
            return FanCalibrationBaselineSet.Empty;
        }

        var cpuOk = FanCalibrationBaseline.TryCreate(
            FanChannel.Cpu,
            _cpuSamples,
            DateTimeOffset.Now,
            "任务 04 校准会话（满速采样）",
            out var cpu,
            out var cpuErrors);

        var gpuOk = FanCalibrationBaseline.TryCreate(
            FanChannel.Gpu,
            _gpuSamples,
            DateTimeOffset.Now,
            "任务 04 校准会话（满速采样）",
            out var gpu,
            out var gpuErrors);

        if (!cpuOk || !gpuOk)
        {
            State = FanCalibrationState.Failed;
            TerminationReason = string.Join(" ", cpuErrors.Concat(gpuErrors));
            return FanCalibrationBaselineSet.Empty;
        }

        State = FanCalibrationState.Completed;
        TerminationReason = null;
        return new FanCalibrationBaselineSet(cpu, gpu);
    }

    /// <summary>给界面用的进度文案。</summary>
    public string Describe()
        => State switch
        {
            FanCalibrationState.Running =>
                $"校准进行中：{ProgressPercent}%，已收集 CPU {_cpuSamples.Count} / GPU {_gpuSamples.Count} 个样本"
                + $"（目标 {Plan.SampleCount}）。",
            FanCalibrationState.Completed =>
                $"校准完成：CPU {_cpuSamples.Count} / GPU {_gpuSamples.Count} 个样本。",
            FanCalibrationState.Cancelled => $"校准已取消：{TerminationReason}已恢复原策略（需核对）。",
            FanCalibrationState.Failed => $"校准失败：{TerminationReason}",
            _ => "校准未开始。"
        };

    private static int? ReadUsableRpm(FanReading reading)
    {
        var rpm = reading.Rpm;
        if (rpm.Status != ReadingStatus.Valid || rpm.Value is not { } value || value < 0)
        {
            return null;
        }

        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
