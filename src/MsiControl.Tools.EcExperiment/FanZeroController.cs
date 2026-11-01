using System.Diagnostics;
using System.Globalization;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Tools.EcExperiment;

/// <summary>
/// 基于 PawnIO + ACPI EC 的风扇 PWM 控制器：目标是把 CPU/GPU 占空比写到自己想要的值，
/// 并<b>验证</b>该值是否真的被固件接受（而不是只报「写入成功」）。
/// </summary>
/// <remarks>
/// <para>
/// <b>通道</b>：PawnIO 驱动（<c>\\.\GlobalRoot\Device\PawnIO</c>）→ 内核模块
/// <c>LpcACPIEC</c>（仅导出 <c>ioctl_pio_read</c> / <c>ioctl_pio_write</c>，
/// 端口白名单 0x62/0x66）→ <see cref="MsiEcPort"/> 的 RD_EC(0x80)/WR_EC(0x81) 事务 →
/// EC 的 0x71（CPU）/ 0x89（GPU）占空比寄存器。模块本身不做任何风扇语义，
/// 它只是一条字节级 I/O 通道；<b>所有策略（曲线、模式、校验、回滚）都在用户态</b>。
/// </para>
/// <para>
/// <b>为什么必须周期写入</b>：MS-15M3 固件在 advanced 模式下会把占空比改写回自身曲线值。
/// 实测回写周期：CPU ≤1 s、GPU ≤100 ms（HWE §23.3、§36.4）。因此维持一个非固件值时，
/// 写入周期必须显著短于回写周期，且每次写后立即读回核对。
/// </para>
/// <para>
/// <b>安全边界</b>：本类不做任何「写入后不回滚」的事。进入手动模式前记录基线
/// （0xD4 原值 + 0x71/0x89 原值），任何退出路径（正常、异常、Ctrl+C、温度看门狗）
/// 都会恢复基线；写入 0 还需要额外的显式开关，因为实测写 0 会让固件进入持续回写 0 的
/// 状态且无法用软件恢复（HWE §36.4）。
/// </para>
/// </remarks>
internal sealed class FanZeroController : IDisposable
{
    /// <summary>风扇模式寄存器：advanced（固件让位，允许软件写占空比）。</summary>
    public const byte ManualMode = 0x8D;

    /// <summary>风扇模式寄存器：auto（交还固件调速）。</summary>
    public const byte AutoMode = 0x0D;

    /// <summary>CPU 占空比寄存器。</summary>
    public const byte CpuDuty = 0x71;

    /// <summary>GPU 占空比寄存器。</summary>
    public const byte GpuDuty = 0x89;

    /// <summary>CPU 温度寄存器。</summary>
    public const byte CpuTemperature = 0x68;

    /// <summary>GPU 温度寄存器。</summary>
    public const byte GpuTemperature = 0x80;

    /// <summary>温度看门狗默认阈值（摄氏度）：超过即强制回滚。</summary>
    public const int DefaultTemperatureLimitC = 88;

    /// <summary>保持性采样的默认周期（毫秒）。必须明显短于固件回写周期。</summary>
    public const int SamplePeriodMs = 100;

    private readonly PawnIoEcChannel _channel;
    private readonly object _gate = new();

    private byte _baselineMode;
    private byte _baselineCpuDuty;
    private byte _baselineGpuDuty;
    private bool _baselineCaptured;
    private bool _manualEntered;
    private int _temperatureLimitC = DefaultTemperatureLimitC;
    private volatile bool _watchdogTripped;
    private string? _watchdogReason;

    public FanZeroController(PawnIoEcChannel channel)
        => _channel = channel ?? throw new ArgumentNullException(nameof(channel));

    /// <summary>看门狗是否已触发（触发后所有写入都会拒绝）。</summary>
    public bool WatchdogTripped => _watchdogTripped;

    /// <summary>看门狗触发原因。</summary>
    public string? WatchdogReason => _watchdogReason;

    /// <summary>读取一个 EC 寄存器。EC 单读瞬时超时率约 20%，这里带 3 次退避重试。</summary>
    public byte Read(byte address)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return _channel.Port.ReadRegister(address);
            }
            catch (EcTimeoutException) when (attempt < 3)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>写入一个 EC 寄存器并立即读回。</summary>
    public bool TryWrite(byte address, byte value, out byte readBack)
    {
        if (_watchdogTripped)
        {
            readBack = 0;
            return false;
        }

        lock (_gate)
        {
            try
            {
                _channel.Port.WriteRegister(address, value);
            }
            catch (EcTimeoutException)
            {
                readBack = 0;
                return false;
            }
        }

        readBack = Read(address);
        return readBack == value;
    }

    /// <summary>记录基线状态（进入手动模式之前调用一次）。</summary>
    public void CaptureBaseline()
    {
        _baselineMode = Read(0xD4);
        _baselineCpuDuty = Read(CpuDuty);
        _baselineGpuDuty = Read(GpuDuty);
        _baselineCaptured = true;
    }

    /// <summary>切到 advanced 模式并校验读回；失败时不进入手动模式。</summary>
    public bool EnterManual(out string message)
    {
        if (!_baselineCaptured)
        {
            CaptureBaseline();
        }

        var before = Read(0xD4);
        if (!TryWrite(0xD4, ManualMode, out var readBack))
        {
            message = $"切 advanced 失败：写 0xD4=0x{ManualMode:X2}，读回 0x{readBack:X2}（原值 0x{before:X2}）。";
            return false;
        }

        _manualEntered = true;
        message = $"已进入 advanced：0xD4 0x{before:X2} → 0x{readBack:X2}。";
        return true;
    }

    /// <summary>
    /// 以固定周期把目标占空比写到两个通道，并统计「读回等于目标」的保持率。
    /// </summary>
    /// <param name="percent">目标占空比（0–100）。</param>
    /// <param name="seconds">保持时长。</param>
    /// <param name="periodMs">写入周期；默认 100 ms（短于实测的 GPU 100 ms 回写）。</param>
    /// <param name="cpuPercent">仅用于展示：CPU 通道是否使用不同目标；null 表示与 <paramref name="percent"/> 相同。</param>
    public HoldReport Hold(byte percent, double seconds, int periodMs = SamplePeriodMs, byte? cpuPercent = null)
    {
        var cpuTarget = cpuPercent ?? percent;
        var deadline = DateTimeOffset.Now.AddSeconds(seconds);
        var total = 0;
        var cpuHeld = 0;
        var gpuHeld = 0;
        var maxCpuTemp = 0;
        var maxGpuTemp = 0;
        var firstCpuMismatch = (string?)null;
        var firstGpuMismatch = (string?)null;
        var stopwatch = Stopwatch.StartNew();

        while (DateTimeOffset.Now < deadline)
        {
            if (!GuardTemperature())
            {
                break;
            }

            var stamp = DateTimeOffset.Now;
            TryWrite(CpuDuty, cpuTarget, out var cpuRead);
            TryWrite(GpuDuty, percent, out var gpuRead);

            total++;
            if (cpuRead == cpuTarget)
            {
                cpuHeld++;
            }
            else
            {
                firstCpuMismatch ??= $"{stamp:HH:mm:ss.fff} 写 {cpuTarget} 读回 {cpuRead}";
            }

            if (gpuRead == percent)
            {
                gpuHeld++;
            }
            else
            {
                firstGpuMismatch ??= $"{stamp:HH:mm:ss.fff} 写 {percent} 读回 {gpuRead}";
            }

            var cpuTemp = Read(CpuTemperature);
            var gpuTemp = Read(GpuTemperature);
            maxCpuTemp = Math.Max(maxCpuTemp, cpuTemp);
            maxGpuTemp = Math.Max(maxGpuTemp, gpuTemp);

            var remaining = periodMs - (int)stopwatch.ElapsedMilliseconds % periodMs;
            Thread.Sleep(Math.Clamp(remaining, 1, periodMs));
        }

        return new HoldReport(
            Target: percent,
            CpuTarget: cpuTarget,
            Seconds: seconds,
            TotalSamples: total,
            CpuHeldSamples: cpuHeld,
            GpuHeldSamples: gpuHeld,
            MaxCpuTemperatureC: maxCpuTemp,
            MaxGpuTemperatureC: maxGpuTemp,
            FirstCpuMismatch: firstCpuMismatch,
            FirstGpuMismatch: firstGpuMismatch,
            Watchdog: _watchdogTripped,
            WatchdogReason: _watchdogReason);
    }

    /// <summary>
    /// 判断当前 EC 是否处于「固件持续回写 0」的污染状态。
    /// </summary>
    /// <remarks>
    /// 判据：把一个已知非 0 值写入 CPU 占空比，读回一致后等待 1.5 秒再读；
    /// 若变成 0 且写回非 0 无效，说明固件已把自身曲线固化为 0，需要重启系统恢复
    /// （HWE §36.4 实测）。此状态下不允许做任何「停转」判定，因为结论不可信。
    /// </remarks>
    public PollutionCheck CheckFirmwarePollution()
    {
        try
        {
            var original = Read(CpuDuty);
            var probe = (byte)(original == 20 ? 40 : 20);
            TryWrite(CpuDuty, probe, out var immediate);
            Thread.Sleep(1500);
            var after = Read(CpuDuty);
            TryWrite(CpuDuty, original, out _);

            var polluted = immediate == probe && after == 0 && probe != 0;
            return new PollutionCheck(
                Polluted: polluted,
                Original: original,
                Probe: probe,
                ImmediateReadBack: immediate,
                After1500Ms: after,
                Error: null);
        }
        catch (Exception exception)
        {
            return new PollutionCheck(false, 0, 0, 0, 0, exception.Message);
        }
    }

    /// <summary>把基线写回 EC 并交还固件调速。可重复调用。</summary>
    public string Restore()
    {
        string modeResult;
        try
        {
            if (_baselineCaptured)
            {
                WriteBypassingWatchdog(CpuDuty, _baselineCpuDuty);
                WriteBypassingWatchdog(GpuDuty, _baselineGpuDuty);
            }

            var target = _manualEntered && _baselineCaptured ? _baselineMode : AutoMode;
            WriteBypassingWatchdog(0xD4, target);
            var readBack = Read(0xD4);
            modeResult = $"0xD4 写 0x{target:X2}，读回 0x{readBack:X2}（{(readBack == target ? "一致" : "不一致")}）";
        }
        catch (Exception exception)
        {
            modeResult = $"恢复过程异常：{exception.Message}";
        }

        _manualEntered = false;
        return modeResult;
    }

    /// <summary>
    /// 不受看门狗限制的写入：<b>只给回滚路径用</b>。
    /// </summary>
    /// <remarks>
    /// 看门狗触发后所有常规写入都会被拒绝，但「恢复基线」必须仍然可执行——
    /// 否则温度超限时反而无法把控制权交还固件，安全机制会变成故障放大。
    /// </remarks>
    private void WriteBypassingWatchdog(byte address, byte value)
    {
        lock (_gate)
        {
            try
            {
                _channel.Port.WriteRegister(address, value);
            }
            catch (EcTimeoutException)
            {
                // 恢复路径尽力而为：单次超时不重试，下一项继续。
            }
        }
    }

    /// <summary>温度看门狗：超过阈值立即置位，调用方的循环会在下一拍退出并回滚。</summary>
    public bool GuardTemperature(int? limitC = null)
    {
        if (_watchdogTripped)
        {
            return false;
        }

        var limit = limitC ?? _temperatureLimitC;
        var cpu = Read(CpuTemperature);
        var gpu = Read(GpuTemperature);
        if (cpu >= limit || gpu >= limit)
        {
            _watchdogTripped = true;
            _watchdogReason = $"温度看门狗：CPU {cpu} °C / GPU {gpu} °C 达到阈值 {limit} °C。";
            return false;
        }

        return true;
    }

    /// <summary>设置温度阈值。</summary>
    public void SetTemperatureLimit(int celsius) => _temperatureLimitC = Math.Clamp(celsius, 60, 100);

    /// <inheritdoc />
    public void Dispose() => Restore();

    /// <summary>保持性统计结果。</summary>
    internal sealed record HoldReport(
        byte Target,
        byte CpuTarget,
        double Seconds,
        int TotalSamples,
        int CpuHeldSamples,
        int GpuHeldSamples,
        int MaxCpuTemperatureC,
        int MaxGpuTemperatureC,
        string? FirstCpuMismatch,
        string? FirstGpuMismatch,
        bool Watchdog,
        string? WatchdogReason)
    {
        public double CpuHoldRatio => TotalSamples == 0 ? 0 : (double)CpuHeldSamples / TotalSamples;

        public double GpuHoldRatio => TotalSamples == 0 ? 0 : (double)GpuHeldSamples / TotalSamples;
    }

    /// <summary>污染检测结果。</summary>
    /// <param name="Polluted">是否处于「固件持续回写 0」的污染状态。</param>
    /// <param name="Original">写入前 CPU 占空比。</param>
    /// <param name="Probe">用于探测的写入值。</param>
    /// <param name="ImmediateReadBack">写入后立即读回值。</param>
    /// <param name="After1500Ms">1.5 秒后的读回值。</param>
    /// <param name="Error">探测过程异常（非空表示结论不可用）。</param>
    internal sealed record PollutionCheck(
        bool Polluted,
        byte Original,
        byte Probe,
        byte ImmediateReadBack,
        byte After1500Ms,
        string? Error);

    /// <summary>格式化为一行摘要。</summary>
    public static string Describe(HoldReport report, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        return string.Create(
            culture,
            $"目标 CPU {report.CpuTarget}% / GPU {report.Target}%，{report.Seconds:0.#} 秒内 {report.TotalSamples} 拍："
            + $"CPU 保持率 {report.CpuHoldRatio:P0}，GPU 保持率 {report.GpuHoldRatio:P0}；"
            + $"峰值温度 CPU {report.MaxCpuTemperatureC} °C / GPU {report.MaxGpuTemperatureC} °C");
    }
}
