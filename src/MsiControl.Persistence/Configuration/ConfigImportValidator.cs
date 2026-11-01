using MsiControl.Core.Models;

namespace MsiControl.Persistence.Configuration;

/// <summary>
/// 配置档导入校验的结果。
/// </summary>
/// <param name="Accepted">是否允许导入。</param>
/// <param name="Profile">被接受的配置档。</param>
/// <param name="Problems">拒绝原因或结构问题。</param>
/// <param name="Notes">需要提示用户的说明，例如「导入后需显式应用」。</param>
public sealed record ConfigImportResult(
    bool Accepted,
    ConfigProfile? Profile,
    IReadOnlyList<string> Problems,
    IReadOnlyList<string> Notes)
{
    /// <summary>构造一个拒绝结果。</summary>
    public static ConfigImportResult Reject(IReadOnlyList<string> problems)
        => new(false, null, problems, Array.Empty<string>());
}

/// <summary>
/// 配置档导入校验。
/// </summary>
/// <remarks>
/// <para>
/// 职责边界：<see cref="ConfigProfileSerializer"/> 只管结构是否合法，
/// 本类型只管**机型是否适用**与**组合是否自洽**。
/// </para>
/// <para>
/// 机型校验必须<b>双重成立</b>（docs/接口与工程约定.md §3.10）：
/// 本机已识别（<see cref="DeviceIdentity.IsIdentified"/>）
/// <b>且</b> 双方主板码一致（<see cref="DeviceIdentity.IsSameBoardAs"/>）。
/// 任一不成立即拒绝——「两台都没读出主板码」不等于同机型。
/// </para>
/// <para>
/// <b>本类型不写入任何硬件。</b>导入成功后仍须由用户显式应用（任务书 §7.3）。
/// </para>
/// </remarks>
public static class ConfigImportValidator
{
    /// <summary>导入成功后的固定提示，避免调用方漏掉「不自动写入」这一点。</summary>
    public const string ExplicitApplyNote =
        "配置已导入到本地。按任务书 §7.3，导入后必须在界面显式应用才会写入硬件；本次导入未改动任何硬件设置。";

    /// <summary>
    /// 校验从 JSON 文本导入的配置。
    /// </summary>
    /// <param name="json">配置文本。</param>
    /// <param name="currentDevice">本机设备标识。</param>
    public static ConfigImportResult Validate(string json, DeviceIdentity currentDevice)
    {
        var deserialized = ConfigProfileSerializer.Deserialize(json);
        return deserialized.Ok && deserialized.Profile is not null
            ? Validate(deserialized.Profile, currentDevice)
            : ConfigImportResult.Reject(deserialized.Errors);
    }

    /// <summary>
    /// 校验一个已重建的配置档是否适用于本机。
    /// </summary>
    /// <param name="profile">配置档。</param>
    /// <param name="currentDevice">本机设备标识。</param>
    public static ConfigImportResult Validate(ConfigProfile profile, DeviceIdentity currentDevice)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(currentDevice);

        var problems = new List<string>();

        if (profile.SchemaVersion > ConfigProfile.CurrentSchemaVersion)
        {
            problems.Add(
                $"配置版本 {profile.SchemaVersion} 高于本程序支持的 {ConfigProfile.CurrentSchemaVersion}，拒绝导入。");
        }

        if (!currentDevice.IsIdentified)
        {
            problems.Add("本机机型尚未识别，无法判断配置适用性，拒绝导入。");
        }
        else if (!profile.Device.IsIdentified)
        {
            problems.Add("该配置未记录有效机型，无法确认适用范围，拒绝导入。");
        }
        else if (!currentDevice.IsSameBoardAs(profile.Device))
        {
            problems.Add(
                $"主板码不一致：本机为 {currentDevice.BoardCode}，配置记录为 {profile.Device.BoardCode}，拒绝导入。");
        }

        if (profile.Stages.Count == 0)
        {
            problems.Add("配置不包含任何性能档位，无内容可导入。");
        }

        foreach (var stage in profile.Stages)
        {
            foreach (var problem in stage.Validate())
            {
                problems.Add($"档位 {stage.Stage}：{problem}");
            }
        }

        if (problems.Count > 0)
        {
            return ConfigImportResult.Reject(problems);
        }

        return new ConfigImportResult(
            Accepted: true,
            Profile: profile,
            Problems: Array.Empty<string>(),
            Notes: new[] { ExplicitApplyNote });
    }
}
