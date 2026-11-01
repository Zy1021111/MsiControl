using MsiControl.Core.Models;

namespace MsiControl.Performance;

/// <summary>
/// 性能档配置中各项设置项的标识。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="SettingResult.Setting"/> 使用同一套标识，使「应用计划」与「逐项结果」
/// 可以按键对齐，逐项核对请求值 / 接口结果 / 读回值（任务书 §8.5）。
/// </para>
/// <para>
/// 每个设置项都绑定一个 <see cref="CapabilityKeys"/> 常量：写入门控与界面禁用文案都必须
/// 依据能力键，而不是散落的字符串判断（docs/接口与工程约定.md §1）。
/// </para>
/// </remarks>
public static class PerformanceSettingKeys
{
    /// <summary>性能档位本身（写入 MSI 固件性能场景）。</summary>
    public const string Stage = "stage";

    /// <summary>CPU 持续功耗上限。</summary>
    public const string CpuLongTerm = "cpu.longterm";

    /// <summary>CPU 短时功耗上限。</summary>
    public const string CpuShortTerm = "cpu.shortterm";

    /// <summary>CPU 睿频上限（频率单位）。</summary>
    public const string CpuTurbo = "cpu.turbo";

    /// <summary>GPU 功耗上限。</summary>
    public const string GpuLimit = "gpu.limit";

    /// <summary>GPU 兼容节能策略（替代策略，仅在目标不受支持时出现）。</summary>
    public const string GpuEco = "gpu.eco";

    /// <summary>全部设置项标识，按应用顺序排列。</summary>
    public static IReadOnlyList<string> All { get; } = new[]
    {
        Stage,
        CpuLongTerm,
        CpuShortTerm,
        CpuTurbo,
        GpuLimit,
        GpuEco
    };

    /// <summary>
    /// 取得设置项对应的能力键。
    /// </summary>
    /// <param name="settingKey">设置项标识。</param>
    /// <returns>对应的 <see cref="CapabilityKeys"/> 常量。</returns>
    /// <exception cref="ArgumentOutOfRangeException">未知的设置项标识。</exception>
    /// <remarks>
    /// 映射只有这一份。界面、计划器与硬件适配层都经此取得能力键，避免三处各写一份而逐渐不一致。
    /// </remarks>
    public static string CapabilityKeyFor(string settingKey) => settingKey switch
    {
        Stage => CapabilityKeys.FirmwareScenario,
        CpuLongTerm => CapabilityKeys.CpuPowerLimit,
        CpuShortTerm => CapabilityKeys.CpuPowerLimit,
        CpuTurbo => CapabilityKeys.CpuTurboLimit,
        GpuLimit => CapabilityKeys.GpuPowerLimit,
        GpuEco => CapabilityKeys.GpuEcoFallback,
        _ => throw new ArgumentOutOfRangeException(
            nameof(settingKey),
            settingKey,
            $"未登记的设置项标识。可用取值见 {nameof(PerformanceSettingKeys)}.All。")
    };

    /// <summary>是否为已登记的设置项标识。</summary>
    public static bool IsKnown(string settingKey)
        => !string.IsNullOrEmpty(settingKey) && All.Contains(settingKey, StringComparer.Ordinal);
}
