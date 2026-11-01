using MsiControl.Core;
using MsiControl.Core.Results;

namespace MsiControl.Fans;

/// <summary>风扇操作类型。</summary>
public enum FanOperationKind
{
    /// <summary>读取硬件状态。</summary>
    ReadState = 0,

    /// <summary>应用自定义曲线。</summary>
    ApplyCurve = 1,

    /// <summary>切回固件自动策略。</summary>
    SetAutomatic = 2,

    /// <summary>切换强冷（全速）。</summary>
    SetFullSpeed = 3,

    /// <summary>恢复本次启动的首次写入前快照。</summary>
    RestoreBootSnapshot = 4
}

/// <summary>
/// 一次风扇操作的结果。
/// </summary>
/// <remarks>
/// <para>
/// 为什么在 <see cref="ControlResult"/> 之外再包一层：<see cref="ControlResult"/>
/// 描述的是**硬件逐项**结果，而「能否发起这次写入」是业务前置判断的结果。
/// 当写入能力未达 <see cref="CapabilityLevel.Verified"/> 时，
/// 正确的做法是**根本不调用硬件**并如实说明（约定 §1.1），而不是伪造一条
/// 「硬件拒绝」的结果。
/// </para>
/// <para>
/// <see cref="OperationStatus.Succeeded"/> 必须有非空的 <see cref="ConfirmationBasis"/>
/// （任务书 §8.5），<see cref="Validate"/> 会在测试中检出违规组合。
/// </para>
/// </remarks>
/// <param name="Kind">操作类型。</param>
/// <param name="Status">操作状态。</param>
/// <param name="Message">面向用户的一句话结果说明。</param>
/// <param name="Control">硬件逐项结果。未下发时为 <see langword="null"/>。</param>
/// <param name="ReadBack">操作后的读回结果。未读回时为 <see langword="null"/>。</param>
/// <param name="ConfirmationBasis">确认依据。成功时必填。</param>
public sealed record FanOperationResult(
    FanOperationKind Kind,
    OperationStatus Status,
    string Message,
    ControlResult? Control = null,
    FanReadResult? ReadBack = null,
    string? ConfirmationBasis = null)
{
    /// <summary>是否成功。</summary>
    public bool IsSuccess => Status == OperationStatus.Succeeded;

    /// <summary>是否被前置条件拦下（能力未达 Verified、约束未验证等）。</summary>
    public bool IsBlocked => Status == OperationStatus.Unsupported;

    /// <summary>是否缺少确认依据却自称成功（违规状态）。</summary>
    public bool IsUnsubstantiatedSuccess
        => Status == OperationStatus.Succeeded && string.IsNullOrWhiteSpace(ConfirmationBasis);

    /// <summary>构造一个被前置条件拦下的结果。**不产生任何硬件结果对象。**</summary>
    /// <param name="kind">操作类型。</param>
    /// <param name="message">拦截原因，必须具体到能力键与依据。</param>
    /// <returns>状态为 <see cref="OperationStatus.Unsupported"/> 的结果。</returns>
    /// <exception cref="ArgumentException"><paramref name="message"/> 为空。</exception>
    public static FanOperationResult Blocked(FanOperationKind kind, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new FanOperationResult(kind, OperationStatus.Unsupported, message);
    }

    /// <summary>构造一个失败结果。</summary>
    public static FanOperationResult Failed(
        FanOperationKind kind,
        string message,
        ControlResult? control = null,
        FanReadResult? readBack = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new FanOperationResult(kind, OperationStatus.Failed, message, control, readBack);
    }

    /// <summary>校验本结果是否自相矛盾。</summary>
    /// <returns>问题列表。为空表示合规。</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(Message))
        {
            problems.Add("结果缺少说明文案：用户必须能看到「为什么」。");
        }

        if (IsUnsubstantiatedSuccess)
        {
            problems.Add("标记为成功但缺少确认依据（ConfirmationBasis）。");
        }

        if (Status == OperationStatus.Unsupported && Control is not null)
        {
            problems.Add("被前置条件拦下时不应产生硬件结果对象：那会让界面以为硬件被调用过。");
        }

        return problems;
    }
}
