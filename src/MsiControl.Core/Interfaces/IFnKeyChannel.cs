using MsiControl.Core.Models;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// Fn 键的原始信号通道（不做动作映射，只上报「哪个通道来了什么码」）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要独立于 <see cref="IFnHotkeySource"/>：WMI 事件通道只承载 F5/F6/F7 三个键
/// （本机实测），音量/亮度/触控板走的是别的通路。把「原始信号」和「动作结论」分开，
/// 才能在码表不全时如实记录、在补齐码表后只改映射而不动采集。
/// </para>
/// <para>
/// 实现必须<b>只读</b>：不得为了让某个键上报而写 EC 寄存器或改系统设置。
/// </para>
/// </remarks>
public interface IFnKeyChannel : IDisposable
{
    /// <summary>通道名，取值见 <see cref="FnKeySignal.Channels"/>。</summary>
    string Channel { get; }

    /// <summary>收到一条原始信号。回调线程由实现决定（非 UI 线程）。</summary>
    event EventHandler<FnKeySignal>? Signaled;

    /// <summary>
    /// 启动失败或不具备条件时的原因；可用时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 放在接口上而不是让调用方向下转型：桌面壳只认识接口，
    /// 由实现自己描述「为什么起不来」，分层才不会被绕过。
    /// </remarks>
    string? StartupError { get; }

    /// <summary>启动采集。</summary>
    /// <returns>启动成功返回 <see langword="true"/>；失败时上层记日志并降级，不抛异常。</returns>
    bool Start();
}
