using MsiControl.Core;
using MsiControl.Core.Models;

namespace MsiControl.Persistence.Configuration;

/// <summary>
/// 配置档的可序列化形状。
/// </summary>
/// <remarks>
/// 为什么不直接序列化领域类型：<see cref="PowerLimit"/>、<see cref="CpuTurboLimit"/>
/// 与 <see cref="FanCurve"/> 的**构造函数是私有的**，只能经工厂方法创建以强制不变量
/// （docs/接口说明.md §3.8）。直接反序列化会绕过这些不变量，
/// 因此必须经过 <see cref="ConfigProfileSerializer"/> 用工厂方法重建并校验。
/// </remarks>
internal sealed class ConfigProfileDto
{
    public int SchemaVersion { get; set; } = ConfigProfile.CurrentSchemaVersion;

    public DeviceIdentityDto? Device { get; set; }

    public string Name { get; set; } = string.Empty;

    public List<string> Tags { get; set; } = new();

    public int SortOrder { get; set; }

    public List<StageDto> Stages { get; set; } = new();
}

/// <summary>设备标识的序列化形状。</summary>
/// <remarks>
/// 独立定义而不是直接序列化 <see cref="DeviceIdentity"/>：
/// 后者带有一批只读派生属性（<c>IsIdentified</c> 等），直接序列化会写入冗余字段。
/// </remarks>
internal sealed class DeviceIdentityDto
{
    public string? Model { get; set; }

    public string? BoardCode { get; set; }

    public string? CpuName { get; set; }

    public string? DiscreteGpuName { get; set; }

    public string? IntegratedGpuName { get; set; }

    public string? BiosVersion { get; set; }

    public string? EcVersion { get; set; }

    public string? WindowsBuild { get; set; }

    public string? GpuDriverVersion { get; set; }

    public static DeviceIdentityDto From(DeviceIdentity device) => new()
    {
        Model = device.Model,
        BoardCode = device.BoardCode,
        CpuName = device.CpuName,
        DiscreteGpuName = device.DiscreteGpuName,
        IntegratedGpuName = device.IntegratedGpuName,
        BiosVersion = device.BiosVersion,
        EcVersion = device.EcVersion,
        WindowsBuild = device.WindowsBuild,
        GpuDriverVersion = device.GpuDriverVersion
    };

    public DeviceIdentity ToDomain() => new(
        Model: Model ?? DeviceIdentity.UnknownBoardCode,
        BoardCode: BoardCode ?? DeviceIdentity.UnknownBoardCode,
        CpuName: CpuName,
        DiscreteGpuName: DiscreteGpuName,
        IntegratedGpuName: IntegratedGpuName,
        BiosVersion: BiosVersion,
        EcVersion: EcVersion,
        WindowsBuild: WindowsBuild,
        GpuDriverVersion: GpuDriverVersion);
}

/// <summary>单个性能档位的序列化形状。</summary>
internal sealed class StageDto
{
    public PerformanceStage Stage { get; set; }

    public LimitDto? CpuLongTerm { get; set; }

    public LimitDto? CpuShortTerm { get; set; }

    public TurboDto? CpuTurbo { get; set; }

    public LimitDto? GpuLimit { get; set; }

    public EcoDto? GpuEcoFallback { get; set; }

    public CurveDto? CpuCurve { get; set; }

    public CurveDto? GpuCurve { get; set; }
}

/// <summary>功耗限制的序列化形状。</summary>
internal sealed class LimitDto
{
    public LimitMode Mode { get; set; }

    public decimal? Watts { get; set; }

    public static LimitDto From(PowerLimit limit) => new() { Mode = limit.Mode, Watts = limit.Watts };
}

/// <summary>睿频上限的序列化形状。</summary>
internal sealed class TurboDto
{
    public LimitMode Mode { get; set; }

    public int? Megahertz { get; set; }

    public static TurboDto From(CpuTurboLimit limit) => new() { Mode = limit.Mode, Megahertz = limit.Megahertz };
}

/// <summary>GPU 兼容节能策略的序列化形状。</summary>
internal sealed class EcoDto
{
    public string? Description { get; set; }

    public LimitDto? Applied { get; set; }

    public string? Limitation { get; set; }
}

/// <summary>风扇曲线的序列化形状。</summary>
internal sealed class CurveDto
{
    public FanChannel Channel { get; set; }

    public List<PointDto> Points { get; set; } = new();

    public static CurveDto From(FanCurve curve) => new()
    {
        Channel = curve.Channel,
        Points = curve.Points.Select(p => new PointDto { TemperatureC = p.TemperatureC, OutputPercent = p.OutputPercent }).ToList()
    };
}

/// <summary>曲线节点的序列化形状。</summary>
internal sealed class PointDto
{
    public double TemperatureC { get; set; }

    public double OutputPercent { get; set; }
}
