using MsiControl.Monitoring.Sources;

namespace MsiControl.Monitoring;

/// <summary>
/// 单个数据源的诊断状态。
/// </summary>
/// <remarks>对应任务书 §6.4「开启有效调试标志时记录数据来源与更新时间，界面折叠展示」。</remarks>
/// <param name="Key">数据源标识。</param>
/// <param name="Description">数据来源说明。</param>
/// <param name="LastAttemptAt">最近一次尝试时刻。</param>
/// <param name="LastSuccessAt">最近一次成功时刻。</param>
/// <param name="LastDuration">最近一次读取耗时。</param>
/// <param name="LastError">最近一次失败原因。</param>
/// <param name="ConsecutiveFailures">连续失败次数。</param>
/// <param name="IsBackedOff">是否正处于退避期。</param>
/// <param name="SkippedByThrottle">本次是否因刷新间隔被跳过（沿用上次结果）。</param>
public sealed record MonitoringSourceDiagnostic(
    string Key,
    string Description,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessAt,
    TimeSpan LastDuration,
    string? LastError,
    int ConsecutiveFailures,
    bool IsBackedOff,
    bool SkippedByThrottle);

/// <summary>
/// 监控模块的诊断快照。
/// </summary>
/// <param name="GeneratedAt">生成时刻。</param>
/// <param name="LastSampleAt">最近一次采样时刻。</param>
/// <param name="LastSampleDuration">最近一次采样总耗时。</param>
/// <param name="Sources">各数据源状态。</param>
/// <param name="Notes">补充说明，例如被能力门控跳过的项。</param>
public sealed record MonitoringDiagnostics(
    DateTimeOffset GeneratedAt,
    DateTimeOffset? LastSampleAt,
    TimeSpan LastSampleDuration,
    IReadOnlyList<MonitoringSourceDiagnostic> Sources,
    IReadOnlyList<string> Notes)
{
    /// <summary>空诊断。</summary>
    public static MonitoringDiagnostics Empty { get; } =
        new(DateTimeOffset.Now, null, TimeSpan.Zero, Array.Empty<MonitoringSourceDiagnostic>(), Array.Empty<string>());
}

/// <summary>
/// 数据源调用闸门：统一处理超时、失败退避与诊断记录。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它：</b>WMI 调用一旦发出就无法中断，若某个提供程序卡死，
/// 简单的「每隔一秒重试」会不断堆积挂起线程。本类把「超时」与「连续失败」转成
/// **退避**，从而对资源占用给出上界（任务书 §20）。
/// </para>
/// <para>
/// <b>取消语义：</b>调用方取消（<paramref name="callerToken"/>）必须原样抛出
/// <see cref="OperationCanceledException"/>；只有本类自己的超时才降级为失败读数
/// （接口与工程约定 §6）。
/// </para>
/// </remarks>
internal sealed class SourceRunner
{
    private readonly MonitoringOptions _options;
    private readonly Dictionary<string, SourceState> _states = new(StringComparer.Ordinal);
    private readonly List<string> _notes = new();

    /// <summary>构造闸门。</summary>
    public SourceRunner(MonitoringOptions options) => _options = options;

    /// <summary>读取一个值；失败、超时或退避时返回 <paramref name="onFailure"/> 产生的结果。</summary>
    /// <typeparam name="T">读数类型。</typeparam>
    /// <param name="source">数据源。</param>
    /// <param name="read">实际读取委托。</param>
    /// <param name="onFailure">失败时的降级读数工厂。参数为失败原因。</param>
    /// <param name="now">当前时刻。</param>
    /// <param name="callerToken">调用方取消标记。</param>
    public async Task<T> RunAsync<T>(
        IMonitoringSource source,
        Func<CancellationToken, Task<T>> read,
        Func<string, T> onFailure,
        DateTimeOffset now,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(onFailure);

        var state = GetState(source);

        if (state.BackoffUntil is { } until && now < until)
        {
            state.IsBackedOff = true;
            state.SkippedByThrottle = false;
            var reason =
                $"数据源「{source.Description}」处于退避期（连续失败 {state.ConsecutiveFailures} 次）" +
                $"{(state.LastError is null ? string.Empty : $"，最近错误：{state.LastError}")}。";
            return onFailure(reason);
        }

        state.IsBackedOff = false;
        var timeout = source.SuggestedTimeout ?? _options.SourceTimeout;
        var startedAt = DateTimeOffset.Now;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            var readTask = read(timeoutCts.Token);
            var completed = await Task.WhenAny(readTask, Task.Delay(timeout, callerToken)).ConfigureAwait(false);

            if (completed != readTask)
            {
                // 超时：放弃等待，观察可能迟到的异常，避免未观测任务异常。
                Observe(readTask);

                // 先区分「调用方取消」与「本闸门超时」：前者必须原样抛出（接口与工程约定 §6）。
                if (callerToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        $"数据源「{source.Description}」的读取被调用方取消。",
                        callerToken);
                }

                return Fail(state, source, startedAt, $"读取超时（>{timeout.TotalSeconds:0.#} 秒），本次读数不可用。", onFailure);
            }

            var value = await readTask.ConfigureAwait(false);
            state.LastAttemptAt = startedAt;
            state.LastSuccessAt = DateTimeOffset.Now;
            state.LastDuration = DateTimeOffset.Now - startedAt;
            state.LastError = null;
            state.ConsecutiveFailures = 0;
            state.BackoffUntil = null;
            return value;
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(state, source, startedAt, $"读取被取消（>{timeout.TotalSeconds:0.#} 秒未返回），本次读数不可用。", onFailure);
        }
        catch (Exception ex)
        {
            return Fail(state, source, startedAt, ex.Message, onFailure);
        }
    }

    /// <summary>记录一次「按刷新间隔跳过」。返回值本身由调用方沿用缓存。</summary>
    public void NoteThrottled(IMonitoringSource source)
    {
        var state = GetState(source);
        state.SkippedByThrottle = true;
    }

    /// <summary>添加一条诊断备注。</summary>
    public void Note(string message)
    {
        if (!string.IsNullOrWhiteSpace(message) && !_notes.Contains(message, StringComparer.Ordinal))
        {
            _notes.Add(message);
        }
    }

    /// <summary>生成诊断快照。</summary>
    public MonitoringDiagnostics Snapshot(DateTimeOffset generatedAt, DateTimeOffset? lastSampleAt, TimeSpan lastSampleDuration)
    {
        var list = _states.Values
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => new MonitoringSourceDiagnostic(
                s.Key,
                s.Description,
                s.LastAttemptAt,
                s.LastSuccessAt,
                s.LastDuration,
                s.LastError,
                s.ConsecutiveFailures,
                s.IsBackedOff,
                s.SkippedByThrottle))
            .ToArray();

        return new MonitoringDiagnostics(generatedAt, lastSampleAt, lastSampleDuration, list, _notes.ToArray());
    }

    private T Fail<T>(SourceState state, IMonitoringSource source, DateTimeOffset startedAt, string reason, Func<string, T> onFailure)
    {
        state.LastAttemptAt = startedAt;
        state.LastDuration = DateTimeOffset.Now - startedAt;
        state.LastError = reason;
        state.ConsecutiveFailures++;

        var message = $"数据源「{source.Description}」读取失败：{reason}";

        if (state.ConsecutiveFailures >= _options.FailureBackoffThreshold
            && _options.FailureBackoffDuration > TimeSpan.Zero)
        {
            state.BackoffUntil = DateTimeOffset.Now + _options.FailureBackoffDuration;
            state.IsBackedOff = true;
            message +=
                $" 连续失败已达 {state.ConsecutiveFailures} 次，暂停 {_options.FailureBackoffDuration.TotalSeconds:0} 秒后重试。";
        }

        return onFailure(message);
    }

    private SourceState GetState(IMonitoringSource source)
    {
        if (_states.TryGetValue(source.Key, out var existing))
        {
            return existing;
        }

        var created = new SourceState(source.Key, source.Description);
        _states[source.Key] = created;
        return created;
    }

    private static void Observe(Task task)
    {
        _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private sealed class SourceState
    {
        public SourceState(string key, string description)
        {
            Key = key;
            Description = description;
        }

        public string Key { get; }

        public string Description { get; }

        public DateTimeOffset? LastAttemptAt { get; set; }

        public DateTimeOffset? LastSuccessAt { get; set; }

        public TimeSpan LastDuration { get; set; }

        public string? LastError { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? BackoffUntil { get; set; }

        public bool IsBackedOff { get; set; }

        public bool SkippedByThrottle { get; set; }
    }
}
