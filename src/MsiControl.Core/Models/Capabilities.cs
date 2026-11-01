namespace MsiControl.Core.Models;

/// <summary>
/// 能力键常量。所有能力判定一律使用本类型，禁止散落字符串字面量。
/// </summary>
public static class CapabilityKeys
{
    /// <summary>风扇曲线读取与写入。</summary>
    public const string FanCurve = "fan.curve";

    /// <summary>强冷（全速）开关。</summary>
    public const string FanFullSpeed = "fan.fullspeed";

    /// <summary>风扇百分比校准。</summary>
    public const string FanCalibration = "fan.calibration";

    /// <summary>风扇模式切换（auto/silent/advanced，EC 0xD4）。</summary>
    public const string FanMode = "fan.mode";

    /// <summary>
    /// 风扇输出（PWM / 转速百分比）调节。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FanCurve"/> 是两个粒度：后者是「曲线整组下发」的 WMI 语义，
    /// 本键指「按周期/按档位直接写风扇输出百分比」的 EC 语义（PawnIO 通道）。
    /// </remarks>
    public const string FanPwm = "fan.pwm";

    /// <summary>温度读取（CPU/GPU 精确温度，依赖 MSI_ACPI 或第三方路径）。</summary>
    public const string ThermalRead = "thermal.read";

    /// <summary>
    /// 通用 ACPI 热区温度读取（兜底温度源）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ThermalRead"/> 是两个不同的数据通路：
    /// 前者为厂商专用精确温度（本机仅线索），后者为 Windows 标准热区（本机已确证返回数据）。
    /// 粒度粗、刷新慢，只能兜底（任务书 §6.4、证据清单 §5）。
    /// </remarks>
    public const string ThermalFallback = "thermal.fallback";

    /// <summary>CPU 功耗限制。</summary>
    public const string CpuPowerLimit = "cpu.power";

    /// <summary>CPU 睿频上限。</summary>
    public const string CpuTurboLimit = "cpu.turbo";

    /// <summary>GPU 功耗限制。</summary>
    public const string GpuPowerLimit = "gpu.power";

    /// <summary>GPU 兼容节能策略。</summary>
    public const string GpuEcoFallback = "gpu.eco";

    /// <summary>可写范围查询（用于「显示支持范围」，任务书 §8.3）。</summary>
    public const string PowerRange = "power.range";

    /// <summary>显卡模式切换。</summary>
    public const string GraphicsMode = "graphics.mode";

    /// <summary>整机功耗读取。</summary>
    public const string SystemPowerRead = "power.system";

    /// <summary>电池侧功耗读取。</summary>
    public const string BatteryPowerRead = "power.battery";

    /// <summary>EC 寄存器访问。</summary>
    public const string EcAccess = "ec.access";

    /// <summary>固件性能场景。</summary>
    public const string FirmwareScenario = "firmware.scenario";

    /// <summary>
    /// 性能档位切换（固件场景写入，EC 0xD2 Shift 模式）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FirmwareScenario"/> 的分工：后者是 MSI_ACPI WMI 侧「场景/设备写入」的
    /// 线索键（本机语义未验证，维持 Detected）；本键专指 EC Shift 通道的档位切换能力
    /// （0xC1/0xC2/0xC4 三编码有本机实验证据，HWE §20）。性能档位按钮以本键为门控依据。
    /// </remarks>
    public const string PerformanceStage = "performance.stage";

    /// <summary>扬声器静音与音量步进（Core Audio 默认播放端点）。</summary>
    /// <remarks>
    /// 数据源与系统音量滑块完全同一（<c>IAudioEndpointVolume</c>），因此不需要「同步」这一层概念：
    /// 界面上显示的就是系统真实音量。F1/F2/F3 由固件与 Windows 原生处理，
    /// 本键只用于「应用内提供等效命令 + 只读展示」。
    /// </remarks>
    public const string AudioVolume = "audio.volume";

    /// <summary>麦克风静音（Core Audio 默认录音端点）。</summary>
    /// <remarks>
    /// 这是「设备级静音」，与 Windows 11 的隐私开关（按应用授权）不是同一机制；
    /// 前者更接近 Fn+F5 的固件行为。端点侧静音不影响隐私开关的状态。
    /// </remarks>
    public const string AudioMicrophone = "audio.microphone";

    /// <summary>内屏亮度写入（WMI WmiMonitorBrightnessMethods）。</summary>
    public const string DisplayBrightness = "display.brightness";

    /// <summary>触控板启用/禁用切换。</summary>
    public const string InputTouchpad = "input.touchpad";

    /// <summary>Fn 组合键事件监听（MSI_ACPI WMI 事件类）。</summary>
    public const string HotkeyFn = "hotkey.fn";

    /// <summary>蓝牙射频开关（WinRT <c>Windows.Devices.Radios</c>）。</summary>
    /// <remarks>
    /// <para>
    /// 与「打开蓝牙设置页」（<c>ms-settings:bluetooth</c>）是两件事：本键指<b>真实切换射频状态</b>，
    /// 状态源是系统同一份 Radio 状态，切换后读回 <c>RadioState</c> 校验。
    /// </para>
    /// <para>
    /// <b>为什么不是禁用设备节点</b>：禁用 USB 蓝牙设备会重枚举整个蓝牙栈、要求管理员权限，
    /// 且可能丢失配对上下文；Radio API 是 Windows 提供的唯一正规开关。
    /// </para>
    /// </remarks>
    public const string SystemBluetooth = "system.bluetooth";

    /// <summary>键盘背光总开关（MSI ACPI Data 0xD3）。</summary>
    public const string KeyboardBacklight = "keyboard.backlight";

    /// <summary>Win 键与 Fn 键交换（MSI ACPI Data 0xE8）。</summary>
    public const string InputWinFnSwap = "input.winfnswap";

    /// <summary>USB 关机共享供电（MSI ACPI Data 0xBF）。</summary>
    public const string PowerUsbShare = "power.usbshare";

    /// <summary>显示省电（刷新率切换，Win32 显示模式 API）。</summary>
    public const string DisplayPowerSaver = "display.powersaver";

    /// <summary>Win 键屏蔽（进程内低级键盘钩子，仅运行期间生效）。</summary>
    public const string InputWindowsKey = "input.winkey";

    /// <summary>全部能力键。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        FanCurve,
        FanFullSpeed,
        FanCalibration,
        FanMode,
        FanPwm,
        ThermalRead,
        ThermalFallback,
        CpuPowerLimit,
        CpuTurboLimit,
        GpuPowerLimit,
        GpuEcoFallback,
        PowerRange,
        GraphicsMode,
        SystemPowerRead,
        BatteryPowerRead,
        EcAccess,
        FirmwareScenario,
        PerformanceStage,
        AudioVolume,
        AudioMicrophone,
        DisplayBrightness,
        InputTouchpad,
        HotkeyFn,
        SystemBluetooth,
        KeyboardBacklight,
        InputWinFnSwap,
        PowerUsbShare,
        DisplayPowerSaver,
        InputWindowsKey
    };
}

/// <summary>
/// 单项能力在本机上的等级与依据。
/// </summary>
/// <remarks>
/// <b>构造只能经工厂方法。</b>构造函数私有，并在构造期强制等级与依据的对应关系，
/// 使「声称已确证却拿不出证据」这类对象无法被构造（与 <see cref="PowerLimit"/> 同构）：
/// <list type="bullet">
/// <item><see cref="CapabilityLevel.Verified"/> 与 <see cref="CapabilityLevel.Detected"/>
///       必须提供 <see cref="Evidence"/>。</item>
/// <item><see cref="CapabilityLevel.Unsupported"/> 必须提供 <see cref="Reason"/>。</item>
/// <item>能力键不得为空。</item>
/// </list>
/// </remarks>
public sealed record CapabilityStatus
{
    private CapabilityStatus(string key, CapabilityLevel level, string? evidence, string? reason)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("能力键不能为空，取值见 CapabilityKeys。", nameof(key));
        }

        switch (level)
        {
            case CapabilityLevel.Verified:
            case CapabilityLevel.Detected:
                if (string.IsNullOrWhiteSpace(evidence))
                {
                    throw new ArgumentException(
                        $"等级 {level} 必须提供可追溯的证据，否则不得声称该能力可用。",
                        nameof(evidence));
                }

                break;

            case CapabilityLevel.Unsupported:
                if (string.IsNullOrWhiteSpace(reason))
                {
                    throw new ArgumentException(
                        "标记为不支持时必须说明依据，否则无法与「未验证」区分。",
                        nameof(reason));
                }

                break;

            case CapabilityLevel.Unknown:
                // 原因可选，但调用方应尽量给出，便于界面展示禁用理由。
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(level), level, "未知的能力等级。");
        }

        Key = key;
        Level = level;
        Evidence = evidence;
        Reason = reason;
    }

    /// <summary>能力键，取值见 <see cref="CapabilityKeys"/>。</summary>
    public string Key { get; }

    /// <summary>验证等级。</summary>
    public CapabilityLevel Level { get; }

    /// <summary>证据描述。<see cref="CapabilityLevel.Detected"/> 及以上必填。</summary>
    public string? Evidence { get; }

    /// <summary>不可用原因。<see cref="CapabilityLevel.Unsupported"/> 必填。</summary>
    public string? Reason { get; }

    /// <summary>是否允许开放写入。仅 <see cref="CapabilityLevel.Verified"/> 允许。</summary>
    public bool AllowsWrite => Level == CapabilityLevel.Verified;

    /// <summary>是否允许只读展示。<see cref="CapabilityLevel.Verified"/> 与 <see cref="CapabilityLevel.Detected"/> 允许。</summary>
    public bool AllowsRead => Level is CapabilityLevel.Verified or CapabilityLevel.Detected;

    /// <summary>构造一个「已确证」状态。必须提供证据。</summary>
    /// <exception cref="ArgumentException">证据为空。</exception>
    public static CapabilityStatus Verified(string key, string evidence)
        => new(key, CapabilityLevel.Verified, evidence, null);

    /// <summary>构造一个「线索」状态：接口存在但语义未验证，仅允许只读。</summary>
    /// <exception cref="ArgumentException">证据为空。</exception>
    public static CapabilityStatus Detected(string key, string evidence)
        => new(key, CapabilityLevel.Detected, evidence, null);

    /// <summary>构造一个「待验证」状态：无证据，一律禁用。</summary>
    public static CapabilityStatus Unknown(string key, string? reason = null)
        => new(key, CapabilityLevel.Unknown, null, reason);

    /// <summary>构造一个「不支持」状态：已确认不可用。必须提供依据。</summary>
    /// <exception cref="ArgumentException">依据为空。</exception>
    public static CapabilityStatus Unsupported(string key, string reason)
        => new(key, CapabilityLevel.Unsupported, null, reason);

    /// <summary>返回给用户看的禁用原因，优先使用 <see cref="Reason"/>，其次退回证据说明。</summary>
    public string DescribeUnavailable()
        => Reason ?? Evidence ?? "该能力尚未在本机取得证据，已按未知处理并禁用。";
}

/// <summary>
/// 一台设备的能力集合。
/// </summary>
/// <remarks>
/// 未登记的能力键一律按 <see cref="CapabilityLevel.Unknown"/> 处理（一律禁用），
/// 而不是默认可用。这是「不显示虚假成功」原则的默认安全方向。
/// </remarks>
public sealed class DeviceCapabilities
{
    private readonly Dictionary<string, CapabilityStatus> _statuses;

    /// <summary>构造能力集合。</summary>
    /// <param name="statuses">各能力的登记状态。同一键重复出现时以最后一个为准。</param>
    /// <remarks>
    /// 键的非空性由 <see cref="CapabilityStatus"/> 在构造期保证，此处不重复校验。
    /// </remarks>
    public DeviceCapabilities(IEnumerable<CapabilityStatus> statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);
        _statuses = new Dictionary<string, CapabilityStatus>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            _statuses[status.Key] = status;
        }
    }

    /// <summary>空能力集合：全部按未知处理。</summary>
    public static DeviceCapabilities Empty { get; } = new(Array.Empty<CapabilityStatus>());

    /// <summary>已登记的能力数量。</summary>
    public int Count => _statuses.Count;

    /// <summary>已登记的全部能力状态。</summary>
    public IReadOnlyCollection<CapabilityStatus> All => _statuses.Values;

    /// <summary>
    /// 取得指定能力的完整状态。未登记时返回 <see cref="CapabilityLevel.Unknown"/>。
    /// </summary>
    /// <exception cref="ArgumentException">能力键为空。</exception>
    public CapabilityStatus Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _statuses.TryGetValue(key, out var status)
            ? status
            : CapabilityStatus.Unknown(key, "未登记该能力，按未知处理。");
    }

    /// <summary>取得指定能力的等级。未登记时为 <see cref="CapabilityLevel.Unknown"/>。</summary>
    public CapabilityLevel LevelOf(string key) => Get(key).Level;

    /// <summary>是否允许对该能力开放写入。</summary>
    public bool AllowsWrite(string key) => Get(key).AllowsWrite;

    /// <summary>是否允许对该能力做只读展示。</summary>
    public bool AllowsRead(string key) => Get(key).AllowsRead;

    /// <summary>返回未登记的、<see cref="CapabilityKeys.All"/> 中列出的能力键。</summary>
    /// <remarks>供启动自检使用：基线与 <see cref="CapabilityKeys"/> 脱节时应能立刻发现。</remarks>
    public IReadOnlyList<string> MissingKeys()
        => CapabilityKeys.All.Where(key => !_statuses.ContainsKey(key)).ToArray();

    /// <summary>返回一个包含新增或覆盖项的新集合，不修改当前实例。</summary>
    public DeviceCapabilities With(CapabilityStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var merged = new List<CapabilityStatus>(_statuses.Values.Count + 1);
        merged.AddRange(_statuses.Values.Where(s => !string.Equals(s.Key, status.Key, StringComparison.Ordinal)));
        merged.Add(status);
        return new DeviceCapabilities(merged);
    }
}
