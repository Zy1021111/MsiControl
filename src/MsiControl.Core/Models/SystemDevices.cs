namespace MsiControl.Core.Models;

/// <summary>
/// 键盘背光电源状态。
/// </summary>
/// <remarks>
/// 依据 <c>docs/hardware-evidence.md</c> §31：MSI ACPI <c>Get_Data(0xD3)</c> 的载荷首字节
/// 低 2 位即背光档位（0=关、1–3=档），高位保留位必须原样写回。
/// </remarks>
/// <param name="Level">背光档位，0–3。</param>
public readonly record struct KeyboardBacklightState(int Level)
{
    /// <summary>是否处于点亮状态（档位 &gt; 0）。</summary>
    public bool Enabled => Level > 0;
}

/// <summary>
/// Win 键与 Fn 键的交换状态。
/// </summary>
/// <remarks>
/// 依据 HWE §31：MSI ACPI <c>Get_Data(0xE8)</c> 载荷首字节 bit4=1 表示已交换。
/// 该位由固件持久保存，重启后仍然有效。
/// </remarks>
/// <param name="Swapped">是否已把 Win 键与 Fn 键对调。</param>
/// <param name="Raw">原始载荷首字节，写回时必须保留其余位。</param>
public readonly record struct WinFnSwapState(bool Swapped, byte Raw);

/// <summary>
/// USB 关机共享供电状态。
/// </summary>
/// <remarks>
/// 依据 HWE §31：<c>Get_Data(0xBF)</c> 载荷首字节 bit3=1 表示硬件支持，
/// bit5=1 表示已开启。UEFI 变量 <c>MsiDCVarData</c> 的 byte[3] bit0 是同一开关的持久副本。
/// </remarks>
/// <param name="Supported">硬件是否声明支持该功能。</param>
/// <param name="Enabled">当前是否开启。</param>
/// <param name="Raw">原始载荷首字节；写回时必须保留其余位。</param>
public readonly record struct UsbPowerShareState(bool Supported, bool Enabled, byte Raw);

/// <summary>
/// 显示刷新率状态（省电模式的读写依据）。
/// </summary>
/// <param name="CurrentHz">当前刷新率。</param>
/// <param name="MinimumHz">该分辨率下支持的最低刷新率。</param>
/// <param name="MaximumHz">该分辨率下支持的最高刷新率。</param>
public readonly record struct DisplayRefreshRateState(int CurrentHz, int MinimumHz, int MaximumHz)
{
    /// <summary>是否处于最低刷新率（省电态）。</summary>
    public bool AtMinimum => CurrentHz <= MinimumHz;
}
