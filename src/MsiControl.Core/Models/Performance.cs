using System.Globalization;

namespace MsiControl.Core.Models;

/// <summary>
/// 功耗限制值。
/// </summary>
/// <remarks>
/// <para>
/// 本类型在构造层强制「<b>禁止用 0、-1 或极大数字隐式代表无限</b>」的约定：
/// <see cref="LimitMode.Native"/> 表达「原生上限」，<see cref="LimitMode.Custom"/> 才携带瓦数，
/// 且瓦数必须为正的有限值。构造只能经 <see cref="Native"/> 或 <see cref="Custom"/>。
/// </para>
/// <para>「原生上限」表示取消本软件的额外限制、恢复设备允许的原生策略，<b>不绕过硬件保护</b>。</para>
/// </remarks>
public sealed record PowerLimit
{
    /// <summary>自定义瓦数的合理性上限，用于拦截明显错误的输入。</summary>
    public const decimal MaxCustomWatts = 1000m;

    private PowerLimit(LimitMode mode, decimal? watts)
    {
        Mode = mode;
        Watts = watts;
    }

    /// <summary>限制模式。</summary>
    public LimitMode Mode { get; }

    /// <summary>自定义瓦数。原生上限时为 <see langword="null"/>。</summary>
    public decimal? Watts { get; }

    /// <summary>原生上限。</summary>
    public static PowerLimit Native { get; } = new(LimitMode.Native, null);

    /// <summary>是否为原生上限。</summary>
    public bool IsNative => Mode == LimitMode.Native;

    /// <summary>
    /// 尝试构造自定义瓦数限制。
    /// </summary>
    /// <param name="watts">目标瓦数，必须为正的有限值。</param>
    /// <param name="limit">成功时为限制对象。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否构造成功。</returns>
    public static bool TryCustom(decimal watts, out PowerLimit? limit, out string? error)
    {
        if (watts <= 0m)
        {
            limit = null;
            error = $"功耗限制必须为正数，收到 {watts}。原生上限请使用 LimitMode.Native，不要用 0 或负数表达。";
            return false;
        }

        if (watts > MaxCustomWatts)
        {
            limit = null;
            error = $"功耗限制 {watts} W 超出合理性上限 {MaxCustomWatts} W。极大数值不得用于表示无限。";
            return false;
        }

        limit = new PowerLimit(LimitMode.Custom, watts);
        error = null;
        return true;
    }

    /// <summary>构造自定义瓦数限制，非法时抛出异常。</summary>
    /// <exception cref="ArgumentOutOfRangeException">瓦数非法。</exception>
    public static PowerLimit Custom(decimal watts)
    {
        if (!TryCustom(watts, out var limit, out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(watts), error);
        }

        return limit!;
    }

    /// <summary>取自定义瓦数，原生上限时返回 <see langword="null"/>。</summary>
    public decimal? AsWattsOrNull() => Watts;

    /// <inheritdoc />
    public override string ToString()
        => IsNative
            ? "原生上限"
            // 显式指定固定区域性：本类型可能被载入未启用 InvariantGlobalization 的宿主进程，
            // 若按当前区域性格式化，德语等区域会输出 "60,5 W"，与界面其他数值不一致。
            : $"{Watts!.Value.ToString("0.##", CultureInfo.InvariantCulture)} W";
}

/// <summary>
/// CPU 睿频上限。
/// </summary>
/// <remarks>
/// 以频率单位表达，不使用「禁用睿频」冒充可调上限（任务书 §8.2）。
/// 可控范围与核心类型约束按接口能力确定。
/// </remarks>
public sealed record CpuTurboLimit
{
    /// <summary>自定义频率的合理性上限（MHz）。</summary>
    public const int MaxCustomMegahertz = 12000;

    private CpuTurboLimit(LimitMode mode, int? megahertz)
    {
        Mode = mode;
        Megahertz = megahertz;
    }

    /// <summary>限制模式。</summary>
    public LimitMode Mode { get; }

    /// <summary>自定义频率上限，单位 MHz。原生上限时为 <see langword="null"/>。</summary>
    public int? Megahertz { get; }

    /// <summary>原生上限。</summary>
    public static CpuTurboLimit Native { get; } = new(LimitMode.Native, null);

    /// <summary>是否为原生上限。</summary>
    public bool IsNative => Mode == LimitMode.Native;

    /// <summary>尝试构造自定义睿频上限。</summary>
    /// <param name="megahertz">目标频率，单位 MHz，必须为正的有限值。</param>
    /// <param name="limit">成功时为限制对象。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>是否构造成功。</returns>
    public static bool TryCustom(int megahertz, out CpuTurboLimit? limit, out string? error)
    {
        if (megahertz <= 0)
        {
            limit = null;
            error = $"睿频上限必须为正数，收到 {megahertz} MHz。原生上限请使用 LimitMode.Native。";
            return false;
        }

        if (megahertz > MaxCustomMegahertz)
        {
            limit = null;
            error = $"睿频上限 {megahertz} MHz 超出合理性上限 {MaxCustomMegahertz} MHz。";
            return false;
        }

        limit = new CpuTurboLimit(LimitMode.Custom, megahertz);
        error = null;
        return true;
    }

    /// <summary>构造自定义睿频上限，非法时抛出异常。</summary>
    /// <exception cref="ArgumentOutOfRangeException">频率非法。</exception>
    public static CpuTurboLimit Custom(int megahertz)
    {
        if (!TryCustom(megahertz, out var limit, out var error))
        {
            throw new ArgumentOutOfRangeException(nameof(megahertz), error);
        }

        return limit!;
    }

    /// <inheritdoc />
    public override string ToString()
        => IsNative
            ? "原生上限"
            : $"{Megahertz!.Value.ToString(CultureInfo.InvariantCulture)} MHz";
}

/// <summary>
/// GPU 兼容节能策略。
/// </summary>
/// <remarks>
/// 当目标瓦数不受支持时使用。<b>必须显式标注，不得静默替换目标值</b>；
/// <b>不保证精确达到目标瓦数</b>，且不得扩展为未授权的 GPU 超频或 VF 编辑（任务书 §8.3）。
/// </remarks>
/// <param name="Description">策略说明，面向用户。</param>
/// <param name="Applied">实际应用的限制。</param>
/// <param name="Limitation">该策略的能力边界说明，必填。</param>
public sealed record GpuEcoFallback(string Description, PowerLimit Applied, string Limitation)
{
    /// <summary>构造一个替代策略。能力边界不能为空。</summary>
    public static GpuEcoFallback Create(string description, PowerLimit applied, string limitation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(limitation);
        return new GpuEcoFallback(description, applied, limitation);
    }
}

/// <summary>
/// 单个性能档位的完整可编辑配置。
/// </summary>
/// <remarks>
/// 每档独立保存（任务书 §8.2）。CPU 持续与短时限制分别保存；GPU 替代节能策略单独记录，
/// <b>避免把替代策略误显示成精确瓦数限制</b>。
/// </remarks>
/// <param name="Stage">所属档位。</param>
/// <param name="CpuLongTerm">CPU 持续功耗上限。</param>
/// <param name="CpuShortTerm">CPU 短时功耗上限。</param>
/// <param name="CpuTurbo">CPU 睿频上限。</param>
/// <param name="GpuLimit">GPU 功耗上限。</param>
/// <param name="GpuEcoFallback">GPU 兼容节能策略。仅在目标不受支持时使用。</param>
/// <param name="CpuCurve">该档位的 CPU 风扇曲线。未配置时为 <see langword="null"/>。</param>
/// <param name="GpuCurve">该档位的 GPU 风扇曲线。未配置时为 <see langword="null"/>。</param>
public sealed record PerformanceSettings(
    PerformanceStage Stage,
    PowerLimit CpuLongTerm,
    PowerLimit CpuShortTerm,
    CpuTurboLimit CpuTurbo,
    PowerLimit GpuLimit,
    GpuEcoFallback? GpuEcoFallback = null,
    FanCurve? CpuCurve = null,
    FanCurve? GpuCurve = null)
{
    /// <summary>是否全部使用原生上限（即不含任何自定义功耗限制）。</summary>
    public bool IsFullyNative
        => CpuLongTerm.IsNative && CpuShortTerm.IsNative && GpuLimit.IsNative && CpuTurbo.IsNative;

    /// <summary>
    /// 校验本档配置的内部一致性。
    /// </summary>
    /// <returns>问题列表。为空表示配置自洽。</returns>
    /// <remarks>
    /// 本类型只保证**单个**限制值合法（由 <see cref="PowerLimit"/> 等负责），
    /// 字段之间的组合关系需在此校验：
    /// <list type="number">
    /// <item>CPU 短时上限不得低于持续上限，否则该配置自相矛盾，
    ///       且「严格限制持续与短时」的要求无法成立（任务书 §8.2）。</item>
    /// <item>CPU 曲线必须绑定在 <see cref="FanChannel.Cpu"/>、GPU 曲线必须绑定在
    ///       <see cref="FanChannel.Gpu"/>，否则会把 CPU 曲线写到 GPU 风扇上。</item>
    /// </list>
    /// </remarks>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        if (CpuLongTerm.Watts is { } longTerm && CpuShortTerm.Watts is { } shortTerm && shortTerm < longTerm)
        {
            problems.Add($"CPU 短时上限 {shortTerm} W 低于持续上限 {longTerm} W：该档配置自相矛盾。");
        }

        if (CpuCurve is not null && CpuCurve.Channel != FanChannel.Cpu)
        {
            problems.Add($"CPU 风扇曲线绑定在 {CpuCurve.Channel} 通道上，应为 {FanChannel.Cpu}。");
        }

        if (GpuCurve is not null && GpuCurve.Channel != FanChannel.Gpu)
        {
            problems.Add($"GPU 风扇曲线绑定在 {GpuCurve.Channel} 通道上，应为 {FanChannel.Gpu}。");
        }

        return problems;
    }
}

/// <summary>一个性能档位的展示信息与默认配置。</summary>
/// <param name="Stage">档位。</param>
/// <param name="DisplayName">简体中文显示名。</param>
/// <param name="Settings">默认配置。</param>
public sealed record PerformanceProfile(PerformanceStage Stage, string DisplayName, PerformanceSettings Settings);

/// <summary>
/// 五档性能模式的默认配置表。
/// </summary>
/// <remarks>
/// <para>数值来自任务书 §8.1。<b>目标是功耗限制目标，不是持续耗电目标</b>。</para>
/// <para>
/// 高性能与狂暴在功耗上均为原生上限。若实机验证表明两者没有额外性能能力差异，
/// 必须明确说明主要由散热配置区分，<b>不得虚构增强能力</b>（任务书 §8.4）。
/// </para>
/// </remarks>
public static class PerformanceProfileCatalog
{
    /// <summary>五档默认配置，按 <see cref="PerformanceStage"/> 顺序排列。</summary>
    public static IReadOnlyList<PerformanceProfile> Defaults { get; } = new[]
    {
        Create(PerformanceStage.Eco, "节能", 10m, 20m),
        Create(PerformanceStage.Quiet, "安静", 50m, 80m),
        Create(PerformanceStage.Balanced, "平衡", 60m, 90m),
        Create(PerformanceStage.HighPerformance, "高性能", null, null),
        Create(PerformanceStage.Extreme, "狂暴", null, null)
    };

    private static PerformanceProfile Create(
        PerformanceStage stage,
        string displayName,
        decimal? cpuWatts,
        decimal? gpuWatts)
    {
        // 受限档位把持续与短时上限设为同一值，落实「CPU 默认严格限制」（任务书 §8.2）。
        var cpuLimit = cpuWatts is null ? PowerLimit.Native : PowerLimit.Custom(cpuWatts.Value);
        var gpuLimit = gpuWatts is null ? PowerLimit.Native : PowerLimit.Custom(gpuWatts.Value);

        var settings = new PerformanceSettings(
            stage,
            CpuLongTerm: cpuLimit,
            CpuShortTerm: cpuLimit,
            CpuTurbo: CpuTurboLimit.Native,
            GpuLimit: gpuLimit);

        return new PerformanceProfile(stage, displayName, settings);
    }

    /// <summary>取得指定档位的默认配置。</summary>
    /// <exception cref="ArgumentOutOfRangeException">档位未在默认表中登记。</exception>
    public static PerformanceProfile GetDefault(PerformanceStage stage)
        => TryGetDefault(stage, out var profile)
            ? profile!
            : throw new ArgumentOutOfRangeException(nameof(stage), stage, "该档位未在默认配置表中登记。");

    /// <summary>尝试取得指定档位的默认配置。</summary>
    public static bool TryGetDefault(PerformanceStage stage, out PerformanceProfile? profile)
    {
        profile = Defaults.FirstOrDefault(p => p.Stage == stage);
        return profile is not null;
    }
}

/// <summary>
/// 从硬件读回的性能状态。
/// </summary>
/// <remarks>
/// 逐项保留读回值，用于 <see cref="Results.SettingResult.ReadBack"/> 与结果核对（任务书 §8.5）。
/// 读回不可得的项保持 <see langword="null"/>，<b>不得用请求值填充</b>。
/// </remarks>
/// <param name="ActiveStage">当前实际生效的档位。未知时为 <see langword="null"/>。</param>
/// <param name="CpuLongTermReadBack">读回的 CPU 持续功耗限制。</param>
/// <param name="CpuShortTermReadBack">读回的 CPU 短时功耗限制。</param>
/// <param name="GpuLimitReadBack">读回的 GPU 功耗限制。</param>
/// <param name="CpuTurboReadBack">读回的 CPU 睿频上限。</param>
/// <param name="FirmwareScenario">读回的 MSI 固件性能场景标识。</param>
public sealed record PerformanceHardwareState(
    PerformanceStage? ActiveStage,
    PowerLimit? CpuLongTermReadBack = null,
    PowerLimit? CpuShortTermReadBack = null,
    PowerLimit? GpuLimitReadBack = null,
    CpuTurboLimit? CpuTurboReadBack = null,
    string? FirmwareScenario = null)
{
    /// <summary>尚未适配的未知状态。</summary>
    // 注意：必须用命名参数。record 会生成拷贝构造函数 PerformanceHardwareState(PerformanceHardwareState)，
    // 写成 new(null) 会被解析到该构造函数，从而触发 CS8625。
    public static PerformanceHardwareState Unknown { get; } = new(ActiveStage: null);

    /// <summary>是否存在任何可读回项。</summary>
    public bool HasAnyReadBack
        => ActiveStage is not null
           || CpuLongTermReadBack is not null
           || CpuShortTermReadBack is not null
           || GpuLimitReadBack is not null
           || CpuTurboReadBack is not null;
}
