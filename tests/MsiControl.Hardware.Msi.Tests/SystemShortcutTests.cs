using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Hotkeys;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests;

/// <summary>
/// Fn 热键码映射回归测试。
/// </summary>
/// <remarks>
/// 码值语义唯一来源是 HWE §29 记录的 <c>MsiCenterOsdWmiEventCode</c>。
/// 这里锁死「已确证的三项必须正确」与「未知码必须落到 Unknown」两个方向：
/// 前者防功能回退，后者防「猜键位」——本机实测 <c>MSIEvt</c> 低字节里
/// 25/29/120 有语义证据，其余都没有。
/// </remarks>
public class FnHotkeyMappingTests
{
    [Theory]
    [InlineData(25, FnHotkeyAction.MicrophoneMute)]
    [InlineData(29, FnHotkeyAction.PerformanceStage)]
    [InlineData(67, FnHotkeyAction.Touchpad)]
    [InlineData(68, FnHotkeyAction.Touchpad)]
    [InlineData(86, FnHotkeyAction.BluetoothToggle)]
    [InlineData(92, FnHotkeyAction.FnLock)]
    [InlineData(120, FnHotkeyAction.BluetoothToggle)]
    public void MapCode_KnownCodesMapToExpectedAction(int code, FnHotkeyAction expected)
    {
        Assert.Equal(expected, MsiWmiHotkeySource.MapCode(code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(34)]   // 0x22：MSIEvt 的高字节，绝不能被当成键码
    [InlineData(35)]   // 本机实测出现过，但无语义证据
    [InlineData(50)]
    [InlineData(999)]
    public void MapCode_UnprovenCodesStayUnknown(int code)
    {
        // 未知码宁可只触发状态刷新，也不能映射成某个功能——错映射比不映射更糟。
        Assert.Equal(FnHotkeyAction.Unknown, MsiWmiHotkeySource.MapCode(code));
    }

    [Fact]
    public void FnHotkeyEvent_RequiresSource()
    {
        Assert.Throws<ArgumentException>(() => FnHotkeyEvent.Create(FnHotkeyAction.Touchpad, 67, "  "));
    }

    [Fact]
    public void FnHotkeyEvent_PreservesRawCodeForDiagnostics()
    {
        var evt = FnHotkeyEvent.Create(FnHotkeyAction.MicrophoneMute, 0x00220019, "MSI_Event");
        Assert.Equal(0x00220019, evt.RawCode);
        Assert.Equal("MSI_Event", evt.Source);
    }
}

/// <summary>
/// 系统快捷功能的状态模型回归测试。
/// </summary>
public class SystemShortcutModelTests
{
    [Fact]
    public void AudioEndpointState_ClampsVolumeToUnitRange()
    {
        Assert.Equal(1f, AudioEndpointState.Create(false, 1.7f).VolumeScalar);
        Assert.Equal(0f, AudioEndpointState.Create(false, -0.2f).VolumeScalar);
    }

    [Fact]
    public void AudioEndpointState_RejectsNonFiniteVolume()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEndpointState.Create(false, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => AudioEndpointState.Create(false, float.PositiveInfinity));
    }

    [Fact]
    public void AudioEndpointState_ExposesPercent()
    {
        Assert.Equal(19, AudioEndpointState.Create(false, 0.19f).VolumePercent);
    }

    [Fact]
    public void BrightnessRange_FromLevels_ReturnsNullWhenEmpty()
    {
        Assert.Null(BrightnessRange.FromLevels(Array.Empty<int>()));
    }

    [Fact]
    public void BrightnessRange_FromLevels_DerivesStepFromShortestGap()
    {
        var range = BrightnessRange.FromLevels(new[] { 100, 0, 50 });
        Assert.NotNull(range);
        Assert.Equal(0, range!.MinPercent);
        Assert.Equal(100, range.MaxPercent);
        Assert.Equal(50, range.StepPercent);
    }

    [Fact]
    public void BrightnessRange_FromLevels_SingleLevelUsesUnitStep()
    {
        var range = BrightnessRange.FromLevels(new[] { 80 });
        Assert.NotNull(range);
        Assert.Equal(1, range!.StepPercent);
        Assert.Equal(80, range.MinPercent);
        Assert.Equal(80, range.MaxPercent);
    }

    [Fact]
    public void PointingDeviceState_RequiresSource()
    {
        Assert.Throws<ArgumentException>(() => PointingDeviceState.Create(true, true, " "));
    }

    [Fact]
    public void SystemShortcutTarget_BluetoothSettingsIsStable()
    {
        // 枚举值进入持久化/日志，禁止随意重排。
        Assert.Equal(0, (int)SystemShortcutTarget.BluetoothSettings);
        Assert.Equal(0, (int)AudioEndpointKind.Playback);
        Assert.Equal(1, (int)AudioEndpointKind.Recording);
    }
}
