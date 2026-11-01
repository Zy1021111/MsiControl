using Microsoft.Win32;
using MsiControl.Core.Models;

namespace MsiControl.Application.Integration;

/// <summary>设备标识读取的结果。</summary>
/// <param name="Device">设备标识。无法读取时字段保持未标识状态。</param>
/// <param name="Notes">读取过程中的说明：哪一项缺失、为什么缺失。</param>
public sealed record DeviceInfoResult(DeviceIdentity Device, IReadOnlyList<string> Notes);

/// <summary>
/// 本机设备标识来源。
/// </summary>
/// <remarks>
/// 抽象出来是为了两件事：① 让启动装配在无注册表访问权限时仍能给出诚实的「未识别」结果；
/// ② 让标识规范化逻辑可被单元测试覆盖，而不必依赖具体机器的注册表内容。
/// </remarks>
public interface IDeviceInfoSource
{
    /// <summary>读取本机设备标识。不抛异常，失败以「未识别 + 说明」表达。</summary>
    DeviceInfoResult Read();
}

/// <summary>SMBIOS 信息的逻辑键。</summary>
public static class BiosInfoKeys
{
    /// <summary>主板型号，例如 <c>MS-15M3</c>。这是能力判定的唯一主键。</summary>
    public const string BaseBoardProduct = "BaseBoardProduct";

    /// <summary>整机型号，例如 <c>Vector 16 HX AI A2XWHG</c>。</summary>
    public const string SystemProductName = "SystemProductName";

    /// <summary>BIOS 版本，例如 <c>E15M3IMS.117</c>。</summary>
    public const string BiosVersion = "BIOSVersion";

    /// <summary>BIOS 发布日期的原始字符串。</summary>
    public const string BiosReleaseDate = "BIOSReleaseDate";

    /// <summary>CPU 型号字符串。</summary>
    public const string ProcessorName = "ProcessorName";
}

/// <summary>
/// SMBIOS 只读读取器。
/// </summary>
/// <remarks>
/// <b>实现方不得读取或保存设备序列号</b>（任务书 §4、§22）。
/// 本接口只需主板型号、整机型号、BIOS 版本与 CPU 型号。
/// </remarks>
public interface IBiosInfoReader
{
    /// <summary>读取 SMBIOS 逻辑键值。缺失的项可省略或给 <see langword="null"/>。</summary>
    /// <returns>键值对。读取失败时返回空字典，由调用方给出说明。</returns>
    IReadOnlyDictionary<string, string?> Read();
}

/// <summary>
/// 从注册表读取 SMBIOS 信息。
/// </summary>
/// <remarks>
/// <para>
/// 数据来源：<c>HKLM\HARDWARE\DESCRIPTION\System\BIOS</c> 与
/// <c>HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0</c>。
/// </para>
/// <para>
/// <b>为什么不走 WMI：</b>项目约定 <c>MsiControl.Hardware.Msi</c> 是唯一允许接触
/// WMI / ACPI / EC 的工程（docs/接口与工程约定.md §5）。设备标识属于操作系统已解析好的
/// SMBIOS 快照，读注册表与厂商接口无关，因此可以放在应用层。
/// 该路径为只读，不需要管理员权限。
/// </para>
/// <para>
/// <b>明确不读取：</b>序列号、UUID、Windows 产品 ID 等可识别个人的字段。
/// </para>
/// </remarks>
public sealed class WindowsRegistryBiosInfoReader : IBiosInfoReader
{
    private const string BiosKeyPath = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string CpuKeyPath = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string?> Read()
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        ReadBios(values);
        ReadCpu(values);
        return values;
    }

    private static void ReadBios(IDictionary<string, string?> values)
    {
        using var key = OpenReadOnly(BiosKeyPath);
        if (key is null)
        {
            return;
        }

        values[BiosInfoKeys.BaseBoardProduct] = AsString(key.GetValue("BaseBoardProduct"));
        values[BiosInfoKeys.SystemProductName] = AsString(key.GetValue("SystemProductName"));
        values[BiosInfoKeys.BiosVersion] = AsString(key.GetValue("BIOSVersion"));
        values[BiosInfoKeys.BiosReleaseDate] = AsString(key.GetValue("BIOSReleaseDate"));
    }

    private static void ReadCpu(IDictionary<string, string?> values)
    {
        using var cpuKey = OpenReadOnly(CpuKeyPath);
        if (cpuKey is null)
        {
            return;
        }

        values[BiosInfoKeys.ProcessorName] = AsString(cpuKey.GetValue("ProcessorNameString"));
    }

    private static RegistryKey? OpenReadOnly(string path)
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(path, writable: false);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException
                                       or UnauthorizedAccessException
                                       or IOException)
        {
            // 读不到不是致命错误：调用方会给出「未识别」结论并说明原因。
            return null;
        }
    }

    private static string? AsString(object? value)
    {
        var text = value as string;
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

/// <summary>
/// 基于 SMBIOS 的设备标识来源。
/// </summary>
/// <remarks>
/// <para>
/// 规范化规则（可单元测试）：
/// </para>
/// <list type="bullet">
/// <item>主板码取 <c>BaseBoardProduct</c>，去掉 <c>REV:x.y</c> 之类的修订后缀
///       —— <see cref="DeviceIdentity.IsTargetDevice"/> 按主板码精确比较，
///       带修订号会导致目标设备被判为「未适配」。</item>
/// <item>读不到主板码时使用 <see cref="DeviceIdentity.UnknownBoardCode"/>，
///       该值**不构成有效标识**（<see cref="DeviceIdentity.IsIdentified"/> 为假），
///       从而让全部写入入口保持禁用——这是默认安全方向。</item>
/// <item>EC 版本保持 <see langword="null"/>：SMBIOS 返回的 255/255 无效，
///       有效值须由 <c>MSI_ACPI.Get_EC</c> 读取（尚未验证）。</item>
/// <item>独显/核显型号不由本类型猜测：需要 WMI/驱动枚举，属硬件适配层。</item>
/// </list>
/// </remarks>
public sealed class RegistryDeviceInfoSource : IDeviceInfoSource
{
    private readonly IBiosInfoReader _reader;

    /// <summary>构造来源。</summary>
    /// <param name="reader">SMBIOS 读取器。为空时使用注册表实现。</param>
    public RegistryDeviceInfoSource(IBiosInfoReader? reader = null)
        => _reader = reader ?? new WindowsRegistryBiosInfoReader();

    /// <inheritdoc />
    public DeviceInfoResult Read()
    {
        IReadOnlyDictionary<string, string?> values;
        try
        {
            values = _reader.Read();
        }
        catch (Exception ex)
        {
            return new DeviceInfoResult(
                DeviceIdentity.Unknown,
                new[] { $"读取 SMBIOS 信息失败：{ex.Message}；本机标识按「未识别」处理，全部写入入口保持禁用。" });
        }

        var notes = new List<string>();

        if (values.Count == 0)
        {
            return new DeviceInfoResult(
                DeviceIdentity.Unknown,
                new[] { "未能从注册表取得 SMBIOS 信息；本机标识按「未识别」处理，全部写入入口保持禁用。" });
        }

        var boardCode = NormalizeBoardCode(Get(values, BiosInfoKeys.BaseBoardProduct));
        var model = Get(values, BiosInfoKeys.SystemProductName);

        if (string.IsNullOrWhiteSpace(boardCode))
        {
            notes.Add($"未能读取主板型号（{BiosInfoKeys.BaseBoardProduct} 缺失）；本机标识按「未识别」处理。");
            boardCode = DeviceIdentity.UnknownBoardCode;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            notes.Add($"未能读取整机型号（{BiosInfoKeys.SystemProductName} 缺失），已回退为主板码。");
            model = boardCode;
        }

        var biosVersion = Get(values, BiosInfoKeys.BiosVersion);
        if (string.IsNullOrWhiteSpace(biosVersion))
        {
            notes.Add("未能读取 BIOS 版本。");
        }

        var cpuName = Get(values, BiosInfoKeys.ProcessorName);
        if (string.IsNullOrWhiteSpace(cpuName))
        {
            notes.Add("未能读取 CPU 型号。");
        }

        notes.Add("EC 版本保持未知：SMBIOS 的 255/255 不构成有效版本，须由 Get_EC 读取（尚未验证）。");
        notes.Add("独显/核显型号未由本来源推断：需要硬件适配层枚举，避免用线缆或型号名猜测拓扑。");

        var device = new DeviceIdentity(
            Model: model,
            BoardCode: boardCode,
            CpuName: cpuName,
            DiscreteGpuName: null,
            IntegratedGpuName: null,
            BiosVersion: biosVersion,
            EcVersion: null,
            WindowsBuild: Environment.OSVersion.Version.Build.ToString(System.Globalization.CultureInfo.InvariantCulture),
            GpuDriverVersion: null);

        return new DeviceInfoResult(device, notes);
    }

    /// <summary>
    /// 规范化主板码：去空白，去掉 <c>REV</c> 修订后缀。
    /// </summary>
    /// <remarks>
    /// 例：<c>"MS-15M3 REV:1.0"</c> → <c>"MS-15M3"</c>。
    /// 任务书 §4 记录的主板为「MS-15M3 REV:1.0」，而能力基线按 <c>MS-15M3</c> 判定，
    /// 不做归一化会让目标设备被误判为未适配。
    /// </remarks>
    public static string? NormalizeBoardCode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var text = raw.Trim();

        // 取 "REV" 之前的部分（大小写不敏感），并去掉尾部分隔符。
        var index = text.IndexOf("REV", StringComparison.OrdinalIgnoreCase);
        if (index > 0)
        {
            text = text[..index];
        }

        return text.Trim().TrimEnd('-', '_', ':', ';', ',');
    }

    private static string? Get(IReadOnlyDictionary<string, string?> values, string key)
        => values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
