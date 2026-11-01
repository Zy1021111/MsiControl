namespace MsiControl.Application.Control;

/// <summary>
/// 协调器行为参数。
/// </summary>
/// <remarks>
/// 全部有默认值：默认值即「保守且诚实」——超时给出明确结论、部分失败必重读、排队请求可被取代。
/// </remarks>
public sealed record ControlCoordinatorOptions
{
    /// <summary>默认参数。</summary>
    public static ControlCoordinatorOptions Default { get; } = new();

    /// <summary>
    /// 单次硬件操作的超时。
    /// </summary>
    /// <remarks>
    /// 超时后返回 <see cref="ControlOutcomeKind.TimedOut"/>，<b>不返回成功</b>——
    /// 没有读回确认就声称生效属于显示虚假成功（任务书 §1 原则 6）。
    /// </remarks>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 部分失败后是否重新读取实际状态。
    /// </summary>
    /// <remarks>
    /// 任务书 §16：「出现部分失败时先读取当前状态，不盲目宣称整组回滚成功」。
    /// 重读结论写入 <see cref="ControlOutcome.Notes"/>，且**不会**据此声称回滚成功。
    /// </remarks>
    public bool ReReadAfterPartialFailure { get; init; } = true;

    /// <summary>是否允许合并（取代）尚未执行的排队请求。</summary>
    public bool SupersedePendingRequests { get; init; } = true;
}
