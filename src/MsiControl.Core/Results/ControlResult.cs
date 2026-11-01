namespace MsiControl.Core.Results;

/// <summary>
/// 单个设置项的操作结果。
/// </summary>
/// <remarks>
/// <para>
/// 关键约束（任务书 §1 原则 6、§8.5）：
/// <list type="bullet">
/// <item>状态为 <see cref="OperationStatus.Succeeded"/> 时，<see cref="ConfirmationBasis"/> 必须非空，
///       说明「凭什么认为它生效了」。</item>
/// <item>无法读回的设置，必须在 <see cref="ConfirmationBasis"/> 中写明确认方式与限制。</item>
/// <item><b>不得因为没有抛异常就判定成功。</b></item>
/// </list>
/// </para>
/// </remarks>
/// <param name="Setting">设置项标识，例如 <c>cpu.longterm</c>。</param>
/// <param name="Requested">用户请求值的可读表达。</param>
/// <param name="ReadBack">读回值的可读表达。无法读回时为 <see langword="null"/>。</param>
/// <param name="Status">操作状态。</param>
/// <param name="Message">补充消息。</param>
/// <param name="ConfirmationBasis">确认依据。成功时为必填。</param>
public sealed record SettingResult(
    string Setting,
    string? Requested,
    string? ReadBack,
    OperationStatus Status,
    string? Message = null,
    string? ConfirmationBasis = null)
{
    /// <summary>成功，且带有明确的确认依据。</summary>
    /// <exception cref="ArgumentException">确认依据为空。</exception>
    public static SettingResult Succeeded(
        string setting,
        string? requested,
        string? readBack,
        string confirmationBasis,
        string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationBasis);
        return new SettingResult(setting, requested, readBack, OperationStatus.Succeeded, message, confirmationBasis);
    }

    /// <summary>部分成功。必须说明哪一部分未达成。</summary>
    public static SettingResult PartiallySucceeded(
        string setting,
        string? requested,
        string? readBack,
        string message,
        string? confirmationBasis = null)
        => new(setting, requested, readBack, OperationStatus.PartiallySucceeded, message, confirmationBasis);

    /// <summary>失败。</summary>
    public static SettingResult Failed(string setting, string? requested, string message, string? readBack = null)
        => new(setting, requested, readBack, OperationStatus.Failed, message);

    /// <summary>已写入，需重启或 S5 关机再开机生效。</summary>
    public static SettingResult PendingRestart(
        string setting,
        string? requested,
        string confirmationBasis,
        string? message = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationBasis);
        return new SettingResult(setting, requested, null, OperationStatus.PendingRestart, message, confirmationBasis);
    }

    /// <summary>本机不支持。必须说明依据。</summary>
    public static SettingResult Unsupported(string setting, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new SettingResult(setting, null, null, OperationStatus.Unsupported, reason);
    }

    /// <summary>状态为成功但缺少确认依据，属于违规结果。</summary>
    public bool IsUnsubstantiatedSuccess
        => Status == OperationStatus.Succeeded && string.IsNullOrWhiteSpace(ConfirmationBasis);
}

/// <summary>
/// 一组设置操作的整体结果。
/// </summary>
/// <remarks>
/// 整体状态由逐项结果归并得出，<b>不允许调用方直接指定</b>。
/// 部分失败时不得宣称整组回滚成功；调用方应先读取实际状态再决定后续动作（任务书 §16）。
/// </remarks>
/// <param name="Status">整体状态。</param>
/// <param name="Settings">逐项结果。</param>
public sealed record ControlResult(OperationStatus Status, IReadOnlyList<SettingResult> Settings)
{
    /// <summary>整体是否完全成功。</summary>
    public bool IsSuccess => Status == OperationStatus.Succeeded;

    /// <summary>整体是否完全失败。</summary>
    public bool IsFailure => Status == OperationStatus.Failed;

    /// <summary>是否存在部分成功。</summary>
    public bool IsPartial => Status == OperationStatus.PartiallySucceeded;

    /// <summary>未成功的项。</summary>
    public IReadOnlyList<SettingResult> Problems
        => Settings.Where(s => s.Status != OperationStatus.Succeeded).ToArray();

    /// <summary>
    /// 指定设置项是否已确认成功（有读回或明确确认依据）。
    /// </summary>
    /// <param name="setting">设置项标识，例如 <c>stage</c>。</param>
    /// <remarks>
    /// <para>
    /// 用于「整组结果里含本机永久不支持项」的场合。典型例子：档位切换会同时尝试 GPU 功耗上限，
    /// 而本机 vBIOS 未开放该旋钮，整组因此归并为 <see cref="OperationStatus.PartiallySucceeded"/>；
    /// 若用 <see cref="IsSuccess"/> 判断，用户已经看到的档位切换会被当成失败，
    /// 且「上次确认档位」永远不会前进（HWE §32.5）。
    /// </para>
    /// <para>
    /// 判据仍是逐项状态，不是「没抛异常」：只有该设置项自己读完确认才算成立。
    /// </para>
    /// </remarks>
    public bool IsSettingSucceeded(string setting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(setting);
        return Settings.Any(s => string.Equals(s.Setting, setting, StringComparison.Ordinal)
                                 && s.Status == OperationStatus.Succeeded);
    }

    /// <summary>需重启生效的项。</summary>
    public IReadOnlyList<SettingResult> PendingRestartSettings
        => Settings.Where(s => s.Status == OperationStatus.PendingRestart).ToArray();

    /// <summary>
    /// 由逐项结果构造整体结果，并归并出整体状态。
    /// </summary>
    /// <param name="settings">逐项结果。</param>
    /// <returns>整体结果。</returns>
    /// <remarks>
    /// 归并规则（确定性，可测试）：
    /// <list type="number">
    /// <item>没有任何项：<see cref="OperationStatus.Failed"/>。</item>
    /// <item>全部失败：<see cref="OperationStatus.Failed"/>。</item>
    /// <item>存在失败但并非全部失败：<see cref="OperationStatus.PartiallySucceeded"/>。</item>
    /// <item>无失败，但存在不支持或部分成功：<see cref="OperationStatus.PartiallySucceeded"/>。</item>
    /// <item>无失败，仅存在待重启：<see cref="OperationStatus.PendingRestart"/>。</item>
    /// <item>其余：<see cref="OperationStatus.Succeeded"/>。</item>
    /// </list>
    /// </remarks>
    public static ControlResult From(IEnumerable<SettingResult> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = settings.ToList();

        OperationStatus status;
        if (list.Count == 0)
        {
            status = OperationStatus.Failed;
        }
        else if (list.All(s => s.Status == OperationStatus.Failed))
        {
            status = OperationStatus.Failed;
        }
        else if (list.Any(s => s.Status == OperationStatus.Failed))
        {
            status = OperationStatus.PartiallySucceeded;
        }
        else if (list.Any(s => s.Status is OperationStatus.Unsupported or OperationStatus.PartiallySucceeded))
        {
            status = OperationStatus.PartiallySucceeded;
        }
        else if (list.Any(s => s.Status == OperationStatus.PendingRestart))
        {
            status = OperationStatus.PendingRestart;
        }
        else
        {
            status = OperationStatus.Succeeded;
        }

        return new ControlResult(status, list);
    }

    /// <summary>由单项结果构造整体结果。</summary>
    public static ControlResult Single(SettingResult setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return From(new[] { setting });
    }

    /// <summary>
    /// 检查结果集合是否违反「成功必须有确认依据」的约定。
    /// </summary>
    /// <returns>违规说明列表。为空表示合规。</returns>
    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (Settings.Count == 0)
        {
            problems.Add("结果集合为空：不允许把「什么都没做」表达为一次操作结果。");
        }

        foreach (var setting in Settings.Where(s => s.IsUnsubstantiatedSuccess))
        {
            problems.Add($"设置项 {setting.Setting} 标记为成功，但缺少确认依据（ConfirmationBasis）。");
        }

        foreach (var setting in Settings.Where(s => s.Status == OperationStatus.Unsupported && string.IsNullOrWhiteSpace(s.Message)))
        {
            problems.Add($"设置项 {setting.Setting} 标记为不支持，但未说明依据。");
        }

        return problems;
    }
}
