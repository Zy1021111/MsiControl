using System.Globalization;
using MsiControl.Core.Models;

namespace MsiControl.Desktop.Presentation;

/// <summary>
/// 性能详细参数表的一行。
/// </summary>
/// <param name="Stage">档位中文名。</param>
/// <param name="CpuPower">
/// CPU 功耗输入值：仅为数字（如 <c>10</c> / <c>10.5</c>），<b>原生上限为空串</b>。
/// </param>
/// <remarks>
/// <para>
/// 界面每个功耗项只有一个数字输入框（叶哥 2026-10-28 要求）：配置模型内部仍分别保存
/// CPU 持续与短时上限（<see cref="PerformanceSettings"/>），提交时同一数值同写两项；
/// 展示取持续上限——两项不一致只可能来自导入配置，此时输入框显示持续值，
/// 提交一次即把两项统一。
/// </para>
/// <para>
/// <b>空串即原生上限</b>：不再用「原生上限」四个字占用输入框，也不允许用 0 或极大数字
/// 表达无限（任务书 §8.1 的反面要求）。
/// </para>
/// <para>
/// <b>没有 GPU 列</b>（叶哥 2026-10-28 要求）：本机 GPU 功耗限制无任何可用写入通道
/// （HWE §26.3：NVML v1/v2 与 nvidia-smi -pl 三者全部返回 NOT_SUPPORTED），
/// 留一个必然失败的输入框只会让人误以为填错了。原因写在能力清单（<c>gpu.power</c>）里，
/// 配置模型仍保留 <see cref="PerformanceSettings.GpuLimit"/> 字段以便将来通道可用时接入。
/// </para>
/// </remarks>
public sealed record PerformanceRow(
    string Stage,
    string CpuPower);

/// <summary>
/// 性能详细参数表的呈现逻辑。
/// </summary>
/// <remarks>
/// <para>
/// <b>数据来源是软件配置模型（<see cref="PerformanceProfileCatalog"/>），不是硬件读回值。</b>
/// 界面必须明确区分两者——任务书 §8.5 要求逐项记录「请求值 / 接口结果 / 读回值」，
/// 把配置目标当成实测值展示属于「不显示虚假成功」的违反。
/// </para>
/// <para>数值表只有一份来源：默认档位表。禁止在界面层另抄一份数字。</para>
/// </remarks>
public static class PerformanceTablePresenter
{
    /// <summary>列标题（不含档位列）。单位进标题，单元格里只留数字。</summary>
    /// <remarks>
    /// 只有 CPU 一列：GPU 功耗限制在本机无可用写入通道（HWE §26.3），
    /// 因此不提供编辑入口，原因见能力清单。
    /// </remarks>
    public static IReadOnlyList<string> ColumnHeaders { get; } = new[]
    {
        "CPU 功耗（W）"
    };

    /// <summary>构建五档参数表。</summary>
    public static IReadOnlyList<PerformanceRow> BuildRows()
        => PerformanceProfileCatalog.Defaults
            .Select(profile => BuildRow(profile.Settings))
            .ToArray();

    /// <summary>构建单个档位的行。</summary>
    public static PerformanceRow BuildRow(PerformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new PerformanceRow(
            ModeFormatter.StageText(settings.Stage),
            FormatPower(settings.CpuLongTerm));
    }

    /// <summary>
    /// 解析功耗输入框的文本。
    /// </summary>
    /// <param name="text">输入框内容。</param>
    /// <param name="limit">成功时为限制对象（空输入对应 <see cref="PowerLimit.Native"/>）。</param>
    /// <param name="error">失败原因。</param>
    /// <remarks>
    /// <b>留空即原生上限</b>：空串与历史上的「原生 / 原生上限」等价，后者保留为兼容输入。
    /// 数字走 <see cref="PowerLimit.TryCustom"/>（拒绝 0、负数与超大值，禁止用极大数字表示无限）。
    /// 解析逻辑放在呈现层而不是窗口里，是为了让这条交互规则可被单元测试锁定。
    /// </remarks>
    public static bool TryParsePower(string text, out PowerLimit? limit, out string? error)
    {
        limit = null;
        error = null;

        if (string.IsNullOrWhiteSpace(text) || text is "原生" or "原生上限")
        {
            limit = PowerLimit.Native;
            return true;
        }

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var watts))
        {
            error = $"无法识别的输入「{text}」。请输入瓦数（如 55），留空表示原生上限。";
            return false;
        }

        return PowerLimit.TryCustom(watts, out limit, out error);
    }

    /// <summary>把功耗上限格式化为输入框内容：原生上限为空串，其余只给数字。</summary>
    private static string FormatPower(PowerLimit limit)
        => limit.Watts is { } watts
            ? watts.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
}
