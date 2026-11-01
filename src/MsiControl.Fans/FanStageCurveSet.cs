using MsiControl.Core;

namespace MsiControl.Fans;

/// <summary>
/// 五档性能模式各自的双风扇曲线集合。
/// </summary>
/// <remarks>
/// <para>
/// 对应任务书 §7.3「每个性能模式保存独立 CPU/GPU 风扇曲线」。
/// 本类型只提供**容器与校验**，不提供任何出厂默认曲线数值——
/// 任务书 §7.2 明确「不直接套用截图或其他机型的曲线数值」，
/// 且本机曲线格式尚未取得证据，任何内置数值都会是编造。
/// </para>
/// <para>
/// 未配置的档位以「键缺失」表达，<see cref="Validate"/> 会逐个列出，
/// 而不是静默用空对象顶替。
/// </para>
/// </remarks>
public sealed record FanStageCurveSet
{
    private readonly Dictionary<PerformanceStage, FanCurveSet> _stages;

    /// <summary>构造集合。</summary>
    /// <param name="stages">各档位的曲线对。同一档位重复出现时以最后一个为准。</param>
    /// <exception cref="ArgumentNullException"><paramref name="stages"/> 为 <see langword="null"/>。</exception>
    public FanStageCurveSet(IEnumerable<KeyValuePair<PerformanceStage, FanCurveSet>> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        _stages = new Dictionary<PerformanceStage, FanCurveSet>();
        foreach (var pair in stages)
        {
            ArgumentNullException.ThrowIfNull(pair.Value);
            _stages[pair.Key] = pair.Value;
        }
    }

    /// <summary>空集合：五档全部未配置。</summary>
    public static FanStageCurveSet Empty { get; }
        = new(Array.Empty<KeyValuePair<PerformanceStage, FanCurveSet>>());

    /// <summary>已配置的档位，按枚举顺序排列。</summary>
    public IReadOnlyList<PerformanceStage> ConfiguredStages
        => Enum.GetValues<PerformanceStage>().Where(_stages.ContainsKey).ToArray();

    /// <summary>取指定档位的曲线。未配置时为 <see langword="null"/>。</summary>
    public FanCurveSet? ForStage(PerformanceStage stage)
        => _stages.TryGetValue(stage, out var set) ? set : null;

    /// <summary>尚未配置的档位，按枚举顺序排列。</summary>
    public IReadOnlyList<PerformanceStage> MissingStages()
        => Enum.GetValues<PerformanceStage>().Where(stage => !_stages.ContainsKey(stage)).ToArray();

    /// <summary>返回一个替换了指定档位的新集合，不修改当前实例。</summary>
    public FanStageCurveSet WithStage(PerformanceStage stage, FanCurveSet set)
    {
        ArgumentNullException.ThrowIfNull(set);

        return new FanStageCurveSet(
            _stages.Where(pair => pair.Key != stage).Append(new KeyValuePair<PerformanceStage, FanCurveSet>(stage, set)));
    }

    /// <summary>
    /// 组合校验：五档必须齐全，且每档的双曲线通道必须一致。
    /// </summary>
    /// <returns>问题列表。为空表示集合合法。</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();

        foreach (var stage in MissingStages())
        {
            problems.Add($"性能档位 {stage} 未配置独立的 CPU/GPU 风扇曲线（任务书 §7.3 要求每档独立）。");
        }

        foreach (var stage in ConfiguredStages)
        {
            foreach (var problem in _stages[stage].Validate())
            {
                problems.Add($"{stage}：{problem}");
            }
        }

        return problems;
    }
}
