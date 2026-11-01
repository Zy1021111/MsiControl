namespace MsiControl.Core.Models;

/// <summary>
/// 性能档位与 EC Shift 模式编码（0xD2）的映射表（MSI EC 配置组 CONF_G2_10）。
/// </summary>
/// <remarks>
/// <para>
/// <b>证据分级（2026-10-17 更新，HWE §20/§21，来源 E9）：</b>
/// </para>
/// <list type="bullet">
/// <item>
/// 0xC1/0xC2/0xC4 三编码：本机受控实验「写入+读回+保持」+ 上游语义
/// （comfort=平衡、eco=节能、turbo=狂暴），证据等级最高。
/// </item>
/// <item>
/// 0xC0（sport/高性能）：msi-ec 上游 9 个固件组一致登记 shift_mode=0xC0=sport；
/// 本机受控实验「写入+读回+保持」成立，且 30 秒满载行为指纹与狂暴/平衡同档
/// （100% 频率、~82°C，HWE §21.2）——接受性、保持性与行为均与 sport 先例无矛盾，
/// 据此开放「高性能」档。上游 CONF_G2_10 组未登记该编码，属上游未覆盖而非本机反证。
/// </item>
/// <item>
/// 0xC3/0xC5：寄存器收存任意字节，但满载行为指纹与满血档一致（绝非低功耗特征）、
/// 上游无任何先例——<b>无语义证据，不收录</b>。
/// </item>
/// </list>
/// <para>
/// <b>「安静」档说明：</b>本机固件组（及 MSI 官方 Shift 体系）没有独立的「安静」
/// 场景编码——MSI Center 的四场景（极速性能/平衡/静音/超级电池）中「静音」即
/// 节能场景加静音风扇策略的组合。因此「安静」在本表以 <see cref="PerformanceStage.Quiet"/>
/// → <see cref="EcoEncoding"/>（Shift 写入）+ 静音风扇（0xD4=0x1D，由协调层组合下发）
/// 实现，逐项如实报告，不虚构独立场景编码的存在。
/// </para>
/// </remarks>
public static class PerformanceShiftMap
{
    /// <summary>固件性能场景寄存器地址（EC 0xD2）。</summary>
    public const byte ShiftModeAddress = 0xD2;

    /// <summary>
    /// 性能档位在操作结果里的设置项标识。
    /// </summary>
    /// <remarks>
    /// <b>必须与 <c>MsiControl.Performance.PerformanceSettingKeys.Stage</c> 完全一致</b>
    /// （两者分别位于 Core 与 Performance，后者还带一份与硬件层对齐的约定）。
    /// 放在这里是因为桌面壳只引用 Core，不该为了一个字符串去引用性能配置工程。
    /// </remarks>
    public const string StageSettingKey = "stage";

    /// <summary>节能档（eco）。</summary>
    public const byte EcoEncoding = 0xC2;

    /// <summary>平衡档（comfort）。</summary>
    public const byte BalancedEncoding = 0xC1;

    /// <summary>高性能档（sport；上游 9 组先例 + 本机 HWE §21 行为一致）。</summary>
    public const byte SportEncoding = 0xC0;

    /// <summary>狂暴档（turbo）。</summary>
    public const byte ExtremeEncoding = 0xC4;

    /// <summary>
    /// 固件默认性能场景：<see cref="PerformanceStage.Balanced"/>（comfort，0xC1）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 用途只有一个：用户主动退出程序时，把本软件改过的 Shift 场景**退回厂商基准场景**，
    /// 避免机器在退出后停留在狂暴/高性能这类高功耗档位上。
    /// </para>
    /// <para>
    /// <b>诚实约束：</b>「平衡是出厂默认场景」是 MSI Center 与固件 Shift 体系的惯例，
    /// 本机未取得「ROM 初始值 = 0xC1」的直接证据（HWE §20 只证明了三个编码可写可读可保持）。
    /// 因此界面与日志一律表述为「固件基准场景（comfort/平衡）」，不称「出厂值」。
    /// </para>
    /// </remarks>
    public static PerformanceStage FirmwareDefaultStage => PerformanceStage.Balanced;

    private static readonly IReadOnlyDictionary<PerformanceStage, byte> StageToEncoding =
        new Dictionary<PerformanceStage, byte>
        {
            [PerformanceStage.Eco] = EcoEncoding,
            [PerformanceStage.Quiet] = EcoEncoding, // 安静=组合档：Shift 走节能，风扇静音由协调层叠加
            [PerformanceStage.Balanced] = BalancedEncoding,
            [PerformanceStage.HighPerformance] = SportEncoding,
            [PerformanceStage.Extreme] = ExtremeEncoding
        };

    // 解码表：0xC2 只解码为节能（Quiet 是组合语义，不是独立编码），跳过 Quiet 防重复键。
    private static readonly IReadOnlyDictionary<byte, PerformanceStage> EncodingToStage =
        StageToEncoding
            .Where(pair => pair.Key != PerformanceStage.Quiet)
            .ToDictionary(pair => pair.Value, pair => pair.Key);

    /// <summary>已开放切换的档位集合（五档全开）。</summary>
    public static IReadOnlyCollection<PerformanceStage> SupportedStages { get; } =
        StageToEncoding.Keys.ToArray();

    /// <summary>该档位是否已开放（有已证实编码或属已说明的组合档）。</summary>
    public static bool IsStageSupported(PerformanceStage stage) => StageToEncoding.ContainsKey(stage);

    /// <summary>
    /// 档位循环序（Fn+F7 / 等效命令按此前进）：节能 → 安静 → 平衡 → 高性能 → 狂暴。
    /// </summary>
    /// <remarks>
    /// 顺序对齐 MSI Center 的场景列表。「安静」排第二是因为它与节能共用场景编码，
    /// 相邻摆放可让「再按一次」立刻把静音风扇叠加/撤下，手感与固件四场景循环一致。
    /// </remarks>
    public static IReadOnlyList<PerformanceStage> Cycle { get; } = new[]
    {
        PerformanceStage.Eco,
        PerformanceStage.Quiet,
        PerformanceStage.Balanced,
        PerformanceStage.HighPerformance,
        PerformanceStage.Extreme
    };

    /// <summary>
    /// 取循环中的下一档。
    /// </summary>
    /// <param name="current">当前档位。<see langword="null"/> 表示读不到，按循环首档处理。</param>
    /// <remarks>
    /// 未被登记的档位（不属于 <see cref="Cycle"/>）同样回到首档——不做「就近猜一个」，
    /// 猜错的档位会直接改变功耗行为。
    /// </remarks>
    public static PerformanceStage NextStage(PerformanceStage? current)
    {
        if (current is null)
        {
            return Cycle[0];
        }

        for (var i = 0; i < Cycle.Count; i++)
        {
            if (Cycle[i] == current.Value)
            {
                return Cycle[(i + 1) % Cycle.Count];
            }
        }

        return Cycle[0];
    }

    /// <summary>
    /// 该档位是否为「场景 + 静音风扇」组合档（安静）。
    /// 组合档的 Shift 写入编码与节能档相同，风扇部分由协调层经 IFanModeSwitch 叠加。
    /// </summary>
    public static bool IsSilentFanComposite(PerformanceStage stage)
        => stage == PerformanceStage.Quiet;

    /// <summary>
    /// 取得档位对应的 Shift 编码（组合档返回其场景承载编码，即节能）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// 档位未开放（<see cref="IsStageSupported"/> 为 <see langword="false"/>）。
    /// </exception>
    public static byte Encode(PerformanceStage stage)
        => StageToEncoding.TryGetValue(stage, out var encoding)
            ? encoding
            : throw new ArgumentOutOfRangeException(
                nameof(stage),
                stage,
                "该档位在本机固件组（CONF_G2_10）上没有已证实的 Shift 编码，不得写入。");

    /// <summary>按编码解码档位；无语义证据的编码返回 <see langword="null"/>（不得猜测）。</summary>
    public static PerformanceStage? Decode(byte encoding)
        => EncodingToStage.TryGetValue(encoding, out var stage) ? stage : null;

    /// <summary>编码的人类可读表达（诊断与读回展示用）。未知编码返回十六进制字面量。</summary>
    public static string Describe(byte encoding) => encoding switch
    {
        EcoEncoding => "0xC2（eco/节能）",
        BalancedEncoding => "0xC1（comfort/平衡）",
        SportEncoding => "0xC0（sport/高性能）",
        ExtremeEncoding => "0xC4（turbo/狂暴）",
        _ => $"0x{encoding:X2}（未登记）"
    };
}
