using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Core.Interfaces;

/// <summary>
/// 键盘背光总开关。
/// </summary>
/// <remarks>
/// <para>
/// 通道为 MSI ACPI <c>Set_Data(0xD3)</c>（见 HWE §31）：载荷首字节低 2 位是档位，
/// 高位保留位必须原样写回，因此实现必须先读后写，不得用常量覆盖整字节。
/// </para>
/// <para>
/// 本接口只服务「总开关」与档位恢复，不承担逐区灯效（设计定稿 §10 明确不做灯效）。
/// </para>
/// </remarks>
public interface IKeyboardBacklightPowerControl
{
    /// <summary>读取当前背光状态。通道不可用时返回 <see langword="null"/>。</summary>
    KeyboardBacklightState? Read();

    /// <summary>开启（恢复到指定档位）或关闭键盘背光，并读回校验。</summary>
    /// <param name="enabled">目标开关状态。</param>
    /// <param name="restoreLevel">开启时使用的档位，1–3。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ControlResult> SetEnabledAsync(bool enabled, int restoreLevel, CancellationToken cancellationToken);
}

/// <summary>
/// Win 键与 Fn 键交换。
/// </summary>
/// <remarks>
/// 通道为 MSI ACPI <c>Set_Data(0xE8)</c> bit4（HWE §31）。该位由固件持久化，
/// 因此写入后会一直生效，直到再次改回或清 CMOS。
/// </remarks>
public interface IWinFnSwapControl
{
    /// <summary>读取当前交换状态。通道不可用时返回 <see langword="null"/>。</summary>
    WinFnSwapState? Read();

    /// <summary>设置交换状态，并读回校验。</summary>
    /// <param name="swapped">目标状态。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ControlResult> SetSwappedAsync(bool swapped, CancellationToken cancellationToken);
}

/// <summary>
/// USB 关机共享供电开关。
/// </summary>
/// <remarks>
/// 通道为 MSI ACPI <c>Set_Data(0xBF)</c> bit5（HWE §31），并把同一开关同步写入
/// UEFI 变量 <c>MsiDCVarData</c> 的 byte[3] bit0，否则重启后固件会用旧值覆盖 EC。
/// </remarks>
public interface IUsbPowerShareControl
{
    /// <summary>读取当前状态。通道不可用时返回 <see langword="null"/>。</summary>
    UsbPowerShareState? Read();

    /// <summary>设置开关状态，并读回校验。</summary>
    /// <param name="enabled">目标状态。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ControlResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// 显示省电（刷新率切换）。
/// </summary>
/// <remarks>
/// 不是硬件写入：通过 <c>ChangeDisplaySettingsEx</c> 把主屏刷新率在
/// 「该分辨率支持的最低 / 最高」之间切换（与 M-Control 的 DisplayPowerSaverController 同路，
/// HWE §31）。只影响内屏与当前分辨率，不改分辨率本身。
/// </remarks>
public interface IDisplayRefreshRatePowerSaver
{
    /// <summary>读取当前刷新率与该分辨率下的可用范围。无可用显示模式时返回 <see langword="null"/>。</summary>
    DisplayRefreshRateState? Read();

    /// <summary>切换到最高刷新率（省电关闭）或最低刷新率（省电开启）。</summary>
    /// <param name="enabled">是否开启省电（= 使用最低刷新率）。</param>
    /// <param name="cancellationToken">取消标记。</param>
    Task<ControlResult> SetEnabledAsync(bool enabled, CancellationToken cancellationToken);
}

/// <summary>
/// Win 键屏蔽（仅应用运行期间生效）。
/// </summary>
/// <remarks>
/// <para>
/// 走进程内的低级键盘钩子（<c>WH_KEYBOARD_LL</c>）吞掉 <c>LWin</c>/<c>RWin</c>，
/// 与 M-Control 的做法一致（<c>MainForm</c> 的键盘钩子，HWE §31）。
/// </para>
/// <para>
/// <b>限制必须如实告知用户</b>：这是软件层屏蔽，不是固件开关，进程退出即失效；
/// 且钩子被系统摘除（超时）时会出现「设置显示已禁用但 Win 键可用」的偏差，
/// 因此实现必须提供 <see cref="IsActive"/> 让界面读回真实状态。
/// </para>
/// </remarks>
public interface IWindowsKeyLock
{
    /// <summary>钩子当前是否真的生效。</summary>
    bool IsActive { get; }

    /// <summary>启用或停用屏蔽。返回钩子是否处于期望状态。</summary>
    /// <param name="active">目标状态。</param>
    bool SetActive(bool active);
}
