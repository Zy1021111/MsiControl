using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Graphics;

/// <summary>显卡写入通道的 EC 寄存器访问抽象（可测试替身替换）。</summary>
public interface IGraphicsEcAccess
{
    /// <summary>通道不可用原因；<see langword="null"/> 表示可用。</summary>
    string? AvailabilityError { get; }

    /// <summary>读取一个 EC 寄存器。</summary>
    byte ReadRegister(byte address);

    /// <summary>写入一个 EC 寄存器（受写白名单约束）。</summary>
    void WriteRegister(byte address, byte value);
}

/// <summary>UEFI 目标模式变量（<c>MsiDCVarData</c>）的访问抽象。</summary>
public interface IMsiDcVariable
{
    /// <summary>变量是否存在并可读；存在时输出 byte[5] 的解码结果。</summary>
    bool TryRead(out MsiDcGpuByte gpu, out string? error);

    /// <summary>只更新 byte[5] 的目标模式位；返回是否写成功。</summary>
    bool TryWrite(GraphicsMode mode, out string? error);
}

/// <summary>
/// MS-15M3 显卡模式写入方：UEFI 目标（byte[5]）+ EC 0xD1（请求/受理/模式位）+ EC 0x2E（MUX）。
/// </summary>
/// <remarks>
/// <para>
/// <b>协议依据（2026-10-30 定性，取代此前仅援引 ElXreno/msi-gpu-switcher 的两态模型）：</b>
/// M-Control 3.x（官方 MSI 控制台，本机安装于 <c>C:\Program Files\M-Control</c>）逆向结果：
/// <c>MControl.Core.EcIoInterface.SetGpuMode</c> 的写入序列为
/// <list type="number">
/// <item>写 UEFI <c>MsiDCVarData</c> 的 <b>byte[5] bit0-1</b> = 目标模式（0=混合、1=独显直连、2=核显），其余位保留；</item>
/// <item>读 EC 0x2E 与 0xD1；</item>
/// <item>写 EC 0xD1 = 原值 | 0x06（bit1=切换请求在途、bit2=显存/输出模式位）；</item>
/// <item>写 EC 0x2E：bit6 = 1（独显直连）/ 0（混合、核显），其余位读改写保留；</item>
/// <item>再写 EC 0xD1 = 原值 | 0x07（bit0=触发）。</item>
/// </list>
/// 官方文案与 <c>requiresPowerOff=true</c> 表明：<b>关机再开机后生效</b>。
/// </para>
/// <para>
/// <b>与此前实现的差异（本次修复的根因）：</b>
/// 旧实现写 UEFI <c>byte[1]</c>（实为初始化标志，与显卡模式无关），且 0xD1 只写 bit0/bit1、
/// 从不置 bit2 —— 固件既收不到目标模式，也看不到「切换请求的语义」，因此重启后模式不变。
/// </para>
/// <para>
/// <b>本机对照（2026-10-30 实机只读）：</b>UEFI byte[5]=0x30（目标混合、新 EC 入口可用、支持 UMA 与独显）、
/// EC 0x2E=0x1B（bit6=0）、EC 0xD1=0x00。
/// </para>
/// <para>
/// <b>诚实性：</b>写入后必须读回 EC 0x2E 的 MUX 位并核对（0xD1 的 bit2 会被固件在受理后清除，
/// 实测写 0x06 读回 0x02，故只作过程记录）；不符一律返回
/// <see cref="OperationStatus.Failed"/> 并附实测值，绝不报「成功」（任务书 §1 原则 6）。
/// 本实现<b>绝不自动重启或关机</b>（任务书 §9.1）。
/// </para>
/// </remarks>
public sealed class Ms15M3GraphicsModeWriter : IGraphicsModeWriter
{
    /// <summary>整段切换序列的最大尝试次数（写后读回不符即整段重写）。</summary>
    /// <remarks>
    /// 依据 HWE §27：EC 写事务无 ACK，实测偶发「写入被固件丢弃、读回仍是旧值」。
    /// 3 次尝试在实测中可覆盖该抖动；再多只会延长失败反馈时间，不会提高成功率。
    /// </remarks>
    public const int MaxWriteAttempts = 3;

    /// <summary>两次写入尝试之间的等待。</summary>
    public static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(40);

    private readonly IGraphicsEcAccess _ec;
    private readonly IMsiDcVariable _uefiVariable;

    /// <summary>构造写入方。</summary>
    /// <param name="ec">EC 访问通道（写白名单须含 0x2E 与 0xD1）。</param>
    /// <param name="uefiVariable">UEFI 目标模式变量访问器。</param>
    public Ms15M3GraphicsModeWriter(IGraphicsEcAccess ec, IMsiDcVariable uefiVariable)
    {
        _ec = ec ?? throw new ArgumentNullException(nameof(ec));
        _uefiVariable = uefiVariable ?? throw new ArgumentNullException(nameof(uefiVariable));
    }

    /// <summary>由既有的 PawnIO EC 访问器构造生产写入方。</summary>
    /// <remarks>
    /// 适配器把 <see cref="PawnIoEcRegisterAccess"/> 的读写包成本模块的
    /// <see cref="IGraphicsEcAccess"/>；通道不可用时按原因如实上报「不支持」。
    /// </remarks>
    public static Ms15M3GraphicsModeWriter? TryCreateFrom(PawnIoEcRegisterAccess ecAccess)
    {
        if (ecAccess is null)
        {
            return null;
        }

        var error = ecAccess.AvailabilityError;
        return error is null
            ? new Ms15M3GraphicsModeWriter(new PawnIoEcAccessAdapter(ecAccess), new MsiDcUefiVariable())
            : null;
    }

    /// <inheritdoc />
    public Task<ControlResult> WriteAsync(
        GraphicsMode mode,
        GraphicsHardwareState currentState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentState);

        // 取消不能跳过必要的状态核对：先完成写入与读回，再响应取消（约定 §6）。
        try
        {
            var result = WriteCore(mode);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        catch (GraphicsWriteException exception)
        {
            return Task.FromResult(ControlResult.Single(SettingResult.Failed(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                GraphicsSwitchPolicy.Describe(mode),
                exception.Message)));
        }
        catch (InvalidOperationException exception)
        {
            return Task.FromResult(ControlResult.Single(SettingResult.Unsupported(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                $"EC 通道不可用，显卡模式切换未执行：{exception.Message}")));
        }
    }

    private ControlResult WriteCore(GraphicsMode mode)
    {
        if (mode == GraphicsMode.Integrated)
        {
            return ControlResult.Single(SettingResult.Unsupported(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                "「核显模式」（完全关闭独显、仅核显输出）的写入路径尚未在本机验证，因此暂不提供："
                + "官方固件确实声明支持（UEFI MsiDCVarData byte[5] bit5=1，编码为目标字节=2、"
                + "EC 0xD1 bit2=0、EC 0x2E bit6=0），但该组合会让内置屏改由独显输出、外接屏可能断开，"
                + "一旦写入被固件部分接受将出现无显示，只能进 BIOS 恢复。"
                + "替代做法：需要彻底关闭独显时，请在本机 BIOS/UEFI 的显示模式选项里设置"
                + "（该项由固件提供，不经过本程序）。"));
        }

        var ecError = _ec.AvailabilityError;
        if (ecError is not null)
        {
            return ControlResult.Single(SettingResult.Unsupported(
                GraphicsSwitchPolicy.GraphicsModeSettingKey,
                $"EC 通道不可用，显卡模式切换未执行：{ecError}"));
        }

        var discrete = mode == GraphicsMode.Discrete;
        var targetCode = MsiGpuModeCodec.ToCode(mode);
        var steps = new List<string>();

        // ① UEFI 目标（byte[5] bit0-1）。这是固件在下次开机时决定 MUX 的依据，必须先写。
        var uefiWritten = false;
        if (_uefiVariable.TryRead(out var gpuBefore, out var uefiReadError))
        {
            uefiWritten = _uefiVariable.TryWrite(mode, out var uefiWriteError);
            steps.Add(uefiWritten
                ? $"UEFI MsiDCVarData byte[5]：0x{gpuBefore.Raw:X2} → 0x{gpuBefore.WithRequested(mode):X2}"
                    + $"（目标编码 {targetCode}）"
                : $"UEFI 目标写入未成功：{uefiWriteError}（继续 EC 通道）");
        }
        else
        {
            steps.Add($"UEFI 目标变量不可用：{uefiReadError}");
        }

        // ② EC 序列：0xD1（请求 + 模式位）→ 0x2E（MUX 读改写）→ 0xD1（触发）。
        var attempts = 0;
        byte muxAfter = 0;
        byte triggerAfter = 0;
        var matched = false;

        while (!matched && attempts < MaxWriteAttempts)
        {
            attempts++;

            var mux = ReadRegisterChecked(Ms15M3EcRegisterMap.GpuMux.Address);
            var trigger = ReadRegisterChecked(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address);

            var muxTarget = discrete
                ? (byte)(mux | Ms15M3EcRegisterMap.GpuMuxBitMask)
                : (byte)(mux & ~Ms15M3EcRegisterMap.GpuMuxBitMask);

            // 两态（混合 / 独显直连）都要求 bit2=1：bit2=0 在固件语义里表示「核显（UMA）」。
            var requestValue = (byte)(trigger
                | Ms15M3EcRegisterMap.GpuSwitchModeBit
                | Ms15M3EcRegisterMap.GpuSwitchAckBit);

            WriteRegisterChecked(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address, requestValue);
            WriteRegisterChecked(Ms15M3EcRegisterMap.GpuMux.Address, muxTarget);
            var fireValue = (byte)(requestValue | Ms15M3EcRegisterMap.GpuSwitchTriggerRequest);
            WriteRegisterChecked(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address, fireValue);

            muxAfter = ReadRegisterChecked(Ms15M3EcRegisterMap.GpuMux.Address);
            triggerAfter = ReadRegisterChecked(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address);

            var muxBit = (muxAfter & Ms15M3EcRegisterMap.GpuMuxBitMask) != 0;
            var modeBit = (triggerAfter & Ms15M3EcRegisterMap.GpuSwitchModeBit) != 0;

            // 成功判据只用 0x2E bit6（MUX 登记态，实测写入后保持）。
            // 0xD1 的 bit2 在固件受理后会被清掉（2026-10-30 实测：写 0x06 读回 0x02），
            // 因此它只能作为过程记录，不能当成功条件——否则把真实成功的切换判成失败。
            matched = muxBit == discrete;

            steps.Add(
                $"第 {attempts} 次：0xD1 写 0x{requestValue:X2}→触发 0x{fireValue:X2}、"
                + $"0x2E 写 0x{muxTarget:X2}；读回 0x2E=0x{muxAfter:X2}（bit6={(muxBit ? 1 : 0)}）、"
                + $"0xD1=0x{triggerAfter:X2}（bit2={(modeBit ? 1 : 0)}）");

            if (!matched && attempts < MaxWriteAttempts)
            {
                Thread.Sleep(WriteRetryDelay);
            }
        }

        if (!matched)
        {
            throw new GraphicsWriteException(
                $"EC 读回不符：连续写入 {attempts} 次后 0x2E=0x{muxAfter:X2}、0xD1=0x{triggerAfter:X2}，"
                + $"目标为 {(discrete ? "独显直连（0x2E bit6=1）" : "混合（0x2E bit6=0）")}。"
                + "模式切换未生效，请勿关机重启；请关闭其他调用 EC 的监控工具后重试，"
                + "若仍失败请通过 BIOS 切换。");
        }

        steps.Add($"最终读回：EC 0x2E=0x{muxAfter:X2}、EC 0xD1=0x{triggerAfter:X2}，与目标一致");

        var requested = GraphicsSwitchPolicy.Describe(mode);
        var basis = "确认依据：" + string.Join("；", steps)
            + "。协议依据：M-Control 3.x 逆向（EcIoInterface.SetGpuMode / MsiGpuModeController）；"
            + "本机对照（2026-10-30 只读）：UEFI byte[5]=0x30、EC 0x2E=0x1B、EC 0xD1=0x00。";
        var message = $"目标模式「{requested}」已写入并读回一致。"
            + (uefiWritten
                ? "请完全关机后再开机（S5 冷启动）以让固件重建显示通路；本程序不会自动重启或关机。"
                    + "注意：首次冷启动时内置屏可能持续数十秒到数分钟无图像（MUX 切换后 Windows 重新枚举显示设备、"
                    + "显卡驱动重新接管面板），这属于重建窗口而非故障——请耐心等待，不要强制断电；"
                    + "若确实长时间无图像，长按电源 10 秒关机后再开机，第二次启动通常恢复正常。"
                : "注意：UEFI 目标变量未能写入，固件可能在下次开机时按自身记录覆盖本次 EC 设置。");

        return ControlResult.Single(SettingResult.PendingRestart(
            GraphicsSwitchPolicy.GraphicsModeSettingKey,
            requested,
            basis,
            message));
    }

    private byte ReadRegisterChecked(byte address)
    {
        try
        {
            return _ec.ReadRegister(address);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or EcPortException)
        {
            throw new GraphicsWriteException($"EC 读取 0x{address:X2} 失败：{exception.Message}，模式切换未执行。");
        }
    }

    private void WriteRegisterChecked(byte address, byte value)
    {
        try
        {
            _ec.WriteRegister(address, value);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or EcPortException)
        {
            throw new GraphicsWriteException($"EC 写入 0x{address:X2}=0x{value:X2} 失败：{exception.Message}，模式切换未完成。");
        }
    }

    private sealed class GraphicsWriteException(string message) : Exception(message);
}

/// <summary>
/// 把 <see cref="PawnIoEcRegisterAccess"/> 适配为 <see cref="IGraphicsEcAccess"/>
/// （显卡写入方与读回方共用；写白名单在 EC 通道层把守）。
/// </summary>
internal sealed class PawnIoEcAccessAdapter : IGraphicsEcAccess
{
    private readonly PawnIoEcRegisterAccess _access;

    public PawnIoEcAccessAdapter(PawnIoEcRegisterAccess access) => _access = access;

    public string? AvailabilityError => _access.AvailabilityError;

    public byte ReadRegister(byte address) => _access.ReadRegister(address);

    public void WriteRegister(byte address, byte value) => _access.WriteRegister(address, value);
}
