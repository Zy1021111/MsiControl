using System.Diagnostics;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 一次 RAPL 能耗采样：能量累加器读数、能量单位指数与读取时刻。
/// </summary>
/// <param name="EnergyUnits">封装能耗累加器的原始计数（32 位，会回绕）。</param>
/// <param name="EnergyUnitExponent">
/// 能量单位指数 <c>1 / 2^N</c> 焦耳，取自 <c>MSR_RAPL_POWER_UNIT[12:8]</c>。
/// </param>
/// <param name="Timestamp"><see cref="Stopwatch.GetTimestamp"/> 刻度，用于计算采样间隔。</param>
public readonly record struct RaplEnergySample(ulong EnergyUnits, int EnergyUnitExponent, long Timestamp);

/// <summary>
/// RAPL 能耗计数器读取接口（把内核通道与功耗换算解耦，便于单元测试）。
/// </summary>
public interface IRaplEnergyReader
{
    /// <summary>读取一次封装能耗累加器。</summary>
    /// <param name="sample">成功时为采样值。</param>
    /// <param name="error">失败原因；成功时为 <see langword="null"/>。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    bool TryReadPackageEnergy(out RaplEnergySample sample, out string? error);
}

/// <summary>
/// 基于 PawnIO <c>IntelMSR</c> 模块的 RAPL 能耗读取器。
/// </summary>
/// <remarks>
/// <para>
/// 只读取两个 MSR：<c>MSR_RAPL_POWER_UNIT</c>（0x606，取能量单位指数，首次读到后缓存）
/// 与 <c>MSR_PKG_ENERGY_STATUS</c>（0x611，封装能耗累加器）。
/// </para>
/// <para>
/// <b>为何取封装（Package）而不是核（PP0）：</b>界面上的「封装功耗」语义就是整颗 CPU 的
/// 封装级功耗，包含 IA 核心、核显与非核心部分；PP0 只覆盖核心，会系统性偏低。
/// </para>
/// <para><b>只读：</b>本读取器不写任何 MSR，功耗上限的写入不在监控模块职责内。</para>
/// </remarks>
public sealed class PawnIoRaplEnergyReader : IRaplEnergyReader
{
    /// <summary>数据来源说明（写入 <see cref="SensorReading.Source"/>）。</summary>
    public const string Description = "Intel RAPL（MSR 0x611）";

    private readonly IntelMsrAccess _access;
    private int? _energyUnitExponent;
    private string? _unitError;

    /// <summary>构造读取器。</summary>
    /// <param name="access">MSR 访问器（所有权不转移）。</param>
    public PawnIoRaplEnergyReader(IntelMsrAccess access)
        => _access = access ?? throw new ArgumentNullException(nameof(access));

    /// <inheritdoc />
    public bool TryReadPackageEnergy(out RaplEnergySample sample, out string? error)
    {
        sample = default;

        if (!EnsureEnergyUnitExponent(out error))
        {
            return false;
        }

        if (!_access.TryReadMsr(IntelMsrModule.PackageEnergyStatus, out var raw, out error))
        {
            return false;
        }

        var timestamp = Stopwatch.GetTimestamp();
        sample = new RaplEnergySample(raw & 0xFFFFFFFFu, _energyUnitExponent!.Value, timestamp);
        return true;
    }

    private bool EnsureEnergyUnitExponent(out string? error)
    {
        error = null;

        if (_energyUnitExponent is { } cached)
        {
            return true;
        }

        if (_unitError is not null)
        {
            error = _unitError;
            return false;
        }

        if (!_access.TryReadMsr(IntelMsrModule.RaplPowerUnit, out var units, out var readError))
        {
            _unitError = readError;
            error = readError;
            return false;
        }

        var exponent = (int)((units >> 8) & 0x1F);
        if (exponent is <= 0 or > 24)
        {
            // 指数 0 表示 1 J 分辨率（不可能用于封装功耗），超出 24 说明读到的是无效值
            // （例如该平台未启用 RAPL）。宁可如实报不可用，也不给出量级错误的瓦数。
            _unitError =
                $"RAPL 能量单位指数异常（MSR 0x606 = 0x{units:X16}，指数 {exponent}），" +
                "该平台的 RAPL 计数不可用于功耗换算。";
            error = _unitError;
            return false;
        }

        _energyUnitExponent = exponent;
        return true;
    }
}

/// <summary>
/// 由 Intel RAPL 封装能耗累加器换算 CPU 封装功耗。
/// </summary>
/// <remarks>
/// <para>
/// <b>换算口径：</b>功率 = ΔE / Δt，ΔE 由 32 位累加器做无符号差分（自动处理回绕），
/// Δt 取两次读取的单调时钟差。这是 Intel RAPL 的标准用法（HWiNFO / LibreHardwareMonitor 同款），
/// 不是估算模型，因此读数可直接标为「封装功耗」。
/// </para>
/// <para>
/// <b>首次采样的处理：</b>没有基线时无法做差分，若直接返回「不可用」，用户会在启动后
/// 看到一秒钟的红字。这里改为在首帧内做一次短间隔测量（<see cref="FirstSampleInterval"/>），
/// 立即给出一个有效值；该代价只发生一次。
/// </para>
/// <para>
/// <b>不参与能力门控：</b>RAPL 是 Intel 平台的通用计数器（对应 MSR 白名单），
/// 不是 MSI 私有能力；可用性由读取结果本身表达。
/// </para>
/// </remarks>
public sealed class RaplCpuPowerSource : ICpuPowerSource
{
    /// <summary>封装功耗的合理上限（瓦）。超过即判定为计数器异常。</summary>
    public const double MaxPlausibleWatt = 400;

    /// <summary>首次采样时用于建立差分基线的短间隔。</summary>
    /// <remarks>
    /// 200 ms 的窗口会把瞬时突发也算进去（实测首帧 39.7 W 而稳态 27 W），
    /// 取 400 ms 换取更接近真实均值的首帧读数；仍远小于单源 2 秒超时。
    /// </remarks>
    public static readonly TimeSpan FirstSampleInterval = TimeSpan.FromMilliseconds(400);

    private readonly IRaplEnergyReader _reader;
    private readonly TimeSpan _firstSampleInterval;
    private readonly Action<TimeSpan> _delay;
    private readonly object _gate = new();

    private RaplEnergySample? _previous;

    /// <summary>构造数据源。</summary>
    /// <param name="reader">能耗计数器读取器。</param>
    /// <param name="firstSampleInterval">首次采样基线间隔；为 <see langword="null"/> 时用默认值。</param>
    /// <param name="delay">等待委托（测试注入点）；为 <see langword="null"/> 时使用 <see cref="Thread.Sleep(TimeSpan)"/>。</param>
    public RaplCpuPowerSource(
        IRaplEnergyReader reader,
        TimeSpan? firstSampleInterval = null,
        Action<TimeSpan>? delay = null)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _firstSampleInterval = firstSampleInterval ?? FirstSampleInterval;
        _delay = delay ?? Thread.Sleep;
    }

    /// <inheritdoc />
    public string Key => "cpu.power.rapl";

    /// <inheritdoc />
    public string Description => PawnIoRaplEnergyReader.Description;

    /// <inheritdoc />
    public Task<SensorReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadFirstSample(out var current, out var error))
        {
            return Task.FromResult(Unavailable(error));
        }

        lock (_gate)
        {
            var previous = _previous;
            if (previous is null)
            {
                // 首次：先读一次做基线，短暂等待后再读一次，用这次短间隔直接得出结论。
                previous = current;
                _delay(_firstSampleInterval);

                if (!_reader.TryReadPackageEnergy(out current, out error))
                {
                    _previous = null;
                    return Task.FromResult(Unavailable(error));
                }
            }

            var elapsed = Elapsed(previous.Value, current);
            if (elapsed <= TimeSpan.Zero)
            {
                // 时钟分辨率不足（同一刻度内完成两次读取）时不下结论，保留基线等下一拍。
                _previous = current;
                return Task.FromResult(Unavailable(
                    "RAPL 采样间隔为零，本次无法换算功率（下一采样周期自动重试）。"));
            }

            _previous = current;

            var watt = EnergyDeltaWatt(previous.Value, current, elapsed);
            if (watt is < 0 or > MaxPlausibleWatt)
            {
                return Task.FromResult(Unavailable(
                    $"RAPL 换算结果 {watt:0.#} W 超出合理区间（0–{MaxPlausibleWatt:0} W），已按不可用处理。"));
            }

            return Task.FromResult(SensorReading.Valid(
                watt,
                Units.Watt,
                $"{Description}:PKG_ENERGY_STATUS",
                DateTimeOffset.Now));
        }
    }

    /// <summary>读取一次能耗采样；失败时给出可读原因。</summary>
    private bool TryReadFirstSample(out RaplEnergySample sample, out string? error)
        => _reader.TryReadPackageEnergy(out sample, out error);

    private static TimeSpan Elapsed(RaplEnergySample previous, RaplEnergySample current)
    {
        var ticks = current.Timestamp - previous.Timestamp;
        return ticks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);
    }

    private static double EnergyDeltaWatt(RaplEnergySample previous, RaplEnergySample current, TimeSpan elapsed)
    {
        // 32 位累加器回绕：无符号差分天然正确（例如 0xFFFFFF00 → 0x00000100 得 512）。
        var delta = (uint)(current.EnergyUnits - previous.EnergyUnits);
        var joules = delta / (double)(1 << current.EnergyUnitExponent);
        return joules / elapsed.TotalSeconds;
    }

    private static SensorReading Unavailable(string? reason) => SensorReading.Unavailable(
        Units.Watt,
        PawnIoRaplEnergyReader.Description,
        string.IsNullOrWhiteSpace(reason) ? "RAPL 能耗计数器本次读取失败。" : reason);
}
