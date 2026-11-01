using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Monitoring.Nvml;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 独显（RTX 5070 Ti Laptop GPU）数据源，基于 NVML 进程内读取。
/// </summary>
/// <remarks>
/// <para>
/// 提供 GPU 四项（温度、核心频率、功耗、风扇）与扩展项（显存占用、GPU 利用率、显存频率）。
/// 全部为**只读**。
/// </para>
/// <para>
/// <b>不持续唤醒独显：</b>NVML 读取不会把独显从低功耗状态拉起（与创建 D3D/OpenGL 上下文不同），
/// 且在探测到独显休眠时**根本不触碰 NVML**，进一步降低轮询代价（任务书 §6.4）。
/// </para>
/// <para>
/// <b>风扇口径：</b>NVML 只给出占空比百分比，不提供 RPM。而 <see cref="FanReading.ActualPercent"/>
/// 的语义是「由校准基准推导的实际转速百分比」，未完成校准时必须为 <see langword="null"/>
/// （任务书 §6.5）。因此 NVML 的百分比**不写入** <see cref="FanReading.ActualPercent"/>，
/// 而是作为扩展传感器「GPU 风扇」的独立读数呈现，避免与校准百分比混淆。
/// </para>
/// </remarks>
public sealed class NvmlGpuSource : IGpuSource, IExtendedSensorSource
{
    /// <summary>摄氏度的合理下限。</summary>
    public const double MinPlausibleCelsius = -20.0;

    /// <summary>摄氏度的合理上限。</summary>
    public const double MaxPlausibleCelsius = 150.0;

    private readonly NvmlSession _session;
    private readonly GpuPresenceProbe _probe;
    private readonly TimeSpan _presenceTtl;
    private readonly object _gate = new();
    private GpuPresence? _cachedPresence;
    private DateTimeOffset _presenceAt;
    private string? _deviceName;

    /// <summary>构造数据源。</summary>
    /// <param name="session">NVML 会话。</param>
    /// <param name="probe">独显在线性探测。</param>
    /// <param name="presenceTtl">在线性探测的缓存有效期。</param>
    public NvmlGpuSource(NvmlSession session, GpuPresenceProbe probe, TimeSpan? presenceTtl = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _presenceTtl = presenceTtl ?? MonitoringOptions.Default.GpuPresenceProbeInterval;
    }

    /// <inheritdoc />
    public string Key => "gpu.nvml";

    /// <inheritdoc />
    public string Description => "NVML（NVIDIA 驱动管理库，进程内调用）";

    /// <inheritdoc />
    public string? RequiredCapabilityKey => CapabilityKeys.ThermalRead;

    /// <inheritdoc />
    public Task<GpuSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadSnapshot());
    }

    /// <inheritdoc />
    Task<IReadOnlyList<ExtendedSensor>> IExtendedSensorSource.ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadExtended());
    }

    private GpuSnapshot ReadSnapshot()
    {
        var presence = Presence();
        if (presence.IsSleeping)
        {
            return GpuSnapshot.Sleeping(Description, presence.Reason);
        }

        if (!_session.TryGetDevice(out var device, out var deviceError))
        {
            return GpuSnapshot.Unavailable(Description, deviceError ?? "无法取得 NVIDIA 设备句柄。");
        }

        var now = DateTimeOffset.Now;
        var source = SourceLabel(device);

        var temperature = ReadTemperature(device, source, now);
        var coreClock = ReadCoreClock(device, source, now);

        SensorReading power;
        if (NvmlSession.TryGetPowerWatt(device, out var watt, out var powerError))
        {
            power = SensorReading.Valid(watt, Units.Watt, source, now);
        }
        else
        {
            power = SensorReading.Unavailable(Units.Watt, source, powerError);
        }

        return new GpuSnapshot(
            temperature,
            coreClock,
            power,
            FanSlot(presence));
    }

    private IReadOnlyList<ExtendedSensor> ReadExtended()
    {
        var presence = Presence();
        var source = Description;

        if (presence.IsSleeping)
        {
            return new[]
            {
                Sleeping("gpu.utilization", "GPU 利用率", Units.Percent, presence.Reason),
                Sleeping("gpu.vram", "显存占用", Units.Percent, presence.Reason),
                Sleeping("gpu.memclock", "显存频率", Units.Megahertz, presence.Reason),
                Sleeping("gpu.fan", "GPU 风扇（NVML 占空比）", Units.Percent, presence.Reason),
                Sleeping("gpu.powerlimit", "GPU 功耗限制", Units.Watt, presence.Reason),
                Sleeping("gpu.temp.limit", "GPU 降频温度阈值", Units.Celsius, presence.Reason)
            };
        }

        if (!_session.TryGetDevice(out var device, out var deviceError))
        {
            var reason = deviceError ?? "无法取得 NVIDIA 设备句柄。";
            return new[]
            {
                Unavailable("gpu.utilization", "GPU 利用率", Units.Percent, source, reason),
                Unavailable("gpu.vram", "显存占用", Units.Percent, source, reason),
                Unavailable("gpu.memclock", "显存频率", Units.Megahertz, source, reason),
                Unavailable("gpu.fan", "GPU 风扇（NVML 占空比）", Units.Percent, source, reason),
                Unavailable("gpu.powerlimit", "GPU 功耗限制", Units.Watt, source, reason),
                Unavailable("gpu.temp.limit", "GPU 降频温度阈值", Units.Celsius, source, reason)
            };
        }

        var now = DateTimeOffset.Now;
        var label = SourceLabel(device);

        SensorReading utilization;
        if (NvmlSession.TryGetUtilization(device, out var gpuPercent, out _, out var utilizationError))
        {
            utilization = SensorReading.Valid(
                Measurement.Clamp(gpuPercent, 0.0, 100.0),
                Units.Percent,
                $"{label}:nvmlDeviceGetUtilizationRates(GPU)",
                now);
        }
        else
        {
            utilization = SensorReading.Unavailable(Units.Percent, label, utilizationError);
        }

        SensorReading vram;
        if (NvmlSession.TryGetMemoryBytes(device, out var used, out var total, out var memoryError))
        {
            vram = total == 0
                ? SensorReading.Unavailable(Units.Percent, label, "NVML 返回的显存总量为 0，无法计算占用率。")
                : SensorReading.Valid(
                    Measurement.Clamp(used * 100.0 / total, 0.0, 100.0),
                    Units.Percent,
                    $"{label}:nvmlDeviceGetMemoryInfo",
                    now);
        }
        else
        {
            vram = SensorReading.Unavailable(Units.Percent, label, memoryError);
        }

        SensorReading memClock;
        if (NvmlSession.TryGetMemoryClockMhz(device, out var memMhz, out var memClockError))
        {
            memClock = SensorReading.Valid(memMhz, Units.Megahertz, $"{label}:nvmlDeviceGetClockInfo(MEM)", now);
        }
        else
        {
            memClock = SensorReading.Unavailable(Units.Megahertz, label, memClockError);
        }

        SensorReading fan;
        if (NvmlSession.TryGetFanPercent(device, out var fanPercent, out var fanError))
        {
            fan = SensorReading.Valid(
                Measurement.Clamp(fanPercent, 0.0, 100.0),
                Units.Percent,
                $"{label}:nvmlDeviceGetFanSpeed（相对最大转速的占空比）",
                now);
        }
        else
        {
            fan = SensorReading.Unavailable(Units.Percent, label, fanError);
        }

        SensorReading powerLimit;
        if (NvmlSession.TryGetPowerLimitWatt(device, out var limitWatt, out var limitError))
        {
            powerLimit = SensorReading.Valid(
                limitWatt,
                Units.Watt,
                $"{label}:nvmlDeviceGetPowerManagementLimit（驱动默认上限，非本软件写入值）",
                now);
        }
        else
        {
            powerLimit = SensorReading.Unavailable(Units.Watt, label, limitError);
        }

        SensorReading tempLimit;
        if (NvmlSession.TryGetSlowdownTemperatureC(device, out var slowC, out var slowError))
        {
            tempLimit = SensorReading.Valid(
                slowC,
                Units.Celsius,
                $"{label}:nvmlDeviceGetTemperatureThreshold(SLOWDOWN)",
                now);
        }
        else
        {
            tempLimit = SensorReading.Unavailable(Units.Celsius, label, slowError);
        }

        return new[]
        {
            new ExtendedSensor("gpu.utilization", "GPU 利用率", utilization),
            new ExtendedSensor("gpu.vram", "显存占用", vram),
            new ExtendedSensor("gpu.memclock", "显存频率", memClock),
            new ExtendedSensor("gpu.fan", "GPU 风扇（NVML 占空比）", fan),
            new ExtendedSensor("gpu.powerlimit", "GPU 功耗限制", powerLimit),
            new ExtendedSensor("gpu.temp.limit", "GPU 降频温度阈值", tempLimit)
        };
    }

    private SensorReading ReadTemperature(IntPtr device, string source, DateTimeOffset now)
    {
        if (!NvmlSession.TryGetTemperature(device, out var celsius, out var error))
        {
            return SensorReading.Unavailable(Units.Celsius, source, error);
        }

        if (celsius is < MinPlausibleCelsius or > MaxPlausibleCelsius)
        {
            return SensorReading.Unavailable(
                Units.Celsius,
                source,
                $"NVML 返回温度 {celsius:0.#} °C，超出合理区间，已按不可用处理。");
        }

        return SensorReading.Valid(celsius, Units.Celsius, source, now);
    }

    private SensorReading ReadCoreClock(IntPtr device, string source, DateTimeOffset now)
    {
        if (!NvmlSession.TryGetCoreClockMhz(device, out var mhz, out var error))
        {
            return SensorReading.Unavailable(Units.Megahertz, source, error);
        }

        if (mhz <= 0 || mhz > 6000)
        {
            return SensorReading.Unavailable(
                Units.Megahertz,
                source,
                $"NVML 返回核心频率 {mhz:0.#} MHz，超出合理区间，已按不可用处理。");
        }

        return SensorReading.Valid(mhz, Units.Megahertz, source, now);
    }

    private FanReading FanSlot(GpuPresence presence)
        => FanReading.Unavailable(
            Description,
            $"NVML 只提供风扇占空比百分比、不提供 RPM；且未完成校准，按其语义不得写入 ActualPercent。" +
            $"读数请见扩展传感器「GPU 风扇」。（{presence.Reason}）");

    private GpuPresence Presence()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.Now;
            if (_cachedPresence is not null && now - _presenceAt <= _presenceTtl)
            {
                return _cachedPresence;
            }

            _cachedPresence = _probe.Probe();
            _presenceAt = now;
            return _cachedPresence;
        }
    }

    private string SourceLabel(IntPtr device)
    {
        if (_deviceName is null)
        {
            _deviceName = NvmlSession.TryGetName(device, out var name, out _) && !string.IsNullOrWhiteSpace(name)
                ? name!
                : "NVIDIA GPU";
        }

        return $"NVML:{_deviceName}";
    }

    private static ExtendedSensor Sleeping(string key, string displayName, string unit, string detail)
        => new(key, displayName, SensorReading.Sleeping(unit, "NVML", detail));

    private static ExtendedSensor Unavailable(
        string key,
        string displayName,
        string unit,
        string source,
        string? detail)
        => new(key, displayName, SensorReading.Unavailable(unit, source, detail));
}
