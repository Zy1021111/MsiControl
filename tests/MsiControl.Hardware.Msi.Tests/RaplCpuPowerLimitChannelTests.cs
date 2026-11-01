using MsiControl.Hardware.Msi.Power;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests;

/// <summary>可编程的 MSR 替身：按编号提供读值，并记录写入。</summary>
internal sealed class FakeMsrAccess : IMsrAccess
{
    private readonly Dictionary<uint, ulong> _values = new();

    /// <summary>读取失败时的错误文案；为 null 表示读取正常。</summary>
    public string? ReadError { get; set; }

    /// <summary>写入失败时的错误文案；为 null 表示写入正常。</summary>
    public string? WriteError { get; set; }

    /// <summary>写入是否被硬件忽略（模拟「写了但没生效」）。</summary>
    public bool IgnoreWrites { get; set; }

    /// <summary>写入次数。</summary>
    public int WriteCount { get; private set; }

    /// <summary>最近一次写入的值（按编号）。</summary>
    public Dictionary<uint, ulong> Writes { get; } = new();

    public void Set(uint index, ulong value) => _values[index] = value;

    public ulong Get(uint index) => _values.TryGetValue(index, out var value) ? value : 0;

    public bool TryReadMsr(uint index, out ulong value, out string? error)
    {
        if (ReadError is not null)
        {
            value = 0;
            error = ReadError;
            return false;
        }

        value = Get(index);
        error = null;
        return true;
    }

    public bool TryWriteMsr(uint index, ulong value, out string? error)
    {
        WriteCount++;
        Writes[index] = value;

        if (WriteError is not null)
        {
            error = WriteError;
            return false;
        }

        if (!IgnoreWrites)
        {
            _values[index] = value;
        }

        error = null;
        return true;
    }
}

/// <summary>
/// RAPL 封装功耗墙（MSR 0x610）的位布局与写入纪律。
/// </summary>
/// <remarks>
/// 常量取自本机实测（2026-10-28，HWE §26）：
/// <c>MSR_RAPL_POWER_UNIT = 0x000A0E03</c>（功率粒度 0.125 W），
/// <c>MSR_PKG_POWER_LIMIT = 0x0042823000DF81E0</c>（PL1 60 W / PL2 70 W，未锁定）。
/// </remarks>
public sealed class RaplCpuPowerLimitChannelTests
{
    private const ulong RaplPowerUnitValue = 0x000A0E03;
    private const ulong ObservedLimits = 0x0042823000DF81E0;

    private static FakeMsrAccess NewAccess(ulong limits = ObservedLimits)
    {
        var access = new FakeMsrAccess();
        access.Set(RaplCpuPowerLimitChannel.RaplPowerUnit, RaplPowerUnitValue);
        access.Set(RaplCpuPowerLimitChannel.PackagePowerLimit, limits);
        return access;
    }

    [Fact]
    public void TryRead_DecodesLongAndShortTermFromObservedRegister()
    {
        var channel = new RaplCpuPowerLimitChannel(NewAccess());

        Assert.True(channel.TryRead(out var state, out var error));
        Assert.Null(error);
        Assert.Equal(60d, state.LongTermWatt, 3);
        Assert.Equal(70d, state.ShortTermWatt, 3);
        Assert.True(state.LongTermEnabled);
        Assert.True(state.ShortTermEnabled);
        Assert.False(state.Locked);
        Assert.Equal(0.125d, state.GranularityWatt, 6);
    }

    [Fact]
    public void FirstRead_BecomesNativeBaseline()
    {
        var channel = new RaplCpuPowerLimitChannel(NewAccess());

        Assert.Null(channel.NativeBaseline);
        _ = channel.TryRead(out _, out _);

        Assert.NotNull(channel.NativeBaseline);
        Assert.Equal(60d, channel.NativeBaseline!.LongTermWatt, 3);
        Assert.Equal(70d, channel.NativeBaseline.ShortTermWatt, 3);
    }

    [Fact]
    public void TryWrite_OnlyChangesValueFields_AndKeepsEnableAndTimeWindowBits()
    {
        var access = NewAccess();
        var channel = new RaplCpuPowerLimitChannel(access);

        Assert.True(channel.TryWrite(35m, 35m, out var readBack, out var error));
        Assert.Null(error);
        Assert.Equal(35d, readBack.LongTermWatt, 3);
        Assert.Equal(35d, readBack.ShortTermWatt, 3);

        var written = access.Get(RaplCpuPowerLimitChannel.PackagePowerLimit);
        var original = ObservedLimits;

        // 使能位（15/47）必须仍为 1；clamp 位（16/48）与时间窗（23:17 / 55:49）必须原样保留。
        Assert.NotEqual(0UL, written & (1UL << 15));
        Assert.NotEqual(0UL, written & (1UL << 47));
        Assert.Equal(original & (1UL << 16), written & (1UL << 16));
        Assert.Equal(original & (1UL << 48), written & (1UL << 48));
        Assert.Equal(original & (0x7FUL << 17), written & (0x7FUL << 17));
        Assert.Equal(original & (0x7FUL << 49), written & (0x7FUL << 49));
        Assert.Equal(0UL, written & (1UL << 63)); // 锁定位不被我们置位
    }

    [Fact]
    public void TryWrite_RoundsToGranularity()
    {
        var access = NewAccess();
        var channel = new RaplCpuPowerLimitChannel(access);

        // 0.125 W 粒度：37.4 W 应落到 37.375 W（299 计数 × 0.125）。
        Assert.True(channel.TryWrite(37.4m, 37.4m, out var readBack, out _));
        Assert.Equal(37.375d, readBack.LongTermWatt, 3);
    }

    [Fact]
    public void TryWrite_RefusesWhenLockBitIsSet()
    {
        // bit63 置位 = 固件已锁定，写入不会生效，必须直接拒绝而不是假装成功。
        var access = NewAccess(ObservedLimits | (1UL << 63));
        var channel = new RaplCpuPowerLimitChannel(access);

        Assert.False(channel.TryWrite(35m, 35m, out _, out var error));
        Assert.Contains("锁定位", error, StringComparison.Ordinal);
        Assert.Equal(0, access.WriteCount);
    }

    [Fact]
    public void TryWrite_ReportsMismatchViaReadBack_NotByIoctlSuccess()
    {
        // IOCTL 成功但寄存器没变（模拟固件忽略写入）：读回值必须与请求不同。
        var access = NewAccess();
        access.IgnoreWrites = true;
        var channel = new RaplCpuPowerLimitChannel(access);

        Assert.True(channel.TryWrite(35m, 35m, out var readBack, out _));
        Assert.Equal(60d, readBack.LongTermWatt, 3);
        Assert.Equal(70d, readBack.ShortTermWatt, 3);
    }

    [Fact]
    public void TryWrite_PropagatesDriverFailure()
    {
        var access = NewAccess();
        access.WriteError = "驱动拒绝";
        var channel = new RaplCpuPowerLimitChannel(access);

        Assert.False(channel.TryWrite(35m, 35m, out _, out var error));
        Assert.Contains("驱动拒绝", error, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRead_PropagatesChannelFailure()
    {
        var access = NewAccess();
        access.ReadError = "PawnIO 设备不可达";
        var channel = new RaplCpuPowerLimitChannel(access);

        Assert.False(channel.TryRead(out _, out var error));
        Assert.Contains("PawnIO", error, StringComparison.Ordinal);
        Assert.False(channel.IsAvailable(out var reason));
        Assert.Contains("PawnIO", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void IsAvailable_TrueWhenPowerUnitReadable()
    {
        var channel = new RaplCpuPowerLimitChannel(NewAccess());
        Assert.True(channel.IsAvailable(out var reason));
        Assert.Null(reason);
    }
}
