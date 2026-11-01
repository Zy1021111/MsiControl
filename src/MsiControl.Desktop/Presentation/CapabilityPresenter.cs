using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Desktop.Presentation;

/// <summary>
/// 单个能力入口在界面上的呈现方式。
/// </summary>
/// <param name="Key">能力键。</param>
/// <param name="Title">界面标题。</param>
/// <param name="Level">能力等级。</param>
/// <param name="Enabled">入口是否可用。</param>
/// <param name="Reason">禁用原因。可用时为空串。</param>
public sealed record CapabilityEntry(
    string Key,
    string Title,
    CapabilityLevel Level,
    bool Enabled,
    string Reason);

/// <summary>
/// 能力等级到界面状态与文案的映射。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §16.1 的强制规则在此落地：
/// <list type="bullet">
/// <item><see cref="CapabilityLevel.Verified"/>：可开放读写。</item>
/// <item><see cref="CapabilityLevel.Detected"/>：<b>仅允许只读展示</b>，写入必须禁用。</item>
/// <item><see cref="CapabilityLevel.Unknown"/>：一律禁用。</item>
/// <item><see cref="CapabilityLevel.Unsupported"/>：显式禁用并说明依据。</item>
/// </list>
/// </para>
/// <para>
/// 任务 02 特有要求：不可用入口**必须给出原因文案**，不能只把按钮变灰
/// （任务书提示词第 5 条）。本类型是产生这些文案的唯一入口。
/// </para>
/// </remarks>
public static class CapabilityPresenter
{
    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [CapabilityKeys.FanCurve] = "风扇曲线",
        [CapabilityKeys.FanFullSpeed] = "强冷（全速）",
        [CapabilityKeys.FanCalibration] = "风扇校准",
        [CapabilityKeys.FanMode] = "风扇模式切换",
        [CapabilityKeys.FanPwm] = "风扇输出调节（EC）",
        [CapabilityKeys.ThermalRead] = "CPU/GPU 精确温度",
        [CapabilityKeys.ThermalFallback] = "通用热区温度（兜底）",
        [CapabilityKeys.CpuPowerLimit] = "CPU 功耗限制",
        [CapabilityKeys.CpuTurboLimit] = "CPU 睿频上限",
        [CapabilityKeys.GpuPowerLimit] = "GPU 功耗限制",
        [CapabilityKeys.GpuEcoFallback] = "GPU 兼容节能策略",
        [CapabilityKeys.PowerRange] = "可写范围查询",
        [CapabilityKeys.GraphicsMode] = "显卡模式切换",
        [CapabilityKeys.SystemPowerRead] = "整机功耗读取",
        [CapabilityKeys.BatteryPowerRead] = "电池侧功耗读取",
        [CapabilityKeys.EcAccess] = "EC 寄存器访问",
        [CapabilityKeys.FirmwareScenario] = "固件性能场景",
        [CapabilityKeys.PerformanceStage] = "性能档位切换（EC Shift）",
        [CapabilityKeys.AudioVolume] = "扬声器音量与静音",
        [CapabilityKeys.AudioMicrophone] = "麦克风静音",
        [CapabilityKeys.DisplayBrightness] = "内屏亮度",
        [CapabilityKeys.InputTouchpad] = "触控板开关",
        [CapabilityKeys.HotkeyFn] = "Fn 组合键监听",
        [CapabilityKeys.SystemBluetooth] = "蓝牙射频开关",
        [CapabilityKeys.KeyboardBacklight] = "键盘背光总开关",
        [CapabilityKeys.InputWinFnSwap] = "Win / Fn 键交换",
        [CapabilityKeys.PowerUsbShare] = "USB 关机共享供电",
        [CapabilityKeys.DisplayPowerSaver] = "显示省电（刷新率）",
        [CapabilityKeys.InputWindowsKey] = "Win 键屏蔽"
    };

    /// <summary>取得能力键对应的界面标题。</summary>
    public static string TitleOf(string key)
        => Titles.TryGetValue(key, out var title) ? title : key;

    /// <summary>取得能力等级的中文说明。</summary>
    public static string LevelText(CapabilityLevel level) => level switch
    {
        CapabilityLevel.Verified => "已确证",
        CapabilityLevel.Detected => "线索",
        CapabilityLevel.Unknown => "待验证",
        CapabilityLevel.Unsupported => "已确认不可用",
        _ => "未知等级"
    };

    /// <summary>
    /// 描述一个能力入口。
    /// </summary>
    /// <param name="capabilities">能力集合。未登记键一律按 <see cref="CapabilityLevel.Unknown"/> 处理。</param>
    /// <param name="key">能力键。</param>
    /// <param name="requiresWrite">该入口是否需要写入权限。</param>
    public static CapabilityEntry Describe(DeviceCapabilities capabilities, string key, bool requiresWrite)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var status = capabilities.Get(key);
        var enabled = requiresWrite ? status.AllowsWrite : status.AllowsRead;

        return new CapabilityEntry(
            key,
            TitleOf(key),
            status.Level,
            enabled,
            enabled ? string.Empty : BuildReason(status, requiresWrite));
    }

    /// <summary>列出全部已登记能力入口，供「设备与诊断」页展示。</summary>
    public static IReadOnlyList<CapabilityEntry> DescribeAll(DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return capabilities.All
            .OrderBy(s => s.Key, StringComparer.Ordinal)
            .Select(s => Describe(capabilities, s.Key, requiresWrite: true))
            .ToArray();
    }

    private static string BuildReason(CapabilityStatus status, bool requiresWrite)
    {
        var detail = status.DescribeUnavailable();

        return status.Level switch
        {
            CapabilityLevel.Verified => requiresWrite
                ? string.Empty
                : "该能力已确证，但本入口需要写入权限。",
            CapabilityLevel.Detected =>
                $"线索：接口或方法存在，但语义与可用性尚未验证，因此仅允许只读展示，写入已禁用。依据：{detail}",
            CapabilityLevel.Unknown =>
                $"待验证：本机尚无该能力的证据，按未知处理并禁用。依据：{detail}",
            CapabilityLevel.Unsupported =>
                $"已确认不可用，入口已禁用。依据：{detail}",
            _ => $"能力等级未知，按最严格方式禁用。依据：{detail}"
        };
    }
}
