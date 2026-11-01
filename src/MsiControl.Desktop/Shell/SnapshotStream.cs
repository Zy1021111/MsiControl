using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 共享采样流。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §16 / design.md §13.4：整个应用只有**一个**采样任务；
/// 各窗口订阅结果，不各自建计时器读硬件。本接口即为该约定的界面侧入口。
/// </para>
/// <para>
/// 实现方负责在采样超时或失败时把读数标记为过期。
/// <b>不得把上一次的有效值当作当前值继续展示</b>（任务书 §6.4）。
/// </para>
/// </remarks>
public interface ISnapshotStream : IDisposable
{
    /// <summary>最近一次采样结果。</summary>
    MonitoringSnapshot Latest { get; }

    /// <summary>最近的更新时刻。</summary>
    DateTimeOffset LastUpdate { get; }

    /// <summary>采样更新通知。可能在非 UI 线程触发，订阅方须自行封送。</summary>
    event EventHandler<MonitoringSnapshot>? Updated;

    /// <summary>开始采样。重复调用为空操作。</summary>
    void Start();

    /// <summary>停止采样。重复调用为空操作。</summary>
    void Stop();

    /// <summary>调整采样间隔。下次采样起生效。</summary>
    /// <param name="interval">新间隔。</param>
    void SetInterval(TimeSpan interval);
}

/// <summary>
/// 基于 <see cref="IMonitoringProvider"/> 的单任务采样流。
/// </summary>
/// <remarks>
/// 本类型只做「调度 + 有效期判定 + 广播」，不决定数据来源——
/// 数据来源由注入的 provider 决定（任务 03 实现，任务 07 装配）。
/// </remarks>
public sealed class SnapshotStream : ISnapshotStream
{
    /// <summary>允许的最小采样间隔。</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>允许的最大采样间隔。</summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromSeconds(60);

    private readonly IMonitoringProvider _provider;
    private readonly object _gate = new();
    private TimeSpan _interval;
    private TimeSpan _maxAge;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>构造采样流。</summary>
    /// <param name="provider">监控数据源。</param>
    /// <param name="interval">初始采样间隔。</param>
    public SnapshotStream(IMonitoringProvider provider, TimeSpan interval)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _interval = Clamp(interval);
        _maxAge = ComputeMaxAge(_interval);
        Latest = MonitoringSnapshot.Empty(DateTimeOffset.Now, "尚未开始采样");
    }

    /// <inheritdoc />
    public MonitoringSnapshot Latest { get; private set; }

    /// <inheritdoc />
    public DateTimeOffset LastUpdate => Latest.SampledAt;

    /// <inheritdoc />
    public event EventHandler<MonitoringSnapshot>? Updated;

    /// <summary>当前采样间隔。</summary>
    public TimeSpan Interval => _interval;

    /// <summary>判定过期所用的最大年龄。</summary>
    public TimeSpan MaxAge => _maxAge;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    /// <inheritdoc />
    public void SetInterval(TimeSpan interval)
    {
        var clamped = Clamp(interval);
        lock (_gate)
        {
            _interval = clamped;
            _maxAge = ComputeMaxAge(clamped);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await _provider.ReadAsync(cancellationToken).ConfigureAwait(false);
                Publish(snapshot);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 采样失败不是致命错误：标记为过期并继续，界面会显示「过期」而非旧值。
                Publish(Latest.MarkStale($"采样失败：{ex.Message}"));
            }

            try
            {
                await Task.Delay(_interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Publish(MonitoringSnapshot snapshot)
    {
        var now = DateTimeOffset.Now;
        if (!snapshot.IsFreshAt(now, _maxAge))
        {
            snapshot = snapshot.MarkStale("采样超时");
        }

        Latest = snapshot;
        Updated?.Invoke(this, snapshot);
    }

    private static TimeSpan Clamp(TimeSpan interval)
    {
        if (interval < MinInterval)
        {
            return MinInterval;
        }

        return interval > MaxInterval ? MaxInterval : interval;
    }

    private static TimeSpan ComputeMaxAge(TimeSpan interval)
    {
        // 允许 3 个采样周期加上 2 秒余量，再设下限 5 秒，避免偶发抖动立刻被判定过期。
        var candidate = TimeSpan.FromTicks(interval.Ticks * 3) + TimeSpan.FromSeconds(2);
        return candidate < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : candidate;
    }
}

/// <summary>
/// 尚未接入监控模块时使用的数据源。
/// </summary>
/// <remarks>
/// <b>返回明确的「不可用」，不返回模拟数据。</b>
/// 任务书提示词第 7、13 条：模拟数据只能由显式开发开关启用并持续标注，
/// 默认流程必须诚实显示「未适配」。
/// </remarks>
public sealed class NotAdaptedMonitoringProvider : IMonitoringProvider
{
    private readonly string _reason;

    /// <summary>构造数据源。</summary>
    /// <param name="reason">不可用原因，会作为读数详情展示。</param>
    public NotAdaptedMonitoringProvider(string reason = "监控模块（任务 03）尚未接入")
    {
        _reason = reason;
    }

    /// <inheritdoc />
    public Task<MonitoringSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MonitoringSnapshot.Empty(DateTimeOffset.Now, _reason));
    }
}
