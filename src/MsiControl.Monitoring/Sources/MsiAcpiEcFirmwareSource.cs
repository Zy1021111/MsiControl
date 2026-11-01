using System.Management;
using System.Text;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport;
using MsiControl.Monitoring.Internal;

namespace MsiControl.Monitoring.Sources;

/// <summary>
/// 一次 EC 固件响应读取的结果。
/// </summary>
/// <param name="RawAscii">从响应字节中提取的可打印文本。未读到为 <see langword="null"/>。</param>
/// <param name="RawHex">响应字节的十六进制表示，供逐字节追溯。</param>
/// <param name="ReadAt">读取时刻。</param>
/// <param name="Source">数据来源说明。</param>
/// <param name="Error">失败原因；成功为 <see langword="null"/>。</param>
/// <remarks>
/// <b>刻意不做字段拆分：</b>本机 <c>Get_EC</c> 返回的 32 字节包中，文本部分**没有分隔符**
/// （实测为 <c>15M3EMS1.1131126202514:59:28</c>）。在取得额外证据前，把「版本」与「构建时间」
/// 切开只能靠猜，因此这里只提供原始文本与原始字节，交由具备完整证据的场景再解析。
/// </remarks>
public sealed record EcFirmwareReading(
    string? RawAscii,
    string? RawHex,
    DateTimeOffset? ReadAt,
    string Source,
    string? Error)
{
    /// <summary>是否读到内容。</summary>
    public bool IsAvailable => RawAscii is not null;

    /// <summary>构造一个不可用结果。</summary>
    public static EcFirmwareReading Unavailable(string source, string error)
        => new(null, null, null, source, error);
}

/// <summary>
/// 通过 MSI_ACPI 只读通道读取 EC 固件响应。
/// </summary>
/// <remarks>
/// <para>
/// 调用 <c>MSI_ACPI.Get_EC</c>——该方法在证据清单 §2.6 中被登记为 <c>out</c> 参数方法，
/// 实测无需输入即可调用并返回 <c>Package_32</c>（其 <c>Bytes</c> 为 <c>UInt8Array</c>）。
/// 读取通道由任务 01 的 <c>IMsiAcpiTransport</c> 提供，且其白名单只放行 <c>Get_*</c>，
/// 因此本数据源**不可能**触发写入。
/// </para>
/// <para>
/// <b>只读一次：</b>EC 固件响应在运行期不会变化，首次读到后即缓存，不再重复调用。
/// 这既是「扩展信息按需读取」的落实，也避免每秒多余地触碰 ACPI 通道。
/// </para>
/// <para>
/// <b>不解释数值：</b>返回内容原样保留（文本 + 十六进制），不猜测其字段含义，也不据此升级任何能力等级。
/// </para>
/// </remarks>
public sealed class MsiAcpiEcFirmwareSource
{
    /// <summary>调用的方法名。</summary>
    public const string MethodName = "Get_EC";

    private readonly IMsiAcpiTransport _transport;
    private readonly object _gate = new();
    private EcFirmwareReading? _cached;

    /// <summary>构造数据源。</summary>
    /// <param name="transport">MSI_ACPI 只读调用通道。</param>
    public MsiAcpiEcFirmwareSource(IMsiAcpiTransport transport)
        => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <summary>数据来源说明。</summary>
    public string Description => $"MSI_ACPI.{MethodName}";

    /// <summary>读取 EC 固件响应（首次成功后不再重复调用）。</summary>
    public async Task<EcFirmwareReading> ReadAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cached is { IsAvailable: true })
            {
                return _cached;
            }
        }

        var result = await _transport.InvokeReadAsync(MethodName, null, cancellationToken).ConfigureAwait(false);

        EcFirmwareReading reading;
        if (result.Data is null)
        {
            reading = EcFirmwareReading.Unavailable(
                Description,
                result.Error ?? "MSI_ACPI.Get_EC 未返回任何数据。");
        }
        else if (!TryExtractBytes(result.Data, out var bytes) || bytes.Length == 0)
        {
            reading = EcFirmwareReading.Unavailable(
                Description,
                $"返回值的类型为 {result.Data.GetType().Name}，未能取出字节内容。");
        }
        else
        {
            reading = new EcFirmwareReading(
                ExtractPrintableText(bytes),
                Convert.ToHexString(bytes),
                DateTimeOffset.Now,
                Description,
                result.Error);
        }

        lock (_gate)
        {
            if (reading.IsAvailable)
            {
                _cached = reading;
            }
        }

        return reading;
    }

    /// <summary>
    /// 从 <c>Data</c> 返回对象中取出字节。
    /// </summary>
    /// <remarks>
    /// MOF 中 <c>Data</c> 为 CIM 类型 <c>Object</c>（VARIANT）。实测本机 provider 返回的是
    /// 嵌入式实例 <c>Package_32</c>，其 <c>Bytes</c> 属性为 <c>UInt8Array</c>；
    /// 但不同 provider 版本可能直接返回字节数组，因此两种形态都接受，不做类型断言。
    /// </remarks>
    public static bool TryExtractBytes(object? data, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();

        switch (data)
        {
            case byte[] raw:
                bytes = raw;
                return true;

            case ManagementBaseObject management:
                if (WmiRead.PropertyValue(management, "Bytes") is byte[] inner)
                {
                    bytes = inner;
                    return true;
                }

                return false;

            case Array array:
            {
                var converted = new List<byte>(array.Length);
                foreach (var item in array)
                {
                    if (item is null)
                    {
                        return false;
                    }

                    converted.Add(Convert.ToByte(item, System.Globalization.CultureInfo.InvariantCulture));
                }

                bytes = converted.ToArray();
                return true;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// 从响应字节中提取可打印文本：跳过前导不可打印字节，遇到第一个不可打印字节即结束。
    /// </summary>
    /// <remarks>
    /// 该规则刻意保持「只做提取、不做解释」：不假定哪个位置是版本、哪个位置是时间戳。
    /// </remarks>
    public static string? ExtractPrintableText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var builder = new StringBuilder(bytes.Length);
        var started = false;

        foreach (var b in bytes)
        {
            var printable = b is >= 0x20 and < 0x7F;
            if (!printable)
            {
                if (started)
                {
                    break;
                }

                continue;
            }

            started = true;
            builder.Append((char)b);
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
