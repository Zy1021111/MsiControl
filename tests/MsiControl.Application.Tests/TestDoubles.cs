using MsiControl.Application.Control;
using MsiControl.Application.Host;
using MsiControl.Application.Integration;
using MsiControl.Application.Logging;
using MsiControl.Application.Policy;
using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;

namespace MsiControl.Application.Tests;

/// <summary>记录全部日志的日志出口。</summary>
internal sealed class RecordingLog : IAppLog
{
    public List<(AppLogLevel Level, string Category, string Message)> Entries { get; } = new();

    public void Write(AppLogLevel level, string category, string message)
        => Entries.Add((level, category, message));

    public bool Contains(string fragment)
        => Entries.Any(e => e.Message.Contains(fragment, StringComparison.Ordinal));

    public string Dump() => string.Join(Environment.NewLine, Entries.Select(e => $"[{e.Level}] {e.Category}: {e.Message}"));
}

/// <summary>可编程的性能适配层替身。</summary>
internal sealed class FakePerformanceHardware : IPerformanceHardware
{    private readonly object _counterLock = new();
    private int _active;
    private int _maxConcurrent;

    public Func<CancellationToken, Task<PerformanceHardwareState>> ReadHandler { get; set; }
        = _ => Task.FromResult(PerformanceHardwareState.Unknown);

    public Func<PerformanceSettings, CancellationToken, Task<ControlResult>> ApplyHandler { get; set; }
        // 键名对齐真实契约：PerformanceSettingKeys.Stage = "stage"（见 Ms15M3 适配层）。
        = (settings, _) => Task.FromResult(Succeed("stage"));

    public int ReadCalls { get; private set; }

    public int ApplyCalls { get; private set; }

    public PerformanceSettings? LastAppliedSettings { get; private set; }

    /// <summary>同时进入 ApplyAsync 的最大数量。串行写入的判据。</summary>
    public int MaxConcurrentApply
    {
        get
        {
            lock (_counterLock)
            {
                return _maxConcurrent;
            }
        }
    }

    public Task<PerformanceHardwareState> ReadAsync(CancellationToken cancellationToken)
    {
        ReadCalls++;
        return ReadHandler(cancellationToken);
    }

    public async Task<ControlResult> ApplyAsync(PerformanceSettings settings, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        LastAppliedSettings = settings;

        lock (_counterLock)
        {
            _active++;
            _maxConcurrent = Math.Max(_maxConcurrent, _active);
        }

        try
        {
            return await ApplyHandler(settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_counterLock)
            {
                _active--;
            }
        }
    }

    /// <summary>构造一个「全部成功且带确认依据」的结果。</summary>
    public static ControlResult Succeed(params string[] settingNames)
    {
        var names = settingNames.Length == 0 ? new[] { "performance.stage" } : settingNames;
        return ControlResult.From(names.Select(name =>
            SettingResult.Succeeded(name, "请求值", "读回值", "写入后读回一致")));
    }

    /// <summary>构造一个「部分成功」的结果。</summary>
    public static ControlResult Partial()
        => ControlResult.From(new[]
        {
            SettingResult.Succeeded("cpu.longterm", "60 W", "60 W", "写入后读回 60 W"),
            SettingResult.Failed("gpu.power", "90 W", "GPU 侧不支持该目标，未静默替换")
        });
}

/// <summary>可编程的风扇适配层替身。</summary>
internal class FakeFanHardware : IFanHardware
{
    public Func<bool, CancellationToken, Task<ControlResult>> SetFullSpeedHandler { get; set; }
        = (enabled, _) => Task.FromResult(ControlResult.Single(
            SettingResult.Succeeded("fan.fullspeed", enabled ? "开启" : "关闭", enabled ? "开启" : "关闭", "写入后读回一致")));

    /// <summary>读回处理器。默认返回「未知状态」，保持既有测试语义不变。</summary>
    public Func<CancellationToken, Task<FanHardwareState>> ReadHandler { get; set; }
        = _ => Task.FromResult(FanHardwareState.Unknown);

    public int ReadCalls { get; private set; }

    public int FullSpeedCalls { get; private set; }

    public bool? LastFullSpeed { get; private set; }

    public Task<FanHardwareState> ReadAsync(CancellationToken cancellationToken)
    {
        ReadCalls++;
        return ReadHandler(cancellationToken);
    }

    public Task<ControlResult> ApplyCurveAsync(FanChannel channel, FanCurve curve, CancellationToken cancellationToken)
        => throw new NotSupportedException("测试替身不实现曲线写入。");

    public Task<ControlResult> SetAutomaticAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("测试替身不实现自动策略切换。");

    public Task<ControlResult> SetFullSpeedAsync(bool enabled, CancellationToken cancellationToken)
    {
        FullSpeedCalls++;
        LastFullSpeed = enabled;
        return SetFullSpeedHandler(enabled, cancellationToken);
    }
}

/// <summary>可编程的显卡适配层替身。</summary>
internal sealed class FakeGraphicsHardware : IGraphicsHardware
{
    public GraphicsHardwareState State { get; set; } = GraphicsHardwareState.Unknown;

    public Func<GraphicsMode, CancellationToken, Task<ControlResult>> RequestHandler { get; set; }
        = (mode, _) => Task.FromResult(ControlResult.Single(SettingResult.PendingRestart(
            "graphics.mode",
            mode.ToString(),
            "已写入，需重启生效")));

    public int RequestCalls { get; private set; }

    public GraphicsMode? LastRequested { get; private set; }

    public Task<GraphicsHardwareState> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(State);

    public Task<ControlResult> RequestModeAsync(GraphicsMode mode, CancellationToken cancellationToken)
    {
        RequestCalls++;
        LastRequested = mode;
        return RequestHandler(mode, cancellationToken);
    }
}

/// <summary>可编程的供电读取器替身。</summary>
internal sealed class FakePowerStatusProbe : IPowerStatusProbe
{
    public PowerSupplyKind Value { get; set; } = PowerSupplyKind.Ac;

    public PowerSupplyKind Read() => Value;
}

/// <summary>可编程的供电监听器替身（不轮询，便于确定性测试）。</summary>
internal sealed class FakePowerSupplyWatcher : IPowerSupplyWatcher
{
    public FakePowerSupplyWatcher(PowerSupplyKind initial = PowerSupplyKind.Ac) => Current = initial;

    public PowerSupplyKind Current { get; private set; }

    public event EventHandler<PowerSupplyKind>? Changed;

    public int StartCalls { get; private set; }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        StartCalls++;
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    /// <summary>只改变状态值，不触发事件。用于构造确定性的「下一次读取结果」。</summary>
    public void SetCurrent(PowerSupplyKind kind) => Current = kind;

    /// <summary>改变状态值并触发事件。</summary>
    public void Raise(PowerSupplyKind kind)
    {
        Current = kind;
        Changed?.Invoke(this, kind);
    }

    public void Dispose() => Stop();
}

/// <summary>可编程的进程枚举替身。</summary>
internal sealed class FakeProcessProbe : IProcessProbe
{
    public IReadOnlyList<string> Names { get; set; } = Array.Empty<string>();

    public IReadOnlyList<string> GetProcessNames() => Names;
}

/// <summary>可编程的 SMBIOS 读取替身。</summary>
internal sealed class FakeBiosInfoReader : IBiosInfoReader
{
    public IReadOnlyDictionary<string, string?> Values { get; set; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string?> Read() => Values;

    public static FakeBiosInfoReader TargetMachine() => new()
    {
        Values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [BiosInfoKeys.BaseBoardProduct] = "MS-15M3 REV:1.0",
            [BiosInfoKeys.SystemProductName] = "Vector 16 HX AI A2XWHG",
            [BiosInfoKeys.BiosVersion] = "E15M3IMS.117",
            [BiosInfoKeys.ProcessorName] = "Intel(R) Core(TM) Ultra 9 275HX"
        }
    };
}

/// <summary>可编程的设备标识来源替身。</summary>
internal sealed class FakeDeviceInfoSource : IDeviceInfoSource
{
    public DeviceInfoResult Result { get; set; } = new(DeviceIdentity.Unknown, new[] { "测试替身未提供标识。" });

    public DeviceInfoResult Read() => Result;
}

/// <summary>可编程的「最近成功应用配置」来源替身。</summary>
internal sealed class FakeLastAppliedConfigSource : ILastAppliedConfigSource
{
    public LastAppliedConfig? Value { get; set; }

    public LastAppliedConfig? Read() => Value;
}

/// <summary>测试用能力集合构造。</summary>
internal static class TestCapabilities
{
    /// <summary>MS-15M3 的真实能力基线（全部为检测/未知，无可写项）。</summary>
    public static DeviceCapabilities TargetMachine()
        => MsiControl.Hardware.Msi.Capabilities.Ms15M3Capabilities.Create();

    /// <summary>按给定能力键构造「全部已确证」的能力集合。</summary>
    public static DeviceCapabilities AllVerified(params string[] keys)
        => new(keys.Select(key => CapabilityStatus.Verified(key, "测试替身提供的证据")));

    /// <summary>写入性能档位所需的最小可写能力集合（原生档位仅需固件场景）。</summary>
    public static DeviceCapabilities StageWritable()
        => AllVerified(
            CapabilityKeys.FirmwareScenario,
            CapabilityKeys.CpuPowerLimit,
            CapabilityKeys.GpuPowerLimit,
            CapabilityKeys.FanCurve);
}

/// <summary>并发进入计数探针，用于验证「写入串行」。</summary>
internal sealed class ConcurrencyProbe
{
    private readonly object _lock = new();
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active;
    private int _max;

    /// <summary>同时处于关键区的最大数量。</summary>
    public int Max
    {
        get
        {
            lock (_lock)
            {
                return _max;
            }
        }
    }

    /// <summary>首次进入时完成的信号。</summary>
    public Task Entered => _entered.Task;

    /// <summary>进入关键区。</summary>
    public void Enter()
    {
        lock (_lock)
        {
            _active++;
            _max = Math.Max(_max, _active);
        }

        _entered.TrySetResult();
    }

    /// <summary>离开关键区。</summary>
    public void Exit()
    {
        lock (_lock)
        {
            _active--;
        }
    }
}

/// <summary>断言辅助。</summary>
internal static class AssertHelpers
{
    /// <summary>断言结果集合未违反「成功必须有确认依据」等约定。</summary>
    public static void Compliant(ControlResult result)
        => Xunit.Assert.Empty(result.Validate());

    /// <summary>断言一次控制结果合规。</summary>
    public static void Compliant(ControlOutcome outcome) => Compliant(outcome.Result);
}

/// <summary>
/// 场景型性能适配层替身（实现 IScenarioPerformanceHardware 标记）。
/// </summary>
/// <remarks>
/// 用于验证协调器对场景型通道的门控分流：只要求 performance.stage 达 Verified，
/// 不因瓦数类能力键整组拒绝。替身不触硬件。
/// </remarks>
internal sealed class FakeScenarioPerformanceHardware : IPerformanceHardware, IScenarioPerformanceHardware
{
    public Func<CancellationToken, Task<PerformanceHardwareState>> ReadHandler { get; set; }
        = _ => Task.FromResult(new PerformanceHardwareState(PerformanceStage.Balanced, FirmwareScenario: "0xC1（comfort/平衡）"));

    public Func<PerformanceSettings, CancellationToken, Task<ControlResult>> ApplyHandler { get; set; }
        = (settings, _) => Task.FromResult(FakePerformanceHardware.Succeed("stage"));

    public int ApplyCalls { get; private set; }

    public PerformanceSettings? LastAppliedSettings { get; private set; }

    public Task<PerformanceHardwareState> ReadAsync(CancellationToken cancellationToken)
        => ReadHandler(cancellationToken);

    public async Task<ControlResult> ApplyAsync(PerformanceSettings settings, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        LastAppliedSettings = settings;
        return await ApplyHandler(settings, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 场景型适配层替身，并实现「仅写场景」的恢复通道（供退出恢复测试）。
/// </summary>
/// <remarks>
/// 记录 <see cref="ApplyCalls"/> 与 <see cref="RestoreCalls"/> 的分离调用次数，
/// 用于证明「退出恢复不应用配置表」：恢复路径只允许走 <see cref="ApplyScenarioOnlyAsync"/>。
/// </remarks>
internal sealed class FakeScenarioRestoreHardware
    : IPerformanceHardware, IScenarioPerformanceHardware, IScenarioRestoreChannel
{
    public Func<PerformanceSettings, CancellationToken, Task<ControlResult>> ApplyHandler { get; set; }
        = (settings, _) => Task.FromResult(FakePerformanceHardware.Succeed("stage"));

    public Func<PerformanceStage, CancellationToken, Task<ControlResult>> RestoreHandler { get; set; }
        = (stage, _) => Task.FromResult(ControlResult.Single(
            SettingResult.Succeeded(
                "stage",
                stage.ToString(),
                "0xC1",
                "写入 0xD2 后立即读回一致（替身）")));

    public int ApplyCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public PerformanceStage? LastRestoredStage { get; private set; }

    public Task<PerformanceHardwareState> ReadAsync(CancellationToken cancellationToken)
        => Task.FromResult(new PerformanceHardwareState(PerformanceStage.Balanced));

    public Task<ControlResult> ApplyAsync(PerformanceSettings settings, CancellationToken cancellationToken)
    {
        ApplyCalls++;
        return ApplyHandler(settings, cancellationToken);
    }

    public Task<ControlResult> ApplyScenarioOnlyAsync(PerformanceStage stage, CancellationToken cancellationToken)
    {
        RestoreCalls++;
        LastRestoredStage = stage;
        return RestoreHandler(stage, cancellationToken);
    }
}

/// <summary>
/// 实现 <see cref="IFanModeSwitch"/> 的风扇适配层替身（用于组合档测试）。
/// </summary>
internal sealed class FakeFanModeSwitchHardware : FakeFanHardware, IFanModeSwitch
{
    public Func<CancellationToken, Task<ControlResult>> SetSilentHandler { get; set; }
        = _ => Task.FromResult(ControlResult.Single(
            SettingResult.Succeeded("fan.mode", "静音", "0x1D", "写入后读回一致（替身）")));

    public int SilentCalls { get; private set; }

    public Task<ControlResult> SetSilentAsync(CancellationToken cancellationToken)
    {
        SilentCalls++;
        return SetSilentHandler(cancellationToken);
    }

    public Task<ControlResult> SetAdvancedAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException("测试替身不实现 advanced 切换。");
}
