using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 迭代加固回归测试。
/// </summary>
/// <remarks>
/// 本文件记录逐轮自我优化中新加的约束，每轮一组，注明依据。
/// 与 <see cref="ContractTests"/> 的分工：ContractTests 覆盖设计约定，
/// 本文件覆盖优化轮次中发现并修复的**具体缺陷**。
/// </remarks>
public class HardeningTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);

    /// <summary>构造一个读数全部有效的快照，用于测试降级行为。</summary>
    private static MonitoringSnapshot FullValidSnapshot() => new(
        Now,
        new CpuSnapshot(
            SensorReading.Valid(78.5, Units.Celsius, "MSI_ACPI.Get_Temperature", Now),
            SensorReading.Valid(4200, Units.Megahertz, "src", Now),
            SensorReading.Valid(45.2, Units.Watt, "src", Now),
            FanReading.Valid(3200, "src", Now, actualPercent: 62, targetPercent: 60)),
        new GpuSnapshot(
            SensorReading.Valid(65, Units.Celsius, "NVML", Now),
            SensorReading.Valid(1800, Units.Megahertz, "NVML", Now),
            SensorReading.Valid(90, Units.Watt, "NVML", Now),
            FanReading.Valid(2800, "src", Now, actualPercent: 55)),
        new FanSnapshot(FanReading.Valid(3200, "src", Now), FanReading.Valid(2800, "src", Now)),
        new SystemPowerReading(
            PowerSourceKind.BatterySide,
            SensorReading.Valid(78.5, Units.Watt, "Get_MasterBattery", Now)),
        new[]
        {
            new ExtendedSensor("ssd.0", "SSD 温度", SensorReading.Valid(41, Units.Celsius, "src", Now)),
            new ExtendedSensor("ram.used", "内存占用", SensorReading.Valid(48, Units.Percent, "src", Now))
        });

    // ═══════════════════════════════════════════════════════════
    // 第 1 轮 · 健壮性：构造期不变量
    // 依据：PowerLimit 已用私有构造锁死非法值，SensorReading 与
    //       CapabilityStatus 仍是公开位置构造，可造出矛盾对象。
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SensorReading_Valid_RejectsEmptyUnit(string unit)
    {
        Assert.Throws<ArgumentException>(
            () => SensorReading.Valid(60.05, unit, "MSI_ACPI", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void SensorReading_Valid_RejectsEmptySource(string source)
    {
        // 无来源的读数无法追溯，不允许进入模型。
        Assert.Throws<ArgumentException>(
            () => SensorReading.Valid(60.05, Units.Celsius, source, Now));
    }

    [Fact]
    public void SensorReading_NonValidFactories_RejectEmptyUnitOrSource()
    {
        Assert.Throws<ArgumentException>(() => SensorReading.Unavailable("", "src"));
        Assert.Throws<ArgumentException>(() => SensorReading.Unavailable(Units.Celsius, ""));
        Assert.Throws<ArgumentException>(() => SensorReading.Sleeping("", "src"));
        Assert.Throws<ArgumentException>(() => SensorReading.Sleeping(Units.Celsius, ""));
        Assert.Throws<ArgumentException>(() => SensorReading.Stale(null, "", "src", Now));
    }

    [Fact]
    public void SensorReading_UnavailableAndSleeping_CarryNoValue()
    {
        // 不变量：不可用与休眠读数绝不能携带数值，
        // 否则界面可能把陈旧值当作当前读数显示。
        Assert.Null(SensorReading.Unavailable(Units.Celsius, "src").Value);
        Assert.Null(SensorReading.Sleeping(Units.Celsius, "src").Value);
    }

    [Fact]
    public void SensorReading_Stale_MayKeepLastKnownValueForDiagnosis()
    {
        var stale = SensorReading.Stale(60.05, Units.Celsius, "src", Now);

        Assert.Equal(60.05, stale.Value);
        Assert.False(stale.IsUsable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CapabilityStatus_VerifiedAndDetected_RequireEvidence(string evidence)
    {
        // 不变量：声称已确证或已发现线索，必须拿得出证据。
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Verified("k", evidence));
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Detected("k", evidence));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CapabilityStatus_Unsupported_RequiresReason(string reason)
    {
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Unsupported("k", reason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void CapabilityStatus_RejectsEmptyKey(string key)
    {
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Unknown(key));
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Verified(key, "evidence"));
    }

    [Fact]
    public void CapabilityStatus_UnknownWithoutReason_DescribesGenericCause()
    {
        var status = CapabilityStatus.Unknown("k");

        Assert.Null(status.Reason);
        Assert.False(string.IsNullOrWhiteSpace(status.DescribeUnavailable()));
    }

    [Fact]
    public void CapabilityStatus_DescribeUnavailable_PrefersReasonThenEvidence()
    {
        Assert.Equal("已确认不可用", CapabilityStatus.Unsupported("k", "已确认不可用").DescribeUnavailable());
        Assert.Equal("有证据", CapabilityStatus.Detected("k", "有证据").DescribeUnavailable());
    }

    [Fact]
    public void FanReading_Valid_RejectsNegativeRpm()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FanReading.Valid(-1, "src", Now));
    }

    [Theory]
    [InlineData(101.0, null)]
    [InlineData(-0.5, null)]
    [InlineData(null, 101.0)]
    [InlineData(null, -0.5)]
    public void FanReading_Valid_RejectsOutOfRangePercent(double? actual, double? target)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => FanReading.Valid(3200, "src", Now, actual, target));
    }

    [Fact]
    public void CapabilityStatus_GuaranteesNonEmptyKey_SoDeviceCapabilitiesNeverSeesABlankOne()
    {
        // 第 1 轮复查发现：DeviceCapabilities 原先对 Key 的空值校验是**不可达的死代码**，
        // 因为 CapabilityStatus 已在构造期拒绝空键。已删除该冗余分支。
        // 这里断言真正的保证链，而不是去测一段永远走不到的分支。
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Unknown(" "));
        Assert.Throws<ArgumentException>(() => CapabilityStatus.Unknown(string.Empty));

        var capabilities = new DeviceCapabilities(new[] { CapabilityStatus.Unknown("ok") });

        Assert.Equal(1, capabilities.Count);
    }

    [Fact]
    public void DeviceCapabilities_MissingKeys_DetectsBaselineDrift()
    {
        // 供启动自检使用：基线未覆盖全部能力键时必须能立刻发现。
        Assert.Equal(CapabilityKeys.All.Count, DeviceCapabilities.Empty.MissingKeys().Count);
        Assert.Empty(Ms15M3LikeFullCoverage().MissingKeys());
    }

    private static DeviceCapabilities Ms15M3LikeFullCoverage()
        => new(CapabilityKeys.All.Select(key => CapabilityStatus.Unknown(key, "测试用")).ToArray());

    // ═══════════════════════════════════════════════════════════
    // 第 2 轮 · 准确性：过期标记完整性与新鲜度
    // 依据：原 MarkStale 只覆盖 CPU/GPU 的温度、频率、功耗，
    //       遗漏双风扇、整机功耗与扩展传感器；且会把 Unavailable
    //       与 Sleeping 一并改写成 Stale，掩盖真实故障。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void MarkStale_CoversFansSystemPowerAndExtendedSensors()
    {
        var stale = FullValidSnapshot().MarkStale("采样超时");

        Assert.Equal(ReadingStatus.Stale, stale.Cpu.Fan.Rpm.Status);
        Assert.Equal(ReadingStatus.Stale, stale.Gpu.Fan.Rpm.Status);
        Assert.Equal(ReadingStatus.Stale, stale.Fans.Cpu.Rpm.Status);
        Assert.Equal(ReadingStatus.Stale, stale.Fans.Gpu.Rpm.Status);
        Assert.Equal(ReadingStatus.Stale, stale.SystemPower.Reading.Status);
        Assert.All(stale.Extended, sensor => Assert.Equal(ReadingStatus.Stale, sensor.Reading.Status));
    }

    [Fact]
    public void MarkStale_PreservesSystemPowerKind()
    {
        // 口径不得因降级而丢失，否则界面会分不清是哪种测量源。
        var stale = FullValidSnapshot().MarkStale("采样超时");

        Assert.Equal(PowerSourceKind.BatterySide, stale.SystemPower.Kind);
    }

    [Fact]
    public void MarkStale_DoesNotRewriteUnavailableAsStale()
    {
        // 关键回归：把「读不到」改写成「数据过期」会掩盖真实故障。
        var snapshot = MonitoringSnapshot.Empty(Now);

        var stale = snapshot.MarkStale("采样超时");

        Assert.Equal(ReadingStatus.Unavailable, stale.Cpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Unavailable, stale.Gpu.Temperature.Status);
        Assert.Equal(ReadingStatus.Unavailable, stale.Fans.Cpu.Rpm.Status);
        Assert.Equal(ReadingStatus.Unavailable, stale.SystemPower.Reading.Status);
    }

    [Fact]
    public void MarkStale_DoesNotRewriteSleepingAsStale()
    {
        // 独显休眠是正常状态，不是过期。
        var snapshot = new MonitoringSnapshot(
            Now,
            CpuSnapshot.Unavailable("src"),
            GpuSnapshot.Sleeping("NVML"),
            FanSnapshot.Unavailable("src"),
            SystemPowerReading.Unavailable("无可靠来源"),
            Array.Empty<ExtendedSensor>());

        var stale = snapshot.MarkStale("采样超时");

        Assert.Equal(ReadingStatus.Sleeping, stale.Gpu.Temperature.Status);
        Assert.True(stale.Gpu.IsSleeping);
    }

    [Fact]
    public void MarkStale_KeepsLastKnownValuesForDiagnosis()
    {
        var stale = FullValidSnapshot().MarkStale("采样超时");

        Assert.Equal(78.5, stale.Cpu.Temperature.Value);
        Assert.Equal(3200.0, stale.Fans.Cpu.Rpm.Value);
    }

    [Fact]
    public void MarkStale_IsIdempotent()
    {
        var once = FullValidSnapshot().MarkStale("采样超时");
        var twice = once.MarkStale("再次超时");

        Assert.Equal(ReadingStatus.Stale, twice.Cpu.Temperature.Status);
        Assert.Equal(once.Cpu.Temperature.Value, twice.Cpu.Temperature.Value);
    }

    [Fact]
    public void IsFreshAt_RespectsMaxAge()
    {
        var snapshot = FullValidSnapshot();

        Assert.True(snapshot.IsFreshAt(Now.AddSeconds(1), TimeSpan.FromSeconds(5)));
        Assert.True(snapshot.IsFreshAt(Now.AddSeconds(5), TimeSpan.FromSeconds(5)));
        Assert.False(snapshot.IsFreshAt(Now.AddSeconds(6), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void SensorReading_Age_ReturnsNullWhenSampleTimeUnknown()
    {
        Assert.Null(SensorReading.Unavailable(Units.Celsius, "src").Age(Now));
        Assert.Equal(TimeSpan.FromSeconds(30),
            SensorReading.Valid(60, Units.Celsius, "src", Now.AddSeconds(-30)).Age(Now));
    }

    [Fact]
    public void AsStaleIfValid_LeavesNonValidReadingsUntouched()
    {
        var unavailable = SensorReading.Unavailable(Units.Celsius, "src");

        var result = unavailable.AsStaleIfValid("采样超时");

        Assert.Equal(ReadingStatus.Unavailable, result.Status);
        Assert.Null(result.Detail); // 未被改写
    }

    [Fact]
    public void FanReading_AsStaleIfValid_PreservesPercentFields()
    {
        var reading = FanReading.Valid(3900, "src", Now, actualPercent: 78, targetPercent: 60);

        var stale = reading.AsStaleIfValid("采样超时");

        Assert.Equal(ReadingStatus.Stale, stale.Rpm.Status);
        Assert.Equal(78, stale.ActualPercent);
        Assert.Equal(60, stale.TargetPercent);
        Assert.True(stale.HasCalibration);
    }

    // ═══════════════════════════════════════════════════════════
    // 第 3 轮 · 正确性：判定逻辑缺陷
    // 依据：IsSameBoardAs 未排除「双方都未知」的情形，
    //       两个未识别设备会被判为同机型，配置导入被错误放行；
    //       PerformanceSettings 只保证单值合法，字段组合无人校验。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DeviceIdentity_TwoUnknownDevices_AreNotTheSameBoard()
    {
        // 关键回归：两个都没读出主板码的设备，绝不能判为同机型。
        Assert.False(DeviceIdentity.Unknown.IsSameBoardAs(DeviceIdentity.Unknown));

        var a = new DeviceIdentity("未知机型 A", DeviceIdentity.UnknownBoardCode);
        var b = new DeviceIdentity("未知机型 B", DeviceIdentity.UnknownBoardCode);

        Assert.False(a.IsSameBoardAs(b));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DeviceIdentity_BlankBoardCode_IsNotIdentified(string boardCode)
    {
        var device = new DeviceIdentity("某机型", boardCode);

        Assert.False(device.IsIdentified);
        Assert.False(device.IsSameBoardAs(new DeviceIdentity("另一台", boardCode)));
    }

    [Fact]
    public void DeviceIdentity_IdentifiedSameBoard_IsSameBoard()
    {
        var a = new DeviceIdentity("MSI Vector 16 HX AI A2XWHG", "MS-15M3");
        var b = new DeviceIdentity("同主板别名", "ms-15m3");

        Assert.True(a.IsIdentified);
        Assert.True(a.IsSameBoardAs(b));
    }

    [Fact]
    public void DeviceIdentity_UnknownNeverMatchesAnIdentifiedDevice()
    {
        // 单向也算：未知的一方不得与其后任何设备匹配。
        Assert.False(DeviceIdentity.Unknown.IsSameBoardAs(new DeviceIdentity("X", "MS-15M3")));
    }

    private static PerformanceSettings SettingsWith(PowerLimit longTerm, PowerLimit shortTerm)
        => new(
            PerformanceStage.Balanced,
            CpuLongTerm: longTerm,
            CpuShortTerm: shortTerm,
            CpuTurbo: CpuTurboLimit.Native,
            GpuLimit: PowerLimit.Custom(90m));

    [Fact]
    public void PerformanceSettings_Validate_RejectsShortTermBelowLongTerm()
    {
        var settings = SettingsWith(PowerLimit.Custom(60m), PowerLimit.Custom(10m));

        var problems = settings.Validate();

        Assert.Single(problems);
        Assert.Contains("自相矛盾", problems[0]);
    }

    [Theory]
    [InlineData(60, 60)]    // 相等：允许（默认就是把短时压到与持续一致）
    [InlineData(60, 80)]    // 短时高于持续：允许
    public void PerformanceSettings_Validate_AcceptsShortTermAtOrAboveLongTerm(decimal longTerm, decimal shortTerm)
    {
        var settings = SettingsWith(PowerLimit.Custom(longTerm), PowerLimit.Custom(shortTerm));

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void PerformanceSettings_Validate_AcceptsNativeLimitsWithoutWatts()
    {
        var settings = SettingsWith(PowerLimit.Native, PowerLimit.Native);

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void PerformanceSettings_Validate_RejectsCurveBoundToWrongChannel()
    {
        // 防呆：把 CPU 曲线写到 GPU 风扇上属于危险错误，必须能被检出。
        var points = new[] { new FanPoint(30, 0), new FanPoint(90, 100) };
        var settings = SettingsWith(PowerLimit.Custom(60m), PowerLimit.Custom(60m)) with
        {
            CpuCurve = FanCurve.Create(FanChannel.Gpu, points),
            GpuCurve = FanCurve.Create(FanChannel.Cpu, points)
        };

        var problems = settings.Validate();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.StartsWith("CPU 风扇曲线", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.StartsWith("GPU 风扇曲线", StringComparison.Ordinal));
    }

    [Fact]
    public void PerformanceSettings_Validate_AcceptsCorrectlyBoundCurves()
    {
        var points = new[] { new FanPoint(30, 0), new FanPoint(90, 100) };
        var settings = SettingsWith(PowerLimit.Custom(60m), PowerLimit.Custom(60m)) with
        {
            CpuCurve = FanCurve.Create(FanChannel.Cpu, points),
            GpuCurve = FanCurve.Create(FanChannel.Gpu, points)
        };

        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void Catalog_DefaultProfiles_AreAllSelfConsistent()
    {
        // 第 3 轮新增校验的直接应用：默认表必须全部通过。
        Assert.All(PerformanceProfileCatalog.Defaults, profile =>
            Assert.Empty(profile.Settings.Validate()));
    }

    // ═══════════════════════════════════════════════════════════
    // 第 4 轮 · 封装与跨区域性
    // 依据：FanCurve.Points 暴露底层数组，可强转后改写，破坏不可变性；
    //       PowerLimit.ToString 依赖当前区域性，宿主未启用
    //       InvariantGlobalization 时德语区域会输出 "60,5 W"。
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void FanCurve_Points_CannotBeCastBackToMutableArray()
    {
        var curve = FanCurve.Create(FanChannel.Cpu, new[] { new FanPoint(30, 0), new FanPoint(90, 100) });

        Assert.False(curve.Points is FanPoint[]);
        Assert.IsNotAssignableFrom<FanPoint[]>(curve.Points);
    }

    [Fact]
    public void FanCurve_Points_RemainsStableAcrossRepeatedReads()
    {
        var curve = FanCurve.Create(FanChannel.Cpu, new[] { new FanPoint(30, 0), new FanPoint(90, 100) });

        var first = curve.Points;
        var second = curve.Points;

        Assert.Same(first, second);
        Assert.Equal(first, second);
    }

    [Fact]
    public void FanCurve_WithChannel_KeepsPointsUnchanged()
    {
        var curve = FanCurve.Create(FanChannel.Cpu, new[] { new FanPoint(30, 0), new FanPoint(90, 100) });

        var gpu = curve.WithChannel(FanChannel.Gpu);

        Assert.Equal(FanChannel.Gpu, gpu.Channel);
        Assert.Equal(curve.Points, gpu.Points);
        Assert.Equal(2, gpu.NodeCount);
    }

    [Fact]
    public void PowerLimit_ToString_UsesFixedFormat()
    {
        // 说明：本工程启用了 InvariantGlobalization，无法在其内构造 de-DE 来验证区域性切换，
        // 因此此处锁定输出格式本身，防止后续把格式化改回依赖当前区域性。
        Assert.Equal("60.5 W", PowerLimit.Custom(60.5m).ToString());
        Assert.Equal("60 W", PowerLimit.Custom(60m).ToString());
        Assert.Equal("原生上限", PowerLimit.Native.ToString());
    }

    [Fact]
    public void CpuTurboLimit_ToString_UsesFixedFormat()
    {
        Assert.Equal("4800 MHz", CpuTurboLimit.Custom(4800).ToString());
        Assert.Equal("原生上限", CpuTurboLimit.Native.ToString());
    }

    [Fact]
    public void FanCurve_NodeCount_AndIndexingStillWorkAfterReadOnlyWrapping()
    {
        // 防回归：改用 ReadOnlyCollection 后，^1 索引与 Count 必须仍然可用。
        var curve = FanCurve.Create(FanChannel.Cpu, new[] { new FanPoint(30, 0), new FanPoint(90, 100) });

        Assert.Equal(2, curve.NodeCount);
        Assert.Equal(30, curve.MinTemperatureC);
        Assert.Equal(90, curve.MaxTemperatureC);
        Assert.Equal(50, curve.Interpolate(60), 6);
    }
}
