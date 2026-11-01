using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Afterburner;
using MsiControl.Monitoring.Sources;
using Xunit;

namespace MsiControl.Monitoring.Tests;

/// <summary>可编程的 MAHM 读取替身。</summary>
/// <remarks>
/// 按（数据源 ID，GPU 索引）提供读数；gpuIndex 为 null 时匹配任一同 ID 条目，
/// 传 <see cref="MonitoringSourceIds.GlobalGpuIndex"/> 时精确匹配全局条目。
/// </remarks>
internal sealed class FakeMahmReader : IMahmReader
{
    private readonly Dictionary<(uint SourceId, uint Gpu), float> _entries;
    private readonly Dictionary<uint, (string Name, string Units)> _meta;

    public FakeMahmReader(Dictionary<(uint SourceId, uint Gpu), float>? entries = null)
    {
        _entries = entries ?? new Dictionary<(uint SourceId, uint Gpu), float>();
        _meta = new Dictionary<uint, (string, string)>();
    }

    /// <summary>登记一个条目（同时登记名称与单位）。</summary>
    public void Add(uint sourceId, uint gpu, float data, string name = "测试项", string units = "")
    {
        _entries[(sourceId, gpu)] = data;
        _meta[sourceId] = (name, units);
    }

    /// <summary>是否表现为「共享内存不可用」（未运行 Afterburner）。</summary>
    public bool Unavailable { get; set; }

    public string UnavailableReason { get; set; } = "未检测到 MSI Afterburner 共享内存。";

    public bool IsAvailable(out string? reason)
    {
        reason = Unavailable ? UnavailableReason : null;
        return !Unavailable;
    }

    public bool TryRead(
        uint sourceId,
        uint? gpuIndex,
        out float value,
        out string? sourceName,
        out string? units,
        out string? error)
    {
        value = 0;
        sourceName = null;
        units = null;
        error = null;

        if (Unavailable)
        {
            error = UnavailableReason;
            return false;
        }

        float exactValue = 0;
        var hit = gpuIndex.HasValue
            ? _entries.TryGetValue((sourceId, gpuIndex.Value), out exactValue)
            : _entries.Any(kv => kv.Key.SourceId == sourceId);

        if (!hit)
        {
            error = $"共享内存中未找到数据源 ID 0x{sourceId:X8}。";
            return false;
        }

        value = gpuIndex.HasValue ? exactValue : _entries.First(kv => kv.Key.SourceId == sourceId).Value;
        sourceName = _meta.TryGetValue(sourceId, out var meta) ? meta.Name : "测试项";
        units = _meta.TryGetValue(sourceId, out var meta2) ? meta2.Units : string.Empty;
        return true;
    }

    public bool TryReadAll(out IReadOnlyList<MahmEntrySnapshot> entries, out string? error)
    {
        if (Unavailable)
        {
            entries = Array.Empty<MahmEntrySnapshot>();
            error = UnavailableReason;
            return false;
        }

        entries = _entries
            .Select(kv => new MahmEntrySnapshot(
                _meta.TryGetValue(kv.Key.SourceId, out var meta) ? meta.Name : "测试项",
                _meta.TryGetValue(kv.Key.SourceId, out var meta2) ? meta2.Units : string.Empty,
                kv.Value,
                kv.Key.Gpu,
                kv.Key.SourceId))
            .ToList();
        error = null;
        return true;
    }
}

/// <summary>Afterburner（MAHM）数据源的解析、映射与降级行为。</summary>
/// <remarks>全部为替身测试，不依赖本机是否安装/运行 Afterburner。</remarks>
public sealed class AfterburnerSourceTests
{
    private const uint Gpu = 0;
    private const uint Global = MonitoringSourceIds.GlobalGpuIndex;

    private static FakeMahmReader CreateFullReader()
    {
        var reader = new FakeMahmReader();
        reader.Add(MonitoringSourceIds.GpuTemperature, Gpu, 54f, "GPU temperature", "°C");
        reader.Add(MonitoringSourceIds.CoreClock, Gpu, 1650f, "Core clock", "MHz");
        reader.Add(MonitoringSourceIds.MemoryClock, Gpu, 9001f, "Memory clock", "MHz");
        reader.Add(MonitoringSourceIds.GpuAbsolutePower, Gpu, 54.4f, "GPU power", "W");
        reader.Add(MonitoringSourceIds.GpuUsage, Gpu, 41f, "GPU usage", "%");
        reader.Add(MonitoringSourceIds.FbUsage, Gpu, 13f, "FB usage", "%");
        reader.Add(MonitoringSourceIds.FanSpeed, Gpu, 42f, "Fan", "%");
        reader.Add(MonitoringSourceIds.GpuTempLimit, Gpu, 87f, "Temp limit", "°C");
        reader.Add(MonitoringSourceIds.GpuPowerLimit, Gpu, 140f, "Power limit", "W");
        reader.Add(MonitoringSourceIds.CpuTemperature, Global, 84f, "CPU temperature", "°C");
        reader.Add(MonitoringSourceIds.CpuPower, Global, 45.6f, "CPU power", "W");
        return reader;
    }

    // ── GPU 源 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 共享内存不可用时_GPU四项均不可用且不伪造数值()
    {
        var source = new AfterburnerGpuSource(new FakeMahmReader { Unavailable = true });

        var snapshot = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, snapshot.Temperature.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.CoreFrequencyMhz.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.PowerWatt.Status);
        Assert.Equal(ReadingStatus.Unavailable, snapshot.Fan.Rpm.Status);
        Assert.Null(snapshot.Temperature.Value);
    }

    [Fact]
    public async Task 共享内存可用时_GPU四项正确映射()
    {
        var source = new AfterburnerGpuSource(CreateFullReader());

        var snapshot = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, snapshot.Temperature.Status);
        Assert.Equal(54f, snapshot.Temperature.Value);
        Assert.Equal(1650f, snapshot.CoreFrequencyMhz.Value);
        Assert.True(Math.Abs(snapshot.PowerWatt.Value!.Value - 54.4) < 0.05); // 0x61 GPU 绝对功耗
    }

    [Fact]
    public async Task 扩展传感器_显存占用使用FB百分比而非MB绝对值()
    {
        // 0x31 是 MB 绝对值（实测 2611 MB），0x32 才是百分比——此前用错了 ID。
        var source = new AfterburnerGpuSource(CreateFullReader());

        var extended = await ((IExtendedSensorSource)source).ReadAsync(CancellationToken.None);

        var vram = extended.Single(s => s.Key == "gpu.vram");
        Assert.Equal(ReadingStatus.Valid, vram.Reading.Status);
        Assert.Equal(13f, vram.Reading.Value);
    }

    [Fact]
    public async Task 扩展传感器_包含功耗与温度限制项()
    {
        var source = new AfterburnerGpuSource(CreateFullReader());

        var extended = await ((IExtendedSensorSource)source).ReadAsync(CancellationToken.None);

        var powerLimit = extended.Single(s => s.Key == "gpu.powerlimit");
        var tempLimit = extended.Single(s => s.Key == "gpu.temp.limit");
        Assert.Equal(140f, powerLimit.Reading.Value);
        Assert.Equal(87f, tempLimit.Reading.Value);
    }

    // ── CPU 源 ────────────────────────────────────────────────────────

    [Fact]
    public async Task CPU源_功耗与温度取全局汇总条目()
    {
        // 全局条目（0xFFFFFFFF）才是总口径；逐核条目（gpu=0..N）不是想要的。
        var reader = CreateFullReader();
        reader.Add(MonitoringSourceIds.CpuTemperature, 0, 66f, "CPU1 temperature", "°C"); // 干扰项：单核值
        reader.Add(MonitoringSourceIds.CpuPower, 0, 8f, "CPU1 power", "W");               // 干扰项：单核值
        var source = new AfterburnerCpuSource(reader);

        var power = await source.ReadAsync(CancellationToken.None);
        var temp = await ((ICpuTemperatureSource)source).ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Valid, power.Status);
        Assert.True(Math.Abs(power.Value!.Value - 45.6) < 0.05);
        Assert.Equal(ReadingStatus.Valid, temp.Status);
        Assert.Equal(84f, temp.Value);
    }

    [Fact]
    public async Task CPU源_条目缺失时给出启用指引()
    {
        var source = new AfterburnerCpuSource(new FakeMahmReader());

        var power = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(ReadingStatus.Unavailable, power.Status);
        Assert.Contains("CPU power", power.Detail, StringComparison.Ordinal);
    }

    // ── 常量与装配 ────────────────────────────────────────────────────

    [Fact]
    public void 监控源ID常量_与本机SDK头文件一致()
    {
        Assert.Equal(0x00000000u, MonitoringSourceIds.GpuTemperature);
        Assert.Equal(0x00000011u, MonitoringSourceIds.FanTachometer);
        Assert.Equal(0x00000020u, MonitoringSourceIds.CoreClock);
        Assert.Equal(0x00000022u, MonitoringSourceIds.MemoryClock);
        Assert.Equal(0x00000030u, MonitoringSourceIds.GpuUsage);
        Assert.Equal(0x00000032u, MonitoringSourceIds.FbUsage);
        Assert.Equal(0x00000061u, MonitoringSourceIds.GpuAbsolutePower);
        Assert.Equal(0x00000070u, MonitoringSourceIds.GpuTempLimit);
        Assert.Equal(0x00000071u, MonitoringSourceIds.GpuPowerLimit);
        Assert.Equal(0x00000080u, MonitoringSourceIds.CpuTemperature);
        Assert.Equal(0x00000090u, MonitoringSourceIds.CpuUsage);
        Assert.Equal(0x000000A0u, MonitoringSourceIds.CpuClock);
        Assert.Equal(0x00000100u, MonitoringSourceIds.CpuPower);
        Assert.Equal(0xFFFFFFFFu, MonitoringSourceIds.GlobalGpuIndex);
    }

    [Fact]
    public void 映射对象名_为官方SDK约定的MAHMSharedMemory()
    {
        Assert.Equal("MAHMSharedMemory", MahmReader.MappingName);
    }

    [Fact]
    public void 工厂_两种GPU偏好均能装配且不抛异常()
    {
        using var nvmlFirst = MonitoringProviderFactory.CreateDefault(DeviceCapabilities.Empty);
        using var abFirst = MonitoringProviderFactory.CreateDefault(
            DeviceCapabilities.Empty,
            preferAfterburner: true);

        Assert.NotNull(nvmlFirst);
        Assert.NotNull(abFirst);
    }
}
