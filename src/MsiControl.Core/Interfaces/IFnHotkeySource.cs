using MsiControl.Core.Models;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// Fn 组合键事件源。
/// </summary>
/// <remarks>
/// <para>
/// <b>这是监听通道，不是劫持通道。</b> Fn 组合键由 EC 固件在键盘扫描码产生前消费，
/// Windows 收不到「Fn 被按下」，因此应用既无法阻拦、也不需要阻拦——
/// 固件已经把动作做完了。本接口的唯一职责是把「刚发生了哪个动作」告诉上层，
/// 让界面状态与硬件真实状态保持一致（HWE §28）。
/// </para>
/// <para>
/// 实现必须做到：订阅失败返回 false 而非抛异常；事件码不认识时以
/// <see cref="FnHotkeyAction.Unknown"/> 上报，绝不猜测键位。
/// </para>
/// </remarks>
public interface IFnHotkeySource : IDisposable
{
    /// <summary>收到一次 Fn 热键事件。可能在工作线程触发。</summary>
    event EventHandler<FnHotkeyEvent>? Pressed;

    /// <summary>
    /// 启动监听。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>是否成功建立订阅。失败时上层应把 <c>hotkey.fn</c> 降级并说明原因。</returns>
    Task<bool> StartAsync(CancellationToken cancellationToken);

    /// <summary>停止监听并释放订阅。</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
