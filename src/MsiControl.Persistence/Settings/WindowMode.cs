namespace MsiControl.Persistence.Settings;

/// <summary>
/// 窗口形态。
/// </summary>
/// <remarks>
/// 两种窗口共用同一份采样与控制核心，切换窗口不重复初始化硬件（任务书 §10、design.md §4.4）。
/// 该枚举同时用于「开机启动显示哪个窗口」与托盘入口。
/// </remarks>
public enum WindowMode
{
    /// <summary>正常窗口：包含全部功能入口。</summary>
    Normal = 0,

    /// <summary>迷你窗口：仅性能/显卡切换与基础监控，不置顶、不含强冷按钮。</summary>
    Mini = 1
}

/// <summary>
/// 风扇数值的显示方式。
/// </summary>
/// <remarks>
/// 任务书 §6.4：校准有效后默认显示百分比；未校准时必须回落到 RPM。
/// 本枚举表达的是**用户偏好**，实际显示由 <c>HasCalibration</c> 决定是否回落。
/// </remarks>
public enum FanDisplayMode
{
    /// <summary>百分比。仅在存在有效校准基准时可用。</summary>
    Percent = 0,

    /// <summary>转速 RPM。</summary>
    Rpm = 1
}
