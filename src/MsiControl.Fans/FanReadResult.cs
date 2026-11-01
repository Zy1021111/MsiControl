using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices;

namespace MsiControl.Fans;

/// <summary>
/// 一次硬件读取的**可信程度**。
/// </summary>
/// <remarks>
/// 与 <see cref="Core.ReadingStatus"/> 不同：后者描述「传感器读数」的有效性，
/// 本枚举描述「本次读取能不能支撑业务结论」。两者不得互相替代。
/// </remarks>
public enum FanReadStatus
{
    /// <summary>模式与双通道曲线均已读到，可用于判断与恢复。</summary>
    Verified = 0,

    /// <summary>
    /// 通道可达，但返回内容无法解读（字节语义未确证）。
    /// <b>不得据此判断当前控制模式，也不得据此构造恢复快照。</b>
    /// </summary>
    TransportOnly = 1,

    /// <summary>读取通道不可用（接口缺失、权限不足或调用失败）。</summary>
    Unavailable = 2
}

/// <summary>
/// 一次风扇硬件读取的结果：状态 + 可信程度 + 原始诊断。
/// </summary>
/// <param name="Status">可信程度。</param>
/// <param name="State">硬件状态。不可信时为 <see cref="FanHardwareState.Unknown"/>。</param>
/// <param name="SampledAt">读取时刻。</param>
/// <param name="Message">说明为什么处于该可信程度。</param>
/// <param name="Raw">原始缓冲区诊断。未取得时为 <see langword="null"/>。</param>
public sealed record FanReadResult(
    FanReadStatus Status,
    FanHardwareState State,
    DateTimeOffset SampledAt,
    string Message,
    FanRawReport? Raw = null)
{
    /// <summary>是否已确证读到固件中生效的曲线。</summary>
    public bool CanSnapshot => Status == FanReadStatus.Verified
                               && State.CpuCurve is not null
                               && State.GpuCurve is not null;

    /// <summary>构造「已确证」结果。</summary>
    public static FanReadResult Verified(
        FanHardwareState state,
        DateTimeOffset sampledAt,
        string message,
        FanRawReport? raw = null)
        => new(FanReadStatus.Verified, state, sampledAt, message, raw);

    /// <summary>构造「通道可达但语义未确证」结果。</summary>
    public static FanReadResult TransportOnly(DateTimeOffset sampledAt, string message, FanRawReport? raw = null)
        => new(FanReadStatus.TransportOnly, FanHardwareState.Unknown, sampledAt, message, raw);

    /// <summary>构造「通道不可用」结果。</summary>
    public static FanReadResult Unavailable(DateTimeOffset sampledAt, string message)
        => new(FanReadStatus.Unavailable, FanHardwareState.Unknown, sampledAt, message);
}
