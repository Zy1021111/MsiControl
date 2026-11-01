namespace MsiControl.Desktop.Windows.Osd;

/// <summary>桌面提示的语气，决定右侧徽标的配色。</summary>
internal enum OsdTone
{
    /// <summary>中性信息（音量、亮度、模式名）。</summary>
    Neutral = 0,

    /// <summary>功能已开启或已恢复。</summary>
    Success = 1,

    /// <summary>被静音、被禁用等需要用户留意的状态。</summary>
    Warning = 2,

    /// <summary>操作未完成。</summary>
    Danger = 3
}

/// <summary>
/// 一条桌面提示的内容。
/// </summary>
/// <param name="Title">左侧主标题，例如「麦克风」。</param>
/// <param name="Badge">右侧徽标文字，例如「已开启」。</param>
/// <param name="Tone">徽标语气。</param>
/// <remarks>
/// 之所以把文案独立成纯数据：提示的**取值规则**（哪个状态配哪个词、哪种语气）
/// 是可以单测的，而窗体绘制不能。把两者分开后，文案回归不需要启动界面。
/// </remarks>
internal sealed record OsdMessage(string Title, string Badge, OsdTone Tone)
{
    /// <summary>扬声器静音状态。</summary>
    public static OsdMessage SpeakerMute(bool muted)
        => new("扬声器", muted ? "已静音" : "已恢复", muted ? OsdTone.Warning : OsdTone.Success);

    /// <summary>麦克风静音状态。</summary>
    /// <remarks>
    /// 「已开启」用于麦克风可用状态，「已静音」用于静音状态：后者是用户会踩坑的状态，
    /// 因此给 Warning 语气（红色徽标），与参考实现的视觉一致。
    /// </remarks>
    public static OsdMessage Microphone(bool muted)
        => new("麦克风", muted ? "已静音" : "已开启", muted ? OsdTone.Warning : OsdTone.Success);

    /// <summary>音量变化结果。</summary>
    public static OsdMessage Volume(int percent)
        => new("音量", $"{percent}%", OsdTone.Neutral);

    /// <summary>亮度变化结果。</summary>
    public static OsdMessage Brightness(int percent)
        => new("亮度", $"{percent}%", OsdTone.Neutral);

    /// <summary>触控板开关状态。</summary>
    public static OsdMessage Touchpad(bool enabled)
        => new("触摸板", enabled ? "已启用" : "已禁用", enabled ? OsdTone.Success : OsdTone.Warning);

    /// <summary>性能模式名称。</summary>
    public static OsdMessage PerformanceStage(string stageName)
        => new("性能模式", stageName, OsdTone.Neutral);

    /// <summary>蓝牙射频开关状态。</summary>
    /// <remarks>
    /// 与 <see cref="BluetoothSettings"/> 区分：前者是射频真的开了/关了，
    /// 后者只是「把设置页拉起来了」，两者不能共用同一句文案。
    /// </remarks>
    public static OsdMessage Bluetooth(bool on)
        => new("蓝牙", on ? "已开启" : "已关闭", on ? OsdTone.Success : OsdTone.Warning);

    /// <summary>蓝牙设置页已打开。</summary>
    public static OsdMessage BluetoothSettings()
        => new("蓝牙", "已打开设置", OsdTone.Neutral);

    /// <summary>操作未完成。</summary>
    /// <remarks>
    /// 失败原因写进日志而不是塞进提示条：提示条只有标题和徽标两段，
    /// 把长原因挤进去会撑破版面且读不清；徽标统一显示「未完成」，颜色用 Danger。
    /// </remarks>
    public static OsdMessage Failure(string title)
        => new(title, "未完成", OsdTone.Danger);
}
