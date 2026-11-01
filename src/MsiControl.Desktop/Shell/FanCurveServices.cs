using MsiControl.Fans;
using MsiControl.Persistence.Configuration;

namespace MsiControl.Desktop.Shell;

/// <summary>
/// 风扇曲线功能包：软件曲线引擎与配置档存储。
/// </summary>
/// <remarks>
/// <para>
/// 桌面壳的约束是「窗口从不调用 IFanHardware」（任务书 §16 规则 11）。
/// 曲线编辑器因此不接触硬件接口，而是消费应用层装配好的
/// <see cref="FanCurveEngine"/>——引擎内部才持有占空比下发通道，
/// 并自带能力门控（<c>Start</c> 在通道不可用时拒绝启动并给出原因）。
/// </para>
/// <para>
/// 为 <see langword="null"/>（未适配装配或引擎未装配）时，风扇曲线窗口
/// 保持「待验证」禁用状态并如实说明原因，绝不模拟功能。
/// </para>
/// </remarks>
/// <param name="Engine">软件曲线引擎（应用层装配）。</param>
/// <param name="Profiles">曲线配置档存储（应用数据目录 <c>fan-curves.json</c>）。</param>
public sealed record FanCurveServices(FanCurveEngine Engine, FanCurveProfileStore Profiles);
