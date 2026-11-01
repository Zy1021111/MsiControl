using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using Xunit;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// 显卡模式切换策略的回归测试。
/// </summary>
/// <remarks>
/// 能力门控用例使用 <see cref="CapabilityLevel.Unknown"/>（生产回退路径），
/// 其余分支用显式构造的 <see cref="CapabilityLevel.Verified"/> 能力集合覆盖
/// 「拓扑未知、外接屏风险、已是目标模式、可安全切换、MUX 有编码模式」的行为。
/// </remarks>
public class GraphicsSwitchPolicyTests
{
    private static readonly DeviceCapabilities UnknownCapabilities = DeviceCapabilities.Empty;

    private static readonly DeviceCapabilities VerifiedCapabilities = new(new[]
    {
        CapabilityStatus.Verified(CapabilityKeys.GraphicsMode, "测试用证据：仅用于覆盖策略分支。")
    });

    private static GraphicsHardwareState HybridState()
        => GraphicsModeInference.Build(TopologyFactory.HybridWithExternalOnDiscrete());

    // ---------- 1. 能力门控 ----------

    [Fact]
    public void Evaluate_UnknownCapability_IsRejectedAndExplainsWhy()
    {
        var decision = GraphicsSwitchPolicy.Evaluate(
            HybridState(),
            GraphicsMode.Integrated,
            UnknownCapabilities,
            topologyKnown: true);

        Assert.False(decision.Allowed);
        Assert.Equal(OperationStatus.Unsupported, decision.Status);
        Assert.False(decision.RequiresRestart);
        Assert.Contains("Unknown", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CapabilityGatePrecedesEveryOtherCheck()
    {
        // 即便拓扑未知、目标也不受支持，也应先报「能力未通过」，避免掩盖首要原因。
        var unknownTopologyState = GraphicsModeInference.Build(GraphicsDisplayTopology.Unknown);

        var decision = GraphicsSwitchPolicy.Evaluate(
            unknownTopologyState,
            GraphicsMode.Discrete,
            UnknownCapabilities,
            topologyKnown: false);

        Assert.False(decision.Allowed);
        Assert.Equal(OperationStatus.Unsupported, decision.Status);
        Assert.Contains("能力门控", decision.Reason, StringComparison.Ordinal);
    }

    // ---------- 2. 拓扑未知 ----------

    [Fact]
    public void Evaluate_KnownCapabilityButUnknownTopology_IsRejectedRatherThanAssumedSafe()
    {
        var decision = GraphicsSwitchPolicy.Evaluate(
            HybridState(),
            GraphicsMode.Discrete,
            VerifiedCapabilities,
            topologyKnown: false);

        Assert.False(decision.Allowed);
        Assert.Equal(OperationStatus.Failed, decision.Status);   // 前置条件不满足，不是「不支持」
        Assert.Contains("拓扑", decision.Reason, StringComparison.Ordinal);
    }

    // ---------- 3. 目标模式支持性 ----------

    [Fact]
    public void Evaluate_TargetModeWithoutEvidence_IsRejected()
    {
        // 「核显模式」在 MUX 协议中没有编码（HWE §21）→ 即便能力为 Verified 也不支持。
        var decision = GraphicsSwitchPolicy.Evaluate(
            HybridState(),
            GraphicsMode.Integrated,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.False(decision.Allowed);
        Assert.Equal(OperationStatus.Unsupported, decision.Status);
        Assert.Contains("核显", decision.Reason, StringComparison.Ordinal);
    }

    // ---------- 3b. 有编码模式的真实切换 ----------

    [Fact]
    public void Evaluate_HybridToDiscrete_WithExternalOnDiscrete_IsAllowed()
    {
        // 本机实测拓扑（HWE §12.1）：外接屏由独显驱动；切到独显直连不会断开外接屏，
        // 切到混合亦保留独显输出（Core 规则 2026-10-17 修订）。混合 → 独显直连应放行。
        var decision = GraphicsSwitchPolicy.Evaluate(
            HybridState(),
            GraphicsMode.Discrete,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresRestart);
        Assert.Equal(OperationStatus.PendingRestart, decision.Status);
        Assert.True(decision.RequiresWrite);
    }

    // ---------- 4. 已是目标模式 ----------

    [Fact]
    public void Evaluate_AlreadyAtTarget_AllowsWithoutRestart()
    {
        var decision = GraphicsSwitchPolicy.Evaluate(
            HybridState(),
            GraphicsMode.Hybrid,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.True(decision.Allowed);
        Assert.False(decision.RequiresRestart);
        Assert.Equal(OperationStatus.Succeeded, decision.Status);
    }

    // ---------- 4b. 已是目标模式，但存在待生效的另一态（HWE §27）----------

    [Fact]
    public void Evaluate_AtTargetButAnotherModePending_StillRequiresWrite()
    {
        // 实测场景：重启后拓扑仍是混合（Current=Hybrid），但之前提交的目标是独显（Pending=Discrete）。
        // 用户此刻点「混合」——若只看 Current 就会判成「无需切换」，于是不写入，
        // 重启后落到独显：这正是「切回去没用」的根因。
        var state = HybridState() with
        {
            Pending = GraphicsMode.Discrete,
            RequiresRestart = true
        };

        Assert.True(state.HasPendingChange);

        var decision = GraphicsSwitchPolicy.Evaluate(
            state,
            GraphicsMode.Hybrid,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresRestart);
        Assert.Equal(OperationStatus.PendingRestart, decision.Status);
        Assert.True(decision.RequiresWrite);
        Assert.Contains("独显直连", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("改回", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_AtTargetWithMatchingPending_IsNoOp()
    {
        // 待生效目标与当前实际相同（EC 登记已被拓扑印证）：没有东西要改，不该写。
        var state = HybridState() with { Pending = GraphicsMode.Hybrid };

        Assert.False(state.HasPendingChange);

        var decision = GraphicsSwitchPolicy.Evaluate(
            state,
            GraphicsMode.Hybrid,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.Equal(OperationStatus.Succeeded, decision.Status);
        Assert.False(decision.RequiresWrite);
    }

    // ---------- 5. 外接屏风险三态 ----------

    [Fact]
    public void Evaluate_ExternalDisplayWouldDisconnect_IsRejected()
    {
        // 当前独显直连且接了外接屏：内置屏与外接屏都由独显驱动。
        var state = GraphicsModeInference.Build(TopologyFactory.DiscreteWithExternalOnDiscrete()) with
        {
            SupportsIntegrated = true
        };

        Assert.True(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));

        var decision = GraphicsSwitchPolicy.Evaluate(
            state,
            GraphicsMode.Integrated,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.False(decision.Allowed);
        Assert.Equal(OperationStatus.Failed, decision.Status);
        Assert.Contains("断开", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_UnknownDisconnectRisk_IsRejected()
    {
        var state = GraphicsModeInference.Build(TopologyFactory.ExternalOnUnknownGpu()) with
        {
            SupportsIntegrated = true
        };

        Assert.Null(state.WouldDisconnectExternalDisplay(GraphicsMode.Integrated));

        var decision = GraphicsSwitchPolicy.Evaluate(
            state,
            GraphicsMode.Integrated,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.False(decision.Allowed);
        Assert.Equal(OperationStatus.Failed, decision.Status);
        Assert.Contains("未知", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RestartRequiredWhenSwitchIsSafe()
    {
        // 未接外接屏，当前独显直连；目标「混合」不涉及外接屏断开 → 允许，但需重启生效。
        var state = GraphicsModeInference.Build(TopologyFactory.InternalOnDiscrete()) with
        {
            SupportsHybrid = true
        };

        var decision = GraphicsSwitchPolicy.Evaluate(
            state,
            GraphicsMode.Hybrid,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresRestart);
        Assert.Equal(OperationStatus.PendingRestart, decision.Status);
        Assert.Contains("重启", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_ExternalOnIntegrated_DoesNotBlockSwitchToDiscrete()
    {
        // 外接屏由核显驱动时，切到独显直连不会断开外接屏（Core 三态规则的边界）。
        var state = GraphicsModeInference.Build(
            TopologyFactory.Create(TopologyFactory.Internal(GpuKind.Discrete))) with
        {
            Current = GraphicsMode.Hybrid,
            ExternalDisplayAttached = true,
            ExternalDisplayDependsOnDiscreteGpu = false,
            SupportsHybrid = true,
            SupportsDiscrete = true
        };

        var decision = GraphicsSwitchPolicy.Evaluate(
            state,
            GraphicsMode.Discrete,
            VerifiedCapabilities,
            topologyKnown: true);

        Assert.True(decision.Allowed);
        Assert.True(decision.RequiresRestart);
    }

    // ---------- 结果构造 ----------

    [Fact]
    public void ToSettingResult_ProducesResultsThatPassCoreValidation()
    {
        var decisions = new[]
        {
            GraphicsSwitchPolicy.Evaluate(HybridState(), GraphicsMode.Integrated, UnknownCapabilities, true),
            GraphicsSwitchPolicy.Evaluate(HybridState(), GraphicsMode.Discrete, VerifiedCapabilities, false),
            GraphicsSwitchPolicy.Evaluate(HybridState(), GraphicsMode.Discrete, VerifiedCapabilities, true),
            GraphicsSwitchPolicy.Evaluate(HybridState(), GraphicsMode.Hybrid, VerifiedCapabilities, true)
        };

        foreach (var decision in decisions)
        {
            var result = ControlResult.Single(decision.ToSettingResult(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                GraphicsSwitchPolicy.Describe(GraphicsMode.Hybrid)));

            var setting = result.Settings[0];

            // 契约：成功必须有 ConfirmationBasis；不支持必须有说明（落在 Message）。
            Assert.Empty(result.Validate());
            Assert.Equal(GraphicsSwitchPolicy.GraphicsModeSettingKey, setting.Setting);
            Assert.False(string.IsNullOrWhiteSpace(setting.Message ?? setting.ConfirmationBasis));
        }
    }

    [Fact]
    public void ToSettingResult_BlockedDecision_IsFailedNotUnsupported()
    {
        var decision = GraphicsSwitchPolicy.Evaluate(
            HybridState(),
            GraphicsMode.Discrete,
            VerifiedCapabilities,
            topologyKnown: false);

        var setting = decision.ToSettingResult("graphics.mode", "独显直连");

        Assert.Equal(OperationStatus.Failed, setting.Status);
        Assert.Contains("拓扑", setting.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiresWrite_OnlyTrueForRealSwitches()
    {
        // 空操作（已是目标模式）：允许但不需要写入。
        var noOp = GraphicsSwitchPolicy.Evaluate(
            HybridState(), GraphicsMode.Hybrid, VerifiedCapabilities, topologyKnown: true);
        Assert.True(noOp.Allowed);
        Assert.False(noOp.RequiresWrite);

        // 拒绝项一律不需要写入。
        Assert.False(GraphicsSwitchPolicy.Evaluate(
            HybridState(), GraphicsMode.Discrete, UnknownCapabilities, true).RequiresWrite);
        Assert.False(GraphicsSwitchPolicy.Evaluate(
            HybridState(), GraphicsMode.Discrete, VerifiedCapabilities, false).RequiresWrite);

        // 真正需要落盘的切换：允许且会产生待重启项。
        var switching = GraphicsSwitchPolicy.Evaluate(
            GraphicsModeInference.Build(TopologyFactory.InternalOnDiscrete()) with { SupportsHybrid = true },
            GraphicsMode.Hybrid,
            VerifiedCapabilities,
            topologyKnown: true);
        Assert.True(switching.Allowed);
        Assert.True(switching.RequiresWrite);
    }

    [Fact]
    public void Describe_UsesChineseModeNames()
    {
        Assert.Equal("核显", GraphicsSwitchPolicy.Describe(GraphicsMode.Integrated));
        Assert.Equal("混合", GraphicsSwitchPolicy.Describe(GraphicsMode.Hybrid));
        Assert.Equal("独显直连", GraphicsSwitchPolicy.Describe(GraphicsMode.Discrete));
    }

    [Fact]
    public void Evaluate_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => GraphicsSwitchPolicy.Evaluate(
            null!, GraphicsMode.Hybrid, VerifiedCapabilities, true));

        Assert.Throws<ArgumentNullException>(() => GraphicsSwitchPolicy.Evaluate(
            HybridState(), GraphicsMode.Hybrid, null!, true));
    }
}
