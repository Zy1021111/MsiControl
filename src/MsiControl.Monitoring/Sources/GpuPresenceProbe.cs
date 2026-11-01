using MsiControl.Monitoring.Internal;

namespace MsiControl.Monitoring.Sources;

/// <summary>独显在线性探测结果。</summary>
/// <param name="Found">是否枚举到 NVIDIA 显示设备。</param>
/// <param name="IsSleeping">是否按「已断电/休眠」处理。</param>
/// <param name="DeviceName">设备名（若可读）。</param>
/// <param name="Availability">WMI <c>Availability</c> 原始值（若可读）。</param>
/// <param name="Reason">判定依据说明，会写入读数详情供追溯。</param>
public sealed record GpuPresence(
    bool Found,
    bool IsSleeping,
    string? DeviceName,
    int? Availability,
    string Reason);

/// <summary>
/// 独显在线性探测：区分「独显休眠」与「独显存在但读取失败」。
/// </summary>
/// <remarks>
/// <para>
/// 任务书 §6.4 / 提示词第 7 条要求：独显不可读时返回 <c>Sleeping</c>，不要返回 <c>Unavailable</c>，
/// 因为「休眠」与「失败」是两种不同状态。要区分二者，必须有一个**独立于读数本身**的在线性依据。
/// </para>
/// <para>
/// 本探测枚举 <c>Win32_VideoController</c> 中 <c>PNPDeviceID</c> 含 <c>VEN_10DE</c>（NVIDIA）的适配器：
/// </para>
/// <list type="bullet">
/// <item>枚举不到 → 独显未在系统中枚举出来，按**已断电（休眠）**处理。</item>
/// <item>枚举到但 <c>Availability</c> 为「Power Off / Off Line / Power Save 系列」→ 按**休眠**处理。</item>
/// <item>枚举到且可用 → 由 NVML 读数决定结果；NVML 失败一律按**不可用（失败）**处理。</item>
/// </list>
/// <para>
/// <b>为什么不用 NVML 的错误码判休眠：</b>NVML 的 <c>DRIVER_NOT_LOADED</c>、<c>GPU_IS_LOST</c>
/// 等错误既可能出现在断电场景，也可能出现在驱动异常场景，无法单凭它区分两者。
/// 因此休眠判定只依据 PnP 在线性与电源状态，NVML 错误只表达「读不到」。
/// </para>
/// </remarks>
public sealed class GpuPresenceProbe
{
    /// <summary>NVIDIA 的 PCI 厂商 ID 片段。</summary>
    public const string NvidiaVendorMarker = "VEN_10DE";

    /// <summary>CIM <c>Availability</c>：Power Off。</summary>
    public const int AvailabilityPowerOff = 7;

    /// <summary>CIM <c>Availability</c>：Off Line。</summary>
    public const int AvailabilityOffLine = 8;

    /// <summary>CIM <c>Availability</c>：Power Save - Unknown。</summary>
    public const int AvailabilityPowerSaveUnknown = 13;

    /// <summary>CIM <c>Availability</c>：Power Save - Low Power Mode。</summary>
    public const int AvailabilityPowerSaveLowPower = 14;

    /// <summary>CIM <c>Availability</c>：Power Save - Standby。</summary>
    public const int AvailabilityPowerSaveStandby = 15;

    /// <summary>CIM <c>Availability</c>：Power Cycle。</summary>
    public const int AvailabilityPowerCycle = 16;

    /// <summary>CIM <c>Availability</c>：Power Save - Warning。</summary>
    public const int AvailabilityPowerSaveWarning = 17;

    private readonly string _namespacePath;

    /// <summary>构造探测。</summary>
    /// <param name="namespacePath">WMI 命名空间，默认 <c>root\cimv2</c>。</param>
    public GpuPresenceProbe(string namespacePath = WmiRead.CimV2Namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespacePath);
        _namespacePath = namespacePath;
    }

    /// <summary>执行一次探测。</summary>
    public GpuPresence Probe()
    {
        string? name = null;
        int? availability = null;
        var found = false;
        string? error = null;

        WmiRead.Query(
            _namespacePath,
            "SELECT Name, PNPDeviceID, Status, Availability FROM Win32_VideoController",
            item =>
            {
                var pnpId = WmiRead.String(item, "PNPDeviceID");
                if (pnpId is null || !pnpId.Contains(NvidiaVendorMarker, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                found = true;
                name = WmiRead.String(item, "Name");
                availability = (int?)WmiRead.Integer(item, "Availability");
            },
            out error);

        if (error is not null)
        {
            // 探测本身失败：不能声称「休眠」，那会把查询故障伪装成设备状态。
            return new GpuPresence(
                false,
                false,
                null,
                null,
                $"独显在线性探测失败，无法判定休眠：{error}");
        }

        if (!found)
        {
            return new GpuPresence(
                false,
                true,
                null,
                null,
                $"未枚举到 NVIDIA 显示设备（PNPDeviceID 含 {NvidiaVendorMarker}），按已断电（休眠）处理。");
        }

        if (availability is { } value && IsPowerSaveAvailability(value))
        {
            return new GpuPresence(
                true,
                true,
                name,
                value,
                $"{name ?? "NVIDIA 显示设备"} 的 Availability={value}（电源关闭/省电状态），按休眠处理。");
        }

        return new GpuPresence(
            true,
            false,
            name,
            availability,
            $"{name ?? "NVIDIA 显示设备"} 正常枚举（Availability={availability?.ToString() ?? "未知"}）。");
    }

    /// <summary>判断 <c>Availability</c> 是否表示未处于全功率运行状态。</summary>
    public static bool IsPowerSaveAvailability(int availability) => availability switch
    {
        AvailabilityPowerOff
            or AvailabilityOffLine
            or AvailabilityPowerSaveUnknown
            or AvailabilityPowerSaveLowPower
            or AvailabilityPowerSaveStandby
            or AvailabilityPowerCycle
            or AvailabilityPowerSaveWarning => true,
        _ => false
    };
}
