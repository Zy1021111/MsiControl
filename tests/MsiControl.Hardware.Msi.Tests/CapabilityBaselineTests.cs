using System.Reflection;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Capabilities;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests;

/// <summary>
/// 能力基线回归测试。
/// </summary>
/// <remarks>
/// 重点验证「不显示虚假成功」的默认安全方向：
/// 证据不足者一律禁用；只有已确证的能力可开放写入。
/// </remarks>
public class CapabilityBaselineTests
{
    private static readonly DeviceCapabilities Baseline = Ms15M3Capabilities.Create();

    // ---------- 覆盖完整性 ----------

    [Fact]
    public void Baseline_RegistersEveryCapabilityKey()
    {
        // 防回归：新增能力键后若忘记登记基线，会在此失败。
        Assert.All(CapabilityKeys.All, key =>
            Assert.Equal(key, Baseline.Get(key).Key));
        Assert.Equal(CapabilityKeys.All.Count, Baseline.Count);
    }

    [Fact]
    public void CapabilityKeys_All_ContainsEveryDeclaredConstant()
    {
        // 防回归：CapabilityKeys 里新增了 public const 却忘记加入 All，会在此失败。
        var declared = typeof(CapabilityKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        Assert.NotEmpty(declared);
        Assert.All(declared, key => Assert.Contains(key, CapabilityKeys.All));
    }

    [Fact]
    public void Baseline_BoardCodeMatchesTargetDevice()
    {
        Assert.Equal(DeviceIdentity.TargetBoardCode, Ms15M3Capabilities.BoardCode);
    }

    // ---------- 等级语义 ----------

    [Fact]
    public void Baseline_FanCurve_IsVerifiedForSoftwareCurveEngine()
    {
        // HWE §23（2026-10-17）：EC 占空比 0x71/0x89 写入+立即读回一致实证，
        // 叶哥授权启用软件曲线引擎。固件整体曲线写入（ApplyCurveAsync）仍不支持。
        Assert.Equal(CapabilityLevel.Verified, Baseline.LevelOf(CapabilityKeys.FanCurve));
        Assert.True(Baseline.AllowsRead(CapabilityKeys.FanCurve));
        Assert.True(Baseline.AllowsWrite(CapabilityKeys.FanCurve));
    }

    [Fact]
    public void Baseline_VerifiedCapabilities_MatchEvidence()
    {
        // 「已确证」能力集合（2026-10-28 更新，HWE §17/§20/§21/§22/§26）：
        // 风扇模式（0xD4 三编码写入）、风扇曲线（0x71/0x89 占空比，软件引擎保持）、
        // 强冷（0x98，叶哥决定启用）、性能档位（0xD2 三编码写入）、
        // 显卡模式（HWE §21 协议 + 本机只读对照，叶哥授权放行）、
        // CPU 功耗限制（HWE §26：MSR 0x610 可写 + 读回 + 切档不被覆盖，本机实测）
        // 与通用 ACPI 热区（读）。
        Assert.Equal(CapabilityLevel.Verified, Baseline.LevelOf(CapabilityKeys.ThermalFallback));
        Assert.True(Baseline.AllowsRead(CapabilityKeys.ThermalFallback));

        var verified = Ms15M3Capabilities.Statuses
            .Where(s => s.Level == CapabilityLevel.Verified)
            .Select(s => s.Key)
            .ToArray();

        Assert.Equal(
            new[]
            {
                CapabilityKeys.FanCurve,
                CapabilityKeys.FanFullSpeed,
                CapabilityKeys.FanMode,
                CapabilityKeys.ThermalFallback,
                CapabilityKeys.PerformanceStage,
                CapabilityKeys.CpuPowerLimit,
                CapabilityKeys.GraphicsMode,
                // 2026-10-30 新增（HWE §29，来源 E19 实机验证）：
                // 音量/麦克风/亮度/触控板走 Windows 官方接口，热键监听走 WMI 事件，
                // 四项读写与一项事件订阅均已实机读回，故一并计入已确证集合。
                CapabilityKeys.AudioVolume,
                CapabilityKeys.AudioMicrophone,
                CapabilityKeys.DisplayBrightness,
                CapabilityKeys.InputTouchpad,
                CapabilityKeys.HotkeyFn,
                // 2026-10-31 新增（HWE §32，来源 E22 实机验证）：
                // 蓝牙射频走 WinRT Radio，Off→On→Off 往返读回一致。
                CapabilityKeys.SystemBluetooth,
                // 2026-10-31 新增（HWE §31，来源 E21 本机受控实验）：
                // 键盘背光 / Win-Fn 交换 / USB 共享供电三项均「写入→读回→恢复」全部一致，
                // 走 MSI_ACPI Get_Data/Set_Data（Package_32 内嵌实例）通道。
                CapabilityKeys.KeyboardBacklight,
                CapabilityKeys.InputWinFnSwap,
                CapabilityKeys.PowerUsbShare,
                // 显示省电：Win32 显示模式 API，本机 260→60→260 Hz 实测读回一致。
                CapabilityKeys.DisplayPowerSaver
            },
            verified);
    }

    [Theory]
    [InlineData(CapabilityKeys.SystemPowerRead)] // 整机功耗：无可靠来源
    public void Baseline_UnconfirmedCapabilities_AreUnknownAndDisabled(string key)
    {
        var status = Baseline.Get(key);

        Assert.Equal(CapabilityLevel.Unknown, status.Level);
        Assert.False(Baseline.AllowsRead(key));
        Assert.False(Baseline.AllowsWrite(key));
        Assert.False(string.IsNullOrWhiteSpace(status.Reason)); // 必须给出禁用原因
    }

    [Fact]
    public void Baseline_GraphicsMode_IsVerifiedWithRestartSemantics()
    {
        // 显卡模式（HWE §21）：协议依据 + 本机只读对照 + 用户授权 → Verified；
        // 写入后需重启生效，本程序不自动重启（任务书 §9.1）。
        var status = Baseline.Get(CapabilityKeys.GraphicsMode);

        Assert.Equal(CapabilityLevel.Verified, status.Level);
        Assert.True(Baseline.AllowsWrite(CapabilityKeys.GraphicsMode));
        Assert.False(string.IsNullOrWhiteSpace(status.Evidence));
    }

    [Fact]
    public void Baseline_NoCapabilityIsUnsupported()
    {
        // MSI_CPU 等 5 个类不是「能力键」，而是被排除的硬件类；
        // 能力键层面不应出现 Unsupported 等级（待实测后再分流）。
        Assert.All(Ms15M3Capabilities.Statuses,
            s => Assert.NotEqual(CapabilityLevel.Unsupported, s.Level));
    }

    [Fact]
    public void Baseline_DetectedOrAbove_CarriesEvidence()
    {
        // 等级为 Detected 及以上时必须有可追溯证据。
        Assert.All(
            Ms15M3Capabilities.Statuses.Where(s => s.Level is CapabilityLevel.Verified or CapabilityLevel.Detected),
            s => Assert.False(string.IsNullOrWhiteSpace(s.Evidence)));
    }

    // ---------- 排除类与未适配机型 ----------

    [Fact]
    public void UnsupportedHardwareClasses_ListsTheFiveExcludedClasses()
    {
        Assert.Equal(5, Ms15M3Capabilities.UnsupportedHardwareClasses.Count);
        Assert.Contains("MSI_CPU", Ms15M3Capabilities.UnsupportedHardwareClasses);
        Assert.Contains("MSI_Slave_Battery", Ms15M3Capabilities.UnsupportedHardwareClasses);
    }

    [Fact]
    public void ForDevice_TargetBoard_ReturnsMs15M3Baseline()
    {
        var device = new DeviceIdentity("MSI Vector 16 HX AI A2XWHG", "MS-15M3");

        var capabilities = CapabilityBaseline.ForDevice(device);

        Assert.Equal(CapabilityLevel.Verified, capabilities.LevelOf(CapabilityKeys.FanCurve));
    }

    [Fact]
    public void ForDevice_UnknownBoard_DisablesEverything()
    {
        // 未适配机型：一律按 Unknown 处理即全部禁用（默认安全方向）。
        var device = new DeviceIdentity("其他机型", "MS-17S1");

        var capabilities = CapabilityBaseline.ForDevice(device);

        Assert.Equal(CapabilityLevel.Unknown, capabilities.LevelOf(CapabilityKeys.FanCurve));
        Assert.False(capabilities.AllowsWrite(CapabilityKeys.FanCurve));
        Assert.False(capabilities.AllowsRead(CapabilityKeys.ThermalFallback));
    }
}
