namespace MsiControl.Core.Models;

/// <summary>
/// 目标设备标识。用于配置适用性校验与证据记录。
/// </summary>
/// <remarks>
/// 未知字段一律使用 <see langword="null"/>，<b>不得用占位字符串冒充已知</b>。
/// 例如本机 EC 版本尚未读到，必须保持 <see langword="null"/>（任务书 §4）。
/// 不收集、不保存设备序列号。
/// </remarks>
/// <param name="Model">完整型号，例如 <c>MSI Vector 16 HX AI A2XWHG</c>。</param>
/// <param name="BoardCode">主板码，例如 <c>MS-15M3</c>。这是能力判定的唯一主键。</param>
/// <param name="CpuName">CPU 型号。</param>
/// <param name="DiscreteGpuName">独显型号。</param>
/// <param name="IntegratedGpuName">核显型号。</param>
/// <param name="BiosVersion">BIOS 版本，例如 <c>E15M3IMS.117</c>。</param>
/// <param name="EcVersion">EC 版本。未读到有效值时保持 <see langword="null"/>，SMBIOS 的 255/255 无效。</param>
/// <param name="WindowsBuild">Windows 构建号。</param>
/// <param name="GpuDriverVersion">独显驱动版本。</param>
public sealed record DeviceIdentity(
    string Model,
    string BoardCode,
    string? CpuName = null,
    string? DiscreteGpuName = null,
    string? IntegratedGpuName = null,
    string? BiosVersion = null,
    string? EcVersion = null,
    string? WindowsBuild = null,
    string? GpuDriverVersion = null)
{
    /// <summary>本项目已验证的首要适配设备主板码。</summary>
    public const string TargetBoardCode = "MS-15M3";

    /// <summary>主板码未知时使用的占位值。</summary>
    /// <remarks>该值不构成有效标识，见 <see cref="IsIdentified"/>。</remarks>
    public const string UnknownBoardCode = "Unknown";

    /// <summary>完全未知的设备标识。用于初始化阶段。</summary>
    public static DeviceIdentity Unknown { get; } = new(UnknownBoardCode, UnknownBoardCode);

    /// <summary>
    /// 本设备是否已取得有效主板标识。
    /// </summary>
    /// <remarks>
    /// 主板码为空或等于 <see cref="UnknownBoardCode"/> 时视为未标识。
    /// </remarks>
    public bool IsIdentified
        => !string.IsNullOrWhiteSpace(BoardCode)
           && !string.Equals(BoardCode, UnknownBoardCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>本设备是否为首要适配设备。</summary>
    public bool IsTargetDevice => string.Equals(BoardCode, TargetBoardCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>EC 版本是否已取得有效值。</summary>
    public bool HasValidEcVersion => !string.IsNullOrWhiteSpace(EcVersion);

    /// <summary>
    /// 判断某个已保存的配置是否适用于本设备。
    /// </summary>
    /// <param name="other">配置中记录的设备标识。</param>
    /// <returns>双方均已标识且主板码一致时返回 <see langword="true"/>。</returns>
    /// <remarks>
    /// <para>
    /// <b>任一方未标识即返回 <see langword="false"/>。</b>
    /// 否则两个都没读出主板码的设备会被判为同机型，
    /// 使跨机型的配置被错误放行导入。
    /// </para>
    /// <para>
    /// 按功能判断能力、不把同系列视为全功能支持（任务书 §2.2）。
    /// 主板码一致只是配置可导入的必要条件，不代表各项能力可用。
    /// </para>
    /// </remarks>
    public bool IsSameBoardAs(DeviceIdentity other)
        => other is not null
           && IsIdentified
           && other.IsIdentified
           && string.Equals(BoardCode, other.BoardCode, StringComparison.OrdinalIgnoreCase);
}
