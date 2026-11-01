using MsiControl.Core;
using MsiControl.Persistence.Configuration;
using Xunit;

namespace MsiControl.Persistence.Tests;

/// <summary>
/// 风扇曲线配置档存储回归测试。
/// </summary>
public class FanCurveProfileStoreTests : IDisposable
{
    private readonly string _directory;

    public FanCurveProfileStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"msi-control-fancurve-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删除失败不影响测试结论。
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsProfilesAndPreferences()
    {
        var store = new FanCurveProfileStore(_directory);
        var profile = new FanCurveProfile(
            Guid.NewGuid(),
            "静音",
            "自定义",
            DateTimeOffset.Now,
            new[] { new FanProfilePoint(0, 0), new FanProfilePoint(75, 100) },
            new[] { new FanProfilePoint(0, 9), new FanProfilePoint(75, 100) });
        var file = new FanCurveProfileFile
        {
            Profiles = new[] { profile },
            SelectedId = profile.Id,
            Sort = "Name",
            TagFilter = "自定义",
            ModePreference = "Manual"
        };

        store.Save(file);
        var loaded = store.Load();

        Assert.Single(loaded.Profiles);
        Assert.Equal(profile.Id, loaded.Profiles[0].Id);
        Assert.Equal(profile.Name, loaded.Profiles[0].Name);
        Assert.Equal(profile.CpuPoints, loaded.Profiles[0].CpuPoints);
        Assert.Equal(profile.GpuPoints, loaded.Profiles[0].GpuPoints);
        Assert.Equal(profile.Id, loaded.SelectedId);
        Assert.Equal("Name", loaded.Sort);
        Assert.Equal("自定义", loaded.TagFilter);
        Assert.Equal("Manual", loaded.ModePreference);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDefaults()
    {
        var store = new FanCurveProfileStore(_directory);

        var loaded = store.Load();

        Assert.Empty(loaded.Profiles);
        Assert.Null(loaded.SelectedId);
        Assert.Equal("Modified", loaded.Sort);
    }

    [Fact]
    public void Load_CorruptFile_QuarantinesAndReturnsEmpty()
    {
        var store = new FanCurveProfileStore(_directory);
        File.WriteAllText(store.FilePath, "{ 这不是 JSON ");

        var loaded = store.Load();

        Assert.Empty(loaded.Profiles);
        // 损坏原件必须被改名保留（隔离），而不是直接覆盖丢失。
        var leftovers = Directory.GetFiles(_directory);
        Assert.Contains(leftovers, f => f.Contains(".corrupt-", StringComparison.Ordinal));
    }

    [Fact]
    public void Save_UsesAtomicReplace_NoTemporaryLeftover()
    {
        var store = new FanCurveProfileStore(_directory);

        store.Save(new FanCurveProfileFile());

        Assert.True(File.Exists(store.FilePath));
        Assert.DoesNotContain(Directory.GetFiles(_directory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void SlotCurves_RoundTripAllSixSlots()
    {
        var store = new FanCurveProfileStore(_directory);
        var slotCurves = new Dictionary<string, FanCurveProfile>(StringComparer.Ordinal);
        foreach (var slot in FanCurveSlots.All)
        {
            slotCurves[FanCurveSlots.KeyOf(slot)] = new FanCurveProfile(
                Guid.NewGuid(),
                slot.ToString(),
                "槽位",
                DateTimeOffset.Now,
                new[] { new FanProfilePoint(0, 0), new FanProfilePoint(75, 100) },
                new[] { new FanProfilePoint(0, 9), new FanProfilePoint(75, 100) });
        }

        store.Save(new FanCurveProfileFile { SlotCurves = slotCurves });
        var loaded = store.Load();

        Assert.Equal(FanCurveSlots.All.Count, loaded.SlotCurves.Count);
        foreach (var slot in FanCurveSlots.All)
        {
            var key = FanCurveSlots.KeyOf(slot);
            Assert.True(loaded.SlotCurves.TryGetValue(key, out var curve), $"槽位 {key} 未被持久化。");
            Assert.Equal(2, curve!.CpuPoints.Count);
            Assert.Equal(100, curve.GpuPoints[1].O);
        }
    }

    [Fact]
    public void SlotCurves_MissingFile_IsEmptyByDefault()
    {
        var store = new FanCurveProfileStore(_directory);

        var loaded = store.Load();

        Assert.Empty(loaded.SlotCurves);
    }

    [Fact]
    public void SlotKeys_MapStagesOneToOne_AndCustomHasNoStage()
    {
        Assert.Equal(6, FanCurveSlots.All.Count);
        Assert.Equal(FanCurveSlots.All.Count, FanCurveSlots.All.Select(FanCurveSlots.KeyOf).Distinct().Count());

        foreach (var stage in Enum.GetValues<PerformanceStage>())
        {
            var slot = FanCurveSlots.ForStage(stage);
            Assert.Equal(stage, FanCurveSlots.StageOf(slot));
        }

        Assert.Null(FanCurveSlots.StageOf(FanCurveSlot.Custom));
        Assert.Equal("custom", FanCurveSlots.KeyOf(FanCurveSlot.Custom));
        Assert.Equal("high", FanCurveSlots.KeyOf(FanCurveSlot.HighPerformance));
    }
}
