using System.Text.Encodings.Web;
using System.Text.Json;
using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Persistence.Configuration;

/// <summary>
/// 配置档反序列化的结果。
/// </summary>
/// <param name="Ok">是否成功重建为领域对象。</param>
/// <param name="Profile">成功时的配置档。</param>
/// <param name="Errors">问题列表，含被构造函数拒绝的具体原因。</param>
public sealed record ConfigDeserializeResult(
    bool Ok,
    ConfigProfile? Profile,
    IReadOnlyList<string> Errors);

/// <summary>
/// 配置档的 JSON 序列化与重建。
/// </summary>
/// <remarks>
/// <para>
/// <b>重建必须经过 Core 的工厂方法</b>（<see cref="PowerLimit.TryCustom"/>、
/// <see cref="CpuTurboLimit.TryCustom"/>、<see cref="FanCurve.TryCreate"/>），
/// 不能依赖反序列化直接构造。原因：这些类型的构造函数私有，
/// 存在的意义就是阻止「0 W 代表无限」「短时低于持续」这类非法组合进入系统。
/// 若反序列化绕开它们，等于在导入路径上开了一个后门。
/// </para>
/// <para>
/// 本类型**不做机型校验**——那是 <see cref="ConfigImportValidator"/> 的职责。
/// 序列化/重建只关心「结构是否合法」。
/// </para>
/// </remarks>
public static class ConfigProfileSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>把配置档序列化为 JSON 文本。</summary>
    /// <exception cref="ArgumentNullException">参数为空。</exception>
    public static string Serialize(ConfigProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(ToDto(profile), SerializerOptions);
    }

    /// <summary>
    /// 从 JSON 文本重建配置档。
    /// </summary>
    /// <returns>重建结果。失败时 <see cref="ConfigDeserializeResult.Errors"/> 列出全部原因。</returns>
    public static ConfigDeserializeResult Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ConfigDeserializeResult(false, null, new[] { "配置内容为空。" });
        }

        ConfigProfileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ConfigProfileDto>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return new ConfigDeserializeResult(false, null, new[] { $"配置格式解析失败：{ex.Message}" });
        }

        if (dto is null)
        {
            return new ConfigDeserializeResult(false, null, new[] { "配置内容为空。" });
        }

        var errors = new List<string>();

        if (dto.SchemaVersion > ConfigProfile.CurrentSchemaVersion)
        {
            errors.Add(
                $"配置版本 {dto.SchemaVersion} 高于本程序支持的 {ConfigProfile.CurrentSchemaVersion}，拒绝导入。");
            return new ConfigDeserializeResult(false, null, errors);
        }

        if (dto.Device is null)
        {
            errors.Add("配置缺少设备标识（device）节点。");
            return new ConfigDeserializeResult(false, null, errors);
        }

        var stages = new List<PerformanceSettings>();
        foreach (var stageDto in dto.Stages)
        {
            var settings = BuildStage(stageDto, errors);
            if (settings is not null)
            {
                stages.Add(settings);
            }
        }

        if (errors.Count > 0)
        {
            return new ConfigDeserializeResult(false, null, errors);
        }

        var profile = new ConfigProfile(
            dto.SchemaVersion,
            dto.Device.ToDomain(),
            dto.Name ?? string.Empty,
            dto.Tags.ToArray(),
            dto.SortOrder,
            stages);

        return new ConfigDeserializeResult(true, profile, Array.Empty<string>());
    }

    private static ConfigProfileDto ToDto(ConfigProfile profile) => new()
    {
        SchemaVersion = profile.SchemaVersion,
        Device = DeviceIdentityDto.From(profile.Device),
        Name = profile.Name,
        Tags = profile.Tags.ToList(),
        SortOrder = profile.SortOrder,
        Stages = profile.Stages.Select(ToDto).ToList()
    };

    private static StageDto ToDto(PerformanceSettings settings) => new()
    {
        Stage = settings.Stage,
        CpuLongTerm = LimitDto.From(settings.CpuLongTerm),
        CpuShortTerm = LimitDto.From(settings.CpuShortTerm),
        CpuTurbo = TurboDto.From(settings.CpuTurbo),
        GpuLimit = LimitDto.From(settings.GpuLimit),
        GpuEcoFallback = settings.GpuEcoFallback is null
            ? null
            : new EcoDto
            {
                Description = settings.GpuEcoFallback.Description,
                Applied = LimitDto.From(settings.GpuEcoFallback.Applied),
                Limitation = settings.GpuEcoFallback.Limitation
            },
        CpuCurve = settings.CpuCurve is null ? null : CurveDto.From(settings.CpuCurve),
        GpuCurve = settings.GpuCurve is null ? null : CurveDto.From(settings.GpuCurve)
    };

    private static PerformanceSettings? BuildStage(StageDto dto, List<string> errors)
    {
        var label = StageLabel(dto.Stage);
        var errorsBefore = errors.Count;

        if (!Enum.IsDefined(dto.Stage))
        {
            errors.Add($"档位取值 {(int)dto.Stage} 无效。");
            return null;
        }

        if (!TryBuildLimit(dto.CpuLongTerm, $"{label}·CPU 持续功耗", errors, out var cpuLong))
        {
            return null;
        }

        if (!TryBuildLimit(dto.CpuShortTerm, $"{label}·CPU 短时功耗", errors, out var cpuShort))
        {
            return null;
        }

        if (!TryBuildTurbo(dto.CpuTurbo, $"{label}·CPU 睿频上限", errors, out var turbo))
        {
            return null;
        }

        if (!TryBuildLimit(dto.GpuLimit, $"{label}·GPU 功耗", errors, out var gpuLimit))
        {
            return null;
        }

        GpuEcoFallback? eco = null;
        if (dto.GpuEcoFallback is not null)
        {
            if (!TryBuildLimit(dto.GpuEcoFallback.Applied, $"{label}·GPU 节能策略", errors, out var ecoApplied))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(dto.GpuEcoFallback.Limitation))
            {
                errors.Add($"{label}·GPU 兼容节能策略缺少能力边界说明（limitation）。");
                return null;
            }

            eco = GpuEcoFallback.Create(
                dto.GpuEcoFallback.Description ?? "兼容节能策略",
                ecoApplied,
                dto.GpuEcoFallback.Limitation);
        }

        var settings = new PerformanceSettings(
            dto.Stage,
            CpuLongTerm: cpuLong,
            CpuShortTerm: cpuShort,
            CpuTurbo: turbo,
            GpuLimit: gpuLimit,
            GpuEcoFallback: eco,
            CpuCurve: null,
            GpuCurve: null);

        if (!TryBuildCurve(dto.CpuCurve, FanChannel.Cpu, $"{label}·CPU 风扇曲线", errors, out var cpuCurve))
        {
            return null;
        }

        if (!TryBuildCurve(dto.GpuCurve, FanChannel.Gpu, $"{label}·GPU 风扇曲线", errors, out var gpuCurve))
        {
            return null;
        }

        settings = settings with { CpuCurve = cpuCurve, GpuCurve = gpuCurve };

        foreach (var problem in settings.Validate())
        {
            errors.Add($"{label}·{problem}");
        }

        return errors.Count > errorsBefore ? null : settings;
    }

    private static bool TryBuildLimit(
        LimitDto? dto,
        string label,
        List<string> errors,
        out PowerLimit limit)
    {
        limit = PowerLimit.Native;

        if (dto is null)
        {
            errors.Add($"{label} 缺失。");
            return false;
        }

        switch (dto.Mode)
        {
            case LimitMode.Native:
                limit = PowerLimit.Native;
                return true;

            case LimitMode.Custom:
                if (dto.Watts is null)
                {
                    errors.Add($"{label} 标记为自定义但未给出瓦数。");
                    return false;
                }

                if (!PowerLimit.TryCustom(dto.Watts.Value, out var custom, out var error))
                {
                    errors.Add($"{label}：{error}");
                    return false;
                }

                limit = custom!;
                return true;

            default:
                errors.Add($"{label} 的限制模式取值 {(int)dto.Mode} 无效。");
                return false;
        }
    }

    private static bool TryBuildTurbo(
        TurboDto? dto,
        string label,
        List<string> errors,
        out CpuTurboLimit turbo)
    {
        turbo = CpuTurboLimit.Native;

        if (dto is null)
        {
            errors.Add($"{label} 缺失。");
            return false;
        }

        switch (dto.Mode)
        {
            case LimitMode.Native:
                turbo = CpuTurboLimit.Native;
                return true;

            case LimitMode.Custom:
                if (dto.Megahertz is null)
                {
                    errors.Add($"{label} 标记为自定义但未给出频率。");
                    return false;
                }

                if (!CpuTurboLimit.TryCustom(dto.Megahertz.Value, out var custom, out var error))
                {
                    errors.Add($"{label}：{error}");
                    return false;
                }

                turbo = custom!;
                return true;

            default:
                errors.Add($"{label} 的限制模式取值 {(int)dto.Mode} 无效。");
                return false;
        }
    }

    private static bool TryBuildCurve(
        CurveDto? dto,
        FanChannel expectedChannel,
        string label,
        List<string> errors,
        out FanCurve? curve)
    {
        curve = null;

        if (dto is null)
        {
            return true;
        }

        if (dto.Channel != expectedChannel)
        {
            errors.Add($"{label} 的通道为 {dto.Channel}，应为 {expectedChannel}。");
            return false;
        }

        var points = dto.Points.Select(p => new FanPoint(p.TemperatureC, p.OutputPercent));
        if (!FanCurve.TryCreate(dto.Channel, points, out var created, out var errors2))
        {
            foreach (var problem in errors2)
            {
                errors.Add($"{label}：{problem}");
            }

            return false;
        }

        curve = created;
        return true;
    }

    private static string StageLabel(PerformanceStage stage) => stage switch
    {
        PerformanceStage.Eco => "节能",
        PerformanceStage.Quiet => "安静",
        PerformanceStage.Balanced => "平衡",
        PerformanceStage.HighPerformance => "高性能",
        PerformanceStage.Extreme => "狂暴",
        _ => $"档位{(int)stage}"
    };
}
