using System.Globalization;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Persistence.Settings;

namespace MsiControl.Desktop.Presentation;

/// <summary>
/// 读数到界面文本的格式化。
/// </summary>
/// <remarks>
/// <para>
/// 集中在一处的理由：任务书 §6.4 要求四态（有效 / 休眠 / 不可用 / 过期）在界面
/// 用语上一致，若各控件各自拼字符串，很快会出现「不可用」「读取失败」「N/A」混用。
/// </para>
/// <para>
/// <b>单位换算一律走 <see cref="Measurement"/>，本类型不自行做算术</b>
/// （docs/接口与工程约定.md §2）。
/// </para>
/// </remarks>
public static class ValueFormatter
{
    /// <summary>无数据时的占位文本。</summary>
    public const string Placeholder = "—";

    /// <summary>过期标注后缀。</summary>
    public const string StaleSuffix = "（过期）";

    /// <summary>返回状态对应的界面文本；有效读数返回空串。</summary>
    public static string StatusText(ReadingStatus status) => status switch
    {
        ReadingStatus.Valid => string.Empty,
        ReadingStatus.Sleeping => "休眠",
        ReadingStatus.Unavailable => "不可用",
        ReadingStatus.Stale => "过期",
        _ => "未知状态"
    };

    /// <summary>
    /// 格式化一个传感器读数。
    /// </summary>
    /// <param name="reading">读数。</param>
    /// <param name="decimals">小数位。</param>
    /// <param name="suffixStale">
    /// 是否在过期值后追加「（过期）」。任务书 §6.4 要求过期数据明确标注，
    /// 因此默认追加；仅当调用方已在别处标注时才传 <see langword="false"/>。
    /// </param>
    /// <returns>界面文本，含单位。</returns>
    public static string Describe(SensorReading reading, int decimals = 1, bool suffixStale = true)
    {
        ArgumentNullException.ThrowIfNull(reading);

        switch (reading.Status)
        {
            case ReadingStatus.Sleeping:
                return "休眠";

            case ReadingStatus.Unavailable:
                return "不可用";

            case ReadingStatus.Valid:
            case ReadingStatus.Stale:
                if (!reading.Value.HasValue)
                {
                    return Placeholder;
                }

                var number = reading.Value.Value.ToString(
                    "F" + decimals.ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture);
                var text = $"{number} {reading.Unit}";
                return reading.Status == ReadingStatus.Stale && suffixStale ? text + StaleSuffix : text;

            default:
                return Placeholder;
        }
    }

    /// <summary>格式化温度读数。</summary>
    public static string Temperature(SensorReading reading) => Describe(reading, decimals: 1);

    /// <summary>
    /// 格式化频率读数：内部单位为 MHz，界面显示 GHz。
    /// </summary>
    /// <remarks>任务书 §6.5：只显示一个统一频率值，并在界面注明统计口径。</remarks>
    public static string FrequencyGigahertz(SensorReading reading, int decimals = 2)
    {
        ArgumentNullException.ThrowIfNull(reading);

        if (reading.Status is ReadingStatus.Sleeping or ReadingStatus.Unavailable)
        {
            return reading.Status == ReadingStatus.Sleeping ? "休眠" : "不可用";
        }

        if (!reading.Value.HasValue)
        {
            return Placeholder;
        }

        var gigahertz = Measurement.MegahertzToGigahertz(reading.Value.Value);
        var text = gigahertz.ToString(
            "F" + decimals.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture) + " GHz";
        return reading.Status == ReadingStatus.Stale ? text + StaleSuffix : text;
    }

    /// <summary>格式化功耗读数（瓦）。</summary>
    public static string Power(SensorReading reading, int decimals = 1) => Describe(reading, decimals);

    /// <summary>
    /// 格式化风扇读数。
    /// </summary>
    /// <param name="fan">风扇读数。</param>
    /// <param name="mode">显示偏好。</param>
    /// <returns>
    /// 界面文本。偏好为百分比但**没有有效校准基准**时回落显示 RPM——
    /// 这是任务书 §6.4 的硬要求，不允许用推算的百分比冒充实际百分比。
    /// </returns>
    public static string Fan(FanReading fan, FanDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(fan);

        var rpm = fan.Rpm;
        if (rpm.Status is ReadingStatus.Sleeping or ReadingStatus.Unavailable)
        {
            return rpm.Status == ReadingStatus.Sleeping ? "休眠" : "不可用";
        }

        var text = mode == FanDisplayMode.Percent && fan.HasCalibration
            ? fan.ActualPercent!.Value.ToString("F0", CultureInfo.InvariantCulture) + " %"
            : rpm.Value.HasValue
                ? rpm.Value.Value.ToString(CultureInfo.InvariantCulture) + " RPM"
                : Placeholder;

        return rpm.Status == ReadingStatus.Stale ? text + StaleSuffix : text;
    }

    /// <summary>
    /// 返回风扇显示的回落说明；无需说明时返回 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 场景：用户偏好百分比但尚未校准。此时界面显示 RPM，必须同时说明原因，
    /// 否则用户会以为设置没生效（任务书 §6.4、§7.5）。
    /// </remarks>
    public static string? FanFallbackNote(FanReading fan, FanDisplayMode mode)
    {
        ArgumentNullException.ThrowIfNull(fan);
        return mode == FanDisplayMode.Percent && !fan.HasCalibration
            ? "无有效校准基准，已回落显示 RPM"
            : null;
    }

    /// <summary>
    /// 格式化曲线目标输出百分比。
    /// </summary>
    /// <remarks>
    /// 与实际转速百分比是**两个不同的量**，因此独立成方法并带「目标」前缀，
    /// 避免被误当成实际转速（任务书 §6.4）。
    /// </remarks>
    public static string TargetPercent(FanReading fan)
    {
        ArgumentNullException.ThrowIfNull(fan);
        return fan.TargetPercent.HasValue
            ? "目标 " + fan.TargetPercent.Value.ToString("F0", CultureInfo.InvariantCulture) + " %"
            : "目标 —";
    }

    /// <summary>格式化整机功耗，并注明口径。</summary>
    public static string SystemPower(SystemPowerReading power)
    {
        ArgumentNullException.ThrowIfNull(power);

        if (!power.IsAvailable)
        {
            return "不可用";
        }

        var text = Describe(power.Reading, decimals: 1);
        return $"{text}（{PowerSourceText(power.Kind)}）";
    }

    /// <summary>整机功耗口径的中文说明。</summary>
    /// <remarks>三种口径不可混用、不可相加（任务书 §6.5）。</remarks>
    public static string PowerSourceText(PowerSourceKind kind) => kind switch
    {
        PowerSourceKind.BatterySide => "电池侧",
        PowerSourceKind.AdapterInput => "适配器输入",
        PowerSourceKind.WallSocket => "插座",
        _ => "来源未确定"
    };

    /// <summary>格式化扩展传感器读数。</summary>
    public static string Extended(SensorReading reading) => Describe(reading, decimals: 1);

    /// <summary>
    /// 格式化采样时间戳，用于「数据来源与更新时间」折叠展示。
    /// </summary>
    public static string SampledAt(DateTimeOffset? sampledAt)
        => sampledAt.HasValue
            ? sampledAt.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : Placeholder;
}
