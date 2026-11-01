using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport;
using MsiControl.Monitoring.Afterburner;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using MsiControl.Monitoring.Nvml;
using MsiControl.Monitoring.Pdh;
using MsiControl.Monitoring.Sources;

namespace MsiControl.Monitoring;

/// <summary>
/// 按本机实际可用的数据通路装配监控数据源。
/// </summary>
/// <remarks>
/// <para>
/// 装配内容是**证据驱动的固定选择**，不是「猜测式探测」：
/// </para>
/// <list type="bullet">
/// <item>CPU 温度：通用 ACPI 热区（<c>thermal.fallback</c>，已确证返回数据）。</item>
/// <item>CPU 频率 / 利用率：Windows 处理器性能计数器。</item>
/// <item>独显：NVML 进程内读取，并以 PnP 在线性区分「休眠」与「读取失败」。</item>
/// <item>电池侧功率：<c>root\WMI:BatteryStatus</c>（只作扩展传感器与 BatterySide 口径）。</item>
/// <item>EC 固件响应：<c>MSI_ACPI.Get_EC</c>（只读通道，一次）。</item>
/// </list>
/// <para>
/// <b>刻意不接入的路径：</b><c>MSI_ACPI.Get_Temperature</c> / <c>Get_Thermal</c> / <c>Get_Fan</c> /
/// <c>Get_Power</c> 的 <c>Data</c> 参数是嵌入式 <c>Package_32</c> 实例，
/// 其命令字节编码**尚未验证**（证据清单 §2.6 / §11）。在这些编码取得证据之前，
/// 把它们放进每秒一次的采样路径只会产生无意义的调用与错误，
/// 因此本机 CPU/GPU 精确温度与风扇 RPM 一律如实返回「不可用」，并说明原因。
/// </para>
/// <para>
/// <b>整机功耗</b>：按 <see cref="DeviceCapabilities"/> 门控，<c>power.system</c> 未达可读等级时恒为
/// <see cref="SystemPowerReading.Unavailable(string)"/>，绝不用 CPU+GPU 功耗之和顶替。
/// </para>
/// </remarks>
public static class MonitoringProviderFactory
{
    /// <summary>CPU 封装功耗完全不可用的原因说明（RAPL 与 Afterburner 两条路都不通时）。</summary>
    public const string CpuPowerUnavailableReason =
        "本机 CPU 封装功耗无可用通道：Intel RAPL 计数器（MSR 0x611）不可读，"
        + "且 MSI Afterburner 共享内存未提供 CPU power 项。";

    /// <summary>风扇转速不可用的原因说明。</summary>
    public const string FanUnavailableReason =
        "本机无已验证的风扇转速读取路径：MSI_ACPI.Get_Fan 的 Data 参数（Package_32）编码未验证；也未完成有效校准，故不提供百分比。";

    /// <summary>整机功耗不可用的原因说明。</summary>
    public const string SystemPowerUnavailableReason =
        "本机无可靠的整机功耗来源：适配器输入（AdapterInput）与插座（WallSocket）均不可得；" +
        "电池侧（BatterySide）仅在拔电放电时有读数。按口径纪律，三者不可混用、不可相加，" +
        "更不得用 CPU 功耗与 GPU 功耗之和顶替整机功耗。";

    /// <summary>
    /// 装配本机默认监控数据源。
    /// </summary>
    /// <param name="capabilities">设备能力集合，用于能力门控。</param>
    /// <param name="options">运行参数。为 <see langword="null"/> 时使用默认值。</param>
    /// <param name="transport">
    /// MSI_ACPI 只读通道。为 <see langword="null"/> 时创建默认实现（<c>root\WMI:MSI_ACPI</c>）。
    /// 通道白名单只放行 <c>Get_*</c>，因此监控模块不可能写入硬件。
    /// </param>
    /// <param name="nvmlSession">NVML 会话。为 <see langword="null"/> 时新建，并由返回的实例负责释放。</param>
    /// <param name="preferAfterburner">
    /// 是否让 MSI Afterburner 共享内存成为 GPU 四项的**主来源**（取代 NVML）。
    /// 为 <see langword="true"/> 时 GPU 温度/核心频率/风扇优先读 Afterburner；但 GPU 功耗仍不可用
    /// （Afterburner 不通过 MAHM 暴露功耗），扩展项「GPU 功耗限制/降频阈值」也不再提供。
    /// 为 <see langword="false"/>（默认）时保持 NVML 为主，Afterburner 不接入。
    /// </param>
    /// <returns>可直接注入桌面壳的监控数据源。</returns>
    public static MonitoringProvider CreateDefault(
        DeviceCapabilities capabilities,
        MonitoringOptions? options = null,
        IMsiAcpiTransport? transport = null,
        NvmlSession? nvmlSession = null,
        bool preferAfterburner = false,
        PawnIoEcRegisterAccess? ecRegisterAccess = null)
    {
        ArgumentNullException.ThrowIfNull(capabilities);

        var effectiveOptions = options ?? MonitoringOptions.Default;
        var sampler = new PdhProcessorSampler();
        var gpuSession = nvmlSession ?? new NvmlSession();

        // ── EC（PawnIO）：风扇占空比与真实 CPU/GPU 温度 ─────────────────
        // 优先复用宿主传入的共享访问器（进程内 EC 通道归一：监控与风扇硬件
        // 共用一条通道、一把访问锁，避免多通道并发端口 IO 事务穿插）；
        // 未提供时自建（写白名单空集，只可能被读取）。
        var ecOwned = ecRegisterAccess is null;
        var ecAccess = ecRegisterAccess ?? new PawnIoEcRegisterAccess();
        var ecFanSource = new PawnIoEcFanSource(ecAccess);
        var ecTemperatureSource = new PawnIoEcTemperatureSource(ecAccess);

        // ── 独显：NVML 与 Afterburner 双源回落链 ────────────────────────
        // 默认 NVML 优先（原生、无需第三方常驻、数据最全），Afterburner 兜底；
        // preferAfterburner = true 时反向。Afterburner 未安装时自动落到 NVML，界面不受影响。
        var nvmlSource = new NvmlGpuSource(
            gpuSession,
            new GpuPresenceProbe(),
            effectiveOptions.GpuPresenceProbeInterval);
        var afterburnerSource = new AfterburnerGpuSource(new MahmReader());

        var gpuChain = preferAfterburner
            ? new CompositeGpuSource(new IGpuSource[] { afterburnerSource, nvmlSource })
            : new CompositeGpuSource(new IGpuSource[] { nvmlSource, afterburnerSource });
        IGpuSource gpuSource = gpuChain;
        IExtendedSensorSource gpuExtended = gpuChain;

        var batterySource = new BatteryStatusPowerSource();

        // ── Afterburner CPU 源：封装功耗（0x100）与温度（0x80）──────────
        // MAHM 新版数据源（本机 SDK 实读核对）补上 CPU 封装功耗这一缺口。
        var afterburnerCpuSource = new AfterburnerCpuSource(new MahmReader());

        // ── CPU 封装功耗：Intel RAPL 优先，Afterburner 兜底 ─────────────
        // RAPL（MSR 0x611）是平台级硬件计数器，不依赖任何第三方常驻程序，
        // 换算为功率后即「封装功耗」本身；Afterburner 只在 RAPL 不可用时兜底。
        var msrAccess = new IntelMsrAccess();
        var cpuPowerSource = new CompositeCpuPowerSource(
            new RaplCpuPowerSource(new PawnIoRaplEnergyReader(msrAccess)),
            afterburnerCpuSource);

        // CPU 温度：EC 实时温度（真实 CPU 温度）优先，Afterburner 次之，ACPI 热区兜底。
        var cpuTemperatureSources = effectiveOptions.UseThermalZoneFallback
            ? new ICpuTemperatureSource[]
              {
                  ecTemperatureSource,
                  afterburnerCpuSource,
                  new AcpiThermalZoneCpuTemperatureSource()
              }
            : new ICpuTemperatureSource[] { ecTemperatureSource, afterburnerCpuSource };

        var extendedSources = new IExtendedSensorSource[]
        {
            new ProcessorUtilizationSource(sampler),
            new MemoryUsageSource(),
            new SsdTemperatureSource(),
            ecTemperatureSource,
            batterySource,
            gpuExtended
        };

        var ecSource = new MsiAcpiEcFirmwareSource(transport ?? new WmiMsiAcpiTransport());

        // 由本工厂创建的资源统一交给 provider 释放。
        var owned = new List<IDisposable>();
        if (nvmlSession is null)
        {
            owned.Add(gpuSession);
        }

        if (ecOwned)
        {
            owned.Add(ecAccess);
        }

        // MSR 通道同样由工厂持有：监控停止时随 provider 一并释放设备句柄。
        owned.Add(msrAccess);

        return new MonitoringProvider(
            capabilities,
            effectiveOptions,
            cpuTemperatureSources,
            new ProcessorFrequencySource(sampler),
            cpuPowerSource: cpuPowerSource,
            fanSpeedSource: ecFanSource,
            gpuSource: gpuSource,
            systemPowerSources: new[] { batterySource },
            extendedSources: extendedSources,
            ecFirmwareSource: ecSource,
            ownedDisposables: owned);
    }
}
