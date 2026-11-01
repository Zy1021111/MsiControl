using System.Diagnostics;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Sources;
using Xunit;

namespace MsiControl.Monitoring.Tests;

/// <summary>可编程的 RAPL 能耗计数器替身。</summary>
/// <remarks>
/// 时间由测试显式推进（<see cref="Advance"/>），因此功率换算的断言不依赖真实等待时长。
/// 功率源首帧的短间隔等待通过构造参数注入为「推进虚拟时钟」。
/// </remarks>
internal sealed class ScriptedRaplReader : IRaplEnergyReader
{
    private readonly Queue<(ulong Energy, string? Error)> _script = new();

    /// <summary>能量单位指数，默认 14（1/16384 J，本机实测值）。</summary>
    public int EnergyUnitExponent { get; set; } = 14;

    /// <summary>虚拟时钟（秒）。</summary>
    public double Seconds { get; private set; }

    /// <summary>被调用的次数。</summary>
    public int Calls { get; private set; }

    /// <summary>按虚拟时钟生成的时刻。</summary>
    public long Tick => (long)(Seconds * Stopwatch.Frequency);

    /// <summary>把焦耳换算成本替身的累加器计数。</summary>
    public ulong Joules(double joules) => (ulong)Math.Round(joules * (1 << EnergyUnitExponent));

    /// <summary>排入一次读取结果。</summary>
    public void Enqueue(ulong energyUnits, string? error = null) => _script.Enqueue((energyUnits, error));

    /// <summary>推进虚拟时钟。</summary>
    public void Advance(double seconds) => Seconds += seconds;

    /// <inheritdoc />
    public bool TryReadPackageEnergy(out RaplEnergySample sample, out string? error)
    {
        Calls++;
        if (_script.Count == 0)
        {
            throw new InvalidOperationException("采样脚本已用尽，测试用例与实现调用次数不一致。");
        }

        var (energy, scriptedError) = _script.Dequeue();
        sample = new RaplEnergySample(energy, EnergyUnitExponent, Tick);
        error = scriptedError;
        return scriptedError is null;
    }
}

/// <summary>
/// RAPL 封装功耗数据源的换算与降级行为。
/// </summary>
/// <remarks>
/// 覆盖点来自实机验证（2026-10-28：MSR 0x606 = 0x…0E03，即 1/16384 J；
/// 0x611 差分得 21–26 W）与三条易错路径：首帧无基线、累加器 32 位回绕、换算结果超区间。
/// </remarks>
public sealed class RaplCpuPowerSourceTests
{
    /// <summary>首帧用一次短间隔测量直接给出有效值，而不是先显示一帧「不可用」。</summary>
    [Fact]
    public async Task FirstSample_MeasuresShortIntervalAndProducesValue()
    {
        var reader = new ScriptedRaplReader();
        reader.Enqueue(0);
        reader.Enqueue(reader.Joules(6)); // 6 J / 0.2 s = 30 W

        var source = new RaplCpuPowerSource(
            reader,
            TimeSpan.FromMilliseconds(200),
            duration => reader.Advance(duration.TotalSeconds));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, reading.Status);
        Assert.Equal(Units.Watt, reading.Unit);
        Assert.Equal(30d, reading.Value!.Value, 6);
        Assert.Equal(2, reader.Calls);
    }

    /// <summary>后续采样按真实采样间隔换算（ΔE/Δt）。</summary>
    [Fact]
    public async Task SubsequentSample_UsesActualInterval()
    {
        var reader = new ScriptedRaplReader();
        reader.Enqueue(1000);
        reader.Enqueue(1000 + reader.Joules(6));

        var source = new RaplCpuPowerSource(
            reader,
            TimeSpan.FromMilliseconds(200),
            duration => reader.Advance(duration.TotalSeconds));

        _ = await source.ReadAsync(CancellationToken.None);

        // 第二拍：间隔 1 秒，封装能耗 25 J → 25 W。
        reader.Advance(1.0);
        reader.Enqueue(1000 + reader.Joules(6) + reader.Joules(25));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, reading.Status);
        Assert.Equal(25d, reading.Value!.Value, 6);
    }

    /// <summary>32 位累加器回绕必须按无符号差分处理，不得出现负功率或巨大跳变。</summary>
    [Fact]
    public async Task EnergyCounterWrap_IsHandledAsUnsignedDelta()
    {
        var reader = new ScriptedRaplReader();
        reader.Enqueue(0xFFFFF000);
        reader.Enqueue(0xFFFFF000);

        var source = new RaplCpuPowerSource(
            reader,
            TimeSpan.FromMilliseconds(200),
            duration => reader.Advance(duration.TotalSeconds));

        _ = await source.ReadAsync(CancellationToken.None);

        // 0xFFFFF000 → 0x0000F000 的增量是 0x10000 = 65536 计数 = 4 J；间隔 1 秒 → 4 W。
        reader.Advance(1.0);
        reader.Enqueue(0x0000F000);

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, reading.Status);
        Assert.Equal(4d, reading.Value!.Value, 6);
    }

    /// <summary>通道不可用时如实给出原因，不返回 0 W。</summary>
    [Fact]
    public async Task ReaderFailure_ReturnsUnavailableWithReason()
    {
        var reader = new ScriptedRaplReader();
        reader.Enqueue(0, "Intel MSR 通道建立失败：模块被驱动拒绝。");

        var source = new RaplCpuPowerSource(reader);
        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, reading.Status);
        Assert.False(reading.Value.HasValue);
        Assert.Contains("Intel MSR 通道建立失败", reading.Detail, StringComparison.Ordinal);
    }

    /// <summary>换算结果超出合理区间时不展示该瓦数（防止计数器异常被当作真实功耗）。</summary>
    [Fact]
    public async Task ImplausibleWatt_ReturnsUnavailable()
    {
        var reader = new ScriptedRaplReader();
        reader.Enqueue(0);
        reader.Enqueue(reader.Joules(1000 * 0.2)); // 1000 W

        var source = new RaplCpuPowerSource(
            reader,
            TimeSpan.FromMilliseconds(200),
            duration => reader.Advance(duration.TotalSeconds));

        var reading = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, reading.Status);
        Assert.Contains("超出合理区间", reading.Detail, StringComparison.Ordinal);
    }

    /// <summary>采样间隔为零时不下结论（保留基线，下一拍重新换算）。</summary>
    [Fact]
    public async Task ZeroInterval_ReturnsUnavailableWithoutLosingBaseline()
    {
        var reader = new ScriptedRaplReader();
        reader.Enqueue(0);
        reader.Enqueue(0);
        reader.Enqueue(0);

        var source = new RaplCpuPowerSource(
            reader,
            TimeSpan.FromMilliseconds(200),
            duration => reader.Advance(duration.TotalSeconds));

        // 第一拍正常建立基线（ΔE=0 → 0 W，但有效）。
        var first = await source.ReadAsync(CancellationToken.None);
        Assert.Equal(ReadingStatus.Valid, first.Status);

        // 第二拍虚拟时钟不前进：间隔为 0，必须降级而不是给出 ∞ 或 0 W 的假读数。
        reader.Enqueue(reader.Joules(1));
        var second = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, second.Status);
        Assert.Contains("采样间隔为零", second.Detail, StringComparison.Ordinal);
    }
}

/// <summary>
/// CPU 封装功耗多源回落链的顺序与降级文案。
/// </summary>
public sealed class CompositeCpuPowerSourceTests
{
    private static SensorReading Valid(double watt) =>
        SensorReading.Valid(watt, Units.Watt, "替身", DateTimeOffset.Now);

    private static SensorReading Unavailable(string reason) =>
        SensorReading.Unavailable(Units.Watt, "替身", reason);

    /// <summary>首选可用时不询问兜底来源。</summary>
    [Fact]
    public async Task PrimaryUsable_DoesNotTouchFallback()
    {
        var primaryCalls = 0;
        var secondaryCalls = 0;
        var chain = new CompositeCpuPowerSource(
            new FakePowerSource(() =>
            {
                primaryCalls++;
                return Valid(42);
            }),
            new FakePowerSource(() =>
            {
                secondaryCalls++;
                return Valid(7);
            }));

        var reading = await chain.ReadAsync(CancellationToken.None);

        Assert.Equal(42d, reading.Value!.Value, 6);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, secondaryCalls);
    }

    /// <summary>首选不可用时回落到兜底来源。</summary>
    [Fact]
    public async Task PrimaryUnavailable_FallsBack()
    {
        var chain = new CompositeCpuPowerSource(
            new FakePowerSource(() => Unavailable("RAPL 不可读")),
            new FakePowerSource(() => Valid(15)));

        var reading = await chain.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, reading.Status);
        Assert.Equal(15d, reading.Value!.Value, 6);
    }

    /// <summary>全部来源不可用时，原因按顺序拼接，便于定位是哪条通道断了。</summary>
    [Fact]
    public async Task AllUnavailable_JoinsReasonsInOrder()
    {
        var chain = new CompositeCpuPowerSource(
            new FakePowerSource(() => Unavailable("RAPL 原因")),
            new FakePowerSource(() => Unavailable("Afterburner 原因")));

        var reading = await chain.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, reading.Status);
        Assert.NotNull(reading.Detail);
        var raplIndex = reading.Detail!.IndexOf("RAPL 原因", StringComparison.Ordinal);
        var afterburnerIndex = reading.Detail.IndexOf("Afterburner 原因", StringComparison.Ordinal);
        Assert.True(raplIndex >= 0 && afterburnerIndex > raplIndex);
    }
}
