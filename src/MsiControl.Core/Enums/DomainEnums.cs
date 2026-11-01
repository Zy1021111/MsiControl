namespace MsiControl.Core;

/// <summary>风扇通道。</summary>
public enum FanChannel
{
    /// <summary>CPU 风扇。</summary>
    Cpu = 0,

    /// <summary>GPU 风扇。</summary>
    Gpu = 1
}

/// <summary>风扇控制模式。</summary>
public enum FanControlMode
{
    /// <summary>固件自动策略。</summary>
    Automatic = 0,

    /// <summary>用户自定义曲线。</summary>
    CustomCurve = 1,

    /// <summary>全速（强冷）。</summary>
    FullSpeed = 2,

    /// <summary>静音模式（EC 0xD4=0x1D，固件静音曲线）。</summary>
    /// <remarks>2026-10-17 增补：EC 写入实验实证 0xD4 支持 auto/silent/advanced 三编码（HWE §17.1）。</remarks>
    Silent = 3
}

/// <summary>显卡工作模式。</summary>
/// <remarks>
/// 禁止以「设备管理器禁用独显」冒充 <see cref="Integrated"/>，
/// 禁止以「应用 GPU 偏好」冒充 MUX 切换（任务书 §9.3）。
/// </remarks>
public enum GraphicsMode
{
    /// <summary>核显模式。</summary>
    Integrated = 0,

    /// <summary>混合模式（MSHybrid）。</summary>
    Hybrid = 1,

    /// <summary>独显直连（MUX / 独显输出）。</summary>
    Discrete = 2
}

/// <summary>五档性能模式。</summary>
/// <remarks>枚举名与任务书 §8.1 的档位一一对应，禁止增删而不更新任务书。</remarks>
public enum PerformanceStage
{
    /// <summary>节能。</summary>
    Eco = 0,

    /// <summary>安静。</summary>
    Quiet = 1,

    /// <summary>平衡。</summary>
    Balanced = 2,

    /// <summary>高性能。</summary>
    HighPerformance = 3,

    /// <summary>狂暴。</summary>
    Extreme = 4
}

/// <summary>
/// 功耗或频率限制的表达方式。
/// </summary>
/// <remarks>
/// <b>禁止使用 0、-1 或极大数字隐式代表「无限」</b>（任务书 §8.1、§16）。
/// 「原生上限」一律使用 <see cref="Native"/>。
/// </remarks>
public enum LimitMode
{
    /// <summary>原生上限：取消本软件的额外限制，恢复设备允许的原生策略，不绕过硬件保护。</summary>
    Native = 0,

    /// <summary>自定义数值。</summary>
    Custom = 1
}

/// <summary>整机功耗的测量口径。</summary>
/// <remarks>三种口径不可混用、不可相加（任务书 §6.5、接口与工程约定 §2.1）。</remarks>
public enum PowerSourceKind
{
    /// <summary>未确定或不可用。</summary>
    Unknown = 0,

    /// <summary>电池侧（电池放电功率）。</summary>
    BatterySide = 1,

    /// <summary>适配器输入侧。</summary>
    AdapterInput = 2,

    /// <summary>插座侧。</summary>
    WallSocket = 3
}

/// <summary>供电来源类型。</summary>
public enum PowerSupplyKind
{
    /// <summary>未知。</summary>
    Unknown = 0,

    /// <summary>电池供电（已拔电）。</summary>
    Battery = 1,

    /// <summary>交流供电（已插电）。</summary>
    Ac = 2
}
