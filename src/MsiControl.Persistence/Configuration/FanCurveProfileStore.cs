using System.Text.Json;
using System.Text.Json.Serialization;
using MsiControl.Core;

namespace MsiControl.Persistence.Configuration;

/// <summary>一条命名风扇曲线配置档（含 CPU/GPU 两通道）。</summary>
/// <param name="Id">稳定标识。</param>
/// <param name="Name">显示名。</param>
/// <param name="Tag">配置标签（如「自定义」「静音」「性能」）。</param>
/// <param name="ModifiedAt">最近修改时刻（本地时间）。</param>
/// <param name="CpuPoints">CPU 通道节点（温度 °C，输出 %）。</param>
/// <param name="GpuPoints">GPU 通道节点（温度 °C，输出 %）。</param>
public sealed record FanCurveProfile(
    Guid Id,
    string Name,
    string Tag,
    DateTimeOffset ModifiedAt,
    IReadOnlyList<FanProfilePoint> CpuPoints,
    IReadOnlyList<FanProfilePoint> GpuPoints);

/// <summary>曲线节点（序列化用）。</summary>
/// <param name="T">温度，摄氏度。</param>
/// <param name="O">输出百分比。</param>
public readonly record struct FanProfilePoint(double T, double O);

/// <summary>
/// 风扇曲线槽位：五个性能档位各一条，外加一条独立的「自定义」。
/// </summary>
/// <remarks>
/// 槽位与 <see cref="PerformanceStage"/> 一一对应（节能/安静/平衡/高性能/狂暴），
/// <see cref="Custom"/> 不绑定任何档位，仅供手动曲线单独存放一组参数。
/// 存储键为稳定小写标识，与枚举值解耦：将来枚举调整不会让旧文件失去意义。
/// </remarks>
public enum FanCurveSlot
{
    /// <summary>节能档位。</summary>
    Eco = 0,

    /// <summary>安静档位。</summary>
    Quiet = 1,

    /// <summary>平衡档位。</summary>
    Balanced = 2,

    /// <summary>高性能档位。</summary>
    HighPerformance = 3,

    /// <summary>狂暴档位。</summary>
    Extreme = 4,

    /// <summary>自定义（不绑定档位）。</summary>
    Custom = 5
}

/// <summary>槽位与存储键、性能档位之间的映射。</summary>
public static class FanCurveSlots
{
    /// <summary>全部槽位，按界面展示顺序排列。</summary>
    public static IReadOnlyList<FanCurveSlot> All { get; } = new[]
    {
        FanCurveSlot.Eco,
        FanCurveSlot.Quiet,
        FanCurveSlot.Balanced,
        FanCurveSlot.HighPerformance,
        FanCurveSlot.Extreme,
        FanCurveSlot.Custom
    };

    /// <summary>槽位的稳定存储键。</summary>
    public static string KeyOf(FanCurveSlot slot) => slot switch
    {
        FanCurveSlot.Eco => "eco",
        FanCurveSlot.Quiet => "quiet",
        FanCurveSlot.Balanced => "balanced",
        FanCurveSlot.HighPerformance => "high",
        FanCurveSlot.Extreme => "extreme",
        FanCurveSlot.Custom => "custom",
        _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "未登记的曲线槽位。")
    };

    /// <summary>槽位对应的性能档位；「自定义」不绑定档位，返回 <see langword="null"/>。</summary>
    public static PerformanceStage? StageOf(FanCurveSlot slot) => slot switch
    {
        FanCurveSlot.Eco => PerformanceStage.Eco,
        FanCurveSlot.Quiet => PerformanceStage.Quiet,
        FanCurveSlot.Balanced => PerformanceStage.Balanced,
        FanCurveSlot.HighPerformance => PerformanceStage.HighPerformance,
        FanCurveSlot.Extreme => PerformanceStage.Extreme,
        _ => null
    };

    /// <summary>性能档位对应的槽位。</summary>
    public static FanCurveSlot ForStage(PerformanceStage stage) => stage switch
    {
        PerformanceStage.Eco => FanCurveSlot.Eco,
        PerformanceStage.Quiet => FanCurveSlot.Quiet,
        PerformanceStage.Balanced => FanCurveSlot.Balanced,
        PerformanceStage.HighPerformance => FanCurveSlot.HighPerformance,
        PerformanceStage.Extreme => FanCurveSlot.Extreme,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "未登记的性能档位。")
    };
}

/// <summary>曲线配置档文件的根（含界面偏好）。</summary>
public sealed record FanCurveProfileFile
{
    /// <summary>格式版本。</summary>
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    /// <summary>全部配置档。</summary>
    [JsonPropertyName("profiles")]
    public IReadOnlyList<FanCurveProfile> Profiles { get; init; } = Array.Empty<FanCurveProfile>();

    /// <summary>上次选中的配置档。</summary>
    [JsonPropertyName("selectedId")]
    public Guid? SelectedId { get; init; }

    /// <summary>排序偏好：Modified（最近修改）/ Name（名称）。</summary>
    [JsonPropertyName("sort")]
    public string Sort { get; init; } = "Modified";

    /// <summary>标签过滤：空串或「全部」表示不过滤。</summary>
    [JsonPropertyName("tagFilter")]
    public string TagFilter { get; init; } = "全部";

    /// <summary>控制模式偏好：Automatic / Manual（仅记忆，应用时仍以实机读回为准）。</summary>
    [JsonPropertyName("modePreference")]
    public string ModePreference { get; init; } = "Automatic";

    /// <summary>
    /// 按槽位保存的曲线（键见 <see cref="FanCurveSlots.KeyOf"/>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Profiles"/> 并存的第二套存储：<see cref="Profiles"/> 是旧版「配置档列表」，
    /// 仅为兼容既有 <c>fan-curves.json</c> 保留，新界面不再读取或写入；
    /// 槽位曲线是当前界面唯一的曲线数据源，未配置的槽位以「键缺失」表达。
    /// </remarks>
    [JsonPropertyName("slotCurves")]
    public IReadOnlyDictionary<string, FanCurveProfile> SlotCurves { get; init; }
        = new Dictionary<string, FanCurveProfile>(StringComparer.Ordinal);
}

/// <summary>
/// 风扇曲线配置档存储（JSON，位于应用数据目录 <c>fan-curves.json</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 损坏处理与 <c>JsonSettingsStore</c> 同向：解析失败时把原文件改名保留，
/// 以空文件重新开始，绝不因配置损坏而拒绝启动。
/// </para>
/// </remarks>
public sealed class FanCurveProfileStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;

    /// <summary>构造存储。传入所在目录（通常为 <see cref="AppPaths.RootDirectory"/>）。</summary>
    public FanCurveProfileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "fan-curves.json");
    }

    /// <summary>存储文件完整路径（诊断用）。</summary>
    public string FilePath => _filePath;

    /// <summary>读取全部内容；文件不存在或损坏时返回空文件并保留损坏原件。</summary>
    public FanCurveProfileFile Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return new FanCurveProfileFile();
            }

            var json = File.ReadAllText(_filePath);
            var file = JsonSerializer.Deserialize<FanCurveProfileFile>(json, SerializerOptions);
            return file ?? new FanCurveProfileFile();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            TryQuarantine();
            return new FanCurveProfileFile();
        }
    }

    /// <summary>保存全部内容（原子写：先临时文件再替换）。</summary>
    public void Save(FanCurveProfileFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var json = JsonSerializer.Serialize(file, SerializerOptions);
        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _filePath, overwrite: true);
    }

    private void TryQuarantine()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                File.Move(_filePath, _filePath + $".corrupt-{stamp}", overwrite: true);
            }
        }
        catch (IOException)
        {
            // 隔离失败不影响返回空文件：损坏文件留在原处，下次保存时被覆盖。
        }
    }
}
