using MsiControl.Core.Results;

namespace MsiControl.Application.Control;

/// <summary>
/// 协调器处理一次请求的方式。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="ControlResult.Status"/> 分开的原因：<b>没有执行写入</b>与
/// <b>执行了写入但失败</b>是两件不同的事。把「被更新的请求取代」报成「失败」
/// 会让界面显示虚假的失败；把它报成成功则是显示虚假成功。
/// </para>
/// <para>
/// 因此本枚举是权威结论，<see cref="ControlOutcome.Result"/> 只承载逐项明细。
/// </para>
/// </remarks>
public enum ControlOutcomeKind
{
    /// <summary>已真正下发到硬件适配层，结果见 <see cref="ControlOutcome.Result"/>。</summary>
    Executed = 0,

    /// <summary>未执行：已被更新的（或优先级更高的）请求取代。未产生任何硬件写入。</summary>
    Superseded = 1,

    /// <summary>未执行：能力等级未达 <c>Verified</c>，写入通道未开放。</summary>
    BlockedByCapability = 2,

    /// <summary>未执行：前置条件未满足（例如外接屏拓扑未确认）。</summary>
    BlockedByPrecondition = 3,

    /// <summary>未执行：操作超时。</summary>
    TimedOut = 4,

    /// <summary>未执行或执行中断：适配层抛出异常。</summary>
    Faulted = 5
}

/// <summary>
/// 一次控制请求的完整结果。
/// </summary>
/// <param name="Kind">处理方式。</param>
/// <param name="Intent">原始请求。</param>
/// <param name="Result">逐项结果。始终非空，便于界面统一渲染。</param>
/// <param name="Notes">补充说明：门控依据、重读结论、超时、取代关系等。</param>
/// <remarks>
/// <see cref="Result"/> 中的逐项明细来自适配层（任务 04/05/06），协调器不重写其内容，
/// 只保证「成功必须有确认依据」这一条在整体上可校验。
/// </remarks>
public sealed record ControlOutcome(
    ControlOutcomeKind Kind,
    ControlIntent Intent,
    ControlResult Result,
    IReadOnlyList<string> Notes)
{
    /// <summary>是否真正下发过写入。</summary>
    public bool WasExecuted => Kind == ControlOutcomeKind.Executed;

    /// <summary>写入是否全部确认成功。</summary>
    public bool IsFullyApplied => Kind == ControlOutcomeKind.Executed && Result.IsSuccess;

    /// <summary>构造一个结果。</summary>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    public static ControlOutcome Create(
        ControlOutcomeKind kind,
        ControlIntent intent,
        ControlResult result,
        params string[] notes)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(result);
        return new ControlOutcome(kind, intent, result, notes ?? Array.Empty<string>());
    }

    /// <summary>供日志使用的单行摘要。</summary>
    public string Describe()
    {
        var summary = $"{Intent.Describe()}；处理方式={KindText(Kind)}；逐项状态={Result.Status}";
        return Notes.Count == 0 ? summary : summary + "；" + string.Join("；", Notes);
    }

    /// <summary>处理方式的中文说明。</summary>
    public static string KindText(ControlOutcomeKind kind) => kind switch
    {
        ControlOutcomeKind.Executed => "已执行",
        ControlOutcomeKind.Superseded => "已被更新的请求取代（未执行）",
        ControlOutcomeKind.BlockedByCapability => "能力未达已确证，写入被禁用",
        ControlOutcomeKind.BlockedByPrecondition => "前置条件未满足（未执行）",
        ControlOutcomeKind.TimedOut => "超时（未确认）",
        ControlOutcomeKind.Faulted => "适配层异常",
        _ => kind.ToString()
    };
}
