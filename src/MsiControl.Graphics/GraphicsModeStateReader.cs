using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Graphics;

/// <summary>
/// 显卡 MUX 状态读回：三个载体（EC 0x2E / EC 0xD1 / UEFI 目标变量）的原始快照。
/// </summary>
/// <param name="Registered">EC 0x2E bit6 登记的模式（0=混合，1=独显直连）。读失败时为 <see langword="null"/>。</param>
/// <param name="TriggerPending">EC 0xD1 的 bit0/bit1 是否非零（切换请求在途或固件已受理待重启）。</param>
/// <param name="UefiTarget">UEFI <c>MsiDCVarData</c> byte[5] bit0-1 的目标模式；变量不可读时为 <see langword="null"/>。</param>
/// <param name="Error">通道不可用或读取失败的原因；成功时为 <see langword="null"/>。</param>
/// <remarks>
/// 语义依据 HWE §21：写入序列为「UEFI 目标 → EC 0xD1 触发 → EC 0x2E 读改写」，
/// 且写入后需重启才在硬件拓扑上生效。因此：
/// <list type="bullet">
/// <item>0xD1 触发位非零 ⇒ EC 0x2E 已是目标值、硬件实际仍是另一态（触发只在 EC 写成功后发出）。</item>
/// <item>0xD1 为零但 UEFI 目标与 EC 登记值不一致 ⇒ 请求未走完整协议（如外部工具只改 UEFI）。</item>
/// </list>
/// 「当前实际模式」的组合判定由 <see cref="GraphicsHardwareService"/> 统一做，读回器只如实返回载体值。
/// </remarks>
public sealed record GraphicsMuxReadback(
    GraphicsMode? Registered,
    bool TriggerPending,
    GraphicsMode? UefiTarget,
    string? Error);

/// <summary>显卡模式状态读回源（只读）。</summary>
public interface IGraphicsModeStateReader
{
    /// <summary>
    /// 读取 MUX 三载体快照。通道不可用时返回带 <see cref="GraphicsMuxReadback.Error"/> 的快照；
    /// 瞬时读取失败返回 <see langword="null"/>（由调用方回落拓扑推断，不编造状态）。
    /// </summary>
    Task<GraphicsMuxReadback?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// MS-15M3 显卡模式读回方：EC 0x2E（MUX 位）+ EC 0xD1（触发位）+ UEFI <c>MsiDCVarData</c>（目标）。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="Ms15M3GraphicsModeWriter"/> 共用同一组通道抽象（<see cref="IGraphicsEcAccess"/>
/// 与 <see cref="IMsiDcVariable"/>），协议依据与本机对照证据同 HWE §21：
/// EC 0x2E bit6（0x40）= MUX 登记态；EC 0xD1 bit0=切换请求、bit1=固件已受理（HWE §21.5 首次观察，§30 复验）；
/// EC 0xD1 bit2 = 输出模式位（0=核显 / 1=独显参与），仅作参考——实测固件受理后会清该位。
/// UEFI byte[5] bit0-1=0 混合 / 1 独显直连 / 2 核显（HWE §30）。
/// </para>
/// <para><b>只读：</b>本类型不写入任何寄存器或变量。</para>
/// </remarks>
public sealed class Ms15M3GraphicsModeStateReader : IGraphicsModeStateReader
{
    private readonly IGraphicsEcAccess _ec;
    private readonly IMsiDcVariable _uefiVariable;

    /// <summary>构造读回方。</summary>
    /// <param name="ec">EC 访问通道（只读使用）。</param>
    /// <param name="uefiVariable">UEFI 目标模式变量访问器。</param>
    public Ms15M3GraphicsModeStateReader(IGraphicsEcAccess ec, IMsiDcVariable uefiVariable)
    {
        _ec = ec ?? throw new ArgumentNullException(nameof(ec));
        _uefiVariable = uefiVariable ?? throw new ArgumentNullException(nameof(uefiVariable));
    }

    /// <summary>由既有的 PawnIO EC 访问器构造生产读回方；通道不可用时返回 <see langword="null"/>。</summary>
    public static Ms15M3GraphicsModeStateReader? TryCreateFrom(PawnIoEcRegisterAccess ecAccess)
    {
        if (ecAccess is null)
        {
            return null;
        }

        var error = ecAccess.AvailabilityError;
        return error is null
            ? new Ms15M3GraphicsModeStateReader(new PawnIoEcAccessAdapter(ecAccess), new MsiDcUefiVariable())
            : null;
    }

    /// <inheritdoc />
    public Task<GraphicsMuxReadback?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ecError = _ec.AvailabilityError;
        if (ecError is not null)
        {
            return Task.FromResult<GraphicsMuxReadback?>(
                new GraphicsMuxReadback(null, false, null, ecError));
        }

        try
        {
            var mux = _ec.ReadRegister(Ms15M3EcRegisterMap.GpuMux.Address);
            var trigger = _ec.ReadRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address);

            var ecRegistered = (mux & Ms15M3EcRegisterMap.GpuMuxBitMask) != 0
                ? GraphicsMode.Discrete
                : GraphicsMode.Hybrid;

            // 触发位：bit0=切换请求、bit1=固件已受理（HWE §21.5 首次观察）；任一位非零都视为
            // 「有切换在途，硬件实际模式尚未跟随 EC 登记值」。
            var triggerPending = (trigger & Ms15M3EcRegisterMap.GpuSwitchTriggerBitsMask) != 0;

            GraphicsMode? uefiTarget = null;
            var registered = ecRegistered;
            if (_uefiVariable.TryRead(out var gpuByte, out _))
            {
                // 目标模式在 byte[5] 的 bit0-1（0=混合、1=独显直连、2=核显）；
                // 旧实现读 byte[1] 实际读的是初始化标志，导致「待生效目标」永远是错的。
                uefiTarget = gpuByte.Requested;

                // 新 EC 路径（byte[5] bit4=1）下固件会自行维护 bit2-3「当前模式」，
                // 它是切换是否已生效的最终裁判（HWE §30.3：切换后 0x31 → 0x35）。
                // 此时以固件自报值为准，EC 0x2E 仅作交叉校验——两者不一致时不得以 0x2E 冒充真相。
                if (gpuByte.NewSwitchSupported && gpuByte.ReportedCurrent is { } reported)
                {
                    registered = reported;
                }
            }

            return Task.FromResult<GraphicsMuxReadback?>(
                new GraphicsMuxReadback(registered, triggerPending, uefiTarget, null));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or EcPortException)
        {
            // 瞬时读取失败：返回 null，由调用方保持上一次状态/回落推断，不编造。
            return Task.FromResult<GraphicsMuxReadback?>(null);
        }
    }
}
