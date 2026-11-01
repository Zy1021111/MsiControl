namespace MsiControl.Core.Models;

/// <summary>
/// 单个传感器的读数。
/// </summary>
/// <remarks>
/// <para>
/// 核心约束：读数失效时 <see cref="Status"/> 必须反映真实情况，
/// <b>不得沿用上一个有效值冒充当前读数</b>（任务书 §6.4）。
/// </para>
/// <para>
/// <b>构造只能经工厂方法。</b>构造函数私有，并在构造期强制以下不变量，
/// 使非法状态在对象诞生前即被拒绝（与 <see cref="PowerLimit"/> 同构）：
/// </para>
/// <list type="bullet">
/// <item><see cref="ReadingStatus.Valid"/> 必须同时携带数值与采样时刻。</item>
/// <item><see cref="ReadingStatus.Sleeping"/> 与 <see cref="ReadingStatus.Unavailable"/>
///       <b>不得携带数值</b>——否则界面可能把陈旧值当作当前读数显示。</item>
/// <item><see cref="ReadingStatus.Stale"/> 允许保留最后已知值，仅供诊断。</item>
/// <item>单位与数据来源不得为空。</item>
/// </list>
/// </remarks>
public sealed record SensorReading
{
    private SensorReading(
        double? value,
        string unit,
        ReadingStatus status,
        string source,
        DateTimeOffset? sampledAt,
        string? detail)
    {
        if (string.IsNullOrWhiteSpace(unit))
        {
            throw new ArgumentException("单位不能为空，取值见 Units。", nameof(unit));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("数据来源不能为空：无法追溯的读数不允许进入模型。", nameof(source));
        }

        switch (status)
        {
            case ReadingStatus.Valid:
                if (!value.HasValue)
                {
                    throw new ArgumentException("有效读数必须携带数值。", nameof(value));
                }

                if (!sampledAt.HasValue)
                {
                    throw new ArgumentException("有效读数必须有采样时刻，否则无法判断是否过期。", nameof(sampledAt));
                }

                break;

            case ReadingStatus.Sleeping:
            case ReadingStatus.Unavailable:
                if (value.HasValue)
                {
                    throw new ArgumentException(
                        $"{status} 状态的读数不得携带数值，否则界面可能误把陈旧值当作当前读数。",
                        nameof(value));
                }

                break;

            case ReadingStatus.Stale:
                // 允许保留最后已知值，仅供诊断；界面须显式标注为过期。
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(status), status, "未知的读数状态。");
        }

        Value = value;
        Unit = unit;
        Status = status;
        Source = source;
        SampledAt = sampledAt;
        Detail = detail;
    }

    /// <summary>数值。仅 <see cref="ReadingStatus.Valid"/> 与 <see cref="ReadingStatus.Stale"/> 可能非空。</summary>
    public double? Value { get; }

    /// <summary>单位，取值见 <see cref="Units"/>。</summary>
    public string Unit { get; }

    /// <summary>有效性状态。</summary>
    public ReadingStatus Status { get; }

    /// <summary>数据来源说明，例如 <c>MSI_ACPI.Get_Temperature</c> 或 <c>NVML</c>。</summary>
    public string Source { get; }

    /// <summary>采样时刻。非有效读数可为 <see langword="null"/>。</summary>
    public DateTimeOffset? SampledAt { get; }

    /// <summary>补充说明，例如错误码或降级原因。</summary>
    public string? Detail { get; }

    /// <summary>该读数是否可用于展示与判断。</summary>
    public bool IsUsable => Status == ReadingStatus.Valid && Value.HasValue;

    /// <summary>
    /// 距指定时刻的年龄。采样时刻未知时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>供界面判断是否过期；实际有效期阈值由调用方（监控模块）决定。</remarks>
    public TimeSpan? Age(DateTimeOffset now) => SampledAt.HasValue ? now - SampledAt.Value : null;

    /// <summary>构造一个有效读数。</summary>
    /// <exception cref="ArgumentException">数值缺失或单位、来源为空。</exception>
    public static SensorReading Valid(double value, string unit, string source, DateTimeOffset sampledAt)
        => new(value, unit, ReadingStatus.Valid, source, sampledAt, null);

    /// <summary>构造成一个休眠读数。用于独显断电等场景，界面显示「休眠」。</summary>
    public static SensorReading Sleeping(string unit, string source, string? detail = null)
        => new(null, unit, ReadingStatus.Sleeping, source, null, detail);

    /// <summary>构造一个不可用读数。界面显示「不可用」。</summary>
    public static SensorReading Unavailable(string unit, string source, string? detail = null)
        => new(null, unit, ReadingStatus.Unavailable, source, null, detail);

    /// <summary>构造一个过期读数。保留上次已知值以供诊断，但状态为过期。</summary>
    public static SensorReading Stale(
        double? lastKnownValue,
        string unit,
        string source,
        DateTimeOffset? sampledAt,
        string? detail = null)
        => new(lastKnownValue, unit, ReadingStatus.Stale, source, sampledAt, detail);

    /// <summary>基于当前读数构造一个过期版本。</summary>
    public SensorReading AsStale(string? detail = null)
        => new(Value, Unit, ReadingStatus.Stale, Source, SampledAt, detail ?? Detail);

    /// <summary>
    /// 仅当当前读数有效时转为过期，否则原样返回。
    /// </summary>
    /// <remarks>
    /// 采样超时的正确语义是「原本有效的读数变旧了」。
    /// 若把 <see cref="ReadingStatus.Unavailable"/> 或 <see cref="ReadingStatus.Sleeping"/>
    /// 也一并改成过期，会把「读不到」和「数据旧了」两种不同情况混为一谈。
    /// </remarks>
    public SensorReading AsStaleIfValid(string? detail = null)
        => Status == ReadingStatus.Valid ? AsStale(detail) : this;
}

/// <summary>CPU 或 GPU 风扇读数。</summary>
/// <remarks>
/// <para>
/// 任务书 §6.4 要求「实际 RPM 百分比和曲线目标输出百分比明确区分」，
/// 因此 <see cref="ActualPercent"/> 与 <see cref="TargetPercent"/> 是独立字段，
/// <b>调用方不得互相替代</b>。
/// </para>
/// <para>
/// <see cref="ActualPercent"/> 的两个合法来源（2026-10-17 扩充）：
/// <b>校准基准推导</b>或<b>硬件直接报告</b>（如 EC 风扇占空比寄存器、Afterburner 风扇百分比）。
/// 硬件直报值比校准推导更准确；两者都不可用时为 <see langword="null"/>，界面回落显示 RPM。
/// </para>
/// <para>构造函数私有：RPM 不得为负，百分比必须落在 0–100。</para>
/// </remarks>
public sealed record FanReading
{
    private FanReading(SensorReading rpm, double? actualPercent, double? targetPercent)
    {
        if (actualPercent.HasValue && !Measurement.IsValidCurveOutput(actualPercent.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(actualPercent),
                actualPercent,
                "实际转速百分比必须落在 0–100。");
        }

        if (targetPercent.HasValue && !Measurement.IsValidCurveOutput(targetPercent.Value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPercent),
                targetPercent,
                "曲线目标输出百分比必须落在 0–100。");
        }

        Rpm = rpm;
        ActualPercent = actualPercent;
        TargetPercent = targetPercent;
    }

    /// <summary>实际转速。</summary>
    public SensorReading Rpm { get; }

    /// <summary>实际转速百分比。来源为校准基准推导，或硬件直接报告（EC 占空比 / Afterburner）。两者皆不可用时为 <see langword="null"/>。</summary>
    public double? ActualPercent { get; }

    /// <summary>曲线目标输出百分比。与 <see cref="ActualPercent"/> 是不同量。</summary>
    public double? TargetPercent { get; }

    /// <summary>是否存在可显示的实际转速百分比（校准基准或硬件直报）。</summary>
    public bool HasCalibration => ActualPercent.HasValue;

    /// <summary>构造一个完全不可用的风扇读数。</summary>
    public static FanReading Unavailable(string source, string? detail = null)
        => new(SensorReading.Unavailable(Units.Rpm, source, detail), null, null);

    /// <summary>构造一个休眠的风扇读数。</summary>
    public static FanReading Sleeping(string source, string? detail = null)
        => new(SensorReading.Sleeping(Units.Rpm, source, detail), null, null);

    /// <summary>构造一个有效的风扇读数。</summary>
    /// <exception cref="ArgumentOutOfRangeException">RPM 为负或百分比越界。</exception>
    public static FanReading Valid(
        int rpm,
        string source,
        DateTimeOffset sampledAt,
        double? actualPercent = null,
        double? targetPercent = null)
    {
        if (rpm < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rpm), rpm, "转速不得为负。");
        }

        return new FanReading(SensorReading.Valid(rpm, Units.Rpm, source, sampledAt), actualPercent, targetPercent);
    }

    /// <summary>返回一个更换了转速读数的副本，保留百分比字段。</summary>
    public FanReading WithRpm(SensorReading rpm) => new(rpm, ActualPercent, TargetPercent);

    /// <summary>
    /// 构造一个由<b>硬件直接报告</b>实际转速百分比的风扇读数（例如 EC 占空比寄存器、Afterburner）。
    /// </summary>
    /// <param name="percent">硬件报告的实际转速百分比（0–100，越界值会被截断）。</param>
    /// <param name="source">数据来源说明。</param>
    /// <param name="sampledAt">采样时刻。</param>
    /// <param name="rpmUnavailableReason">
    /// RPM 槽位不可用的原因。部分机型（如 MS-15M3 的 EC 寄存器表）只提供风扇占空比、
    /// 不提供 RPM 读数，此时 RPM 如实置为「不可用」，百分比仍可显示。
    /// </param>
    /// <remarks>
    /// 硬件直报百分比比校准推导更准确；本工厂<b>不得</b>用于把估算值冒充实际转速。
    /// </remarks>
    public static FanReading FromHardwarePercent(
        double percent,
        string source,
        DateTimeOffset sampledAt,
        string? rpmUnavailableReason = null)
        => new(
            SensorReading.Unavailable(Units.Rpm, source, rpmUnavailableReason),
            Measurement.Clamp(percent, Measurement.MinCurveOutputPercent, Measurement.MaxCurveOutputPercent),
            null);

    /// <summary>仅当转速读数有效时转为过期，否则原样返回。</summary>
    public FanReading AsStaleIfValid(string? detail = null) => WithRpm(Rpm.AsStaleIfValid(detail));
}

/// <summary>CPU 四项监控快照。</summary>
/// <param name="Temperature">温度，摄氏度。</param>
/// <param name="FrequencyMhz">统一频率，内部单位为 MHz。</param>
/// <param name="PackagePowerWatt">封装功耗，瓦。</param>
/// <param name="Fan">风扇读数。</param>
/// <param name="FrequencyBasis">频率统计口径。界面必须注明。</param>
public sealed record CpuSnapshot(
    SensorReading Temperature,
    SensorReading FrequencyMhz,
    SensorReading PackagePowerWatt,
    FanReading Fan,
    FrequencyAggregation FrequencyBasis = FrequencyAggregation.AverageActive)
{
    /// <summary>构造一个全部不可用的快照。</summary>
    public static CpuSnapshot Unavailable(string source, string? detail = null) => new(
        SensorReading.Unavailable(Units.Celsius, source, detail),
        SensorReading.Unavailable(Units.Megahertz, source, detail),
        SensorReading.Unavailable(Units.Watt, source, detail),
        FanReading.Unavailable(source, detail));
}

/// <summary>GPU 四项监控快照。默认对应独显（RTX 5070 Ti Laptop）。</summary>
/// <param name="Temperature">温度，摄氏度。</param>
/// <param name="CoreFrequencyMhz">核心频率，内部单位为 MHz。</param>
/// <param name="PowerWatt">功耗，瓦。</param>
/// <param name="Fan">风扇读数。</param>
public sealed record GpuSnapshot(
    SensorReading Temperature,
    SensorReading CoreFrequencyMhz,
    SensorReading PowerWatt,
    FanReading Fan)
{
    /// <summary>独显是否处于休眠。用于避免轮询持续唤醒独显（任务书 §6.4）。</summary>
    public bool IsSleeping
        => Temperature.Status == ReadingStatus.Sleeping
           || CoreFrequencyMhz.Status == ReadingStatus.Sleeping
           || Fan.Rpm.Status == ReadingStatus.Sleeping;

    /// <summary>构造一个休眠快照。</summary>
    public static GpuSnapshot Sleeping(string source, string? detail = null) => new(
        SensorReading.Sleeping(Units.Celsius, source, detail),
        SensorReading.Sleeping(Units.Megahertz, source, detail),
        SensorReading.Sleeping(Units.Watt, source, detail),
        FanReading.Sleeping(source, detail));

    /// <summary>构造一个全部不可用的快照。</summary>
    public static GpuSnapshot Unavailable(string source, string? detail = null) => new(
        SensorReading.Unavailable(Units.Celsius, source, detail),
        SensorReading.Unavailable(Units.Megahertz, source, detail),
        SensorReading.Unavailable(Units.Watt, source, detail),
        FanReading.Unavailable(source, detail));
}

/// <summary>双风扇快照。</summary>
/// <param name="Cpu">CPU 风扇。</param>
/// <param name="Gpu">GPU 风扇。</param>
public sealed record FanSnapshot(FanReading Cpu, FanReading Gpu)
{
    /// <summary>按通道取值。</summary>
    public FanReading For(FanChannel channel) => channel == FanChannel.Cpu ? Cpu : Gpu;

    /// <summary>构造一个全部不可用的快照。</summary>
    public static FanSnapshot Unavailable(string source, string? detail = null)
        => new(FanReading.Unavailable(source, detail), FanReading.Unavailable(source, detail));
}

/// <summary>
/// 整机功耗读数及其测量口径。
/// </summary>
/// <remarks>
/// <b>三种口径不可混用、不可相加，更不得用 CPU+GPU 功耗之和顶替</b>（任务书 §6.4、§6.5）。
/// </remarks>
/// <param name="Kind">测量口径。</param>
/// <param name="Reading">读数值。</param>
public sealed record SystemPowerReading(PowerSourceKind Kind, SensorReading Reading)
{
    /// <summary>是否存在可信来源。</summary>
    public bool IsAvailable => Kind != PowerSourceKind.Unknown && Reading.IsUsable;

    /// <summary>构造一个明确不可用的整机功耗，不使用替代数据。</summary>
    public static SystemPowerReading Unavailable(string reason)
        => new(PowerSourceKind.Unknown, SensorReading.Unavailable(Units.Watt, "整机功耗", reason));

    /// <summary>仅当读数值有效时转为过期，口径保持不变。</summary>
    public SystemPowerReading AsStaleIfValid(string? detail = null)
        => this with { Reading = Reading.AsStaleIfValid(detail) };
}

/// <summary>扩展传感器条目。</summary>
/// <param name="Key">稳定标识，用于配置与订阅。</param>
/// <param name="DisplayName">界面显示名。</param>
/// <param name="Reading">读数。界面按能力折叠展示。</param>
public sealed record ExtendedSensor(string Key, string DisplayName, SensorReading Reading)
{
    /// <summary>仅当读数值有效时转为过期。</summary>
    public ExtendedSensor AsStaleIfValid(string? detail = null)
        => this with { Reading = Reading.AsStaleIfValid(detail) };
}

/// <summary>
/// 一次完整采样的结果。由单一共享采样任务产出，各窗口只订阅、不各自读取硬件。
/// </summary>
/// <param name="SampledAt">采样时刻。</param>
/// <param name="Cpu">CPU 快照。</param>
/// <param name="Gpu">GPU 快照。</param>
/// <param name="Fans">双风扇快照。</param>
/// <param name="SystemPower">整机功耗。</param>
/// <param name="Extended">扩展传感器。默认按需读取。</param>
public sealed record MonitoringSnapshot(
    DateTimeOffset SampledAt,
    CpuSnapshot Cpu,
    GpuSnapshot Gpu,
    FanSnapshot Fans,
    SystemPowerReading SystemPower,
    IReadOnlyList<ExtendedSensor> Extended)
{
    /// <summary>构造一个全部不可用的快照，用于适配尚未接入时的空状态。</summary>
    public static MonitoringSnapshot Empty(DateTimeOffset sampledAt, string source = "未适配")
        => new(
            sampledAt,
            CpuSnapshot.Unavailable(source),
            GpuSnapshot.Unavailable(source),
            FanSnapshot.Unavailable(source),
            SystemPowerReading.Unavailable("无可靠来源"),
            Array.Empty<ExtendedSensor>());

    /// <summary>
    /// 本次采样是否仍在有效期内。
    /// </summary>
    /// <param name="now">当前时刻。</param>
    /// <param name="maxAge">允许的最大年龄。刷新间隔策略由监控模块决定。</param>
    public bool IsFreshAt(DateTimeOffset now, TimeSpan maxAge) => now - SampledAt <= maxAge;

    /// <summary>
    /// 把超出有效期的读数标记为过期，用于采样任务超时处理。
    /// </summary>
    /// <param name="detail">过期原因。</param>
    /// <remarks>
    /// <b>只标记原本有效的读数。</b>已经不可用或处于休眠的读数保持原状态——
    /// 把「读不到」改写成「数据过期」会掩盖真实故障。
    /// 覆盖范围包含 CPU、GPU、<b>双风扇</b>、<b>整机功耗</b>与<b>扩展传感器</b>。
    /// </remarks>
    public MonitoringSnapshot MarkStale(string? detail = null) => this with
    {
        Cpu = Cpu with
        {
            Temperature = Cpu.Temperature.AsStaleIfValid(detail),
            FrequencyMhz = Cpu.FrequencyMhz.AsStaleIfValid(detail),
            PackagePowerWatt = Cpu.PackagePowerWatt.AsStaleIfValid(detail),
            Fan = Cpu.Fan.AsStaleIfValid(detail)
        },
        Gpu = Gpu with
        {
            Temperature = Gpu.Temperature.AsStaleIfValid(detail),
            CoreFrequencyMhz = Gpu.CoreFrequencyMhz.AsStaleIfValid(detail),
            PowerWatt = Gpu.PowerWatt.AsStaleIfValid(detail),
            Fan = Gpu.Fan.AsStaleIfValid(detail)
        },
        Fans = new FanSnapshot(Fans.Cpu.AsStaleIfValid(detail), Fans.Gpu.AsStaleIfValid(detail)),
        SystemPower = SystemPower.AsStaleIfValid(detail),
        Extended = Extended.Select(sensor => sensor.AsStaleIfValid(detail)).ToArray()
    };
}
