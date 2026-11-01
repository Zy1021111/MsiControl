namespace MsiControl.Core;

/// <summary>
/// 界面显示用的单位字符串常量。
/// </summary>
public static class Units
{
    /// <summary>摄氏度。</summary>
    public const string Celsius = "°C";

    /// <summary>瓦特。</summary>
    public const string Watt = "W";

    /// <summary>兆赫兹（内部频率单位）。</summary>
    public const string Megahertz = "MHz";

    /// <summary>转速。</summary>
    public const string Rpm = "RPM";

    /// <summary>百分比。</summary>
    public const string Percent = "%";
}

/// <summary>
/// 单位换算与数值约束的唯一入口。
/// </summary>
/// <remarks>
/// <b>所有单位换算必须走本类型，禁止各模块自行换算</b>（接口与工程约定 §2）。
/// 特别是 ACPI 热区温度：原始值为「十分之一开尔文」，是冷却类工具最常见的错误来源。
/// </remarks>
public static class Measurement
{
    /// <summary>绝对零度对应的摄氏度偏移量。</summary>
    public const double KelvinToCelsiusOffset = 273.15;

    /// <summary>风扇曲线温度的允许下限（摄氏度）。</summary>
    public const double MinCurveTemperatureC = -20.0;

    /// <summary>风扇曲线温度的允许上限（摄氏度）。</summary>
    public const double MaxCurveTemperatureC = 130.0;

    /// <summary>风扇曲线输出的允许下限（百分比）。</summary>
    public const double MinCurveOutputPercent = 0.0;

    /// <summary>风扇曲线输出的允许上限（百分比）。</summary>
    public const double MaxCurveOutputPercent = 100.0;

    /// <summary>风扇满速标定锚点：100% 输出对应 9000 转/分钟（叶哥 2026-10-17 提供）。</summary>
    public const double FanFullSpeedRpm = 9000.0;

    /// <summary>
    /// 风扇标准工况标定锚点：45% 输出对应 3000 转/分钟（叶哥 2026-10-31 提供）。
    /// </summary>
    /// <remarks>
    /// 该锚点推翻了「转速与占空比线性过原点」的旧假设：按旧线性模型 45% 会估算成
    /// 4050 转/分钟，比实测 3000 高 35%。风扇在低占空比段受启动死区与最低转速平台影响，
    /// 每增加 1% 占空比带来的转速增量明显小于高段，因此本锚点与满速锚点一起构成两段折线标定。
    /// </remarks>
    public const double FanStandardDutyPercent = 45.0;

    /// <summary>标准工况锚点对应的转速（转/分钟）。</summary>
    public const double FanStandardRpm = 3000.0;

    /// <summary>
    /// 将 ACPI 热区温度（十分之一开尔文）换算为摄氏度。
    /// </summary>
    /// <param name="deciKelvin">十分之一开尔文单位的原始值，例如 <c>3332</c>。</param>
    /// <returns>摄氏度，例如 <c>3332</c> → <c>60.05</c>。</returns>
    public static double DeciKelvinToCelsius(double deciKelvin)
        => deciKelvin / 10.0 - KelvinToCelsiusOffset;

    /// <summary>
    /// 将摄氏度换算为 ACPI 热区温度（十分之一开尔文）。
    /// </summary>
    /// <param name="celsius">摄氏度。</param>
    /// <returns>十分之一开尔文单位的整数值（四舍五入）。</returns>
    public static long CelsiusToDeciKelvin(double celsius)
        => (long)Math.Round((celsius + KelvinToCelsiusOffset) * 10.0, MidpointRounding.AwayFromZero);

    /// <summary>兆赫兹转吉赫兹。内部统一使用 MHz，仅在界面显示时换算。</summary>
    public static double MegahertzToGigahertz(double megahertz) => megahertz / 1000.0;

    /// <summary>吉赫兹转兆赫兹。</summary>
    public static double GigahertzToMegahertz(double gigahertz) => gigahertz * 1000.0;

    /// <summary>将数值限制在闭区间内。</summary>
    public static double Clamp(double value, double min, double max)
        => value < min ? min : value > max ? max : value;

    /// <summary>将数值限制在闭区间内。</summary>
    public static decimal Clamp(decimal value, decimal min, decimal max)
        => value < min ? min : value > max ? max : value;

    /// <summary>判断给定温度是否落在风扇曲线允许范围内。</summary>
    public static bool IsValidCurveTemperature(double celsius)
        => celsius >= MinCurveTemperatureC && celsius <= MaxCurveTemperatureC;

    /// <summary>判断给定输出百分比是否落在允许范围内。</summary>
    public static bool IsValidCurveOutput(double percent)
        => percent >= MinCurveOutputPercent && percent <= MaxCurveOutputPercent;

    /// <summary>
    /// 按两段折线标定把风扇输出百分比估算为转速（转/分钟）。
    /// </summary>
    /// <param name="percent">输出百分比（0–100，越界按边界截断）。</param>
    /// <returns>估算转速；输入非有限值时返回 0。</returns>
    /// <remarks>
    /// <para>
    /// 标定锚点两个：<c>0% → 0</c>（物理停转）、<see cref="FanStandardDutyPercent"/> →
    /// <see cref="FanStandardRpm"/>（标准工况实测）、<see cref="MaxCurveOutputPercent"/> →
    /// <see cref="FanFullSpeedRpm"/>（满速）。低段与高段分别线性插值。
    /// </para>
    /// <para>
    /// <b>为什么不是单段线性：</b>旧实现用 <c>满速/100 = 90 转/百分比</c> 一条直线，
    /// 在 45% 处给出 4050 转，与实测 3000 转相差 35%（HWE §33）。风扇的占空比—转速关系
    /// 在低段与高段的斜率并不相同，单点线性在整条区间上都不可靠，故必须用两点锚定。
    /// </para>
    /// <para>本值仍是<b>估算</b>（EC 无转速寄存器），不得表述为转速计实测值。</para>
    /// </remarks>
    public static double FanDutyToEstimatedRpm(double percent)
    {
        if (!double.IsFinite(percent))
        {
            return 0;
        }

        var duty = Clamp(percent, MinCurveOutputPercent, MaxCurveOutputPercent);

        if (duty <= FanStandardDutyPercent)
        {
            return duty / FanStandardDutyPercent * FanStandardRpm;
        }

        var span = MaxCurveOutputPercent - FanStandardDutyPercent;
        return FanStandardRpm
            + (duty - FanStandardDutyPercent)
                * (FanFullSpeedRpm - FanStandardRpm) / span;
    }

    /// <summary>
    /// 按两段折线标定把转速反推为输出百分比（<see cref="FanDutyToEstimatedRpm"/> 的逆运算）。
    /// </summary>
    /// <param name="rpm">转速（转/分钟）。</param>
    /// <returns>等效输出百分比（0–100）；输入非有限或为负时返回 0。</returns>
    /// <remarks>用于把读到的转速换算成「等效占空比」展示，不代表可以按该比例设定输出。</remarks>
    public static double FanRpmToEstimatedDuty(double rpm)
    {
        if (!double.IsFinite(rpm) || rpm <= 0)
        {
            return 0;
        }

        if (rpm <= FanStandardRpm)
        {
            return rpm / FanStandardRpm * FanStandardDutyPercent;
        }

        var span = FanFullSpeedRpm - FanStandardRpm;
        return Clamp(
            FanStandardDutyPercent + (rpm - FanStandardRpm) * (MaxCurveOutputPercent - FanStandardDutyPercent) / span,
            MinCurveOutputPercent,
            MaxCurveOutputPercent);
    }
}
