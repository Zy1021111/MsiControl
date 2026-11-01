using MsiControl.Core;
using MsiControl.Core.Interfaces;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices.Ms15M3;

namespace MsiControl.Graphics;

/// <summary>
/// 显卡模式模块对共享契约 <see cref="IGraphicsHardware"/> 的实现。
/// </summary>
/// <remarks>
/// <para>
/// 组装方式（由任务 07 在应用启动时注入）：
/// <code>
/// IGraphicsHardware graphics = new GraphicsHardwareService(
///     new GraphicsTopologyProbe(),                 // 只读显示拓扑探测
///     CapabilityBaseline.ForDevice(device),        // 能力门控
///     writer: graphicsWriter,                      // EC 通道就绪时为 Ms15M3GraphicsModeWriter（HWE §21）
///     stateReader: graphicsStateReader);           // EC 通道就绪时为 Ms15M3GraphicsModeStateReader（HWE §21）
/// </code>
/// </para>
/// <para>
/// <b>本类绝不自动切换显卡模式：</b>没有任何定时器、事件或启动钩子，
/// <see cref="RequestModeAsync"/> 只在被显式调用时执行（任务书 §9.1：不在启动与供电变化时自动切换）。
/// </para>
/// <para>
/// <b>写入方语义：</b>传入 <see cref="Ms15M3GraphicsModeWriter"/>（协议依据 HWE §21）时，
/// 混合 ⇄ 独显直连经 EC/UEFI 通道切换，写入后需重启生效（不自动重启）；
/// 传入 <see langword="null"/> 或 EC 通道不可用时回退
/// <see cref="UnsupportedGraphicsModeWriter"/>，一律返回「不支持」并附依据。
/// </para>
/// </remarks>
public sealed class GraphicsHardwareService : IGraphicsHardware
{
    private readonly IDisplayTopologyProbe _probe;
    private readonly DeviceCapabilities _capabilities;
    private readonly IGraphicsModeWriter _writer;
    private readonly Func<GraphicsDisplayTopology, GraphicsHardwareState> _stateBuilder;
    private readonly IGraphicsModeStateReader? _stateReader;

    /// <summary>构造显卡模式模块。</summary>
    /// <param name="probe">只读显示拓扑探测。</param>
    /// <param name="capabilities">设备能力集合（通常取自 <c>CapabilityBaseline.ForDevice</c>）。</param>
    /// <param name="writer">
    /// 写入方。省略或传 <see langword="null"/> 时使用 <see cref="UnsupportedGraphicsModeWriter.Instance"/>，
    /// 即「本机无可用切换路径」。
    /// </param>
    /// <param name="stateReader">
    /// MUX 状态读回方（EC 0x2E / 0xD1 / UEFI，HWE §21）。省略或传 <see langword="null"/> 时
    /// 「当前模式」回落为拓扑推断（旧行为，无读回证据时不冒充直读）。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="probe"/> 或 <paramref name="capabilities"/> 为 <see langword="null"/>。</exception>
    public GraphicsHardwareService(
        IDisplayTopologyProbe probe,
        DeviceCapabilities capabilities,
        IGraphicsModeWriter? writer = null,
        IGraphicsModeStateReader? stateReader = null)
        : this(probe, capabilities, writer, GraphicsModeInference.Build, stateReader)
    {
    }

    /// <summary>
    /// 构造显卡模式模块，并指定「拓扑 → 状态」的转换函数。
    /// </summary>
    /// <param name="probe">只读显示拓扑探测。</param>
    /// <param name="capabilities">设备能力集合。</param>
    /// <param name="writer">写入方。</param>
    /// <param name="stateBuilder">拓扑到状态的转换函数。生产环境固定为 <see cref="GraphicsModeInference.Build"/>。</param>
    /// <param name="stateReader">MUX 状态读回方；生产环境为 <see cref="Ms15M3GraphicsModeStateReader"/>。</param>
    /// <remarks>
    /// <para>
    /// <b>为什么存在这个重载：</b>本机未定位到任何显卡模式切换接口，因此
    /// <see cref="GraphicsModeInference"/> 只会把「当前正在生效的模式」标为受支持，
    /// 导致「允许写入」这条分支在生产环境中**不可达**（切到当前模式属于空操作，不需要写入）。
    /// 为了让写入方委派逻辑仍能被回归测试覆盖，此重载允许测试注入一个受控的状态构造函数。
    /// </para>
    /// <para>生产代码不应使用本重载。</para>
    /// </remarks>
    internal GraphicsHardwareService(
        IDisplayTopologyProbe probe,
        DeviceCapabilities capabilities,
        IGraphicsModeWriter? writer,
        Func<GraphicsDisplayTopology, GraphicsHardwareState> stateBuilder,
        IGraphicsModeStateReader? stateReader = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(stateBuilder);

        _probe = probe;
        _capabilities = capabilities;
        _writer = writer ?? UnsupportedGraphicsModeWriter.Instance;
        _stateBuilder = stateBuilder;
        _stateReader = stateReader;
    }

    /// <inheritdoc />
    public async Task<GraphicsHardwareState> ReadAsync(CancellationToken cancellationToken)
    {
        var topology = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return await ComposeStateAsync(topology, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把已取得的拓扑与三载体读回组合成完整状态（含「待生效」信息）。
    /// </summary>
    private async Task<GraphicsHardwareState> ComposeStateAsync(
        GraphicsDisplayTopology topology,
        CancellationToken cancellationToken)
    {
        var inferred = _stateBuilder(topology);

        if (_stateReader is null)
        {
            return inferred;
        }

        var readback = await _stateReader.ReadAsync(cancellationToken).ConfigureAwait(false);

        // 读回方不可用或瞬时失败：保持拓扑推断结果（旧行为），不编造直读值。
        if (readback is null || readback.Error is not null || readback.Registered is not { } registered)
        {
            return inferred;
        }

        // 无切换在途：EC 登记值即实际生效值（HWE §21.2 三载体一致性与拓扑一致）。
        if (!readback.TriggerPending)
        {
            // UEFI 目标与 EC 登记不一致（如外部工具只改 UEFI 未触发）：登记值仍是实际态，
            // UEFI 目标是待生效项。
            if (readback.UefiTarget is { } uefiTarget && uefiTarget != registered)
            {
                return inferred with
                {
                    Current = registered,
                    Pending = uefiTarget,
                    RequiresRestart = true
                };
            }

            return inferred with { Current = registered };
        }

        // 切换在途（EC 0xD1 触发位非零）：协议顺序保证 EC 0x2E 已被写为目标值，
        // 而硬件实际模式仍是另一态（写入后需重启生效，HWE §21.5）。
        // 实际态优先取拓扑推断；两态 MUX 下推断不出时取登记目标的反面——
        // 触发位只在 EC 写成功后置位，故写入前寄存器必为另一态（推断，有协议依据）。
        var actual = inferred.Current ?? Opposite(registered);
        return inferred with
        {
            Current = actual,
            Pending = registered,
            RequiresRestart = true
        };
    }

    /// <summary>两态 MUX 的对面模式（核显模式无编码，不参与）。</summary>
    private static GraphicsMode Opposite(GraphicsMode mode) => mode == GraphicsMode.Discrete
        ? GraphicsMode.Hybrid
        : GraphicsMode.Discrete;

    /// <inheritdoc />
    public async Task<ControlResult> RequestModeAsync(GraphicsMode mode, CancellationToken cancellationToken)
    {
        // 请求前必须重新读取实际状态：待生效判断与拓扑风险都取决于**当下**的拓扑与三载体，
        // 不能用缓存的旧状态（任务书 §9.1「生效后确认状态」）。
        //
        // 2026-10-28 修正（HWE §27）：这里原先只用拓扑推断，导致「已提交但待重启」的目标
        // 对策略不可见——用户在待生效状态下点回原模式时被判为「已是目标模式、无需切换」，
        // 于是不写入，重启后仍是之前提交的那一态，表现为「切换不生效」。
        var topology = await _probe.ProbeAsync(cancellationToken).ConfigureAwait(false);
        var state = await ComposeStateAsync(topology, cancellationToken).ConfigureAwait(false);

        var decision = GraphicsSwitchPolicy.Evaluate(state, mode, _capabilities, topology.IsKnown);

        // 拒绝项与空操作都不需要写入，直接回报结论即可——
        // 尤其不能把「已是目标模式」变成一次多余的写入尝试。
        if (!decision.RequiresWrite)
        {
            return ControlResult.Single(decision.ToSettingResult(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                GraphicsSwitchPolicy.Describe(mode)));
        }

        // 取消不能跳过必要的状态核对：写入方在收到取消时仍应完成读回，再抛出取消异常（约定 §6）。
        return await _writer.WriteAsync(mode, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 只读返回最近一次探测到的显示拓扑。
    /// </summary>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>显示拓扑。探测失败时为部分结果或 <see cref="GraphicsDisplayTopology.Unknown"/>。</returns>
    /// <remarks>
    /// 供诊断界面与证据记录使用，不属于共享契约的一部分。
    /// </remarks>
    public Task<GraphicsDisplayTopology> ReadTopologyAsync(CancellationToken cancellationToken)
        => _probe.ProbeAsync(cancellationToken);
}
