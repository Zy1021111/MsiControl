using Xunit;

namespace MsiControl.Performance.Tests;

/// <summary>测试用的假进程存在性探测。</summary>
internal sealed class FakeProcessPresenceProbe : IProcessPresenceProbe
{
    private readonly HashSet<string> _running;

    public FakeProcessPresenceProbe(params string[] running)
        => _running = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FindRunning(IReadOnlyList<string> processNames)
        => processNames.Where(_running.Contains).ToArray();
}

/// <summary>
/// 第三方控制工具的共存检测。
/// </summary>
/// <remarks>
/// 核心回归点：<b>「第三方在运行」不等于「设置已被覆盖」</b>；
/// 且检测结果必须携带「不修改、不停止、不卸载、不无限重写」的边界说明。
/// </remarks>
public sealed class ThirdPartyConflictTests
{
    [Fact]
    public void NoRunningProcess_YieldsNoConflict()
    {
        var conflicts = ThirdPartyConflictDetector.Detect(new FakeProcessPresenceProbe());

        Assert.Empty(conflicts);
        Assert.Null(ThirdPartyConflictDetector.Describe(conflicts));
    }

    [Fact]
    public void DetectsXtu_WithItsOverlappingSettingsOnly()
    {
        var conflicts = ThirdPartyConflictDetector.Detect(new FakeProcessPresenceProbe("XTU"));

        var conflict = Assert.Single(conflicts);
        Assert.Equal("Intel XTU", conflict.DisplayName);
        Assert.Contains(PerformanceSettingKeys.CpuLongTerm, conflict.OverlappingSettings);
        Assert.Contains(PerformanceSettingKeys.CpuTurbo, conflict.OverlappingSettings);
        Assert.DoesNotContain(PerformanceSettingKeys.GpuLimit, conflict.OverlappingSettings);
    }

    [Fact]
    public void DetectsBothKnownTools()
    {
        var conflicts = ThirdPartyConflictDetector.Detect(
            new FakeProcessPresenceProbe("XTU3SERVICE", "MSIAfterburner"));

        Assert.Equal(2, conflicts.Count);
        Assert.Contains(conflicts, c => c.DisplayName == "MSI Afterburner"
            && c.OverlappingSettings.Contains(PerformanceSettingKeys.GpuLimit));
    }

    [Fact]
    public void Guidance_DistinguishesRunningFromBeingOverridden()
    {
        var conflicts = ThirdPartyConflictDetector.Detect(new FakeProcessPresenceProbe("XTU"));

        var guidance = Assert.Single(conflicts).Guidance;
        Assert.Contains("不代表设置已被覆盖", guidance);
        Assert.Contains("读回值与请求值不一致", guidance);
    }

    [Fact]
    public void Guidance_StatesBoundaryOfNotTouchingThirdParty()
    {
        var conflicts = ThirdPartyConflictDetector.Detect(new FakeProcessPresenceProbe("MSIAfterburner"));

        var guidance = Assert.Single(conflicts).Guidance;
        Assert.Contains("不会修改、停止或卸载", guidance);
        Assert.Contains("无限循环写入", guidance);
    }

    [Fact]
    public void KnownSources_CoverOnlyDocumentedTools()
    {
        var names = ThirdPartyConflictDetector.KnownSources.Select(s => s.DisplayName).ToArray();

        Assert.Contains("Intel XTU", names);
        Assert.Contains("MSI Afterburner", names);
        Assert.Equal(2, names.Length);
    }

    [Fact]
    public void Detect_RejectsNullProbe()
        => Assert.Throws<ArgumentNullException>(() => ThirdPartyConflictDetector.Detect(null!));
}
