using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// CPU/GPU 风扇转速来源：读取 MS-15M3 EC 的风扇占空比寄存器（PawnIO 通道）。
/// </summary>
/// <remarks>
/// <para>
/// 寄存器来自 <see cref="Ms15M3EcRegisterMap"/>（msi-ec <c>CONF_G2_10</c>，
/// 固件白名单精确覆盖本机 15M3EMS1.105–.113）。该表<b>不提供 RPM 寄存器</b>，因此：
/// </para>
/// <list type="bullet">
/// <item><b>实际转速百分比</b>：硬件直接报告（EC 占空比），经
///       <see cref="FanReading.FromHardwarePercent"/> 进入 <see cref="FanReading.ActualPercent"/>。</item>
/// <item><b>RPM</b>：按用户提供的校准基准线性换算——满转速 100% 对应 9000 转/分钟
///       （2026-10-17，叶哥提供）。换算值为<b>估算</b>而非转速计实测，
///       来源字符串如实标注；小占空比下实际风扇可能存在最低转速平台，
///       估算值在低区间偏差更大。</item>
/// </list>
/// <para>
/// <b>只读：</b>本数据源只调用 <see cref="IEcRegisterAccess.ReadRegister"/>；
/// EC 通道的写白名单恒为空集，风扇<b>控制</b>（写 0xD4/0x98）须待受控对照实验把
/// <c>fan.pwm</c>/<c>fan.curve</c> 升至 <c>Verified</c> 后由风扇模块开放（任务书 §16.1）。
/// </para>
/// </remarks>
public sealed class PawnIoEcFanSource : IFanSpeedSource
{
    /// <summary>EC 占空比合理区间上限。超出视为读数异常。</summary>
    public const double MaxPlausiblePercent = 100.0;

    /// <summary>
    /// 满转速校准基准：100% 占空比对应 9000 转/分钟（用户提供的单点锚点）。
    /// </summary>
    /// <remarks>
    /// 换算本身统一走 <see cref="Measurement.FanDutyToEstimatedRpm"/> 的两段折线标定
    /// （45% → 3000、100% → 9000），本常量只作为满速锚点的说明性引用。
    /// 换算结果是<b>估算</b>，不是转速计实测；完整校准流程（任务 04）可在将来细化。
    /// </remarks>
    public const double FullSpeedRpm = Measurement.FanFullSpeedRpm;

    /// <summary>标准工况锚点：45% 占空比对应 3000 转/分钟（2026-10-31）。</summary>
    public const double StandardDutyPercent = Measurement.FanStandardDutyPercent;

    /// <summary>标准工况锚点对应的转速（转/分钟）。</summary>
    public const double StandardRpm = Measurement.FanStandardRpm;

    private readonly IEcRegisterAccess _access;

    /// <summary>构造数据源。</summary>
    /// <param name="access">EC 寄存器只读访问。</param>
    public PawnIoEcFanSource(IEcRegisterAccess access)
        => _access = access ?? throw new ArgumentNullException(nameof(access));

    /// <inheritdoc />
    public string Key => "fan.percent.ec";

    /// <inheritdoc />
    public string Description => "EC 风扇占空比（PawnIO）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.FanPwm;

    /// <inheritdoc />
    public Task<FanReading> ReadAsync(FanChannel channel, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var register = channel == FanChannel.Cpu
            ? Ms15M3EcRegisterMap.CpuFanPercent
            : Ms15M3EcRegisterMap.GpuFanPercent;

        var error = _access.AvailabilityError;
        if (error is not null)
        {
            return Task.FromResult(FanReading.Unavailable(Description, error));
        }

        byte raw;
        try
        {
            raw = _access.ReadRegister(register.Address);
        }
        catch (Exception ex)
        {
            return Task.FromResult(FanReading.Unavailable(
                Description,
                $"读取 {register.Description}（0x{register.Address:X2}）失败：{ex.Message}"));
        }

        if (raw > (byte)MaxPlausiblePercent)
        {
            return Task.FromResult(FanReading.Unavailable(
                Description,
                $"EC 报告{register.Description}为 {raw}，超出 0–100 合理区间，已按不可用处理。"));
        }

        // RPM 为两段折线标定估算（45% = 3000、100% = 9000，叶哥提供的锚点），非转速计实测；
        // 来源字符串如实标注估算属性与所用锚点，百分比保持硬件直报值。
        var estimatedRpm = (int)Math.Round(Measurement.FanDutyToEstimatedRpm(raw));
        var sourceLabel = $"{Description}:0x{register.Address:X2}";

        return Task.FromResult(FanReading
            .FromHardwarePercent(raw, sourceLabel, DateTimeOffset.Now)
            .WithRpm(SensorReading.Valid(
                estimatedRpm,
                Units.Rpm,
                $"{sourceLabel}（折线估算：占空比 {raw}%，锚点 "
                + $"{Measurement.FanStandardDutyPercent:0}%={Measurement.FanStandardRpm:0} RPM、"
                + $"{Measurement.MaxCurveOutputPercent:0}%={Measurement.FanFullSpeedRpm:0} RPM，非转速计实测）",
                DateTimeOffset.Now)));
    }
}
