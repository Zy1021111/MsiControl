namespace MsiControl.Core.Models;

/// <summary>
/// 音频端点的当前状态。
/// </summary>
/// <remarks>
/// 构造经工厂方法，强制「有读数才有对象」：读失败必须返回 <see langword="null"/>，
/// 不允许用 <c>Muted=false, Volume=0</c> 之类的默认值冒充读数（与
/// <see cref="FanReading"/>、<see cref="SensorReading"/> 同构）。
/// </remarks>
public sealed record AudioEndpointState
{
    private AudioEndpointState(bool muted, float volumeScalar, int channelCount)
    {
        Muted = muted;
        VolumeScalar = volumeScalar;
        ChannelCount = channelCount;
    }

    /// <summary>是否已静音。</summary>
    public bool Muted { get; }

    /// <summary>主音量标量，0.0–1.0。</summary>
    public float VolumeScalar { get; }

    /// <summary>端点声道数。</summary>
    public int ChannelCount { get; }

    /// <summary>主音量的百分比表达（四舍五入到整数）。</summary>
    public int VolumePercent => (int)Math.Round(VolumeScalar * 100, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 构造一个状态。
    /// </summary>
    /// <param name="muted">是否静音。</param>
    /// <param name="volumeScalar">主音量标量，会被夹到 0–1。</param>
    /// <param name="channelCount">声道数。未知时传 0。</param>
    /// <exception cref="ArgumentOutOfRangeException">音量标量不是有限数。</exception>
    public static AudioEndpointState Create(bool muted, float volumeScalar, int channelCount = 0)
    {
        if (float.IsNaN(volumeScalar) || float.IsInfinity(volumeScalar))
        {
            throw new ArgumentOutOfRangeException(nameof(volumeScalar), volumeScalar, "音量标量必须是有限数值。");
        }

        return new AudioEndpointState(muted, Math.Clamp(volumeScalar, 0f, 1f), Math.Max(0, channelCount));
    }
}

/// <summary>
/// 内屏亮度取值范围。
/// </summary>
/// <param name="MinPercent">最小百分比。</param>
/// <param name="MaxPercent">最大百分比。</param>
/// <param name="StepPercent">建议步进百分比（由取值档位推导，至少为 1）。</param>
public sealed record BrightnessRange(int MinPercent, int MaxPercent, int StepPercent)
{
    /// <summary>
    /// 由 WMI 上报的可用档位推导范围。
    /// </summary>
    /// <param name="levels">各档位百分比，允许无序与重复。</param>
    /// <returns>档位为空时返回 <see langword="null"/>。</returns>
    public static BrightnessRange? FromLevels(IReadOnlyList<int> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        if (levels.Count == 0)
        {
            return null;
        }

        var ordered = levels.Distinct().OrderBy(v => v).ToArray();
        var step = ordered.Length > 1 ? ordered[1] - ordered[0] : 1;
        return new BrightnessRange(ordered[0], ordered[^1], Math.Max(1, step));
    }
}

/// <summary>
/// 指向设备（触控板）状态。
/// </summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="IsPrecisionTouchpad">状态是否来自 Windows 精密触控板官方状态位。</param>
/// <param name="Source">读数来源说明，用于界面如实呈现依据。</param>
public sealed record PointingDeviceState(bool Enabled, bool IsPrecisionTouchpad, string Source)
{
    /// <summary>
    /// 构造状态。
    /// </summary>
    /// <param name="enabled">是否启用。</param>
    /// <param name="isPrecisionTouchpad">是否来自精密触控板官方状态。</param>
    /// <param name="source">来源说明。</param>
    /// <exception cref="ArgumentException">来源说明为空。</exception>
    public static PointingDeviceState Create(bool enabled, bool isPrecisionTouchpad, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new PointingDeviceState(enabled, isPrecisionTouchpad, source);
    }
}

/// <summary>
/// 蓝牙射频（Radio）状态。
/// </summary>
/// <remarks>
/// <para>
/// 只描述「射频开关是否打开」，不含配对设备、可见性等语义。
/// 取值来自 WinRT <c>Windows.Devices.Radios.Radio.State</c>，与系统快速设置面板同一状态源，
/// 因此不存在「应用侧副本」。
/// </para>
/// <para>
/// <see cref="IsOn"/> 为 <see langword="null"/> 表示射频处于中间态（<c>Unknown</c>／<c>Disabled</c>），
/// 此时不得把它当作「已关闭」呈现——那会把「被系统策略禁用」说成「用户关掉了」。
/// </para>
/// </remarks>
/// <param name="StateText">系统上报的状态名（<c>On</c>/<c>Off</c>/<c>Disabled</c>/<c>Unknown</c>）。</param>
/// <param name="Name">射频设备名（通常为「蓝牙」）。</param>
public sealed record BluetoothRadioState(string StateText, string Name)
{
    /// <summary>射频是否确定处于开启状态。中间态为 <see langword="null"/>。</summary>
    public bool? IsOn => StateText switch
    {
        "On" => true,
        "Off" => false,
        _ => null
    };

    /// <summary>人类可读的状态描述。</summary>
    public string Describe() => StateText switch
    {
        "On" => "已开启",
        "Off" => "已关闭",
        "Disabled" => "已被系统禁用（可能由策略或飞行模式导致）",
        "Unknown" => "状态不可知",
        _ => $"未知状态 {StateText}"
    };

    /// <summary>
    /// 构造状态。
    /// </summary>
    /// <param name="stateText">系统状态名。</param>
    /// <param name="name">设备名。</param>
    /// <exception cref="ArgumentException">状态名为空。</exception>
    public static BluetoothRadioState Create(string stateText, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateText);
        return new BluetoothRadioState(stateText, string.IsNullOrWhiteSpace(name) ? "蓝牙" : name);
    }
}
