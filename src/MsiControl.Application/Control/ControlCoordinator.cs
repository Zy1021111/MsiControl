using System.Globalization;
using MsiControl.Application.Logging;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Application.Control;

/// <summary>
/// 统一控制协调器：所有硬件写入的唯一入口。
/// </summary>
/// <remarks>
/// <para>
/// 对应任务书 §16 与 design.md §13.4，是任务 07 的核心交付物：
/// </para>
/// <code>
/// 用户操作 / 供电变化 / 启动恢复
///              ↓
///     ControlCoordinator
///              ↓
/// 检查能力与条件 → 顺序应用设置 → 读取确认
///              ↓
///     更新共享状态与逐项操作结果
/// </code>
/// <para><b>硬性保证：</b></para>
/// <list type="number">
/// <item><b>串行写入</b>：同一时刻只有一个硬件操作在执行（<see cref="SemaphoreSlim"/> 独占），
///       避免性能切换与风扇编辑互相覆盖。</item>
/// <item><b>能力门控</b>：只要请求涉及的能力键未达 <see cref="CapabilityLevel.Verified"/>，
///       就**不调用适配层**，并逐项返回 <see cref="OperationStatus.Unsupported"/> 与实际依据
///       （任务书 §16.1）。</item>
/// <item><b>合并过期请求</b>：仍在排队的旧请求会被更新的请求取代，
///       返回 <see cref="ControlOutcomeKind.Superseded"/> 且**不产生任何写入**；
///       自动策略发起的请求永远不会取代仍在排队的手动请求（任务书 §11）。</item>
/// <item><b>不阻塞 UI</b>：全异步，且单次操作有超时，超时给出明确结论而不是成功。</item>
/// <item><b>不宣称回滚</b>：部分失败后重新读取实际状态并记录结论，
///       但**不声称整组回滚成功**（任务书 §16、§8.5）。</item>
/// <item><b>不显示虚假成功</b>：适配层返回的结果会经过 <see cref="ControlResult.Validate"/> 复核；
///       检出「成功但无确认依据」时记错误日志并写入结果说明。</item>
/// </list>
/// <para>
/// 本类型<b>不接触任何硬件</b>：适配层（<see cref="IPerformanceHardware"/> /
/// <see cref="IFanHardware"/> / <see cref="IGraphicsHardware"/>）由宿主注入，
/// 未注入时一律返回「未接入」而不是模拟成功。
/// </para>
/// </remarks>
public sealed class ControlCoordinator : IDisposable
{
    private readonly DeviceCapabilities _capabilities;
    private readonly IPerformanceHardware? _performance;
    private readonly IFanHardware? _fan;
    private readonly IGraphicsHardware? _graphics;
    private readonly ControlCoordinatorOptions _options;
    private readonly IAppLog _log;
    private readonly Dictionary<PerformanceStage, PerformanceSettings> _stageSettings;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>保护 <see cref="_stageSettings"/> 的读写。字典本身非线程安全，
    /// 配置更新来自界面线程，而档位执行在请求队列线程读取。</summary>
    private readonly object _settingsLock = new();

    /// <summary>保护入队状态。与 <see cref="_gate"/> 无关：入队不等待执行。</summary>
    private readonly object _enqueueLock = new();

    /// <summary>各请求种类「最后入队」的请求。用于判定排队请求是否已被取代。</summary>
    private readonly Dictionary<ControlIntentKind, PendingEntry> _pending = new();

    private long _sequence;
    private bool _disposed;

    /// <summary>构造协调器。</summary>
    /// <param name="capabilities">本机能力集合。写入门控的唯一依据。</param>
    /// <param name="performanceHardware">性能与功耗适配层（任务 05）。未接入时传 <see langword="null"/>。</param>
    /// <param name="fanHardware">风扇适配层（任务 04）。未接入时传 <see langword="null"/>。</param>
    /// <param name="graphicsHardware">显卡适配层（任务 06）。未接入时传 <see langword="null"/>。</param>
    /// <param name="stageSettings">各档位的配置。为空时使用 <see cref="PerformanceProfileCatalog"/> 默认表。</param>
    /// <param name="options">行为参数。为空时使用默认值。</param>
    /// <param name="log">日志出口。为空时不记录。</param>
    /// <exception cref="ArgumentNullException">能力集合为空。</exception>
    public ControlCoordinator(
        DeviceCapabilities capabilities,
        IPerformanceHardware? performanceHardware = null,
        IFanHardware? fanHardware = null,
        IGraphicsHardware? graphicsHardware = null,
        IReadOnlyDictionary<PerformanceStage, PerformanceSettings>? stageSettings = null,
        ControlCoordinatorOptions? options = null,
        IAppLog? log = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _performance = performanceHardware;
        _fan = fanHardware;
        _graphics = graphicsHardware;
        _options = options ?? ControlCoordinatorOptions.Default;
        _log = log ?? NullAppLog.Instance;

        _stageSettings = new Dictionary<PerformanceStage, PerformanceSettings>();
        if (stageSettings is null)
        {
            foreach (var profile in PerformanceProfileCatalog.Defaults)
            {
                _stageSettings[profile.Stage] = profile.Settings;
            }
        }
        else
        {
            foreach (var pair in stageSettings)
            {
                _stageSettings[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>本机能力集合。</summary>
    public DeviceCapabilities Capabilities => _capabilities;

    /// <summary>各适配层是否已接入。用于装配自检与界面说明。</summary>
    public PerformanceAdapterAvailability AdapterAvailability => new(
        Performance: _performance is not null,
        Fans: _fan is not null,
        Graphics: _graphics is not null);

    /// <summary>最近一次**确认成功**应用并经读回确认的档位。未成功应用过时为 <see langword="null"/>。</summary>
    /// <remarks>
    /// 只反映本软件写成功的档位，**不冒充硬件实际档位**。
    /// 硬件实际状态须由 <see cref="IPerformanceHardware.ReadAsync"/> 读回。
    /// </remarks>
    public PerformanceStage? LastConfirmedStage { get; private set; }

    /// <summary>一次请求处理完毕（含未执行的情形）后发布。可能在任意线程触发。</summary>
    public event EventHandler<ControlOutcome>? OutcomePublished;

    /// <summary>取得指定档位的目标配置。</summary>
    /// <exception cref="ArgumentOutOfRangeException">档位未登记。</exception>
    public PerformanceSettings SettingsFor(PerformanceStage stage)
    {
        lock (_settingsLock)
        {
            return _stageSettings.TryGetValue(stage, out var settings)
                ? settings
                : throw new ArgumentOutOfRangeException(nameof(stage), stage, "该档位未在配置表中登记。");
        }
    }

    /// <summary>
    /// 更新指定档位的软件配置目标。
    /// </summary>
    /// <param name="settings">新的档位配置。</param>
    /// <returns>更新结果。拒绝时 <see cref="StageSettingsUpdateResult.Problems"/> 列出全部原因。</returns>
    /// <remarks>
    /// <para>
    /// 本方法只改写<b>软件配置目标</b>：后续切换到该档位时，计划按新目标逐项生成、
    /// 逐项门控与如实报告；本方法本身<b>不触发任何硬件写入</b>。
    /// </para>
    /// <para>更新前执行 <see cref="PerformanceSettings.Validate"/> 的内部一致性校验
    /// （如短时不得低于持续），非法配置整体拒绝，不做部分接受。</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">配置为空。</exception>
    public StageSettingsUpdateResult UpdateStageSettings(PerformanceSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var problems = new List<string>(settings.Validate());
        lock (_settingsLock)
        {
            if (!_stageSettings.ContainsKey(settings.Stage))
            {
                problems.Add($"档位 {settings.Stage} 未在配置表中登记。");
            }
            else if (problems.Count == 0)
            {
                _stageSettings[settings.Stage] = settings;
            }
        }

        return problems.Count == 0
            ? new StageSettingsUpdateResult(true, Array.Empty<string>(), settings)
            : new StageSettingsUpdateResult(false, problems, null);
    }

    /// <summary>请求切换到指定性能档位（视为手动请求）。</summary>
    public Task<ControlOutcome> RequestPerformanceStageAsync(
        PerformanceStage stage,
        CancellationToken cancellationToken)
        => RequestPerformanceStageAsync(stage, ControlRequestOrigin.Manual, cancellationToken);

    /// <summary>请求切换到指定性能档位。</summary>
    /// <param name="stage">目标档位。</param>
    /// <param name="origin">请求来源。自动来源不会取代仍在排队的手动请求。</param>
    /// <param name="cancellationToken">取消标记。取消不会跳过必要的状态核对，且不被吞掉。</param>
    public async Task<ControlOutcome> RequestPerformanceStageAsync(
        PerformanceStage stage,
        ControlRequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var intent = ControlIntent.ForPerformanceStage(stage, origin);
        return await SubmitAsync(intent, cancellationToken, ExecutePerformanceStageAsync).ConfigureAwait(false);
    }

    /// <summary>
    /// 把固件性能场景恢复为默认档位（<see cref="PerformanceShiftMap.FirmwareDefaultStage"/>）。
    /// </summary>
    /// <param name="origin">请求来源。退出流程走 <see cref="ControlRequestOrigin.Manual"/>。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <remarks>
    /// <para>
    /// <b>只写 EC 0xD2</b>：不应用档位配置表、不写 MSR 0x610、不动风扇模式与曲线。
    /// 语义是「撤销本软件对性能场景的改动」，不是「再应用一次某个档位」。
    /// </para>
    /// <para>
    /// 本方法**不修改** <see cref="LastConfirmedStage"/>：退出恢复不是用户的档位确认，
    /// 「上次用户确认档位」由调用方（桌面壳的设置文件）独立持有。
    /// </para>
    /// <para>
    /// 适配层未实现 <see cref="IScenarioRestoreChannel"/> 时如实返回
    /// <see cref="OperationStatus.Unsupported"/>，不降级为整档应用。
    /// </para>
    /// </remarks>
    public async Task<ControlOutcome> RestoreDefaultStageAsync(
        ControlRequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var intent = ControlIntent.ForDefaultStageRestore(origin);
        return await SubmitAsync(intent, cancellationToken, ExecuteDefaultStageRestoreAsync).ConfigureAwait(false);
    }

    /// <summary>请求切换到指定显卡模式（视为手动请求）。</summary>    /// <remarks>本协调器**不会**因为启动或供电变化而自动调用本方法（任务书 §9.1）。</remarks>
    public Task<ControlOutcome> RequestGraphicsModeAsync(
        GraphicsMode mode,
        CancellationToken cancellationToken)
        => RequestGraphicsModeAsync(mode, ControlRequestOrigin.Manual, cancellationToken);

    /// <summary>请求切换到指定显卡模式。</summary>
    public async Task<ControlOutcome> RequestGraphicsModeAsync(
        GraphicsMode mode,
        ControlRequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var intent = ControlIntent.ForGraphicsMode(mode, origin);
        return await SubmitAsync(intent, cancellationToken, ExecuteGraphicsModeAsync).ConfigureAwait(false);
    }

    /// <summary>请求切换到指定风扇模式（视为手动请求）。</summary>
    public Task<ControlOutcome> RequestFanModeAsync(
        FanControlMode mode,
        CancellationToken cancellationToken)
        => RequestFanModeAsync(mode, ControlRequestOrigin.Manual, cancellationToken);

    /// <summary>请求切换到指定风扇模式。</summary>
    public async Task<ControlOutcome> RequestFanModeAsync(
        FanControlMode mode,
        ControlRequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var intent = ControlIntent.ForFanMode(mode, origin);
        return await SubmitAsync(intent, cancellationToken, ExecuteFanModeAsync).ConfigureAwait(false);
    }

    /// <summary>切换强冷（全速）。</summary>
    public Task<ControlOutcome> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken)
        => SetFullSpeedAsync(enabled, ControlRequestOrigin.Manual, cancellationToken);

    /// <summary>切换强冷（全速）。</summary>
    public async Task<ControlOutcome> SetFullSpeedAsync(
        bool enabled,
        ControlRequestOrigin origin,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var intent = ControlIntent.ForFullSpeed(enabled, origin);
        return await SubmitAsync(intent, cancellationToken, ExecuteFullSpeedAsync).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }

    // ── 请求排队与取代 ────────────────────────────────────────────────

    private async Task<ControlOutcome> SubmitAsync(
        ControlIntent intent,
        CancellationToken cancellationToken,
        Func<ControlIntent, CancellationToken, Task<ControlOutcome>> executor)
    {
        var sequence = Interlocked.Increment(ref _sequence);

        if (!TryEnqueue(intent, sequence))
        {
            var superseded = Superseded(
                intent,
                "未入队：已有仍在排队的手动请求，自动策略请求不会取代用户的手动选择（任务书 §11）。");
            Publish(superseded);
            return superseded;
        }

        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkCompleted(intent.Kind, sequence);
            throw;
        }

        try
        {
            if (_options.SupersedePendingRequests && IsStale(intent.Kind, sequence))
            {
                var superseded = Superseded(
                    intent,
                    $"未执行：等待期间出现了更新的同类请求，本次请求已合并（未产生任何硬件写入）。");
                Publish(superseded);
                return superseded;
            }

            ControlOutcome outcome;
            try
            {
                outcome = await executor(intent, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方取消：按约定向上抛出，绝不吞掉（docs/接口与工程约定.md §6）。
                throw;
            }
            catch (Exception ex)
            {
                outcome = Faulted(intent, ex);
            }

            Publish(outcome);
            return outcome;
        }
        finally
        {
            MarkCompleted(intent.Kind, sequence);
            _gate.Release();
        }
    }

    /// <summary>
    /// 记录入队。返回 <see langword="false"/> 表示本次请求已被更高优先级来源取代。
    /// </summary>
    private bool TryEnqueue(ControlIntent intent, long sequence)
    {
        lock (_enqueueLock)
        {
            if (_pending.TryGetValue(intent.Kind, out var previous)
                && previous.Origin == ControlRequestOrigin.Manual
                && intent.Origin == ControlRequestOrigin.Automatic)
            {
                // 自动策略永远不覆盖仍在排队的手动选择。
                return false;
            }

            _pending[intent.Kind] = new PendingEntry(sequence, intent.Origin);
            return true;
        }
    }

    /// <summary>本请求是否已被更新的同类请求取代。</summary>
    private bool IsStale(ControlIntentKind kind, long sequence)
    {
        lock (_enqueueLock)
        {
            return !_pending.TryGetValue(kind, out var current) || current.Sequence != sequence;
        }
    }

    private void MarkCompleted(ControlIntentKind kind, long sequence)
    {
        lock (_enqueueLock)
        {
            if (_pending.TryGetValue(kind, out var current) && current.Sequence == sequence)
            {
                _pending.Remove(kind);
            }
        }
    }

    // ── 性能档位 ─────────────────────────────────────────────────────

    private async Task<ControlOutcome> ExecutePerformanceStageAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        var stage = intent.Stage!.Value;
        var settings = SettingsFor(stage);
        var requested = DescribeStageRequest(settings);

        // 1) 配置自洽性：组合关系非法时不下发硬件（任务书 §8.2、接口说明 §3.9）。
        var problems = settings.Validate();
        if (problems.Count > 0)
        {
            var outcome = ControlOutcome.Create(
                ControlOutcomeKind.Faulted,
                intent,
                ControlResult.Single(SettingResult.Failed(
                    "performance.stage",
                    requested,
                    "本档配置自相矛盾，已拒绝下发：" + string.Join(" ", problems))),
                "配置校验未通过；未调用任何适配层（任务书 §8.2）。");
            _log.Write(AppLogLevel.Error, "Control", outcome.Describe());
            return outcome;
        }

        // 2) 适配层是否接入。场景型通道（EC Shift）与 WMI 通道的能力键不同。
        var scenarioBased = _performance is IScenarioPerformanceHardware;
        var scenarioCapabilityKey = scenarioBased
            ? CapabilityKeys.PerformanceStage
            : CapabilityKeys.FirmwareScenario;

        if (_performance is null)
        {
            return NotIntegrated(intent, scenarioCapabilityKey, requested, "性能与功耗（任务 05）");
        }

        // 3) 能力门控：涉及的能力键必须全部达到 Verified，否则整组拒绝，不做部分写入。
        //    场景型通道只要求 performance.stage（瓦数目标由适配层逐项如实报 Unsupported）；
        //    WMI 通道承载瓦数语义，维持原有逐键要求。
        var blocked = BlockedByCapability(
            intent,
            requested,
            RequiredWriteCapabilities(settings, scenarioBased, stage));
        if (blocked is not null)
        {
            return blocked;
        }

        // 4) 下发并等待读回确认。
        var call = await InvokeAsync(token => _performance.ApplyAsync(settings, token), cancellationToken)
            .ConfigureAwait(false);
        if (call.TimedOut)
        {
            return TimedOut(intent, "performance.stage", requested);
        }

        var result = call.Value!;
        var notes = new List<string>();
        AddValidationNotes(result, notes);

        // 5) 组合档（安静）：场景写入成功后叠加静音风扇。
        //    依据（HWE §21.3）：本机固件组与 MSI 官方 Shift 体系均无独立「安静」场景编码，
        //    MSI Center 的「静音」场景即 节能场景 + 静音风扇 的组合；逐项如实报告，不虚构独立编码。
        if (scenarioBased
            && PerformanceShiftMap.IsSilentFanComposite(stage)
            && result.Settings.Any(s => s.Status == OperationStatus.Succeeded))
        {
            var fanPart = await ComposeSilentFanAsync(cancellationToken).ConfigureAwait(false);
            result = ControlResult.From(result.Settings.Concat(fanPart.Settings));
            AddValidationNotes(result, notes);
            notes.Add(
                "「安静」为组合档：固件场景走节能（0xC2）并叠加静音风扇（0xD4=0x1D）；"
                + "切到其它档位不会自动改回风扇模式，如需恢复请在风扇区手动选择。");
        }

        if (result.Problems.Count > 0)
        {
            notes.Add(await ReReadPerformanceStateAsync(cancellationToken).ConfigureAwait(false));
        }

        // 判据是「档位项自己读完确认」，不是整组 IsSuccess：
        // 本机 GPU 功耗上限永久不受支持，整组必然是 PartiallySucceeded，
        // 用整组状态判断会让 LastConfirmedStage 永远停在旧值（HWE §32.5）。
        if (result.IsSettingSucceeded(PerformanceShiftMap.StageSettingKey))
        {
            LastConfirmedStage = stage;
            notes.Add($"已确认应用到 {ControlIntent.StageText(stage)}。");
        }

        return ControlOutcome.Create(ControlOutcomeKind.Executed, intent, result, notes.ToArray());
    }

    /// <summary>
    /// 恢复固件默认性能场景：只写 EC 场景寄存器，不应用配置表。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="ExecutePerformanceStageAsync"/> 的差别是刻意的：那次写入会连带把
    /// 档位配置表里的 CPU 功耗目标写进 MSR 0x610，属于「应用配置」；
    /// 本条路径属于「撤销本软件的场景改动」，因此只写 0xD2 并读回核对。
    /// </remarks>
    private async Task<ControlOutcome> ExecuteDefaultStageRestoreAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        var stage = intent.Stage!.Value;
        var requested = $"{ControlIntent.StageText(stage)}（固件基准场景）";

        if (_performance is null)
        {
            return NotIntegrated(intent, CapabilityKeys.PerformanceStage, requested, "性能与功耗（任务 05）");
        }

        var blocked = BlockedByCapability(
            intent,
            requested,
            new[] { CapabilityKeys.PerformanceStage });
        if (blocked is not null)
        {
            return blocked;
        }

        if (_performance is not IScenarioRestoreChannel restore)
        {
            return ControlOutcome.Create(
                ControlOutcomeKind.BlockedByCapability,
                intent,
                ControlResult.Single(SettingResult.Unsupported(
                    "performance.stage",
                    $"当前性能适配层（{_performance.GetType().Name}）未实现「仅写场景」的恢复通道"
                    + "（IScenarioRestoreChannel）：为避免连带改写功率上限与风扇曲线，本次不做任何写入。")),
                "退出恢复要求只写场景寄存器；未实现该通道时不下发，也不降级为整档应用。");
        }

        var call = await InvokeAsync(
                token => restore.ApplyScenarioOnlyAsync(stage, token),
                cancellationToken)
            .ConfigureAwait(false);
        if (call.TimedOut)
        {
            return TimedOut(intent, "performance.stage", requested);
        }

        var result = call.Value!;
        var notes = new List<string>
        {
            "只写入固件性能场景寄存器（EC 0xD2），未改动 CPU 功耗上限、睿频上限与风扇模式/曲线；"
            + "本次写入不改变「上次用户确认档位」记录。"
        };

        AddValidationNotes(result, notes);
        return ControlOutcome.Create(ControlOutcomeKind.Executed, intent, result, notes.ToArray());
    }

    /// <summary>组合档（安静）的风扇部分：经 EC 风扇适配层切换静音，逐项如实报告。</summary>
    private async Task<ControlResult> ComposeSilentFanAsync(CancellationToken cancellationToken)
    {
        if (_fan is not IFanModeSwitch silentSwitch)
        {
            return ControlResult.Single(SettingResult.Unsupported(
                "fan.mode",
                "组合档的风扇部分需要 EC 风扇适配层（IFanModeSwitch）；当前适配层不支持，"
                + "固件场景部分已生效，风扇模式未改动。"));
        }

        var call = await InvokeAsync(token => silentSwitch.SetSilentAsync(token), cancellationToken)
            .ConfigureAwait(false);
        if (call.TimedOut)
        {
            return ControlResult.Single(SettingResult.Failed(
                "fan.mode",
                "静音",
                "组合档风扇部分操作超时；固件场景部分已生效，风扇模式状态未知。"));
        }

        return call.Value!;
    }

    private async Task<string> ReReadPerformanceStateAsync(CancellationToken cancellationToken)
    {
        if (!_options.ReReadAfterPartialFailure || _performance is null)
        {
            return "部分失败：未重新读取实际状态；未执行整组回滚，也不宣称回滚成功（任务书 §16）。";
        }

        var reread = await InvokeAsync(token => _performance.ReadAsync(token), cancellationToken)
            .ConfigureAwait(false);

        if (reread.Value is not { } state)
        {
            return "部分失败后重新读取实际状态未成功（超时或异常）；" +
                   "未执行整组回滚，也不宣称回滚成功（任务书 §16）。";
        }

        return $"部分失败后已重新读取实际状态（{DescribePerformanceState(state)}）；" +
               "未执行整组回滚，也不宣称回滚成功（任务书 §16）。";
    }

    private static string DescribePerformanceState(PerformanceHardwareState state)
    {
        if (!state.HasAnyReadBack)
        {
            return "读回为空，实际档位仍未知";
        }

        var parts = new List<string>();
        if (state.ActiveStage is { } stage)
        {
            parts.Add($"实际档位={ControlIntent.StageText(stage)}");
        }

        if (state.CpuLongTermReadBack is { } longTerm)
        {
            parts.Add($"CPU 持续={longTerm}");
        }

        if (state.CpuShortTermReadBack is { } shortTerm)
        {
            parts.Add($"CPU 短时={shortTerm}");
        }

        if (state.CpuTurboReadBack is { } turbo)
        {
            parts.Add($"CPU 睿频上限={turbo}");
        }

        if (state.GpuLimitReadBack is { } gpu)
        {
            parts.Add($"GPU={gpu}");
        }

        if (!string.IsNullOrWhiteSpace(state.FirmwareScenario))
        {
            parts.Add($"固件场景={state.FirmwareScenario}");
        }

        return string.Join("，", parts);
    }

    private static string DescribeStageRequest(PerformanceSettings settings)
    {
        var parts = new List<string>
        {
            $"CPU 持续={settings.CpuLongTerm}",
            $"CPU 短时={settings.CpuShortTerm}",
            $"CPU 睿频={settings.CpuTurbo}",
            $"GPU={settings.GpuLimit}"
        };

        if (settings.GpuEcoFallback is { } eco)
        {
            parts.Add($"GPU 节能策略={eco.Description}（不保证精确达到目标瓦数）");
        }

        var curveNodes = 0;
        if (settings.CpuCurve is not null)
        {
            curveNodes += settings.CpuCurve.NodeCount;
        }

        if (settings.GpuCurve is not null)
        {
            curveNodes += settings.GpuCurve.NodeCount;
        }

        if (curveNodes > 0)
        {
            parts.Add($"风扇曲线节点={curveNodes}");
        }

        return $"{ControlIntent.StageText(settings.Stage)}（{string.Join(" / ", parts)}）";
    }

    // ── 显卡模式 ─────────────────────────────────────────────────────

    private async Task<ControlOutcome> ExecuteGraphicsModeAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        var mode = intent.GraphicsMode!.Value;
        var requested = ControlIntent.GraphicsText(mode);

        if (_graphics is null)
        {
            return NotIntegrated(intent, CapabilityKeys.GraphicsMode, requested, "显卡（任务 06）");
        }

        var blocked = BlockedByCapability(intent, requested, new[] { CapabilityKeys.GraphicsMode });
        if (blocked is not null)
        {
            return blocked;
        }

        // 前置条件：外接屏拓扑未确认时不得按「无风险」处理（任务书 §9.2、接口说明 §5 任务 06）。
        var stateCall = await InvokeAsync(token => _graphics.ReadAsync(token), cancellationToken)
            .ConfigureAwait(false);
        if (stateCall.TimedOut)
        {
            return TimedOut(intent, "graphics.mode", requested);
        }

        if (stateCall.Value is { } state)
        {
            var risk = state.WouldDisconnectExternalDisplay(mode);
            if (risk is null)
            {
                return ControlOutcome.Create(
                    ControlOutcomeKind.BlockedByPrecondition,
                    intent,
                    ControlResult.Single(SettingResult.Failed(
                        "graphics.mode",
                        requested,
                        "检测到外接显示器，但外接屏的实际连接路径（是否依赖独显）尚未确认；" +
                        "按任务书 §9.2 不得按「无风险」处理，已阻止本次切换。请先确认显示器挂在核显还是独显上。")),
                    "前置条件未满足，未调用切换接口；拓扑未知时不得推断为无风险。");
            }
        }

        var call = await InvokeAsync(token => _graphics.RequestModeAsync(mode, token), cancellationToken)
            .ConfigureAwait(false);
        if (call.TimedOut)
        {
            return TimedOut(intent, "graphics.mode", requested);
        }

        var result = call.Value!;
        var notes = new List<string>
        {
            "本协调器不自动重启；待生效状态需用户自行重启或 S5 关机再开机（任务书 §9.1）。"
        };

        AddValidationNotes(result, notes);
        if (result.PendingRestartSettings.Count > 0)
        {
            notes.Add("结果包含待重启生效项：已写入但尚未生效，界面须与实际模式分列显示。");
        }

        return ControlOutcome.Create(ControlOutcomeKind.Executed, intent, result, notes.ToArray());
    }

    // ── 强冷 ─────────────────────────────────────────────────────────

    private async Task<ControlOutcome> ExecuteFullSpeedAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        var enabled = intent.FullSpeed!.Value;
        var requested = enabled ? "开启" : "关闭";

        if (_fan is null)
        {
            return NotIntegrated(intent, CapabilityKeys.FanFullSpeed, requested, "风扇（任务 04）");
        }

        var blocked = BlockedByCapability(intent, requested, new[] { CapabilityKeys.FanFullSpeed });
        if (blocked is not null)
        {
            return blocked;
        }

        var call = await InvokeAsync(token => _fan.SetFullSpeedAsync(enabled, token), cancellationToken)
            .ConfigureAwait(false);
        if (call.TimedOut)
        {
            return TimedOut(intent, "fan.fullspeed", requested);
        }

        var result = call.Value!;
        var notes = new List<string>
        {
            "强冷不与狂暴档位自动绑定：开启与关闭均由用户显式触发（任务书 §7.5）。"
        };

        AddValidationNotes(result, notes);
        return ControlOutcome.Create(ControlOutcomeKind.Executed, intent, result, notes.ToArray());
    }

    // ── 风扇模式 ─────────────────────────────────────────────────────

    private async Task<ControlOutcome> ExecuteFanModeAsync(
        ControlIntent intent,
        CancellationToken cancellationToken)
    {
        var mode = intent.FanMode!.Value;
        var requested = ControlIntent.FanModeText(mode);

        if (_fan is null)
        {
            return NotIntegrated(intent, CapabilityKeys.FanMode, requested, "风扇（任务 04）");
        }

        var blocked = BlockedByCapability(intent, requested, new[] { CapabilityKeys.FanMode });
        if (blocked is not null)
        {
            return blocked;
        }

        // 自动走接口固有入口；静音/advanced 需要适配层实现 IFanModeSwitch（EC 三编码），
        // 未实现时如实报 Unsupported，不猜测、不降级成自动。
        Task<ControlResult> write = mode switch
        {
            FanControlMode.Automatic => _fan.SetAutomaticAsync(cancellationToken),
            FanControlMode.Silent when _fan is IFanModeSwitch silentSwitch => silentSwitch.SetSilentAsync(cancellationToken),
            FanControlMode.CustomCurve when _fan is IFanModeSwitch advancedSwitch => advancedSwitch.SetAdvancedAsync(cancellationToken),
            _ => Task.FromResult(ControlResult.Single(SettingResult.Unsupported(
                "fan.mode",
                $"当前风扇适配层（{_fan.GetType().Name}）未实现静音/advanced 档切换"
                + "（IFanModeSwitch），该模式仅 EC 通道（0xD4 三编码）可用。")))
        };

        var call = await InvokeAsync(async token => await write.ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);
        if (call.TimedOut)
        {
            return TimedOut(intent, "fan.mode", requested);
        }

        var result = call.Value!;
        var notes = new List<string>();
        AddValidationNotes(result, notes);
        return ControlOutcome.Create(ControlOutcomeKind.Executed, intent, result, notes.ToArray());
    }

    // ── 公共工具 ─────────────────────────────────────────────────────

    private ControlOutcome? BlockedByCapability(
        ControlIntent intent,
        string requested,
        IReadOnlyList<string> requiredKeys)
    {
        var missing = requiredKeys
            .Where(key => !_capabilities.AllowsWrite(key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (missing.Length == 0)
        {
            return null;
        }

        var settings = missing
            .Select(key => SettingResult.Unsupported(
                key,
                $"写入未开放：{_capabilities.Get(key).DescribeUnavailable()}"))
            .ToArray();

        var notes = new[]
        {
            $"能力门控拦下本次请求：涉及 {missing.Length} 项未达「已确证」，整组未下发任何写入" +
            "（不做部分写入，也不静默替换目标值，任务书 §8.3、§16.1）。"
        };

        var outcome = ControlOutcome.Create(
            ControlOutcomeKind.BlockedByCapability,
            intent,
            ControlResult.From(settings),
            notes);

        _log.Write(AppLogLevel.Warning, "Control", outcome.Describe());
        return outcome;
    }

    private ControlOutcome NotIntegrated(
        ControlIntent intent,
        string settingKey,
        string requested,
        string moduleName)
    {
        var outcome = ControlOutcome.Create(
            ControlOutcomeKind.BlockedByCapability,
            intent,
            ControlResult.Single(SettingResult.Unsupported(
                settingKey,
                $"{moduleName}适配层尚未接入：本版本不执行任何硬件写入，也不使用模拟数据。")),
            $"{moduleName}适配层的接入状态见模块接入状态表（docs/integration.md）。");

        _log.Write(AppLogLevel.Warning, "Control", outcome.Describe());
        return outcome;
    }

    private void AddValidationNotes(ControlResult result, ICollection<string> notes)
    {
        var problems = result.Validate();
        if (problems.Count == 0)
        {
            return;
        }

        foreach (var problem in problems)
        {
            notes.Add("结果合规性问题：" + problem);
            _log.Write(AppLogLevel.Error, "Control", "结果违规：" + problem);
        }
    }

    private static IReadOnlyList<string> RequiredWriteCapabilities(
        PerformanceSettings settings,
        bool scenarioBasedHardware,
        PerformanceStage stage)
    {
        // 场景型通道（EC Shift）：按场景键门控；瓦数目标不经该通道下发，
        // 由适配层在逐项结果中报告 Unsupported，不在此整组拦截。
        // 组合档（安静）还要叠加静音风扇写入（0xD4），故同时要求 fan.mode 达 Verified。
        if (scenarioBasedHardware)
        {
            return PerformanceShiftMap.IsSilentFanComposite(stage)
                ? new[] { CapabilityKeys.PerformanceStage, CapabilityKeys.FanMode }
                : new[] { CapabilityKeys.PerformanceStage };
        }

        var keys = new List<string> { CapabilityKeys.FirmwareScenario };

        if (!settings.CpuLongTerm.IsNative || !settings.CpuShortTerm.IsNative)
        {
            keys.Add(CapabilityKeys.CpuPowerLimit);
        }

        if (!settings.CpuTurbo.IsNative)
        {
            keys.Add(CapabilityKeys.CpuTurboLimit);
        }

        if (!settings.GpuLimit.IsNative)
        {
            keys.Add(CapabilityKeys.GpuPowerLimit);
        }

        if (settings.GpuEcoFallback is not null)
        {
            keys.Add(CapabilityKeys.GpuEcoFallback);
        }

        if (settings.CpuCurve is not null || settings.GpuCurve is not null)
        {
            keys.Add(CapabilityKeys.FanCurve);
        }

        return keys;
    }

    private async Task<OperationCall<T>> InvokeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
        where T : class
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(_options.OperationTimeout);

        try
        {
            var value = await operation(linked.Token).ConfigureAwait(false);
            return new OperationCall<T>(value, TimedOut: false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 仅当取消来自本协调器的超时而非调用方时，才转成「超时」结论。
            _log.Write(
                AppLogLevel.Warning,
                "Control",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"操作超时（{_options.OperationTimeout.TotalSeconds:0.#} 秒）：未取得读回确认，按未确认处理。"));
            return new OperationCall<T>(null, TimedOut: true);
        }
    }

    private ControlOutcome Superseded(ControlIntent intent, string note)
        => ControlOutcome.Create(
            ControlOutcomeKind.Superseded,
            intent,
            ControlResult.Single(SettingResult.Failed(
                SettingKeyOf(intent),
                RequestedOf(intent),
                "已被更新的请求取代，未执行写入。")),
            note);

    private ControlOutcome TimedOut(ControlIntent intent, string settingKey, string requested)
        => ControlOutcome.Create(
            ControlOutcomeKind.TimedOut,
            intent,
            ControlResult.Single(SettingResult.Failed(
                settingKey,
                requested,
                "操作超时（" + _options.OperationTimeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) +
                " 秒），未取得读回确认；按未确认处理，不视为成功。")),
            "超时后未产生任何成功声明；实际状态未知，须重新读取确认。");

    private static ControlOutcome Faulted(ControlIntent intent, Exception exception)
        => ControlOutcome.Create(
            ControlOutcomeKind.Faulted,
            intent,
            ControlResult.Single(SettingResult.Failed(
                SettingKeyOf(intent),
                RequestedOf(intent),
                "适配层抛出异常：" + exception.Message)),
            "异常已转换为明确结果，未向上抛出；未产生成功声明。");

    private static string SettingKeyOf(ControlIntent intent) => intent.Kind switch
    {
        ControlIntentKind.PerformanceStage => "performance.stage",
        ControlIntentKind.DefaultStageRestore => "performance.stage",
        ControlIntentKind.GraphicsMode => "graphics.mode",
        ControlIntentKind.FullSpeed => "fan.fullspeed",
        ControlIntentKind.FanMode => "fan.mode",
        _ => "control"
    };

    private static string RequestedOf(ControlIntent intent) => intent.Kind switch
    {
        ControlIntentKind.PerformanceStage => ControlIntent.StageText(intent.Stage!.Value),
        ControlIntentKind.DefaultStageRestore =>
            ControlIntent.StageText(intent.Stage!.Value) + "（固件基准场景）",
        ControlIntentKind.GraphicsMode => ControlIntent.GraphicsText(intent.GraphicsMode!.Value),
        ControlIntentKind.FullSpeed => intent.FullSpeed!.Value ? "开启" : "关闭",
        ControlIntentKind.FanMode => ControlIntent.FanModeText(intent.FanMode!.Value),
        _ => "—"
    };

    private void Publish(ControlOutcome outcome)
    {
        _log.Write(
            outcome.IsFullyApplied ? AppLogLevel.Info : AppLogLevel.Warning,
            "Control",
            outcome.Describe());

        try
        {
            OutcomePublished?.Invoke(this, outcome);
        }
        catch (Exception ex)
        {
            // 订阅方异常不得影响控制流程。
            _log.Write(AppLogLevel.Error, "Control", "结果订阅方抛出异常：" + ex.Message);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ControlCoordinator), "协调器已释放，不再接受请求。");
        }
    }

    /// <summary>已入队的请求记录。</summary>
    private readonly record struct PendingEntry(long Sequence, ControlRequestOrigin Origin);

    /// <summary>适配层调用的结果包装。</summary>
    private readonly record struct OperationCall<T>(T? Value, bool TimedOut);
}

/// <summary>各适配层的接入状态。</summary>
/// <param name="Performance">性能与功耗适配层（任务 05）。</param>
/// <param name="Fans">风扇适配层（任务 04）。</param>
/// <param name="Graphics">显卡适配层（任务 06）。</param>
public sealed record PerformanceAdapterAvailability(bool Performance, bool Fans, bool Graphics)
{
    /// <summary>是否全部接入。</summary>
    public bool AllIntegrated => Performance && Fans && Graphics;

    /// <summary>未接入的项。</summary>
    public IReadOnlyList<string> Missing
    {
        get
        {
            var missing = new List<string>(3);
            if (!Performance)
            {
                missing.Add("性能与功耗（任务 05）");
            }

            if (!Fans)
            {
                missing.Add("风扇（任务 04）");
            }

            if (!Graphics)
            {
                missing.Add("显卡（任务 06）");
            }

            return missing;
        }
    }

    /// <summary>人类可读描述。</summary>
    public string Describe()
        => AllIntegrated
            ? "适配层已全部接入。"
            : "未接入：" + string.Join("、", Missing) + "；相关写入路径一律返回「未接入」，不使用模拟数据。";
}
