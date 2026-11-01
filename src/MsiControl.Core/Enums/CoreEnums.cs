namespace MsiControl.Core;

/// <summary>
/// 传感器读数的有效性与生命周期状态。
/// </summary>
/// <remarks>
/// 关键约束：数据过期时必须置为 <see cref="Stale"/>，
/// <b>不得沿用上一个有效值冒充当前读数</b>（任务书 §6.4、接口与工程约定 §3.1）。
/// </remarks>
public enum ReadingStatus
{
    /// <summary>读数有效，且在当前采样周期内。</summary>
    Valid = 0,

    /// <summary>设备处于休眠（例如独显已断电），当前不可读数。界面显示「休眠」。</summary>
    Sleeping = 1,

    /// <summary>读数不可用（接口不支持、驱动缺失或读取失败）。界面显示「不可用」。</summary>
    Unavailable = 2,

    /// <summary>数据已超过有效期。界面须显式标注为过期。</summary>
    Stale = 3
}

/// <summary>
/// 单次设置或整组控制操作的结果状态。
/// </summary>
/// <remarks>
/// 关键约束：
/// <list type="bullet">
/// <item><see cref="Succeeded"/> 必须有可追溯的确认依据（见 <c>SettingResult.ConfirmationBasis</c>），
/// 不得因为「调用没有抛异常」就判定成功。</item>
/// <item><see cref="PendingRestart"/> 与 v1.1 的「待生效」同义，仅表示需要重启或 S5 关机再开机才生效。
/// 若出现「不重启但存在过渡期」的情形，不得复用该值，须新增枚举成员（任务书 §16）。</item>
/// </list>
/// </remarks>
public enum OperationStatus
{
    /// <summary>已确认生效。</summary>
    Succeeded = 0,

    /// <summary>整组操作中部分项目成功、部分失败。不得宣称整组回滚成功。</summary>
    PartiallySucceeded = 1,

    /// <summary>操作失败。</summary>
    Failed = 2,

    /// <summary>已写入，但需要重启或 S5 关机再开机才会生效。</summary>
    PendingRestart = 3,

    /// <summary>本机不支持该操作。必须附带原因与依据。</summary>
    Unsupported = 4
}

/// <summary>
/// 单项能力在本机上的验证等级。
/// </summary>
/// <remarks>
/// 这是把「不显示虚假成功」原则落成可判定规则的核心机制（任务书 §16.1）。
/// <b>仅凭「WMI 类存在」或「方法名存在」不得升级等级。</b>
/// </remarks>
public enum CapabilityLevel
{
    /// <summary>已确证：有实机返回值或实测证据。可开放读写。</summary>
    Verified = 0,

    /// <summary>线索：接口、方法或设备存在，但语义与可用性未验证。仅允许只读展示。</summary>
    Detected = 1,

    /// <summary>待验证：无任何证据。一律禁用。</summary>
    Unknown = 2,

    /// <summary>不支持：已确认不可用（例如返回 <c>WBEM_E_NOT_SUPPORTED</c> 的类）。显式禁用。</summary>
    Unsupported = 3
}

/// <summary>
/// CPU 统一频率的统计口径。
/// </summary>
/// <remarks>任务书 §6.5 要求界面注明口径。本项目默认使用 <see cref="AverageActive"/>。</remarks>
public enum FrequencyAggregation
{
    /// <summary>所有活跃核心时钟频率的均值。</summary>
    AverageActive = 0,

    /// <summary>所有活跃核心时钟频率的最大值。</summary>
    MaximumActive = 1
}
