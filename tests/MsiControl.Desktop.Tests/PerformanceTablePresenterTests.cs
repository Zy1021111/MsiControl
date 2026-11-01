using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Desktop.Presentation;
using Xunit;

namespace MsiControl.Desktop.Tests;

/// <summary>
/// 性能详细参数表的呈现测试。
/// </summary>
/// <remarks>
/// <para>
/// 重点：界面数值必须来自唯一来源 <see cref="PerformanceProfileCatalog"/>；
/// 每个功耗项只有一个输入框，<b>原生上限显示为空串</b>（留空即不限），
/// 既不用 0、也不用极大数字表达无限（任务书 §8.1）。
/// </para>
/// <para>CPU 持续与短时在界面合并为一个输入项；底层模型仍分别保存两项。</para>
/// <para>
/// <b>没有 GPU 列</b>（叶哥 2026-10-28 要求）：本机 GPU 功耗限制无可用写入通道
/// （HWE §26.3），留一个必然失败的输入框会误导用户；原因写在能力清单里。
/// 配置模型的 <see cref="PerformanceSettings.GpuLimit"/> 字段仍保留。
/// </para>
/// </remarks>
public sealed class PerformanceTablePresenterTests
{
    [Fact]
    public void BuildRows_CoversAllFiveStagesInEnumOrder()
    {
        var rows = PerformanceTablePresenter.BuildRows();

        Assert.Equal(5, rows.Count);
        Assert.Equal(
            new[] { "节能", "安静", "平衡", "高性能", "狂暴" },
            rows.Select(row => row.Stage));
    }

    [Fact]
    public void BuildRows_MatchesCatalogNumbersExactly()
    {
        var rows = PerformanceTablePresenter.BuildRows();
        var defaults = PerformanceProfileCatalog.Defaults;

        Assert.Equal(defaults.Count, rows.Count);

        for (var index = 0; index < defaults.Count; index++)
        {
            var settings = defaults[index].Settings;
            var row = rows[index];

            // 默认表中持续与短时同值，单元格只放数字；原生上限为空串。
            Assert.Equal(Expected(settings.CpuLongTerm), row.CpuPower);
        }
    }

    [Fact]
    public void EcoRow_ShowsTenWattCpuTarget()
    {
        var eco = PerformanceTablePresenter.BuildRows()[0];

        Assert.Equal("10", eco.CpuPower);
    }

    [Fact]
    public void ExtremeRow_LeavesBoxEmptyToMeanNativeLimit()
    {
        var extreme = PerformanceTablePresenter.BuildRows()[4];

        // 留空即原生上限：不放「原生上限」四个字进输入框。
        Assert.Equal(string.Empty, extreme.CpuPower);
    }

    [Fact]
    public void BuildRow_CpuPowerShowsLongTermWhenLongAndShortDiffer()
    {
        // 单个输入框只能表达一个值：两项不一致（仅可能来自导入配置）时显示持续上限，
        // 提交一次即把两项统一；此处锁定该取舍，避免以后又退回「50 W / 80 W」。
        var settings = new PerformanceSettings(
            PerformanceStage.Balanced,
            CpuLongTerm: PowerLimit.Custom(50m),
            CpuShortTerm: PowerLimit.Custom(80m),
            CpuTurbo: CpuTurboLimit.Native,
            GpuLimit: PowerLimit.Native);

        var row = PerformanceTablePresenter.BuildRow(settings);

        Assert.Equal("50", row.CpuPower);
    }

    [Fact]
    public void BuildRow_RejectsNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = PerformanceTablePresenter.BuildRow(null!);
        });
    }

    [Fact]
    public void ColumnHeaders_AreLocalizedAndCarryUnits()
    {
        var headers = PerformanceTablePresenter.ColumnHeaders;

        // 只剩 CPU 一列：GPU 功耗限制在本机无可用写入通道（HWE §26.3）。
        Assert.Single(headers);
        Assert.DoesNotContain("档位", headers);
        Assert.Contains("CPU 功耗（W）", headers);

        // 已按需求移除的列，不得回归。
        Assert.DoesNotContain("GPU 功耗（W）", headers);
        Assert.DoesNotContain("CPU 睿频上限", headers);
        Assert.DoesNotContain("CPU 持续功耗", headers);
        Assert.DoesNotContain("说明", headers);
    }

    [Fact]
    public void Row_HasNoGpuColumn()
    {
        // 行结构只有「档位 + CPU 功耗」两项：一旦有人加回 GPU 字段，
        // 表格会多出一列必然报不支持的输入框 —— 用反射把这条约束钉住。
        var properties = typeof(PerformanceRow).GetProperties();

        Assert.Equal(2, properties.Length);
        Assert.Contains(properties, p => p.Name == nameof(PerformanceRow.Stage));
        Assert.Contains(properties, p => p.Name == nameof(PerformanceRow.CpuPower));
    }

    /// <summary>留空（或空白的旧写法「原生」）表示该项无上限。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("原生")]
    [InlineData("原生上限")]
    public void TryParsePower_EmptyOrNativeWordingMeansNoLimit(string text)
    {
        var ok = PerformanceTablePresenter.TryParsePower(text, out var limit, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(limit);
        Assert.True(limit!.IsNative);
    }

    /// <summary>数字输入构造自定义瓦数。</summary>
    [Theory]
    [InlineData("10", 10)]
    [InlineData(" 55 ", 55)]
    [InlineData("10.5", 10.5)]
    public void TryParsePower_ParsesWatts(string text, double expected)
    {
        var ok = PerformanceTablePresenter.TryParsePower(text, out var limit, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(limit);
        Assert.False(limit!.IsNative);
        Assert.Equal((decimal)expected, limit.Watts);
    }

    /// <summary>非法输入给出可读原因，且不返回任何限制对象（避免静默按 0 处理）。</summary>
    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("2000")]
    public void TryParsePower_RejectsInvalidInput(string text)
    {
        var ok = PerformanceTablePresenter.TryParsePower(text, out var limit, out var error);

        Assert.False(ok);
        Assert.Null(limit);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    /// <summary>回显与解析必须互逆：格式化出的文本能被解析回同一限制。</summary>
    [Fact]
    public void FormatAndParseAreInverse()
    {
        var settings = new PerformanceSettings(
            PerformanceStage.Eco,
            CpuLongTerm: PowerLimit.Custom(37.5m),
            CpuShortTerm: PowerLimit.Custom(37.5m),
            CpuTurbo: CpuTurboLimit.Native,
            GpuLimit: PowerLimit.Native);

        var row = PerformanceTablePresenter.BuildRow(settings);

        Assert.True(PerformanceTablePresenter.TryParsePower(row.CpuPower, out var cpu, out _));
        Assert.Equal(37.5m, cpu!.Watts);

        // 空串回解析为原生上限（「留空＝不限」这条语义仍被锁定）。
        Assert.True(PerformanceTablePresenter.TryParsePower(string.Empty, out var native, out _));
        Assert.True(native!.IsNative);
    }

    private static string Expected(PowerLimit limit)
        => limit.Watts is { } watts
            ? watts.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
}
