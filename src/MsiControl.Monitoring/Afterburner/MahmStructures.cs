using System.Runtime.InteropServices;

namespace MsiControl.Monitoring.Afterburner;

/// <summary>
/// MSI Afterburner 共享内存接口（MAHM）的监控源 ID。
/// </summary>
/// <remarks>
/// 取自 MSI Afterburner 官方 SDK 头文件 <c>MAHMSharedMemory.h</c>（Unwinder，guru3D）。
/// <b>注意</b>：这些 ID 只覆盖 GPU 侧量（温度/频率/显存/利用率/风扇/电压/帧率），
/// **不含 CPU 温度、CPU 频率、CPU 封装功耗、GPU 功耗**。
/// </remarks>
public static class MonitoringSourceIds
{
    /// <summary>未知。</summary>
    public const uint Unknown = 0xFFFFFFFF;

    /// <summary>GPU 核心温度。</summary>
    public const uint GpuTemperature = 0x00000000;

    /// <summary>PCB 温度。</summary>
    public const uint PcbTemperature = 0x00000001;

    /// <summary>风扇转速百分比。</summary>
    public const uint FanSpeed = 0x00000010;

    /// <summary>风扇转速（RPM 转速计）。</summary>
    public const uint FanTachometer = 0x00000011;

    /// <summary>核心频率。</summary>
    public const uint CoreClock = 0x00000020;

    /// <summary>着色器频率。</summary>
    public const uint ShaderClock = 0x00000021;

    /// <summary>显存频率。</summary>
    public const uint MemoryClock = 0x00000022;

    /// <summary>GPU 利用率。</summary>
    public const uint GpuUsage = 0x00000030;

    /// <summary>显存利用率。</summary>
    public const uint MemoryUsage = 0x00000031;

    /// <summary>显存控制器利用率（百分比；0x31 是 MB 绝对值，不要混用）。</summary>
    public const uint FbUsage = 0x00000032;

    /// <summary>GPU 电压。</summary>
    public const uint GpuVoltage = 0x00000040;

    /// <summary>辅助电压。</summary>
    public const uint AuxVoltage = 0x00000041;

    /// <summary>显存电压。</summary>
    public const uint MemoryVoltage = 0x00000042;

    /// <summary>帧率。</summary>
    public const uint Framerate = 0x00000050;

    /// <summary>GPU 绝对功耗（瓦）。</summary>
    public const uint GpuAbsolutePower = 0x00000061;

    /// <summary>GPU 温度上限。</summary>
    public const uint GpuTempLimit = 0x00000070;

    /// <summary>GPU 功耗上限。</summary>
    public const uint GpuPowerLimit = 0x00000071;

    /// <summary>CPU 温度。</summary>
    public const uint CpuTemperature = 0x00000080;

    /// <summary>CPU 利用率。</summary>
    public const uint CpuUsage = 0x00000090;

    /// <summary>内存占用。</summary>
    public const uint RamUsage = 0x00000091;

    /// <summary>CPU 频率。</summary>
    public const uint CpuClock = 0x000000A0;

    /// <summary>CPU 功耗（瓦）。</summary>
    public const uint CpuPower = 0x00000100;

    /// <summary>全局条目的 GPU 索引标记（CPU 汇总、帧率等非 GPU 专属条目）。</summary>
    public const uint GlobalGpuIndex = 0xFFFFFFFF;
}

/// <summary>
/// MAHM 条目标志位。
/// </summary>
internal static class MonitoringEntryFlags
{
    /// <summary>该数据源配置为在 OSD 显示。</summary>
    public const uint ShowInOsd = 0x00000001;

    /// <summary>该数据源配置为在键盘 LCD 显示。</summary>
    public const uint ShowInLcd = 0x00000002;

    /// <summary>该数据源配置为在托盘图标显示。</summary>
    public const uint ShowInTray = 0x00000004;
}

/// <summary>MAHM 头部标志位。</summary>
/// <remarks>
/// <b>本机 SDK 版本的头部没有 Flags 字段</b>（2026-10-17 实读核对），
/// 本类型仅保留旧版布局的常量供追溯，不参与解析。
/// </remarks>
[Obsolete("本机 SDK 头部布局无 Flags 字段，仅作历史追溯。")]
internal static class MonitoringHeaderFlags
{
    /// <summary>共享内存初始化完成。</summary>
    public const uint Initialized = 0x00000001;
}

/// <summary>
/// MAHM 共享内存头部（<c>MAHM_SHARED_MEMORY_HEADER</c>，按本机安装的官方 SDK 布局）。
/// </summary>
/// <remarks>
/// 布局依据：本机 <c>E:\Program Files(x86)\MSI Afterburner\SDK\Include\MAHMSharedMemory.h</c>
/// （2026-10-17 实读）。x64 下 <c>time</c> 为 4 字节 <c>__time32_t</c>，且该版本头部<b>无 Flags 字段</b>。
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct MahmHeader
{
    /// <summary>签名：<see cref="MahmLayout.SignatureValid"/> 表示数据有效。</summary>
    public uint Signature;

    /// <summary>版本（高 16 位主版本，低 16 位次版本；v2.0 为 0x00020000）。</summary>
    public uint Version;

    /// <summary>头部大小。</summary>
    public uint HeaderSize;

    /// <summary>监控条目数。</summary>
    public uint EntryCount;

    /// <summary>每个条目的字节数。</summary>
    public uint EntrySize;

    /// <summary>最近轮询时间（<c>__time32_t</c>，x64 下 4 字节）。</summary>
    public uint Time;

    /// <summary>GPU 条目数组大小。</summary>
    public uint GpuEntryCount;

    /// <summary>每个 GPU 条目的字节数。</summary>
    public uint GpuEntrySize;
}

/// <summary>
/// MAHM 监控条目（<c>MAHM_SHARED_MEMORY_ENTRY</c>，v2.0 布局）。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Ansi)]
internal struct MahmEntry
{
    /// <summary>数据源名（例如 "Core clock"）。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SourceName;

    /// <summary>数据源单位（例如 "MHz"）。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string SourceUnits;

    /// <summary>本地化数据源名。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string LocalizedSourceName;

    /// <summary>本地化单位。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string LocalizedSourceUnits;

    /// <summary>推荐输出格式（例如 "%.3f"）。</summary>
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
    public string RecommendedFormat;

    /// <summary>最近一次轮询值；不可用时为 <see cref="float.MaxValue"/>。</summary>
    public float Data;

    /// <summary>图形最小值。</summary>
    public float MinLimit;

    /// <summary>图形最大值。</summary>
    public float MaxLimit;

    /// <summary>标志位。</summary>
    public uint Flags;

    /// <summary>所属 GPU 索引（全局数据源为 0xFFFFFFFF）。</summary>
    public uint GpuIndex;

    /// <summary>数据源 ID（<see cref="MonitoringSourceIds"/>）。</summary>
    public uint SourceId;
}

/// <summary>
/// MAHM 的 Windows 文件映射对象名与布局常量。
/// </summary>
internal static class MahmLayout
{
    /// <summary>共享内存对象名。</summary>
    public const string MappingName = "MAHMSharedMemory";

    /// <summary>
    /// 数据有效签名：C 多字符常量 <c>'MAHM'</c>（0x4D41484D）。
    /// </summary>
    /// <remarks>
    /// <b>不是 0xDEAD</b>——本机 SDK 头文件明确 0xDEAD 表示「已标记释放、数据无效」。
    /// 内存字节序为小端：0x4D41484D 落盘为 4D 48 41 4D。
    /// </remarks>
    public const uint SignatureValid = 0x4D41484D;

    /// <summary>头版本 v2.0（<c>(major&lt;&lt;16) + minor</c>）。</summary>
    public const uint Version2 = 0x00020000;

    /// <summary><c>MAX_PATH</c>（结构体内定长字符串宽度）。</summary>
    public const int MaxPath = 260;
}
