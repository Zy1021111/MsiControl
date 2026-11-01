using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using MsiControl.Monitoring.Sources;
using Xunit;

namespace MsiControl.Monitoring.Tests;

/// <summary>可编程的 EC 寄存器访问替身。</summary>
internal sealed class FakeEcAccess : IEcRegisterAccess
{
    private readonly Dictionary<byte, byte> _registers;

    public FakeEcAccess(Dictionary<byte, byte>? registers = null)
        => _registers = registers ?? new Dictionary<byte, byte>();

    public string? AvailabilityError { get; set; }

    public int ReadCount { get; private set; }

    public byte ReadRegister(byte address)
    {
        if (AvailabilityError is not null)
        {
            throw new InvalidOperationException($"EC 通道不可用：{AvailabilityError}");
        }

        ReadCount++;
        return _registers.TryGetValue(address, out var value) ? value : byte.MinValue;
    }
}

/// <summary>EC 风扇/温度源与回落链的行为。</summary>
public sealed class EcSourceTests
{
    private static readonly byte CpuFanAddress = Ms15M3EcRegisterMap.CpuFanPercent.Address;
    private static readonly byte GpuFanAddress = Ms15M3EcRegisterMap.GpuFanPercent.Address;
    private static readonly byte CpuTempAddress = Ms15M3EcRegisterMap.CpuTemperature.Address;
    private static readonly byte GpuTempAddress = Ms15M3EcRegisterMap.GpuTemperature.Address;

    // ── FanReading.FromHardwarePercent ────────────────────────────────

    [Fact]
    public void 硬件百分比工厂_RPM槽位为不可用并带原因_百分比为硬件值()
    {
        var fan = FanReading.FromHardwarePercent(
            42,
            "EC 风扇占空比:0x71",
            Fake.Now,
            "EC 寄存器表不提供 RPM 读数。");

        Assert.True(fan.HasCalibration);
        Assert.Equal(42, fan.ActualPercent);
        Assert.Equal(ReadingStatus.Unavailable, fan.Rpm.Status);
        Assert.Null(fan.Rpm.Value);
        Assert.Contains("不提供 RPM", fan.Rpm.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void 硬件百分比工厂_越界值被截断到0到100()
    {
        Assert.Equal(100, FanReading.FromHardwarePercent(180, "替身", Fake.Now).ActualPercent);
        Assert.Equal(0, FanReading.FromHardwarePercent(-5, "替身", Fake.Now).ActualPercent);
    }

    // ── EC 风扇源 ─────────────────────────────────────────────────────

    [Fact]
    public async Task EC风扇源_读取两个通道的占空比寄存器并按基准换算RPM()
    {
        var access = new FakeEcAccess(new Dictionary<byte, byte>
        {
            [CpuFanAddress] = 33,
            [GpuFanAddress] = 21
        });
        var source = new PawnIoEcFanSource(access);

        var cpu = await source.ReadAsync(FanChannel.Cpu, CancellationToken.None);
        var gpu = await source.ReadAsync(FanChannel.Gpu, CancellationToken.None);

        // 百分比为硬件直报，RPM 按两段折线标定（45% = 3000、100% = 9000）估算。
        Assert.Equal(33, cpu.ActualPercent);
        Assert.Equal(21, gpu.ActualPercent);

        Assert.Equal(ReadingStatus.Valid, cpu.Rpm.Status);
        Assert.Equal(2200, cpu.Rpm.Value); // 33 / 45 × 3000
        Assert.Contains("折线估算", cpu.Rpm.Source, StringComparison.Ordinal);
        Assert.Contains("非转速计实测", cpu.Rpm.Source, StringComparison.Ordinal);

        Assert.Equal(ReadingStatus.Valid, gpu.Rpm.Status);
        Assert.Equal(1400, gpu.Rpm.Value); // 21 / 45 × 3000
    }

    [Fact]
    public async Task EC风扇源_满速占空比换算为9000转()
    {
        var access = new FakeEcAccess(new Dictionary<byte, byte> { [CpuFanAddress] = 100 });
        var source = new PawnIoEcFanSource(access);

        var fan = await source.ReadAsync(FanChannel.Cpu, CancellationToken.None);

        Assert.Equal(100, fan.ActualPercent);
        Assert.Equal(9000, fan.Rpm.Value);
    }

    [Fact]
    public void 满速基准常量_为叶哥提供的9000转()
    {
        Assert.Equal(9000.0, PawnIoEcFanSource.FullSpeedRpm);
        Assert.Equal(45.0, PawnIoEcFanSource.StandardDutyPercent);
        Assert.Equal(3000.0, PawnIoEcFanSource.StandardRpm);
    }

    [Fact]
    public async Task EC风扇源_标准转速设定45对应3000转()
    {
        var access = new FakeEcAccess(new Dictionary<byte, byte> { [CpuFanAddress] = 45 });
        var source = new PawnIoEcFanSource(access);

        var fan = await source.ReadAsync(FanChannel.Cpu, CancellationToken.None);

        Assert.Equal(45, fan.ActualPercent);
        Assert.Equal(3000, fan.Rpm.Value);
    }

    [Fact]
    public async Task EC风扇源_寄存器值超出100时按不可用处理()
    {
        var access = new FakeEcAccess(new Dictionary<byte, byte> { [CpuFanAddress] = 255 });
        var source = new PawnIoEcFanSource(access);

        var fan = await source.ReadAsync(FanChannel.Cpu, CancellationToken.None);

        Assert.Null(fan.ActualPercent);
        Assert.Equal(ReadingStatus.Unavailable, fan.Rpm.Status);
        Assert.Contains("超出", fan.Rpm.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EC风扇源_通道不可用时给出精确原因()
    {
        var access = new FakeEcAccess { AvailabilityError = "需要管理员权限运行。" };
        var source = new PawnIoEcFanSource(access);

        var fan = await source.ReadAsync(FanChannel.Cpu, CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, fan.Rpm.Status);
        Assert.Contains("需要管理员权限", fan.Rpm.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void EC风扇源_能力键为fan_pwm且按当前等级允许只读()
    {
        var source = new PawnIoEcFanSource(new FakeEcAccess());
        var status = Ms15M3Capabilities.StatusOf(CapabilityKeys.FanPwm);

        Assert.Equal(CapabilityKeys.FanPwm, source.RequiredCapabilityKey);
        Assert.True(status.AllowsRead, "fan.pwm 当前登记为 Detected，应允许只读展示。");
        Assert.False(status.AllowsWrite, "fan.pwm 未达 Verified，写入必须保持禁用。");
    }

    // ── EC 温度源 ─────────────────────────────────────────────────────

    [Fact]
    public async Task EC温度源_读取CPU实时温度()
    {
        var access = new FakeEcAccess(new Dictionary<byte, byte>
        {
            [CpuTempAddress] = 64,
            [GpuTempAddress] = 49
        });
        var source = new PawnIoEcTemperatureSource(access);

        var cpu = await source.ReadAsync(CancellationToken.None);
        var extended = await ((IExtendedSensorSource)source).ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, cpu.Status);
        Assert.Equal(64, cpu.Value);
        Assert.Equal(ReadingStatus.Valid, extended[0].Reading.Status);
        Assert.Equal(49, extended[0].Reading.Value);
    }

    [Fact]
    public async Task EC温度源_零值视为不可用而非零度()
    {
        var access = new FakeEcAccess(new Dictionary<byte, byte> { [CpuTempAddress] = 0 });
        var source = new PawnIoEcTemperatureSource(access);

        var cpu = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, cpu.Status);
        Assert.Contains("合理区间", cpu.Detail, StringComparison.Ordinal);
    }

    // ── GPU 回落链 ────────────────────────────────────────────────────

    [Fact]
    public async Task GPU回落链_主来源可用时不调用后备来源()
    {
        var primary = new FakeGpuSource(() => new GpuSnapshot(
            Fake.Celsius(49), Fake.Megahertz(300), Fake.Watt(14), FanReading.Unavailable("替身")));
        var fallbackCalled = false;
        var fallback = new FakeGpuSource(() =>
        {
            fallbackCalled = true;
            return GpuSnapshot.Unavailable("替身", "后备");
        });

        var chain = new CompositeGpuSource(new IGpuSource[] { primary, fallback });
        var snapshot = await chain.ReadAsync(CancellationToken.None);

        Assert.Equal(49, snapshot.Temperature.Value);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public async Task GPU回落链_主来源不可用时落到后备来源()
    {
        var primary = new FakeGpuSource(() => GpuSnapshot.Unavailable("主", "驱动缺失"));
        var fallback = new FakeGpuSource(() => new GpuSnapshot(
            Fake.Celsius(50), Fake.Megahertz(300), Fake.Watt(15), FanReading.Unavailable("替身")));

        var chain = new CompositeGpuSource(new IGpuSource[] { primary, fallback });
        var snapshot = await chain.ReadAsync(CancellationToken.None);

        Assert.Equal(50, snapshot.Temperature.Value);
    }

    [Fact]
    public async Task GPU回落链_休眠立即采纳不再尝试后备()
    {
        var primary = new FakeGpuSource(() => GpuSnapshot.Sleeping("主", "独显已断电"));
        var fallbackCalled = false;
        var fallback = new FakeGpuSource(() =>
        {
            fallbackCalled = true;
            return new GpuSnapshot(
                Fake.Celsius(50), Fake.Megahertz(300), Fake.Watt(15), FanReading.Unavailable("替身"));
        });

        var chain = new CompositeGpuSource(new IGpuSource[] { primary, fallback });
        var snapshot = await chain.ReadAsync(CancellationToken.None);

        Assert.True(snapshot.IsSleeping);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public async Task GPU回落链_全部不可用时合并各来源原因()
    {
        var primary = new FakeGpuSource(() => GpuSnapshot.Unavailable("主", "驱动缺失"));
        var fallback = new FakeGpuSource(() => GpuSnapshot.Unavailable("后备", "Afterburner 未运行"));

        var chain = new CompositeGpuSource(new IGpuSource[] { primary, fallback });
        var snapshot = await chain.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, snapshot.Temperature.Status);
        Assert.Null(snapshot.Temperature.Value);
        Assert.Contains("驱动缺失", snapshot.Temperature.Detail, StringComparison.Ordinal);
        Assert.Contains("Afterburner 未运行", snapshot.Temperature.Detail, StringComparison.Ordinal);
    }
}
