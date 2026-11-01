using System.Text;

namespace MsiControl.Monitoring.Nvml;

/// <summary>
/// NVML 会话：负责一次性初始化与设备句柄获取，并在失败时退避重试。
/// </summary>
/// <remarks>
/// <para>
/// NVML 初始化与设备句柄在整个进程内只需一次。初始化失败（例如驱动未加载）不应每帧重试，
/// 因此本类型记录了下次允许重试的时刻（默认 60 秒），把失败开销限制为每秒可忽略的一次判断。
/// </para>
/// <para>
/// <b>线程安全：</b>由单一采样循环使用；内部仍加锁以免宿主误用造成竞态。
/// </para>
/// </remarks>
public sealed class NvmlSession : IDisposable
{
    /// <summary>初始化失败后的重试间隔。</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _retryInterval;
    private readonly object _gate = new();
    private bool _initialized;
    private bool _shutdown;
    private string? _lastError;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private uint _deviceCount;
    private IntPtr _device = IntPtr.Zero;

    /// <summary>构造会话。</summary>
    /// <param name="retryInterval">初始化失败后的重试间隔。</param>
    public NvmlSession(TimeSpan? retryInterval = null)
        => _retryInterval = retryInterval ?? DefaultRetryInterval;

    /// <summary>NVML 是否已成功初始化。</summary>
    public bool IsInitialized
    {
        get
        {
            lock (_gate)
            {
                return _initialized;
            }
        }
    }

    /// <summary>最近一次失败原因。</summary>
    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    /// <summary>枚举到的 NVIDIA 设备数量。</summary>
    public uint DeviceCount
    {
        get
        {
            lock (_gate)
            {
                return _deviceCount;
            }
        }
    }

    /// <summary>
    /// 取得首个 NVIDIA 设备句柄。
    /// </summary>
    /// <param name="device">成功时为设备句柄。</param>
    /// <param name="error">失败原因。</param>
    /// <returns>成功返回 <see langword="true"/>。</returns>
    public bool TryGetDevice(out IntPtr device, out string? error)
    {
        lock (_gate)
        {
            device = IntPtr.Zero;
            error = null;

            if (_shutdown)
            {
                error = "NVML 会话已释放。";
                return false;
            }

            if (!_initialized && DateTimeOffset.Now < _retryAfter)
            {
                error = _lastError ?? "NVML 尚未初始化成功，处于重试退避期。";
                return false;
            }

            if (!_initialized && !TryInitializeLocked())
            {
                error = _lastError;
                return false;
            }

            if (_deviceCount == 0)
            {
                error = "NVML 已初始化，但未发现任何 NVIDIA 设备。";
                return false;
            }

            if (_device == IntPtr.Zero && !TryGetHandleLocked())
            {
                error = _lastError;
                return false;
            }

            device = _device;
            return true;
        }
    }

    /// <summary>读取设备名称。</summary>
    /// <param name="device">设备句柄。</param>
    /// <param name="name">成功时为设备名。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryGetName(IntPtr device, out string? name, out string? error)
    {
        name = null;
        error = null;

        try
        {
            var buffer = new byte[96];
            var result = NvmlNative.GetDeviceName(device, buffer, (uint)buffer.Length);
            if (result != NvmlNative.Success)
            {
                error = NvmlNative.Describe(result);
                return false;
            }

            name = Encoding.ASCII.GetString(buffer).TrimEnd('\0');
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取 GPU 温度（摄氏度）。</summary>
    public static bool TryGetTemperature(IntPtr device, out double celsius, out string? error)
    {
        celsius = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetTemperature(device, NvmlNative.TemperatureGpu, out var raw);
            if (result != NvmlNative.Success)
            {
                error = $"读取温度失败：{NvmlNative.Describe(result)}";
                return false;
            }

            celsius = raw;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取核心频率（MHz）。</summary>
    public static bool TryGetCoreClockMhz(IntPtr device, out double megahertz, out string? error)
        => TryGetClockMhz(device, NvmlNative.ClockGraphics, "核心频率", out megahertz, out error);

    /// <summary>读取显存频率（MHz）。</summary>
    public static bool TryGetMemoryClockMhz(IntPtr device, out double megahertz, out string? error)
        => TryGetClockMhz(device, NvmlNative.ClockMemory, "显存频率", out megahertz, out error);

    private static bool TryGetClockMhz(
        IntPtr device,
        uint clockType,
        string label,
        out double megahertz,
        out string? error)
    {
        megahertz = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetClockInfo(device, clockType, out var raw);
            if (result != NvmlNative.Success)
            {
                error = $"读取{label}失败：{NvmlNative.Describe(result)}";
                return false;
            }

            megahertz = raw;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取功耗（瓦）。</summary>
    public static bool TryGetPowerWatt(IntPtr device, out double watt, out string? error)
    {
        watt = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetPowerUsage(device, out var milliwatts);
            if (result != NvmlNative.Success)
            {
                error = $"读取功耗失败：{NvmlNative.Describe(result)}";
                return false;
            }

            watt = milliwatts / 1000.0;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取风扇占空比（百分比，NVML 语义为相对最大转速的百分比）。</summary>
    public static bool TryGetFanPercent(IntPtr device, out double percent, out string? error)
    {
        percent = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetFanSpeed(device, out var value);
            if (result != NvmlNative.Success)
            {
                error = $"读取风扇百分比失败：{NvmlNative.Describe(result)}";
                return false;
            }

            percent = value;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取利用率（GPU 与显存控制器，百分比）。</summary>
    public static bool TryGetUtilization(IntPtr device, out double gpuPercent, out double memoryPercent, out string? error)
    {
        gpuPercent = 0;
        memoryPercent = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetUtilizationRates(device, out var utilization);
            if (result != NvmlNative.Success)
            {
                error = $"读取利用率失败：{NvmlNative.Describe(result)}";
                return false;
            }

            gpuPercent = utilization.Gpu;
            memoryPercent = utilization.Memory;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取显存信息（已用 / 总量，字节）。</summary>
    public static bool TryGetMemoryBytes(IntPtr device, out ulong used, out ulong total, out string? error)
    {
        used = 0;
        total = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetMemoryInfo(device, out var memory);
            if (result != NvmlNative.Success)
            {
                error = $"读取显存信息失败：{NvmlNative.Describe(result)}";
                return false;
            }

            used = memory.Used;
            total = memory.Total;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取 GPU 功耗管理上限（瓦）。为驱动默认上限，非本软件的写入值。</summary>
    public static bool TryGetPowerLimitWatt(IntPtr device, out double watt, out string? error)
    {
        watt = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetPowerManagementLimit(device, out var milliwatts);
            if (result != NvmlNative.Success)
            {
                error = $"读取 GPU 功耗限制失败：{NvmlNative.Describe(result)}";
                return false;
            }

            watt = milliwatts / 1000.0;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <summary>读取 GPU 温度降频阈值（摄氏度，<c>NVML_TEMPERATURE_THRESHOLD_SLOWDOWN</c>）。</summary>
    public static bool TryGetSlowdownTemperatureC(IntPtr device, out double celsius, out string? error)
    {
        celsius = 0;
        error = null;

        try
        {
            var result = NvmlNative.GetTemperatureThreshold(device, NvmlNative.TemperatureThresholdSlowdown, out var raw);
            if (result != NvmlNative.Success)
            {
                error = $"读取 GPU 温度阈值失败：{NvmlNative.Describe(result)}";
                return false;
            }

            celsius = raw;
            return true;
        }
        catch (Exception ex)
        {
            error = DescribeException(ex);
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            _device = IntPtr.Zero;

            if (_initialized)
            {
                try
                {
                    NvmlNative.Shutdown();
                }
                catch (Exception)
                {
                    // 释放阶段的失败无补救价值，不向上传播。
                }

                _initialized = false;
            }
        }
    }

    private bool TryInitializeLocked()
    {
        try
        {
            var result = NvmlNative.Init();
            if (result != NvmlNative.Success)
            {
                _lastError = $"NVML 初始化失败：{NvmlNative.Describe(result)}";
                _retryAfter = DateTimeOffset.Now + _retryInterval;
                return false;
            }

            _initialized = true;

            result = NvmlNative.GetDeviceCount(out _deviceCount);
            if (result != NvmlNative.Success)
            {
                _initialized = false;
                _deviceCount = 0;
                _lastError = $"NVML 枚举设备失败：{NvmlNative.Describe(result)}";
                _retryAfter = DateTimeOffset.Now + _retryInterval;
                return false;
            }

            _lastError = null;
            return true;
        }
        catch (DllNotFoundException)
        {
            _lastError = "未找到 nvml.dll（NVIDIA 管理库未安装），独显读数不可用。";
            _retryAfter = DateTimeOffset.Now + _retryInterval;
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            _lastError = $"nvml.dll 缺少所需入口：{ex.Message}";
            _retryAfter = DateTimeOffset.Now + _retryInterval;
            return false;
        }
        catch (BadImageFormatException ex)
        {
            _lastError = $"nvml.dll 无法加载（架构不匹配）：{ex.Message}";
            _retryAfter = DateTimeOffset.Now + _retryInterval;
            return false;
        }
        catch (Exception ex)
        {
            _lastError = DescribeException(ex);
            _retryAfter = DateTimeOffset.Now + _retryInterval;
            return false;
        }
    }

    private bool TryGetHandleLocked()
    {
        try
        {
            var result = NvmlNative.GetDeviceHandle(0, out _device);
            if (result != NvmlNative.Success)
            {
                _device = IntPtr.Zero;
                _lastError = $"获取 NVIDIA 设备句柄失败：{NvmlNative.Describe(result)}";
                return false;
            }

            _lastError = null;
            return true;
        }
        catch (Exception ex)
        {
            _device = IntPtr.Zero;
            _lastError = DescribeException(ex);
            return false;
        }
    }

    private static string DescribeException(Exception ex)
        => ex switch
        {
            DllNotFoundException => "未找到 nvml.dll（NVIDIA 管理库未安装）。",
            EntryPointNotFoundException => $"nvml.dll 缺少所需入口：{ex.Message}",
            BadImageFormatException => $"nvml.dll 无法加载（架构不匹配）：{ex.Message}",
            _ => $"{ex.GetType().Name}：{ex.Message}"
        };
}
