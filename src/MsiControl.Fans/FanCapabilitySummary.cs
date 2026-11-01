using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Fans;

/// <summary>
/// 风扇相关能力的对外说明。
/// </summary>
/// <remarks>
/// 任务书 §1 原则 8：不可用入口**必须给出原因**。本类型把三项风扇能力键的等级与依据
/// 统一成可直接展示的文案，供桌面层（任务 02）与设置窗口复用，
/// 避免每处界面各写一套措辞。
/// </remarks>
public static class FanCapabilitySummary
{
    /// <summary>风扇模块涉及的能力键。</summary>
    public static IReadOnlyList<string> Keys { get; } = new[]
    {
        CapabilityKeys.FanCurve,
        CapabilityKeys.FanFullSpeed,
        CapabilityKeys.FanMode,
        CapabilityKeys.FanCalibration
    };

    /// <summary>逐项说明各能力的等级与依据。</summary>
    /// <param name="capabilities">设备能力集合。</param>
    /// <returns>与 <see cref="Keys"/> 等长的说明列表。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="capabilities"/> 为 <see langword="null"/>。</exception>
    public static IReadOnlyList<string> Describe(DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        return Keys.Select(key =>
        {
            var status = capabilities.Get(key);
            var line = $"{key}：{status.Level}";
            return status.AllowsWrite
                ? $"{line}（可读写）"
                : $"{line}（写入禁用：{status.DescribeUnavailable()}）";
        }).ToArray();
    }

    /// <summary>是否存在任一可写入的风扇能力。</summary>
    public static bool AnyWriteAllowed(DeviceCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        return Keys.Any(capabilities.AllowsWrite);
    }

    /// <summary>是否三项全部未达可写入等级。</summary>
    public static bool AllWritesBlocked(DeviceCapabilities capabilities) => !AnyWriteAllowed(capabilities);
}
