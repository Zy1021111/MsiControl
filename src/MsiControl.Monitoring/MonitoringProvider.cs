using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Internal;
using MsiControl.Monitoring.Sources;

namespace MsiControl.Monitoring;

/// <summary>
/// 实时监控数据源实现：把各传感器来源聚合成一次完整采样。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界：</b>本类型只负责「被调用一次 → 读一轮 → 产出快照」。
/// 采样节奏、有效期判定与广播由桌面壳的 <c>SnapshotStream</c>（任务 02）持有，
/// 因此整个应用只有一个采样循环（任务书 §16、§20）。本类型**不创建任何计时器或线程**。
/// </para>
/// <para>
/// <b>状态真实性（任务书 §6.4）：</b>
/// </para>
/// <list type="bullet">
/// <item>读不到 → <see cref="ReadingStatus.Unavailable"/>，界面显示「不可用」，**绝不沿用上一有效值**。</item>
/// <item>独显断电 → <see cref="ReadingStatus.Sleeping"/>，界面显示「休眠」；休眠与失败是不同状态。</item>
/// <item>采样整体超时 → 由 <c>SnapshotStream</c> 调用 <see cref="MonitoringSnapshot.MarkStale"/>；
/// 单个数据源失败只影响它自己，不会把整份快照改成过期。</item>
/// </list>
/// <para>
/// <b>能力门控（任务书 §16.1）：</b>数据源若声明了
/// <see cref="IMonitoringSource.RequiredCapabilityKey"/>，则在
/// <see cref="DeviceCapabilities.AllowsRead(string)"/> 为 <see langword="false"/> 时**不被调用**，
/// 直接给出禁用原因。因此本机 <c>power.system</c>（<c>Unknown</c>）对应的整机功耗恒为「不可用」，
/// 即使读到了电池侧功率也只作为带口径标注的扩展传感器出现。
/// </para>
/// </remarks>
public sealed class MonitoringProvider : IMonitoringProvider, IDisposable
{
    private const string CpuTemperatureSlot = "CPU 温度";
    private const string NoFrequencyReason = "未提供 CPU 频率数据源。";
    private const string NoPowerReason = "本机无已验证的 CPU 封装功耗读取路径（MSI_ACPI.Get_Power 的参数编码未验证）。";
    private const string NoFanReason = "本机无已验证的风扇转速读取路径（MSI_ACPI.Get_Fan 的参数编码未验证，也无校准基准）。";

    private readonly DeviceCapabilities _capabilities;
    private readonly MonitoringOptions _options;
    private readonly IReadOnlyList<ICpuTemperatureSource> _cpuTemperatureSources;
    private readonly ICpuFrequencySource? _cpuFrequencySource;
    private readonly ICpuPowerSource? _cpuPowerSource;
    private readonly IFanSpeedSource? _fanSpeedSource;
    private readonly IGpuSource? _gpuSource;
    private readonly IReadOnlyList<ISystemPowerSource> _systemPowerSources;
    private readonly IReadOnlyList<IExtendedSensorSource> _extendedSources;
    private readonly MsiAcpiEcFirmwareSource? _ecFirmwareSource;
    private readonly SourceRunner _runner;
    private readonly Dictionary<string, ExtendedCacheEntry> _extendedCache = new(StringComparer.Ordinal);
    private readonly List<string> _constructorNotes = new();
    private readonly IReadOnlyList<IDisposable> _ownedDisposables;
    private readonly object _gate = new();
    private bool _disposed;

    private MonitoringSnapshot? _latest;
    private MonitoringDiagnostics _diagnostics = MonitoringDiagnostics.Empty;
    private DateTimeOffset _lastSampleAt;
    private TimeSpan _lastSampleDuration;
    private EcFirmwareReading? _ecReading;
    private DateTimeOffset _ecNextAttemptAt = DateTimeOffset.MinValue;

    /// <summary>构造监控数据源。</summary>
    /// <param name="capabilities">设备能力集合，用于能力门控。</param>
    /// <param name="options">运行参数。为 <see langword="null"/> 时使用默认值。</param>
    /// <param name="cpuTemperatureSources">CPU 温度来源，按顺序取第一个可用者。</param>
    /// <param name="cpuFrequencySource">CPU 频率来源。</param>
    /// <param name="cpuPowerSource">CPU 封装功耗来源。</param>
    /// <param name="fanSpeedSource">风扇转速来源。</param>
    /// <param name="gpuSource">独显来源。</param>
    /// <param name="systemPowerSources">整机功耗来源，按顺序取第一个可用者。</param>
    /// <param name="extendedSources">扩展传感器来源。</param>
    /// <param name="ecFirmwareSource">EC 固件响应来源（只读，一次）。</param>
    /// <param name="ownedDisposables">由本实例负责释放的资源（例如 NVML 会话）。</param>
    /// <exception cref="ArgumentException">参数组合非法。</exception>
    public MonitoringProvider(
        DeviceCapabilities capabilities,
        MonitoringOptions? options = null,
        IReadOnlyList<ICpuTemperatureSource>? cpuTemperatureSources = null,
        ICpuFrequencySource? cpuFrequencySource = null,
        ICpuPowerSource? cpuPowerSource = null,
        IFanSpeedSource? fanSpeedSource = null,
        IGpuSource? gpuSource = null,
        IReadOnlyList<ISystemPowerSource>? systemPowerSources = null,
        IReadOnlyList<IExtendedSensorSource>? extendedSources = null,
        MsiAcpiEcFirmwareSource? ecFirmwareSource = null,
        IReadOnlyList<IDisposable>? ownedDisposables = null)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _options = options ?? MonitoringOptions.Default;

        var errors = _options.Validate();
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(options));
        }

        _cpuTemperatureSources = cpuTemperatureSources ?? Array.Empty<ICpuTemperatureSource>();
        _cpuFrequencySource = cpuFrequencySource;
        _cpuPowerSource = cpuPowerSource;
        _fanSpeedSource = fanSpeedSource;
        _gpuSource = gpuSource;
        _systemPowerSources = systemPowerSources ?? Array.Empty<ISystemPowerSource>();
        _extendedSources = Deduplicate(extendedSources ?? Array.Empty<IExtendedSensorSource>(), _constructorNotes);
        _ecFirmwareSource = ecFirmwareSource;
        _ownedDisposables = ownedDisposables ?? Array.Empty<IDisposable>();
        _runner = new SourceRunner(_options);

        foreach (var note in _constructorNotes)
        {
            _runner.Note(note);
        }
    }

    /// <summary>最近一次采样结果。尚未采样时为 <see langword="null"/>。</summary>
    public MonitoringSnapshot? Latest
    {
        get
        {
            lock (_gate)
            {
                return _latest;
            }
        }
    }

    /// <summary>各数据源的来源说明、最近成功时刻与失败原因。</summary>
    public MonitoringDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    /// <summary>EC 固件响应（只读一次；失败按退避重试）。</summary>
    public EcFirmwareReading? EcFirmware
    {
        get
        {
            lock (_gate)
            {
                return _ecReading;
            }
        }
    }

    /// <inheritdoc />
    public async Task<MonitoringSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        var startedAt = DateTimeOffset.Now;

        // ── CPU ────────────────────────────────────────────────────────
        var cpuTemperature = await ReadFirstUsableTemperatureAsync(startedAt, cancellationToken).ConfigureAwait(false);
        var cpuFrequency = await ReadFrequencyAsync(startedAt, cancellationToken).ConfigureAwait(false);
        var cpuPower = await ReadCpuPowerAsync(startedAt, cancellationToken).ConfigureAwait(false);

        // ── GPU（先算 GPU，风扇槽位需要知道独显是否休眠）─────────────────
        var gpu = await ReadGpuAsync(startedAt, cancellationToken).ConfigureAwait(false);

        // ── 双风扇 ─────────────────────────────────────────────────────
        var cpuFan = await ReadFanAsync(FanChannel.Cpu, startedAt, cancellationToken).ConfigureAwait(false);
        var gpuFan = gpu.IsSleeping
            ? FanReading.Sleeping(FanDescription, "独显处于休眠，其风扇读数不适用。")
            : await ReadFanAsync(FanChannel.Gpu, startedAt, cancellationToken).ConfigureAwait(false);

        // ── 整机功耗 ───────────────────────────────────────────────────
        var systemPower = await ReadSystemPowerAsync(startedAt, cancellationToken).ConfigureAwait(false);

        // ── 扩展传感器 ─────────────────────────────────────────────────
        var extended = await ReadExtendedAsync(startedAt, cancellationToken).ConfigureAwait(false);

        // ── EC 固件响应（只读一次，失败按 60 秒退避重试）──────────────────
        await TryReadEcFirmwareAsync(cancellationToken).ConfigureAwait(false);

        // GPU 快照的风扇槽位以 EC 读数覆盖：独显采集链（NVML/Afterburner）在本机
        // 不提供可用风扇读数（NVML 返回 Not Supported、MAHM 无 RPM），而 EC 0x71/0x89
        // 是硬件直报的权威来源——与 CPU 侧口径对齐，界面「GPU 风扇」格子由此恢复显示。
        var gpuWithEcFan = gpu with { Fan = gpuFan };

        var snapshot = new MonitoringSnapshot(
            startedAt,
            new CpuSnapshot(
                cpuTemperature,
                cpuFrequency,
                cpuPower,
                cpuFan,
                _cpuFrequencySource?.Basis ?? FrequencyAggregation.AverageActive),
            gpuWithEcFan,
            new FanSnapshot(cpuFan, gpuFan),
            systemPower,
            extended);

        var duration = DateTimeOffset.Now - startedAt;

        lock (_gate)
        {
            _latest = snapshot;
            _lastSampleAt = startedAt;
            _lastSampleDuration = duration;
            _diagnostics = _runner.Snapshot(DateTimeOffset.Now, startedAt, duration);
        }

        return snapshot;
    }

    /// <summary>
    /// 释放本实例持有的资源（例如 NVML 会话）。
    /// </summary>
    /// <remarks>
    /// 宿主应在退出时释放。未释放不会造成句柄泄漏增长——NVML 会在进程退出时由驱动回收——
    /// 但它会让 NVML 会话在本进程内保持初始化状态，因此仍以显式释放为正确做法。
    /// </remarks>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        foreach (var resource in _ownedDisposables)
        {
            try
            {
                resource.Dispose();
            }
            catch (Exception)
            {
                // 释放阶段失败无补救价值，不向上传播以免影响宿主退出流程。
            }
        }
    }

    private const string FanDescription = "风扇转速";

    private async Task<SensorReading> ReadFirstUsableTemperatureAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_cpuTemperatureSources.Count == 0)
        {
            return SensorReading.Unavailable(Units.Celsius, CpuTemperatureSlot, "未提供 CPU 温度数据源。");
        }

        var reasons = new List<string>();

        foreach (var source in _cpuTemperatureSources)
        {
            var reading = await ReadSourceAsync(
                source,
                token => source.ReadAsync(token),
                reason => SensorReading.Unavailable(Units.Celsius, source.Description, reason),
                now,
                cancellationToken).ConfigureAwait(false);

            if (reading.IsUsable)
            {
                return reading;
            }

            reasons.Add(reading.Detail ?? $"{source.Description}：本次不可用。");
        }

        return SensorReading.Unavailable(Units.Celsius, CpuTemperatureSlot, string.Join(" ", reasons));
    }

    private Task<SensorReading> ReadFrequencyAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_cpuFrequencySource is not { } source)
        {
            return Task.FromResult(SensorReading.Unavailable(Units.Megahertz, "CPU 频率", NoFrequencyReason));
        }

        return ReadSourceAsync(
            source,
            token => source.ReadAsync(token),
            reason => SensorReading.Unavailable(Units.Megahertz, source.Description, reason),
            now,
            cancellationToken);
    }

    private Task<SensorReading> ReadCpuPowerAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_cpuPowerSource is not { } source)
        {
            return Task.FromResult(SensorReading.Unavailable(Units.Watt, "CPU 封装功耗", NoPowerReason));
        }

        return ReadSourceAsync(
            source,
            token => source.ReadAsync(token),
            reason => SensorReading.Unavailable(Units.Watt, source.Description, reason),
            now,
            cancellationToken);
    }

    private Task<FanReading> ReadFanAsync(FanChannel channel, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_fanSpeedSource is not { } source)
        {
            return Task.FromResult(FanReading.Unavailable(FanDescription, NoFanReason));
        }

        return ReadSourceAsync(
            source,
            token => source.ReadAsync(channel, token),
            reason => FanReading.Unavailable(source.Description, reason),
            now,
            cancellationToken);
    }

    private Task<GpuSnapshot> ReadGpuAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (_gpuSource is not { } source)
        {
            return Task.FromResult(GpuSnapshot.Unavailable("独显", "未提供独显数据源。"));
        }

        return ReadSourceAsync(
            source,
            token => source.ReadAsync(token),
            reason => GpuSnapshot.Unavailable(source.Description, reason),
            now,
            cancellationToken);
    }

    private async Task<SystemPowerReading> ReadSystemPowerAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // 整机功耗槽位由「整机功耗读取」能力键单独门控（本机为 Unknown → 一律不得读取）。
        // 这与数据源自身的能力键是两层判定：
        //   槽位键（power.system）决定「界面上是否允许出现整机功耗」；
        //   来源键（例如 power.battery）决定「该具体数据通路是否允许被调用」。
        // 本机的电池侧功率因此只以带口径标注的扩展传感器形式出现，不会被冒充成整机功耗。
        if (!_capabilities.AllowsRead(CapabilityKeys.SystemPowerRead))
        {
            var status = _capabilities.Get(CapabilityKeys.SystemPowerRead);
            _runner.Note(
                $"整机功耗槽位已按能力门控关闭：{CapabilityKeys.SystemPowerRead} = {status.Level}。" +
                "已探明电池侧（power.battery）在拔电时可得放电功率，但口径为 BatterySide，" +
                "需由任务 01 复核该键等级后才可提升为整机功耗；未复核前不使用替代数据。");

            return SystemPowerReading.Unavailable(
                $"整机功耗读取能力「{CapabilityKeys.SystemPowerRead}」当前为 {status.Level}，" +
                $"按能力等级制度不得读取。依据：{status.DescribeUnavailable()}" +
                $"（{MonitoringProviderFactory.SystemPowerUnavailableReason}）");
        }

        if (_systemPowerSources.Count == 0)
        {
            return SystemPowerReading.Unavailable("未提供整机功耗数据源。");
        }

        var reasons = new List<string>();

        foreach (var source in _systemPowerSources)
        {
            var reading = await ReadSourceAsync(
                source,
                token => source.ReadAsync(token),
                SystemPowerReading.Unavailable,
                now,
                cancellationToken).ConfigureAwait(false);

            if (reading.IsAvailable)
            {
                return reading;
            }

            reasons.Add(reading.Reading.Detail ?? $"{source.Description}：本次不可用。");
        }

        return SystemPowerReading.Unavailable(string.Join(" ", reasons));
    }

    private async Task<IReadOnlyList<ExtendedSensor>> ReadExtendedAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = new List<ExtendedSensor>();

        foreach (var source in _extendedSources)
        {
            IReadOnlyList<ExtendedSensor> sensors;

            if (TryGetCachedExtended(source, now, out var cached))
            {
                _runner.NoteThrottled(source);
                sensors = cached;
            }
            else
            {
                sensors = await ReadSourceAsync(
                    source,
                    token => source.ReadAsync(token),
                    reason => UnavailableSet(source, reason),
                    now,
                    cancellationToken).ConfigureAwait(false);

                StoreExtended(source, sensors, now);
            }

            result.AddRange(Annotate(sensors, source.MinRefreshInterval));
        }

        if (_options.EnableDiagnostics)
        {
            result.AddRange(BuildDiagnosticSensors(now));
        }

        return result;
    }

    private async Task TryReadEcFirmwareAsync(CancellationToken cancellationToken)
    {
        if (_ecFirmwareSource is not { } source)
        {
            return;
        }

        lock (_gate)
        {
            if (_ecReading is { IsAvailable: true } || DateTimeOffset.Now < _ecNextAttemptAt)
            {
                return;
            }
        }

        var reading = await source.ReadAsync(cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _ecReading = reading;
            if (!reading.IsAvailable)
            {
                _ecNextAttemptAt = DateTimeOffset.Now + TimeSpan.FromSeconds(60);
            }
        }

        _runner.Note(reading.IsAvailable
            ? $"{source.Description} 已读到内容：{reading.RawAscii}（原始字节 {reading.RawHex}）。"
            : $"{source.Description} 读取失败：{reading.Error}");
    }

    private async Task<T> ReadSourceAsync<T>(
        IMonitoringSource source,
        Func<CancellationToken, Task<T>> read,
        Func<string, T> onFailure,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!IsCapabilityAllowed(source, out var reason))
        {
            return onFailure(reason);
        }

        return await _runner.RunAsync(source, read, onFailure, now, cancellationToken).ConfigureAwait(false);
    }

    private bool IsCapabilityAllowed(IMonitoringSource source, out string reason)
    {
        reason = string.Empty;

        if (source.RequiredCapabilityKey is not { } key)
        {
            return true;
        }

        if (_capabilities.AllowsRead(key))
        {
            return true;
        }

        var status = _capabilities.Get(key);
        reason =
            $"能力「{key}」当前为 {status.Level}，不允许读取，因此未调用该数据源。" +
            $"依据：{status.DescribeUnavailable()}";
        _runner.Note($"已按能力门控跳过数据源 {source.Key}（{key} = {status.Level}）。");
        return false;
    }

    private bool TryGetCachedExtended(
        IExtendedSensorSource source,
        DateTimeOffset now,
        out IReadOnlyList<ExtendedSensor> cached)
    {
        cached = Array.Empty<ExtendedSensor>();

        if (source.MinRefreshInterval <= TimeSpan.Zero)
        {
            return false;
        }

        if (_extendedCache.TryGetValue(source.Key, out var entry)
            && now - entry.ReadAt < source.MinRefreshInterval)
        {
            cached = entry.Sensors;
            return true;
        }

        return false;
    }

    private void StoreExtended(
        IExtendedSensorSource source,
        IReadOnlyList<ExtendedSensor> sensors,
        DateTimeOffset now)
    {
        if (source.MinRefreshInterval > TimeSpan.Zero)
        {
            _extendedCache[source.Key] = new ExtendedCacheEntry(sensors, now);
        }
    }

    private static IReadOnlyList<ExtendedSensor> Annotate(
        IReadOnlyList<ExtendedSensor> sensors,
        TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            return sensors;
        }

        return sensors
            .Select(sensor => sensor with
            {
                DisplayName = $"{sensor.DisplayName}（每 {interval.TotalSeconds:0.#} 秒刷新）"
            })
            .ToArray();
    }

    private static IReadOnlyList<ExtendedSensor> UnavailableSet(IExtendedSensorSource source, string reason)
        => new[]
        {
            new ExtendedSensor(
                $"source.{source.Key}.unavailable",
                $"{source.Description}（不可用）",
                SensorReading.Unavailable("—", source.Description, reason))
        };

    private IReadOnlyList<ExtendedSensor> BuildDiagnosticSensors(DateTimeOffset now)
    {
        // 使用 runner 的实时状态：本周期已读到的数据源此刻已经记录了成功时刻。
        var live = _runner.Snapshot(now, _lastSampleAt, _lastSampleDuration);
        var list = new List<ExtendedSensor>
        {
            new(
                "diag.sample.duration",
                "上次采样耗时（诊断）",
                SensorReading.Valid(
                    _lastSampleDuration.TotalMilliseconds,
                    "ms",
                    "MonitoringProvider",
                    _lastSampleAt == default ? now : _lastSampleAt))
        };

        foreach (var source in live.Sources)
        {
            SensorReading reading;
            if (source.LastSuccessAt is { } successAt)
            {
                reading = SensorReading.Valid(
                    Math.Max(0, (now - successAt).TotalSeconds),
                    "s",
                    source.Description,
                    successAt);
            }
            else
            {
                reading = SensorReading.Unavailable(
                    "s",
                    source.Description,
                    source.LastError ?? "该数据源尚未成功读取过。");
            }

            list.Add(new ExtendedSensor($"diag.source.{source.Key}", $"来源：{source.Description}", reading));
        }

        return list;
    }

    private static IReadOnlyList<IExtendedSensorSource> Deduplicate(
        IReadOnlyList<IExtendedSensorSource> sources,
        List<string> notes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<IExtendedSensorSource>(sources.Count);

        foreach (var source in sources)
        {
            if (seen.Add(source.Key))
            {
                result.Add(source);
            }
            else
            {
                notes.Add($"扩展传感器数据源 {source.Key} 重复登记，已保留第一个。");
            }
        }

        return result;
    }

    private readonly record struct ExtendedCacheEntry(IReadOnlyList<ExtendedSensor> Sensors, DateTimeOffset ReadAt);
}
