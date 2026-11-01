namespace MsiControl.Core.Models;

/// <summary>风扇曲线上的一个节点。</summary>
/// <param name="TemperatureC">温度阈值，摄氏度。</param>
/// <param name="OutputPercent">风扇输出，百分比（0–100）。</param>
public sealed record FanPoint(double TemperatureC, double OutputPercent);

/// <summary>
/// 单个通道的风扇曲线。
/// </summary>
/// <remarks>
/// <para>
/// 本类型强制以下不变量，构造时校验，不满足即拒绝创建：
/// <list type="number">
/// <item>至少 2 个节点。</item>
/// <item>温度严格递增（不允许重复温度）。</item>
/// <item>温度落在 <see cref="Measurement.MinCurveTemperatureC"/> 至
///       <see cref="Measurement.MaxCurveTemperatureC"/> 之间。</item>
/// <item>输出落在 0–100 之间。</item>
/// </list>
/// </para>
/// <para>
/// 节点数量、温度是否可编辑、输出范围与步长以实际固件接口为准（任务书 §7.2）。
/// 上层在应用曲线前必须用硬件返回的能力信息再做一次校验。
/// </para>
/// </remarks>
public sealed class FanCurve
{
    private readonly FanPoint[] _points;
    private readonly IReadOnlyList<FanPoint> _view;

    private FanCurve(FanChannel channel, FanPoint[] points)
    {
        Channel = channel;
        _points = points;
        // 用只读包装而非直接暴露数组：IReadOnlyList 可被调用方强转回 FanPoint[]
        // 并就地改写元素，从而绕过本类型的不可变性保证。
        _view = Array.AsReadOnly(points);
    }

    /// <summary>曲线所属通道。</summary>
    public FanChannel Channel { get; }

    /// <summary>曲线节点，按温度升序排列。返回只读视图，无法强转回数组改写。</summary>
    public IReadOnlyList<FanPoint> Points => _view;

    /// <summary>节点数量。</summary>
    public int NodeCount => _points.Length;

    /// <summary>最低温度阈值。</summary>
    public double MinTemperatureC => _points[0].TemperatureC;

    /// <summary>最高温度阈值。</summary>
    public double MaxTemperatureC => _points[^1].TemperatureC;

    /// <summary>
    /// 校验一组节点<b>按给定顺序</b>是否构成合法曲线。
    /// </summary>
    /// <returns>校验问题列表。为空表示合法。</returns>
    /// <remarks>
    /// 本方法不排序输入：它检查的就是调用方给的顺序。
    /// 若希望接受乱序输入并自动规范化，请使用 <see cref="TryCreate"/>。
    /// </remarks>
    public static IReadOnlyList<string> Validate(IEnumerable<FanPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var list = points.ToList();
        var errors = new List<string>();

        if (list.Count < 2)
        {
            errors.Add($"风扇曲线至少需要 2 个节点，当前为 {list.Count} 个。");
            return errors;
        }

        for (var i = 0; i < list.Count; i++)
        {
            var point = list[i];
            if (!Measurement.IsValidCurveTemperature(point.TemperatureC))
            {
                errors.Add(
                    $"第 {i + 1} 个节点温度 {point.TemperatureC} °C 超出允许范围 " +
                    $"{Measurement.MinCurveTemperatureC}–{Measurement.MaxCurveTemperatureC} °C。");
            }

            if (!Measurement.IsValidCurveOutput(point.OutputPercent))
            {
                errors.Add($"第 {i + 1} 个节点输出 {point.OutputPercent}% 超出允许范围 0–100%。");
            }

            if (i > 0 && point.TemperatureC <= list[i - 1].TemperatureC)
            {
                errors.Add(
                    $"第 {i + 1} 个节点温度 {point.TemperatureC} °C 未高于前一节点 " +
                    $"{list[i - 1].TemperatureC} °C，温度必须严格递增。");
            }
        }

        return errors;
    }

    /// <summary>
    /// 创建曲线。校验失败时抛出异常。
    /// </summary>
    /// <exception cref="ArgumentException">节点不构成合法曲线。</exception>
    public static FanCurve Create(FanChannel channel, IEnumerable<FanPoint> points)
    {
        if (!TryCreate(channel, points, out var curve, out var errors))
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(points));
        }

        return curve!;
    }

    /// <summary>
    /// 尝试创建曲线，不抛异常。
    /// </summary>
    /// <param name="channel">通道。</param>
    /// <param name="points">节点序列。可以是乱序的。</param>
    /// <param name="curve">成功时为创建的曲线，否则为 <see langword="null"/>。</param>
    /// <param name="errors">校验问题列表。</param>
    /// <returns>是否创建成功。</returns>
    /// <remarks>
    /// 温度是曲线的自变量，因此按温度升序是唯一的规范顺序。
    /// 本方法<b>先按温度规范化排序、再校验</b>，所以乱序输入会被正常接受；
    /// 若需要检查调用方给定的顺序本身是否合法，请直接用 <see cref="Validate"/>。
    /// </remarks>
    public static bool TryCreate(
        FanChannel channel,
        IEnumerable<FanPoint> points,
        out FanCurve? curve,
        out IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(points);

        var ordered = points.OrderBy(p => p.TemperatureC).ToArray();
        errors = Validate(ordered);
        if (errors.Count > 0)
        {
            curve = null;
            return false;
        }

        curve = new FanCurve(channel, ordered);
        return true;
    }

    /// <summary>
    /// 按线性插值计算给定温度下的目标输出。
    /// </summary>
    /// <param name="temperatureC">当前温度，摄氏度。</param>
    /// <returns>目标输出百分比。低于首节点取首节点值，高于末节点取末节点值。</returns>
    /// <remarks>
    /// 这是<b>曲线目标输出</b>，不是实际转速。实际转速还受固件策略与硬件限制影响，
    /// 两者不得互相替代（任务书 §6.4）。
    /// </remarks>
    public double Interpolate(double temperatureC)
    {
        if (temperatureC <= _points[0].TemperatureC)
        {
            return _points[0].OutputPercent;
        }

        if (temperatureC >= _points[^1].TemperatureC)
        {
            return _points[^1].OutputPercent;
        }

        for (var i = 1; i < _points.Length; i++)
        {
            var lower = _points[i - 1];
            var upper = _points[i];
            if (temperatureC > upper.TemperatureC)
            {
                continue;
            }

            var span = upper.TemperatureC - lower.TemperatureC;
            if (span <= 0)
            {
                return upper.OutputPercent;
            }

            var ratio = (temperatureC - lower.TemperatureC) / span;
            return lower.OutputPercent + ratio * (upper.OutputPercent - lower.OutputPercent);
        }

        return _points[^1].OutputPercent;
    }

    /// <summary>返回一个更换了通道的等价曲线。</summary>
    public FanCurve WithChannel(FanChannel channel) => new(channel, _points);
}

/// <summary>
/// 硬件报告的风扇状态：当前控制模式与设备上已生效的曲线。
/// </summary>
/// <remarks>
/// 与 <see cref="FanSnapshot"/> 的区别：本类型描述 <b>配置</b>（固件里是什么），
/// <see cref="FanSnapshot"/> 描述 <b>实时读数</b>（现在转多快）。二者不可混用。
/// </remarks>
/// <param name="Mode">当前控制模式。</param>
/// <param name="CpuCurve">CPU 通道上已生效的曲线。自动模式下为 <see langword="null"/>。</param>
/// <param name="GpuCurve">GPU 通道上已生效的曲线。自动模式下为 <see langword="null"/>。</param>
/// <param name="SupportsFullSpeed">是否支持强冷（全速）开关。</param>
/// <param name="CalibrationAvailable">是否存在有效的百分比校准基准。</param>
public sealed record FanHardwareState(
    FanControlMode Mode,
    FanCurve? CpuCurve,
    FanCurve? GpuCurve,
    bool SupportsFullSpeed = false,
    bool CalibrationAvailable = false)
{
    /// <summary>取指定通道上已生效的曲线。</summary>
    public FanCurve? CurveFor(FanChannel channel) => channel == FanChannel.Cpu ? CpuCurve : GpuCurve;

    /// <summary>构造一个尚未适配的未知状态。</summary>
    public static FanHardwareState Unknown { get; } = new(FanControlMode.Automatic, null, null);
}
