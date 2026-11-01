using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Fans;
using Xunit;

namespace MsiControl.Fans.Tests;

/// <summary>
/// 软件曲线引擎回归测试。
/// </summary>
/// <remarks>
/// 覆盖：门控拒绝启动、周期下发（插值 + 升快降慢限幅）、温度失败保持上次输出、
/// 连续失败自动停机、停止后交还固件（不主动恢复）。
/// 周期统一传 <see cref="Timeout.InfiniteTimeSpan"/> 级别的长间隔，
/// 由测试用 <see cref="FanCurveEngine.TickOnce"/> 显式驱动，避免定时器竞态。
/// </remarks>
public class FanCurveEngineTests
{
    /// <summary>测试用下发周期：远长于测试时长，等效「定时器不触发」。</summary>
    private static readonly TimeSpan TestInterval = TimeSpan.FromMinutes(10);

    private static FanCurveSet DefaultSet()
    {
        var points = new[]
        {
            new FanPoint(40, 20),
            new FanPoint(60, 40),
            new FanPoint(80, 100)
        };
        Assert.True(
            FanCurveSet.TryCreate(points, points, out var set, out var errors),
            string.Join(" ", errors));
        return set!;
    }

    [Fact]
    public void Start_WhenDutyWriteDisabled_RefusesAndExplains()
    {
        var output = new FakeFanOutputControl { IsDutyWriteEnabled = false };
        using var engine = new FanCurveEngine(output, _ => 60.0);

        var started = engine.Start(DefaultSet(), TestInterval);

        Assert.False(started);
        Assert.False(engine.IsRunning);
        Assert.Contains("不可用", engine.AutoStopReason, StringComparison.Ordinal);
        Assert.Empty(output.Requests);
    }

    [Fact]
    public void Tick_InterpolatesCurveTargetAndWritesBothChannels()
    {
        var output = new FakeFanOutputControl();
        using var engine = new FanCurveEngine(output, channel => channel == FanChannel.Cpu ? 60.0 : 70.0);
        Assert.True(engine.Start(DefaultSet(), TestInterval));
        Assert.Empty(output.Requests); // 首拍与后续周期同延迟，启动本身不写入。

        engine.TickOnce(DateTimeOffset.Now);

        // 60°C → 40%，70°C → 70%（线性插值）。
        Assert.Equal(40.0, output.Requests[FanChannel.Cpu]);
        Assert.Equal(70.0, output.Requests[FanChannel.Gpu]);
        Assert.True(engine.IsRunning);
        Assert.Equal(40.0, engine.LastSnapshots[FanChannel.Cpu].Percent);
        Assert.Equal(60.0, engine.LastSnapshots[FanChannel.Cpu].TemperatureC);
    }

    [Fact]
    public void Tick_RateLimitsFasterRiseAndSlowerFall()
    {
        var output = new FakeFanOutputControl();
        double temperature = 40;
        using var engine = new FanCurveEngine(output, _ => temperature);
        Assert.True(engine.Start(DefaultSet(), TestInterval));

        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(20.0, output.Requests[FanChannel.Cpu]); // 首个周期：无历史输出，直接写曲线值。

        // 温度骤升到 80°C：曲线目标 100%，但单周期升幅限 +20 → 40。
        temperature = 80;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(40.0, output.Requests[FanChannel.Cpu]);

        // 温度骤降回 40°C：曲线目标 20%，但单周期降幅限 -10 → 30。
        temperature = 40;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(30.0, output.Requests[FanChannel.Cpu]);
    }

    [Fact]
    public void Tick_TemperatureUnavailable_KeepsLastOutputAndRewrites()
    {
        var output = new FakeFanOutputControl();
        double? temperature = 60;
        using var engine = new FanCurveEngine(output, _ => temperature);
        Assert.True(engine.Start(DefaultSet(), TestInterval));
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(40.0, output.Requests[FanChannel.Cpu]);

        // 温度消失：保持上次输出并重写下发（对抗固件回写），不盲目输出、也不停机。
        temperature = null;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(40.0, output.Requests[FanChannel.Cpu]);
        Assert.True(engine.IsRunning);
        Assert.Null(engine.LastSnapshots[FanChannel.Cpu].TemperatureC);
    }

    [Fact]
    public void Tick_ConsecutiveSensorFailures_AutoStopsAndHandsBackToFirmware()
    {
        var output = new FakeFanOutputControl();
        double? temperature = 60;
        using var engine = new FanCurveEngine(output, _ => temperature);
        Assert.True(engine.Start(DefaultSet(), TestInterval));

        temperature = null;
        for (var i = 0; i < FanCurveEngine.MaxConsecutiveSensorFailures; i++)
        {
            engine.TickOnce(DateTimeOffset.Now);
        }

        Assert.False(engine.IsRunning);
        Assert.Contains("自动停机", engine.AutoStopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Tick_ConsecutiveWriteFailures_AutoStops()
    {
        var output = new FakeFanOutputControl { FailWrites = true };
        using var engine = new FanCurveEngine(output, _ => 60.0);
        Assert.True(engine.Start(DefaultSet(), TestInterval));

        // 写失败与温度失败阈值不同：写失败可自愈（固件接管），阈值放宽到 20 拍。
        Assert.True(FanCurveEngine.MaxConsecutiveWriteFailures > FanCurveEngine.MaxConsecutiveSensorFailures);

        for (var i = 0; i < FanCurveEngine.MaxConsecutiveWriteFailures - 1; i++)
        {
            engine.TickOnce(DateTimeOffset.Now);
        }

        // 未到阈值前必须继续运行，不得因瞬时失败停机。
        Assert.True(engine.IsRunning);

        engine.TickOnce(DateTimeOffset.Now);

        Assert.False(engine.IsRunning);
        Assert.Contains("下发失败", engine.AutoStopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Stop_ProducesNoRecoveryWrite_FirmwareSelfHeals()
    {
        var output = new FakeFanOutputControl();
        using var engine = new FanCurveEngine(output, _ => 60.0);
        Assert.True(engine.Start(DefaultSet(), TestInterval));
        engine.TickOnce(DateTimeOffset.Now);
        var writesAtRun = output.Requests.Count;

        engine.Stop();

        Assert.False(engine.IsRunning);
        Assert.Null(engine.RunningCurveSet);
        Assert.Equal(writesAtRun, output.Requests.Count); // 停止不产生任何额外写入。
    }

    [Fact]
    public void TickOnce_BeforeStart_IsNoOp()
    {
        var output = new FakeFanOutputControl();
        using var engine = new FanCurveEngine(output, _ => 60.0);

        engine.TickOnce(DateTimeOffset.Now);

        Assert.Empty(output.Requests);
    }

    // ── 启停抖动（2026-10-31 实测：风扇转一下停一下）─────────────────

    [Fact]
    public void Tick_ZeroTarget_ReleasesChannelAndNeverWritesZero()
    {
        var output = new FakeFanOutputControl();
        var points = new[]
        {
            new FanPoint(30, 0),
            new FanPoint(60, 0),
            new FanPoint(80, 100)
        };
        Assert.True(FanCurveSet.TryCreate(points, points, out var set, out var errors), string.Join(" ", errors));

        using var engine = new FanCurveEngine(output, _ => 55.0);
        Assert.True(engine.Start(set!, TestInterval));

        engine.TickOnce(DateTimeOffset.Now);

        // 0% 不是固件能保持的状态（写 0 会被回写成更高值，风扇被拉成「转一下停一下」），
        // 因此该通道必须交还固件：一拍都不写。
        Assert.Equal(0, output.WriteCount);
        var snapshot = engine.LastSnapshots[FanChannel.Cpu];
        Assert.True(snapshot.ReleasedToFirmware);
        Assert.Contains("交还固件", snapshot.Note, StringComparison.Ordinal);

        // 后续拍同样不写 0%，直到曲线目标回升。
        engine.TickOnce(DateTimeOffset.Now);
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(0, output.WriteCount);
        Assert.DoesNotContain(output.History, w => w.Percent <= 0);
    }

    [Fact]
    public void Tick_ReleasedChannel_RetakesWhenTargetBecomesPositiveAgain()
    {
        var output = new FakeFanOutputControl();
        var points = new[]
        {
            new FanPoint(40, 0),
            new FanPoint(60, 20),
            new FanPoint(80, 100)
        };
        Assert.True(FanCurveSet.TryCreate(points, points, out var set, out var errors), string.Join(" ", errors));

        double temperature = 40; // 目标 0%：交还固件，不写。
        using var engine = new FanCurveEngine(output, _ => temperature);
        Assert.True(engine.Start(set!, TestInterval));

        engine.TickOnce(DateTimeOffset.Now);
        Assert.True(engine.LastSnapshots[FanChannel.Cpu].ReleasedToFirmware);
        Assert.Equal(0, output.WriteCount);

        // 目标变正（45°C → 5%）：重新接管，并按可维持下限 20% 下发。
        temperature = 45;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.False(engine.LastSnapshots[FanChannel.Cpu].ReleasedToFirmware);
        Assert.Equal(FanCurveEngine.MinimumControlledDuty, output.Requests[FanChannel.Cpu]);

        // 目标回到 0（40°C）：再次交还，且不再写 0。
        temperature = 40;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.True(engine.LastSnapshots[FanChannel.Cpu].ReleasedToFirmware);
        Assert.DoesNotContain(output.History, w => w.Percent <= 0);
    }

    [Fact]
    public void Tick_TargetBelowMinimumRunDuty_IsRaisedAndLoggedOnce()
    {
        var output = new FakeFanOutputControl();
        var points = new[]
        {
            new FanPoint(40, 5),
            new FanPoint(80, 100)
        };
        Assert.True(FanCurveSet.TryCreate(points, points, out var set, out var errors), string.Join(" ", errors));

        using var engine = new FanCurveEngine(output, _ => 40.0);
        Assert.True(engine.Start(set!, TestInterval));

        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(FanCurveEngine.MinimumControlledDuty, output.Requests[FanChannel.Cpu]);
        Assert.False(engine.LastSnapshots[FanChannel.Cpu].ReleasedToFirmware);

        // 下一拍数值不变，仍按同一值下发（对抗固件回写），但不会重复记日志。
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(FanCurveEngine.MinimumControlledDuty, output.Requests[FanChannel.Cpu]);
    }

    [Fact]
    public void Tick_SmallCoolingWithinHysteresis_DoesNotLowerDuty()
    {
        var output = new FakeFanOutputControl();
        double temperature = 60;
        using var engine = new FanCurveEngine(output, _ => temperature);
        Assert.True(engine.Start(DefaultSet(), TestInterval));

        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(40.0, output.Requests[FanChannel.Cpu]); // 60°C → 40%

        // 回落 1°C：在迟滞带内，不改变求值温度 → 输出保持 40%（否则会在边界抖动）。
        temperature = 59;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(40.0, output.Requests[FanChannel.Cpu]);

        // 回落超过 2°C：迟滞带外，按实际温度求值（57°C → 37%），降幅 3 ≤ 10 不触发降速限幅。
        temperature = 57;
        engine.TickOnce(DateTimeOffset.Now);
        Assert.Equal(37.0, output.Requests[FanChannel.Cpu]);
    }

    /// <summary>测试替身：可编程的占空比下发通道。</summary>
    private sealed class FakeFanOutputControl : IFanOutputControl
    {
        public bool IsDutyWriteEnabled { get; set; } = true;

        public bool FailWrites { get; set; }

        public Dictionary<FanChannel, double> Requests { get; } = new();

        /// <summary>累计写入次数（含重复写同一值）。用于断言「没有写入」。</summary>
        public int WriteCount { get; private set; }

        /// <summary>按通道记录的写入序列，用于检查是否出现过 0%。</summary>
        public List<(FanChannel Channel, double Percent)> History { get; } = new();

        public Task<ControlResult> SetDutyPercentAsync(
            FanChannel channel,
            double percent,
            CancellationToken cancellationToken)
        {
            WriteCount++;
            History.Add((channel, percent));
            Requests[channel] = percent;
            var setting = FailWrites
                ? SettingResult.Failed($"fan.duty.{channel}".ToLowerInvariant(), $"{percent:0}%", "测试替身：写入失败。")
                : SettingResult.Succeeded(
                    $"fan.duty.{channel}".ToLowerInvariant(),
                    $"{percent:0}%",
                    $"{percent:0}%",
                    "测试替身：写入+读回一致。");
            return Task.FromResult(ControlResult.Single(setting));
        }
    }
}
