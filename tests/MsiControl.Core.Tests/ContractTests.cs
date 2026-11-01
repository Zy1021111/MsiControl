using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 「不显示虚假成功」相关约定的回归测试：限制类型、能力等级、结果归并、读数状态。
/// </summary>
public class ContractTests
{
    // ---------- PowerLimit ----------

    [Fact]
    public void PowerLimit_Native_CarriesNoWatts()
    {
        Assert.True(PowerLimit.Native.IsNative);
        Assert.Equal(LimitMode.Native, PowerLimit.Native.Mode);
        Assert.Null(PowerLimit.Native.Watts);
        Assert.Equal("原生上限", PowerLimit.Native.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PowerLimit_TryCustom_RejectsNonPositiveValues(decimal watts)
    {
        // 防回归：禁止用 0 或 -1 隐式表示「无限」。
        Assert.False(PowerLimit.TryCustom(watts, out var limit, out var error));
        Assert.Null(limit);
        Assert.NotNull(error);
    }

    [Fact]
    public void PowerLimit_TryCustom_RejectsUnreasonablyLargeValue()
    {
        Assert.False(PowerLimit.TryCustom(PowerLimit.MaxCustomWatts + 1, out var limit, out var error));
        Assert.Null(limit);
        Assert.Contains("极大数值", error!);
    }

    [Fact]
    public void PowerLimit_Custom_CarriesWatts()
    {
        var limit = PowerLimit.Custom(60);

        Assert.Equal(LimitMode.Custom, limit.Mode);
        Assert.Equal(60m, limit.Watts);
        Assert.Equal("60 W", limit.ToString());
    }

    [Fact]
    public void PowerLimit_Custom_ThrowsOnInvalidValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerLimit.Custom(0m));
    }

    // ---------- CpuTurboLimit ----------

    [Fact]
    public void CpuTurboLimit_Native_CarriesNoFrequency()
    {
        Assert.True(CpuTurboLimit.Native.IsNative);
        Assert.Null(CpuTurboLimit.Native.Megahertz);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    public void CpuTurboLimit_TryCustom_RejectsNonPositiveValues(int megahertz)
    {
        Assert.False(CpuTurboLimit.TryCustom(megahertz, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void CpuTurboLimit_Custom_ExpressesFrequencyNotRarity()
    {
        var limit = CpuTurboLimit.Custom(4800);

        Assert.Equal(LimitMode.Custom, limit.Mode);
        Assert.Equal(4800, limit.Megahertz);
    }

    // ---------- Capability ----------

    [Fact]
    public void Capability_UnregisteredKeyDefaultsToUnknownAndDisallowsWrite()
    {
        var capabilities = DeviceCapabilities.Empty;

        Assert.Equal(CapabilityLevel.Unknown, capabilities.LevelOf("not.registered"));
        Assert.False(capabilities.AllowsWrite("not.registered"));
        Assert.False(capabilities.AllowsRead("not.registered"));
    }

    [Fact]
    public void Capability_DetectedAllowsReadButNotWrite()
    {
        // 关键约定：接口存在（Detected）不足以开放写入（任务书 §16.1）。
        var capabilities = DeviceCapabilities.Empty.With(
            CapabilityStatus.Detected(CapabilityKeys.FanCurve, "Get_Fan/Set_Fan 存在，未验证"));

        Assert.True(capabilities.AllowsRead(CapabilityKeys.FanCurve));
        Assert.False(capabilities.AllowsWrite(CapabilityKeys.FanCurve));
    }

    [Fact]
    public void Capability_VerifiedAllowsWrite()
    {
        var capabilities = DeviceCapabilities.Empty.With(
            CapabilityStatus.Verified(CapabilityKeys.ThermalRead, "实机读回 60.05 °C"));

        Assert.True(capabilities.AllowsRead(CapabilityKeys.ThermalRead));
        Assert.True(capabilities.AllowsWrite(CapabilityKeys.ThermalRead));
    }

    [Fact]
    public void Capability_UnsupportedIsNeverReadable()
    {
        var capabilities = DeviceCapabilities.Empty.With(
            CapabilityStatus.Unsupported("msi.cpu", "0x8004100c WBEM_E_NOT_SUPPORTED"));

        Assert.Equal(CapabilityLevel.Unsupported, capabilities.LevelOf("msi.cpu"));
        Assert.False(capabilities.AllowsRead("msi.cpu"));
        Assert.False(capabilities.AllowsWrite("msi.cpu"));
    }

    [Fact]
    public void Capability_With_OverridesWithoutMutatingOriginal()
    {
        var original = DeviceCapabilities.Empty.With(CapabilityStatus.Unknown(CapabilityKeys.GraphicsMode));

        var upgraded = original.With(CapabilityStatus.Verified(CapabilityKeys.GraphicsMode, "实机切换验证通过"));

        Assert.Equal(CapabilityLevel.Unknown, original.LevelOf(CapabilityKeys.GraphicsMode));
        Assert.Equal(CapabilityLevel.Verified, upgraded.LevelOf(CapabilityKeys.GraphicsMode));
        Assert.Equal(1, upgraded.Count);
    }

    // ---------- SettingResult ----------

    [Fact]
    public void SettingResult_Succeeded_RequiresConfirmationBasis()
    {
        Assert.Throws<ArgumentException>(() =>
            SettingResult.Succeeded("cpu.longterm", "60 W", "60 W", confirmationBasis: "  "));
    }

    [Fact]
    public void SettingResult_FlagsUnsubstantiatedSuccess()
    {
        var bad = new SettingResult("cpu.longterm", "60 W", null, OperationStatus.Succeeded);

        Assert.True(bad.IsUnsubstantiatedSuccess);
    }

    [Fact]
    public void SettingResult_Unsupported_RequiresReason()
    {
        Assert.Throws<ArgumentException>(() => SettingResult.Unsupported("gpu.power", "   "));
    }

    // ---------- ControlResult 归并 ----------

    [Fact]
    public void ControlResult_FromEmpty_IsFailure()
    {
        var result = ControlResult.From(Array.Empty<SettingResult>());

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.IsSuccess);
        Assert.NotEmpty(result.Validate());
    }

    [Fact]
    public void ControlResult_AllFailed_IsFailure()
    {
        var result = ControlResult.From(new[]
        {
            SettingResult.Failed("cpu.longterm", "60 W", "写入被固件拒绝"),
            SettingResult.Failed("gpu.limit", "90 W", "接口不可用")
        });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.True(result.IsFailure);
    }

    [Fact]
    public void ControlResult_MixedSuccessAndFailure_IsPartiallySucceeded()
    {
        // 关键约定：部分失败时不得宣称整组成功（任务书 §16）。
        var result = ControlResult.From(new[]
        {
            SettingResult.Succeeded("cpu.longterm", "60 W", "60 W", "读回一致"),
            SettingResult.Failed("gpu.limit", "90 W", "写入被拒绝")
        });

        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
        Assert.True(result.IsPartial);
        Assert.Single(result.Problems);
    }

    [Fact]
    public void ControlResult_AllSucceeded_IsSuccess()
    {
        var result = ControlResult.From(new[]
        {
            SettingResult.Succeeded("cpu.longterm", "60 W", "60 W", "读回一致"),
            SettingResult.Succeeded("gpu.limit", "90 W", "90 W", "读回一致")
        });

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public void ControlResult_UnsupportedAlongsideSuccess_IsPartiallySucceeded()
    {
        var result = ControlResult.From(new[]
        {
            SettingResult.Succeeded("cpu.longterm", "60 W", "60 W", "读回一致"),
            SettingResult.Unsupported("gpu.eco", "本机不支持 20 W 目标，实际下界 35 W")
        });

        Assert.Equal(OperationStatus.PartiallySucceeded, result.Status);
    }

    [Fact]
    public void ControlResult_PendingRestartTakesPrecedenceOverPlainSuccess()
    {
        var result = ControlResult.From(new[]
        {
            SettingResult.Succeeded("cpu.longterm", "60 W", "60 W", "读回一致"),
            SettingResult.PendingRestart("graphics.mode", "Discrete", "写入后读回 Pending", "需重启生效")
        });

        Assert.Equal(OperationStatus.PendingRestart, result.Status);
        Assert.Single(result.PendingRestartSettings);
    }

    [Fact]
    public void ControlResult_Validate_ReportsUnsubstantiatedSuccess()
    {
        var result = ControlResult.From(new[]
        {
            new SettingResult("cpu.longterm", "60 W", "60 W", OperationStatus.Succeeded)
        });

        var problems = result.Validate();

        Assert.Single(problems);
        Assert.Contains("缺少确认依据", problems[0]);
    }

    [Fact]
    public void ControlResult_Validate_ReportsUnsupportedWithoutReason()
    {
        var result = ControlResult.From(new[]
        {
            new SettingResult("gpu.eco", null, null, OperationStatus.Unsupported)
        });

        Assert.Contains(result.Validate(), p => p.Contains("未说明依据"));
    }

    // ---------- 读数状态 ----------

    [Fact]
    public void SensorReading_Unavailable_IsNotUsable()
    {
        var reading = SensorReading.Unavailable(Units.Celsius, "MSI_ACPI");

        Assert.False(reading.IsUsable);
        Assert.Equal(ReadingStatus.Unavailable, reading.Status);
        Assert.Null(reading.Value);
    }

    [Fact]
    public void SensorReading_Valid_IsUsable()
    {
        var reading = SensorReading.Valid(60.05, Units.Celsius, "MSI_ACPI", DateTimeOffset.UtcNow);

        Assert.True(reading.IsUsable);
    }

    [Fact]
    public void SensorReading_Stale_KeepsLastKnownValueButIsNotUsable()
    {
        // 关键约定：过期数据保留供诊断，但不得当作当前有效读数使用。
        var stale = SensorReading.Stale(60.05, Units.Celsius, "MSI_ACPI", DateTimeOffset.UtcNow.AddMinutes(-5));

        Assert.False(stale.IsUsable);
        Assert.Equal(ReadingStatus.Stale, stale.Status);
        Assert.Equal(60.05, stale.Value);
    }

    [Fact]
    public void SensorReading_AsStale_ChangesStatus()
    {
        var valid = SensorReading.Valid(60.05, Units.Celsius, "MSI_ACPI", DateTimeOffset.UtcNow);

        var stale = valid.AsStale("采样超时");

        Assert.Equal(ReadingStatus.Stale, stale.Status);
        Assert.Equal("采样超时", stale.Detail);
        Assert.Equal(valid.Value, stale.Value);
    }

    [Fact]
    public void GpuSnapshot_Sleeping_ReportsSleeping()
    {
        var snapshot = GpuSnapshot.Sleeping("NVML");

        Assert.True(snapshot.IsSleeping);
        Assert.Equal(ReadingStatus.Sleeping, snapshot.Temperature.Status);
    }

    [Fact]
    public void MonitoringSnapshot_Empty_HasNoUsableReadings()
    {
        var snapshot = MonitoringSnapshot.Empty(DateTimeOffset.UtcNow);

        Assert.False(snapshot.Cpu.Temperature.IsUsable);
        Assert.False(snapshot.Gpu.Temperature.IsUsable);
        Assert.False(snapshot.SystemPower.IsAvailable);
        Assert.Empty(snapshot.Extended);
    }

    [Fact]
    public void MonitoringSnapshot_MarkStale_MarksCoreReadingsAsStale()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new MonitoringSnapshot(
            now,
            new CpuSnapshot(
                SensorReading.Valid(75, Units.Celsius, "src", now),
                SensorReading.Valid(4200, Units.Megahertz, "src", now),
                SensorReading.Valid(45, Units.Watt, "src", now),
                FanReading.Valid(3200, "src", now)),
            GpuSnapshot.Unavailable("src"),
            FanSnapshot.Unavailable("src"),
            SystemPowerReading.Unavailable("无可靠来源"),
            Array.Empty<ExtendedSensor>());

        var stale = snapshot.MarkStale("采样超时");

        Assert.Equal(ReadingStatus.Stale, stale.Cpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Stale, stale.Cpu.FrequencyMhz.Status);
        Assert.Equal(ReadingStatus.Stale, stale.Cpu.PackagePowerWatt.Status);
        Assert.False(stale.Cpu.Temperature.IsUsable);
    }

    // ---------- 风扇读数：实际与目标必须区分 ----------

    [Fact]
    public void FanReading_WithoutCalibration_HasNoActualPercent()
    {
        var reading = FanReading.Valid(3200, "MSI_ACPI", DateTimeOffset.UtcNow, actualPercent: null, targetPercent: 60);

        Assert.False(reading.HasCalibration);
        Assert.Null(reading.ActualPercent);
        Assert.Equal(60, reading.TargetPercent);
    }

    [Fact]
    public void FanReading_WithCalibration_ExposesBothActualAndTarget()
    {
        var reading = FanReading.Valid(3900, "MSI_ACPI", DateTimeOffset.UtcNow, actualPercent: 78, targetPercent: 60);

        Assert.True(reading.HasCalibration);
        Assert.NotEqual(reading.ActualPercent, reading.TargetPercent);
    }

    // ---------- 整机功耗口径 ----------

    [Fact]
    public void SystemPowerReading_Unavailable_DoesNotFabricateValue()
    {
        var power = SystemPowerReading.Unavailable("本机无适配器侧读数");

        Assert.False(power.IsAvailable);
        Assert.Equal(PowerSourceKind.Unknown, power.Kind);
        Assert.Null(power.Reading.Value);
    }

    [Fact]
    public void SystemPowerReading_BatterySide_IsAvailable()
    {
        var power = new SystemPowerReading(
            PowerSourceKind.BatterySide,
            SensorReading.Valid(78.5, Units.Watt, "Get_MasterBattery", DateTimeOffset.UtcNow));

        Assert.True(power.IsAvailable);
    }

    // ---------- 五档默认配置 ----------

    [Fact]
    public void Catalog_HasExactlyFiveStages()
    {
        Assert.Equal(5, PerformanceProfileCatalog.Defaults.Count);
        Assert.Equal(
            new[]
            {
                PerformanceStage.Eco,
                PerformanceStage.Quiet,
                PerformanceStage.Balanced,
                PerformanceStage.HighPerformance,
                PerformanceStage.Extreme
            },
            PerformanceProfileCatalog.Defaults.Select(p => p.Stage).ToArray());
    }

    [Theory]
    [InlineData(PerformanceStage.Eco, 10, 20, "节能")]
    [InlineData(PerformanceStage.Quiet, 50, 80, "安静")]
    [InlineData(PerformanceStage.Balanced, 60, 90, "平衡")]
    public void Catalog_ConstrainedStages_MatchDesignValues(
        PerformanceStage stage,
        decimal expectedCpu,
        decimal expectedGpu,
        string expectedName)
    {
        var profile = PerformanceProfileCatalog.GetDefault(stage);

        Assert.Equal(expectedName, profile.DisplayName);
        Assert.Equal(expectedCpu, profile.Settings.CpuLongTerm.Watts);
        Assert.Equal(expectedCpu, profile.Settings.CpuShortTerm.Watts); // 默认严格限制：短时=持续
        Assert.Equal(expectedGpu, profile.Settings.GpuLimit.Watts);
        Assert.False(profile.Settings.IsFullyNative);
    }

    [Theory]
    [InlineData(PerformanceStage.HighPerformance)]
    [InlineData(PerformanceStage.Extreme)]
    public void Catalog_NativeStages_UseNativeLimitMode(PerformanceStage stage)
    {
        var profile = PerformanceProfileCatalog.GetDefault(stage);

        Assert.True(profile.Settings.CpuLongTerm.IsNative);
        Assert.True(profile.Settings.CpuShortTerm.IsNative);
        Assert.True(profile.Settings.CpuTurbo.IsNative);
        Assert.True(profile.Settings.GpuLimit.IsNative);
        Assert.True(profile.Settings.IsFullyNative);

        // 防回归：不得用 0 或极大数字表达原生上限。
        Assert.Null(profile.Settings.CpuLongTerm.Watts);
        Assert.Null(profile.Settings.GpuLimit.Watts);
    }

    [Fact]
    public void Catalog_DefaultsCarryNoCurves()
    {
        // 曲线由风扇模块与上层填充，默认表不得凭空编造曲线数值。
        Assert.All(PerformanceProfileCatalog.Defaults, p =>
        {
            Assert.Null(p.Settings.CpuCurve);
            Assert.Null(p.Settings.GpuCurve);
        });
    }

    [Fact]
    public void Catalog_GetDefault_ThrowsForUnregisteredStage()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => PerformanceProfileCatalog.GetDefault((PerformanceStage)99));
    }

    // ---------- 显卡状态 ----------

    [Fact]
    public void GraphicsState_Unknown_HasNoCurrentMode()
    {
        Assert.Null(GraphicsHardwareState.Unknown.Current);
        Assert.False(GraphicsHardwareState.Unknown.HasPendingChange);
    }

    [Fact]
    public void GraphicsState_HasPendingChange_OnlyWhenPendingDiffersFromCurrent()
    {
        var same = new GraphicsHardwareState(Current: GraphicsMode.Hybrid, Pending: GraphicsMode.Hybrid);
        var different = new GraphicsHardwareState(Current: GraphicsMode.Hybrid, Pending: GraphicsMode.Discrete);

        Assert.False(same.HasPendingChange);
        Assert.True(different.HasPendingChange);
    }

    [Fact]
    public void GraphicsState_Supports_ReflectsPerModeFlags()
    {
        // 按功能判断能力，不把整个系列视为全功能（任务书 §2.2）。
        var state = new GraphicsHardwareState(SupportsHybrid: true, SupportsDiscrete: true);

        Assert.True(state.Supports(GraphicsMode.Hybrid));
        Assert.True(state.Supports(GraphicsMode.Discrete));
        Assert.False(state.Supports(GraphicsMode.Integrated));
    }

    [Fact]
    public void GraphicsState_WouldDisconnectExternalDisplay_ReturnsNullWhenTopologyUnknown()
    {
        // 关键约定：拓扑未确认时不得默认按「无风险」处理（任务书 §9.2）。
        var state = new GraphicsHardwareState(
            ExternalDisplayAttached: true,
            ExternalDisplayDependsOnDiscreteGpu: null);

        Assert.Null(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));
    }

    [Fact]
    public void GraphicsState_WouldDisconnectExternalDisplay_ReportsRiskWhenDependent()
    {
        var state = new GraphicsHardwareState(
            ExternalDisplayAttached: true,
            ExternalDisplayDependsOnDiscreteGpu: true);

        Assert.True(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));
        Assert.False(state.WouldDisconnectExternalDisplay(GraphicsMode.Discrete));
    }

    [Fact]
    public void GraphicsState_WouldDisconnectExternalDisplay_FalseWhenNoExternalDisplay()
    {
        var state = new GraphicsHardwareState(
            ExternalDisplayAttached: false,
            ExternalDisplayDependsOnDiscreteGpu: true);

        Assert.False(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));
    }

    // ---------- 设备标识 ----------

    [Fact]
    public void DeviceIdentity_DoesNotPretendEcVersionIsKnown()
    {
        var device = new DeviceIdentity("MSI Vector 16 HX AI A2XWHG", "MS-15M3");

        Assert.False(device.HasValidEcVersion);
        Assert.Null(device.EcVersion);
        Assert.True(device.IsTargetDevice);
    }

    [Fact]
    public void DeviceIdentity_IsSameBoardAs_ComparesBoardCodeCaseInsensitively()
    {
        var a = new DeviceIdentity("A", "MS-15M3");
        var b = new DeviceIdentity("B", "ms-15m3");
        var c = new DeviceIdentity("C", "MS-17S1");

        Assert.True(a.IsSameBoardAs(b));
        Assert.False(a.IsSameBoardAs(c));
    }
}
