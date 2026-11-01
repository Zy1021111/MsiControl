using System.Runtime.InteropServices;

namespace MsiControl.Monitoring.Nvml;

/// <summary>NVML 利用率结构的本机布局。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlUtilization
{
    /// <summary>GPU（SM）利用率，百分比。</summary>
    public uint Gpu;

    /// <summary>显存控制器利用率，百分比。</summary>
    public uint Memory;
}

/// <summary>NVML 显存信息结构（<c>nvmlMemory_t</c>，v1 布局）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NvmlMemory
{
    /// <summary>总显存，字节。</summary>
    public ulong Total;

    /// <summary>空闲显存，字节。</summary>
    public ulong Free;

    /// <summary>已用显存，字节。</summary>
    public ulong Used;
}

/// <summary>
/// NVML（NVIDIA Management Library）原生入口。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 NVML 而不是外部命令：</b>任务书 §13.4 禁止周期性创建外部命令进程读传感器。
/// <c>nvml.dll</c> 由 NVIDIA 驱动安装到 <c>%WINDIR%\System32</c>，可**进程内**直接调用，
/// 且 NVML 的读取不会把独显从低功耗状态唤醒（不像 D3D/OpenGL 上下文）。
/// </para>
/// <para>
/// <b>动态加载：</b>本机未安装 NVIDIA 驱动时 <c>nvml.dll</c> 不存在，
/// 调用会抛 <see cref="DllNotFoundException"/>。所有公开入口都由
/// <see cref="NvmlSession"/> 包裹并按错误码降级，绝不把异常抛给采样循环。
/// </para>
/// <para>
/// <b>不使用的入口：</b>不调用任何设置类函数（如 <c>nvmlDeviceSetPowerManagementLimit</c>）。
/// 本类型只包含读取所需的最小函数集（任务书 §2：监控模块只读）。
/// </para>
/// </remarks>
internal static class NvmlNative
{
    /// <summary>动态库名。由 Windows 在 System32 中解析。</summary>
    public const string Library = "nvml.dll";

    // ── 返回码（nvmlReturn_t 的相关取值）────────────────────────────
    public const int Success = 0;
    public const int ErrorUninitialized = 1;
    public const int ErrorInvalidArgument = 2;
    public const int ErrorNotSupported = 3;
    public const int ErrorNoPermission = 4;
    public const int ErrorAlreadyInitialized = 5;
    public const int ErrorNotFound = 6;
    public const int ErrorInsufficientSize = 7;
    public const int ErrorDriverNotLoaded = 9;
    public const int ErrorTimeout = 10;
    public const int ErrorLibraryNotFound = 12;
    public const int ErrorFunctionNotFound = 13;
    public const int ErrorGpuIsLost = 15;
    public const int ErrorUnknown = 999;

    // ── 传感器与时钟类型 ───────────────────────────────────────────
    /// <summary><c>NVML_TEMPERATURE_GPU</c>。</summary>
    public const uint TemperatureGpu = 0;

    /// <summary><c>NVML_CLOCK_GRAPHICS</c>（核心频率）。</summary>
    public const uint ClockGraphics = 0;

    /// <summary><c>NVML_CLOCK_MEM</c>（显存频率）。</summary>
    public const uint ClockMemory = 2;

    /// <summary><c>NVML_TEMPERATURE_THRESHOLD_SLOWDOWN</c>（温度导致降频的阈值）。</summary>
    public const uint TemperatureThresholdSlowdown = 1;

    [DllImport(Library, EntryPoint = "nvmlInit_v2", ExactSpelling = true)]
    public static extern int Init();

    [DllImport(Library, EntryPoint = "nvmlShutdown", ExactSpelling = true)]
    public static extern int Shutdown();

    [DllImport(Library, EntryPoint = "nvmlDeviceGetCount_v2", ExactSpelling = true)]
    public static extern int GetDeviceCount(out uint count);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetHandleByIndex_v2", ExactSpelling = true)]
    public static extern int GetDeviceHandle(uint index, out IntPtr device);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetName", ExactSpelling = true)]
    public static extern int GetDeviceName(IntPtr device, byte[] name, uint length);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperature", ExactSpelling = true)]
    public static extern int GetTemperature(IntPtr device, uint sensorType, out uint celsius);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetClockInfo", ExactSpelling = true)]
    public static extern int GetClockInfo(IntPtr device, uint clockType, out uint megahertz);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetPowerUsage", ExactSpelling = true)]
    public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetFanSpeed", ExactSpelling = true)]
    public static extern int GetFanSpeed(IntPtr device, out uint percent);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetUtilizationRates", ExactSpelling = true)]
    public static extern int GetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetMemoryInfo", ExactSpelling = true)]
    public static extern int GetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetPowerManagementLimit", ExactSpelling = true)]
    public static extern int GetPowerManagementLimit(IntPtr device, out uint milliwatts);

    [DllImport(Library, EntryPoint = "nvmlDeviceGetTemperatureThreshold", ExactSpelling = true)]
    public static extern int GetTemperatureThreshold(IntPtr device, uint thresholdType, out uint celsius);

    [DllImport(Library, EntryPoint = "nvmlErrorString", ExactSpelling = true)]
    public static extern IntPtr ErrorString(int result);

    /// <summary>把返回码转成可读文本（含 NVML 官方字符串）。</summary>
    public static string Describe(int result)
    {
        var name = result switch
        {
            Success => "NVML_SUCCESS",
            ErrorUninitialized => "NVML_ERROR_UNINITIALIZED",
            ErrorInvalidArgument => "NVML_ERROR_INVALID_ARGUMENT",
            ErrorNotSupported => "NVML_ERROR_NOT_SUPPORTED",
            ErrorNoPermission => "NVML_ERROR_NO_PERMISSION",
            ErrorAlreadyInitialized => "NVML_ERROR_ALREADY_INITIALIZED",
            ErrorNotFound => "NVML_ERROR_NOT_FOUND",
            ErrorInsufficientSize => "NVML_ERROR_INSUFFICIENT_SIZE",
            ErrorDriverNotLoaded => "NVML_ERROR_DRIVER_NOT_LOADED",
            ErrorTimeout => "NVML_ERROR_TIMEOUT",
            ErrorLibraryNotFound => "NVML_ERROR_LIBRARY_NOT_FOUND",
            ErrorFunctionNotFound => "NVML_ERROR_FUNCTION_NOT_FOUND",
            ErrorGpuIsLost => "NVML_ERROR_GPU_IS_LOST",
            _ => "NVML_ERROR_UNKNOWN"
        };

        var text = string.Empty;
        try
        {
            var pointer = ErrorString(result);
            if (pointer != IntPtr.Zero)
            {
                text = Marshal.PtrToStringAnsi(pointer) ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // 官方字符串取不到不影响错误码本身的表达。
        }

        return string.IsNullOrEmpty(text)
            ? $"{name}（{result}）"
            : $"{name}（{result}）：{text}";
    }
}
