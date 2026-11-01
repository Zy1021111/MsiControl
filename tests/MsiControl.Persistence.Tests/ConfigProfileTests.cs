using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Persistence.Configuration;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 配置档序列化与导入校验测试。
/// </summary>
/// <remarks>
/// 覆盖重点：重建必须经过 Core 的工厂方法（否则会绕过「禁止用 0 表示无限」等不变量）、
/// 机型校验必须双重成立（本机已识别 且 主板码一致）。
/// </remarks>
public sealed class ConfigProfileTests
{
    private static readonly DeviceIdentity TargetDevice = new(
        Model: "MSI Vector 16 HX AI A2XWHG",
        BoardCode: "MS-15M3",
        CpuName: "Intel Core Ultra 9 275HX",
        BiosVersion: "E15M3IMS.117");

    private static ConfigProfile CreateProfile(params PerformanceSettings[] stages)
        => new(
            ConfigProfile.CurrentSchemaVersion,
            TargetDevice,
            "测试配置",
            new[] { "测试" },
            SortOrder: 1,
            Stages: stages.Length > 0 ? stages : PerformanceProfileCatalog.Defaults.Select(p => p.Settings).ToArray());

    [Fact]
    public void RoundTrip_PreservesDeviceStagesAndCurves()
    {
        var cpuCurve = FanCurve.Create(
            FanChannel.Cpu,
            new[] { new FanPoint(40, 20), new FanPoint(70, 60), new FanPoint(95, 100) });
        var gpuCurve = FanCurve.Create(
            FanChannel.Gpu,
            new[] { new FanPoint(45, 25), new FanPoint(85, 90) });

        var stage = new PerformanceSettings(
            PerformanceStage.Balanced,
            CpuLongTerm: PowerLimit.Custom(60m),
            CpuShortTerm: PowerLimit.Custom(75m),
            CpuTurbo: CpuTurboLimit.Custom(4800),
            GpuLimit: PowerLimit.Custom(90m),
            CpuCurve: cpuCurve,
            GpuCurve: gpuCurve);

        var json = ConfigProfileSerializer.Serialize(CreateProfile(stage));
        var result = ConfigProfileSerializer.Deserialize(json);

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        var profile = Assert.IsType<ConfigProfile>(result.Profile);
        Assert.Equal("MS-15M3", profile.Device.BoardCode);
        Assert.True(profile.Device.IsSameBoardAs(TargetDevice));

        var restored = Assert.IsType<PerformanceSettings>(profile.ForStage(PerformanceStage.Balanced));
        Assert.Equal(60m, restored.CpuLongTerm.Watts);
        Assert.Equal(75m, restored.CpuShortTerm.Watts);
        Assert.Equal(4800, restored.CpuTurbo.Megahertz);
        Assert.Equal(90m, restored.GpuLimit.Watts);
        Assert.Equal(3, restored.CpuCurve!.NodeCount);
        Assert.Equal(2, restored.GpuCurve!.NodeCount);
        Assert.Equal(FanChannel.Gpu, restored.GpuCurve.Channel);
    }

    [Fact]
    public void RoundTrip_PreservesNativeLimitsWithoutInventingNumbers()
    {
        var json = ConfigProfileSerializer.Serialize(CreateProfile(
            PerformanceProfileCatalog.GetDefault(PerformanceStage.Extreme).Settings));

        var result = ConfigProfileSerializer.Deserialize(json);

        Assert.True(result.Ok, string.Join(" / ", result.Errors));
        var extreme = result.Profile!.ForStage(PerformanceStage.Extreme)!;
        Assert.True(extreme.CpuLongTerm.IsNative);
        Assert.True(extreme.GpuLimit.IsNative);
        Assert.Null(extreme.CpuLongTerm.Watts);
        Assert.True(extreme.IsFullyNative);
    }

    [Fact]
    public void Deserialize_RejectsZeroWattCustomLimit()
    {
        // 0 不得被解释为「无限」，重建时必须被工厂方法拒绝。
        var json = """
                   {
                     "schemaVersion": 1,
                     "device": { "model": "X", "boardCode": "MS-15M3" },
                     "name": "坏配置",
                     "stages": [
                       {
                         "stage": 2,
                         "cpuLongTerm": { "mode": 1, "watts": 0 },
                         "cpuShortTerm": { "mode": 0 },
                         "cpuTurbo": { "mode": 0 },
                         "gpuLimit": { "mode": 0 }
                       }
                     ]
                   }
                   """;

        var result = ConfigProfileSerializer.Deserialize(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, error => error.Contains("必须为正数", StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_RejectsShortTermLowerThanLongTerm()
    {
        var settings = PerformanceProfileCatalog.GetDefault(PerformanceStage.Balanced).Settings;
        var broken = settings with
        {
            CpuLongTerm = PowerLimit.Custom(60m),
            CpuShortTerm = PowerLimit.Custom(30m)
        };

        var json = ConfigProfileSerializer.Serialize(CreateProfile(broken));
        var result = ConfigProfileSerializer.Deserialize(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, error => error.Contains("低于持续上限", StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_RejectsCurveBoundToWrongChannel()
    {
        var json = """
                   {
                     "schemaVersion": 1,
                     "device": { "model": "X", "boardCode": "MS-15M3" },
                     "name": "通道错位",
                     "stages": [
                       {
                         "stage": 2,
                         "cpuLongTerm": { "mode": 0 },
                         "cpuShortTerm": { "mode": 0 },
                         "cpuTurbo": { "mode": 0 },
                         "gpuLimit": { "mode": 0 },
                         "gpuCurve": { "channel": 0, "points": [ { "temperatureC": 40, "outputPercent": 20 }, { "temperatureC": 80, "outputPercent": 80 } ] }
                       }
                     ]
                   }
                   """;

        var result = ConfigProfileSerializer.Deserialize(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, error => error.Contains("应为 Gpu", StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_RejectsNewerSchemaVersion()
    {
        var json = """
                   {
                     "schemaVersion": 99,
                     "device": { "model": "X", "boardCode": "MS-15M3" },
                     "name": "未来版本",
                     "stages": []
                   }
                   """;

        var result = ConfigProfileSerializer.Deserialize(json);

        Assert.False(result.Ok);
        Assert.Contains(result.Errors, error => error.Contains("高于本程序支持", StringComparison.Ordinal));
    }

    [Fact]
    public void Deserialize_ReportsInvalidJsonWithoutThrowing()
    {
        var result = ConfigProfileSerializer.Deserialize("{ not json");

        Assert.False(result.Ok);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Import_AcceptsMatchingBoardAndRequiresExplicitApply()
    {
        var json = ConfigProfileSerializer.Serialize(CreateProfile());

        var result = ConfigImportValidator.Validate(json, TargetDevice);

        Assert.True(result.Accepted, string.Join(" / ", result.Problems));
        Assert.Contains(result.Notes, note => note.Contains("显式应用", StringComparison.Ordinal));
        Assert.Contains(result.Notes, note => note.Contains("未改动任何硬件设置", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportThenImport_DefaultCatalog_IsAcceptedAndKeepsNativeStages()
    {
        // 与「设置与诊断」窗口的导出/导入流程完全一致：
        // 导出默认档位表 → 校验机型 → 重新读回。
        var exportProfile = new ConfigProfile(
            ConfigProfile.CurrentSchemaVersion,
            TargetDevice,
            "默认配置档（导出时本机无自定义修改）",
            new[] { "默认", "未自定义" },
            SortOrder: 0,
            Stages: PerformanceProfileCatalog.Defaults.Select(p => p.Settings).ToArray());

        var json = ConfigProfileSerializer.Serialize(exportProfile);

        var imported = ConfigImportValidator.Validate(json, TargetDevice);

        Assert.True(imported.Accepted, string.Join(" / ", imported.Problems));
        var profile = Assert.IsType<ConfigProfile>(imported.Profile);
        Assert.Equal(5, profile.Stages.Count);
        Assert.Equal("默认配置档（导出时本机无自定义修改）", profile.Name);
        Assert.Contains("默认", profile.Tags);
        Assert.True(profile.ForStage(PerformanceStage.Eco)!.CpuLongTerm.Watts == 10m);
        Assert.True(profile.ForStage(PerformanceStage.Extreme)!.IsFullyNative);
    }

    [Fact]
    public void Import_RejectsWhenLocalDeviceIsNotIdentified()
    {
        var json = ConfigProfileSerializer.Serialize(CreateProfile());

        var result = ConfigImportValidator.Validate(json, DeviceIdentity.Unknown);

        Assert.False(result.Accepted);
        Assert.Contains(result.Problems, problem => problem.Contains("本机机型尚未识别", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_RejectsWhenProfileHasNoValidIdentity()
    {
        var profile = CreateProfile() with { Device = DeviceIdentity.Unknown };
        var json = ConfigProfileSerializer.Serialize(profile);

        var result = ConfigImportValidator.Validate(json, TargetDevice);

        Assert.False(result.Accepted);
        Assert.Contains(result.Problems, problem => problem.Contains("未记录有效机型", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_RejectsDifferentBoardCode()
    {
        var other = new DeviceIdentity("Other Laptop", "MS-17S1");
        var json = ConfigProfileSerializer.Serialize(CreateProfile());

        var result = ConfigImportValidator.Validate(json, other);

        Assert.False(result.Accepted);
        Assert.Contains(result.Problems, problem => problem.Contains("主板码不一致", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_RejectsProfileWithoutStages()
    {
        var json = """
                   {
                     "schemaVersion": 1,
                     "device": { "model": "MSI Vector 16 HX AI A2XWHG", "boardCode": "MS-15M3" },
                     "name": "空配置",
                     "stages": []
                   }
                   """;

        var result = ConfigImportValidator.Validate(json, TargetDevice);

        Assert.False(result.Accepted);
        Assert.Contains(result.Problems, problem => problem.Contains("不包含任何性能档位", StringComparison.Ordinal));
    }

    [Fact]
    public void Import_RejectsStructurallyInvalidJson()
    {
        var result = ConfigImportValidator.Validate("{ not json", TargetDevice);

        Assert.False(result.Accepted);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Import_ChecksBothSidesIdentifiedSoUnidentifiedIsNeverSameModel()
    {
        // 关键回归：两台都没读出主板码的设备不得判为同机型。
        Assert.False(DeviceIdentity.Unknown.IsSameBoardAs(DeviceIdentity.Unknown));
    }
}
