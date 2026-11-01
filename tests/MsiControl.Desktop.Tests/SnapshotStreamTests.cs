using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Desktop.Shell;
using MsiControl.Persistence.Settings;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 共享采样流测试。
/// </summary>
/// <remarks>
/// 覆盖任务书 §6.4 的关键约定：采样失败时读数必须转为「过期」，
/// 但<b>不得把「不可用」改写成「过期」</b>；采样间隔必须有下限。
/// </remarks>
public sealed class SnapshotStreamTests
{
    private static MonitoringSnapshot CreateValidSnapshot()
    {
        var now = DateTimeOffset.Now;
        return new MonitoringSnapshot(
            now,
            new CpuSnapshot(
                SensorReading.Valid(62.0, Units.Celsius, "MSI_ACPI", now),
                SensorReading.Valid(4100, Units.Megahertz, "MSI_ACPI", now),
                SensorReading.Valid(55.0, Units.Watt, "MSI_ACPI", now),
                FanReading.Valid(3200, "MSI_ACPI", now)),
            GpuSnapshot.Unavailable("NVML", "独显未初始化"),
            FanSnapshot.Unavailable("MSI_ACPI"),
            SystemPowerReading.Unavailable("无可靠来源"),
            Array.Empty<ExtendedSensor>());
    }

    [Fact]
    public async Task Start_PublishesSnapshotFromProvider()
    {
        var provider = new FakeProvider(CreateValidSnapshot);
        using var stream = new SnapshotStream(provider, TimeSpan.FromMilliseconds(250));
        var published = new TaskCompletionSource<MonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        stream.Updated += (_, snapshot) => published.TrySetResult(snapshot);
        stream.Start();

        var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stream.Stop();

        Assert.Equal(ReadingStatus.Valid, result.Cpu.Temperature.Status);
        Assert.Equal(62.0, result.Cpu.Temperature.Value);
    }

    [Fact]
    public async Task SamplingFailure_MarksPreviouslyValidReadingsAsStale()
    {
        var provider = new FlakyProvider();
        using var stream = new SnapshotStream(provider, TimeSpan.FromMilliseconds(250));
        var published = new List<MonitoringSnapshot>();
        var second = new TaskCompletionSource<MonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        stream.Updated += (_, snapshot) =>
        {
            lock (published)
            {
                published.Add(snapshot);
                if (published.Count >= 2)
                {
                    second.TrySetResult(snapshot);
                }
            }
        };

        stream.Start();
        var latest = await second.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stream.Stop();

        Assert.True(published.Count >= 2);
        Assert.Equal(ReadingStatus.Stale, latest.Cpu.Temperature.Status);
        // 过期读数保留最后已知值仅供诊断，但状态已不再是有效。
        Assert.False(latest.Cpu.Temperature.IsUsable);
    }

    [Fact]
    public async Task SamplingFailure_DoesNotRewriteUnavailableAsStale()
    {
        // 关键回归：「读不到」与「数据旧了」是两件事，不能混为一谈。
        var provider = new FakeProvider(() => MonitoringSnapshot.Empty(DateTimeOffset.Now, "未适配"));
        using var stream = new SnapshotStream(provider, TimeSpan.FromMilliseconds(250));
        var published = new TaskCompletionSource<MonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        stream.Updated += (_, snapshot) => published.TrySetResult(snapshot);
        stream.Start();

        var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stream.Stop();

        Assert.Equal(ReadingStatus.Unavailable, result.Cpu.Temperature.Status);
    }

    [Fact]
    public void SetInterval_ClampsToMinimum()
    {
        using var stream = new SnapshotStream(new FakeProvider(CreateValidSnapshot), TimeSpan.FromSeconds(1));

        stream.SetInterval(TimeSpan.FromMilliseconds(1));

        Assert.Equal(SnapshotStream.MinInterval, stream.Interval);
    }

    [Fact]
    public void SetInterval_ClampsToMaximum()
    {
        using var stream = new SnapshotStream(new FakeProvider(CreateValidSnapshot), TimeSpan.FromSeconds(1));

        stream.SetInterval(TimeSpan.FromHours(1));

        Assert.Equal(SnapshotStream.MaxInterval, stream.Interval);
    }

    [Fact]
    public void Constructor_RejectsNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = new SnapshotStream(null!, TimeSpan.FromSeconds(1));
        });
    }

    [Fact]
    public async Task NotAdaptedProviderReportsUnavailableInsteadOfFakeData()
    {
        var provider = new NotAdaptedMonitoringProvider("监控模块尚未接入");
        using var stream = new SnapshotStream(provider, TimeSpan.FromMilliseconds(250));
        var published = new TaskCompletionSource<MonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        stream.Updated += (_, snapshot) => published.TrySetResult(snapshot);
        stream.Start();

        var result = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stream.Stop();

        Assert.Equal(ReadingStatus.Unavailable, result.Cpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Unavailable, result.Gpu.Temperature.Status);
        Assert.False(result.SystemPower.IsAvailable);
        Assert.Empty(result.Extended);
    }

    [Fact]
    public async Task DisposeAfterStart_StopsPublishing()
    {
        var provider = new FakeProvider(CreateValidSnapshot);
        var stream = new SnapshotStream(provider, TimeSpan.FromMilliseconds(250));
        var first = new TaskCompletionSource<MonitoringSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        stream.Updated += (_, snapshot) => first.TrySetResult(snapshot);
        stream.Start();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(10));

        stream.Dispose();
        var callsAfterDispose = provider.CallCount;
        await Task.Delay(600);

        Assert.InRange(provider.CallCount, callsAfterDispose, callsAfterDispose + 1);
    }

    private sealed class FakeProvider : IMonitoringProvider
    {
        private readonly Func<MonitoringSnapshot> _factory;
        private int _calls;

        internal FakeProvider(Func<MonitoringSnapshot> factory)
        {
            _factory = factory;
        }

        internal int CallCount => Volatile.Read(ref _calls);

        public Task<MonitoringSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_factory());
        }
    }

    private sealed class FlakyProvider : IMonitoringProvider
    {
        private int _calls;

        public Task<MonitoringSnapshot> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return Interlocked.Increment(ref _calls) == 1
                ? Task.FromResult(CreateValidSnapshot())
                : throw new InvalidOperationException("模拟接口故障");
        }
    }
}
