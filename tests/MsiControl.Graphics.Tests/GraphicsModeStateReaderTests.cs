using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Devices.Ms15M3;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using Xunit;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// 显卡 MUX 状态读回方（<see cref="Ms15M3GraphicsModeStateReader"/>）的回归测试。
/// </summary>
/// <remarks>
/// 全部使用测试替身，不接触真实硬件；读回方本身也只读，无任何写入路径。
/// 载体语义依据 HWE §21：EC 0x2E bit6=MUX 登记态；EC 0xD1 bit0=请求、bit1=固件已受理；
/// UEFI <c>MsiDCVarData</c> byte[1]=目标模式（0=混合，1=独显直连）。
/// </remarks>
public class GraphicsModeStateReaderTests
{
    [Fact]
    public async Task ReadAsync_HybridMachine_ReportsHybridRegisteredAndTarget()
    {
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x1B, trigger: 0x00),
            new FakeMsiDcVariable(exists: true, gpuByte: 0x00));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Null(readback!.Error);
        Assert.Equal(GraphicsMode.Hybrid, readback.Registered);
        Assert.False(readback.TriggerPending);
        Assert.Equal(GraphicsMode.Hybrid, readback.UefiTarget);
    }

    [Fact]
    public async Task ReadAsync_DiscreteMachine_ReportsDiscreteBothCarriers()
    {
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x5B, trigger: 0x00),
            new FakeMsiDcVariable(exists: true, gpuByte: 0x01));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Equal(GraphicsMode.Discrete, readback!.Registered);
        Assert.False(readback.TriggerPending);
        Assert.Equal(GraphicsMode.Discrete, readback.UefiTarget);
    }

    [Theory]
    [InlineData(0x01)] // bit0：切换请求在途
    [InlineData(0x02)] // bit1：固件已受理，待重启生效（HWE §21.5 首次观察）
    [InlineData(0x03)] // 两位同时置位
    public async Task ReadAsync_TriggerBitsSet_ReportsPendingReboot(byte trigger)
    {
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x5B, trigger: trigger),
            new FakeMsiDcVariable(exists: true, gpuByte: 0x01));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.True(readback!.TriggerPending);
        Assert.Equal(GraphicsMode.Discrete, readback.Registered);
    }

    [Fact]
    public async Task ReadAsync_UefiTargetDiffersFromRegister_ReportsBothCarriersUntouched()
    {
        // 外部工具只改 UEFI 目标未触发：读回方只如实呈现两个载体，组合判定交给服务层。
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x1B, trigger: 0x00),
            new FakeMsiDcVariable(exists: true, gpuByte: 0x01));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Equal(GraphicsMode.Hybrid, readback!.Registered);
        Assert.Equal(GraphicsMode.Discrete, readback.UefiTarget);
        Assert.False(readback.TriggerPending);
    }

    [Fact]
    public async Task ReadAsync_UefiVariableMissing_TargetIsNull()
    {
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x1B, trigger: 0x00),
            new FakeMsiDcVariable(exists: false, gpuByte: 0x00));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Null(readback!.Error);
        Assert.Null(readback.UefiTarget);
    }

    [Fact]
    public async Task ReadAsync_ChannelUnavailable_ReturnsErrorSnapshot()
    {
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x1B, trigger: 0x00) { AvailabilityError = "PawnIO 通道不可用" },
            new FakeMsiDcVariable(exists: true, gpuByte: 0x00));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Null(readback!.Registered);
        Assert.Equal("PawnIO 通道不可用", readback.Error);
    }

    [Fact]
    public async Task ReadAsync_RegisterReadThrows_ReturnsNullForFallback()
    {
        // 瞬时失败返回 null，由服务层回落拓扑推断——不编造直读值。
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x1B, trigger: 0x00) { ThrowOnRead = true },
            new FakeMsiDcVariable(exists: true, gpuByte: 0x00));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.Null(readback);
    }

    [Fact]
    public async Task ReadAsync_FirmwareReportedCurrentWinsOverMuxBit()
    {
        // HWE §30：新 EC 路径（byte[5] bit4=1）下固件自行维护 bit2-3「当前模式」，
        // 它是切换是否已生效的最终裁判。0x2E 与之不一致时不得以 0x2E 冒充真相。
        // byte[5]=0x35 → 目标=1、当前=1（固件自报独显直连），而 0x2E 仍是 0x1B。
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x1B, trigger: 0x00),
            new FakeMsiDcVariable(exists: true, gpuByte: 0x35));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Equal(GraphicsMode.Discrete, readback!.Registered);
        Assert.Equal(GraphicsMode.Discrete, readback.UefiTarget);
    }

    [Fact]
    public async Task ReadAsync_NoNewEcSupport_FallsBackToMuxBit()
    {
        // 旧机型（bit4=0）没有固件自报字段，必须回落 EC 0x2E，不能把 bit2-3 当有效值。
        var reader = new Ms15M3GraphicsModeStateReader(
            new FakeEcAccess(mux: 0x5B, trigger: 0x00),
            new FakeMsiDcVariable(exists: true, gpuByte: 0x01));

        var readback = await reader.ReadAsync(CancellationToken.None);

        Assert.NotNull(readback);
        Assert.Equal(GraphicsMode.Discrete, readback!.Registered);
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Ms15M3GraphicsModeStateReader(null!, new FakeMsiDcVariable(true, 0)));
        Assert.Throws<ArgumentNullException>(() =>
            new Ms15M3GraphicsModeStateReader(new FakeEcAccess(0x1B, 0x00), null!));
    }

    private sealed class FakeEcAccess : IGraphicsEcAccess
    {
        public FakeEcAccess(byte mux, byte trigger)
        {
            Mux = mux;
            Trigger = trigger;
        }

        private byte Mux { get; }

        private byte Trigger { get; }

        public string? AvailabilityError { get; set; }

        /// <summary>注入后：任何读取都抛 <see cref="InvalidOperationException"/>（模拟通道瞬时故障）。</summary>
        public bool ThrowOnRead { get; set; }

        public byte ReadRegister(byte address)
        {
            if (ThrowOnRead)
            {
                throw new InvalidOperationException("EC 读取超时");
            }

            return address switch
            {
                0x2E => Mux,
                0xD1 => Trigger,
                _ => 0
            };
        }

        public void WriteRegister(byte address, byte value) =>
            throw new InvalidOperationException("读回方测试不应发生写入。");
    }

    private sealed class FakeMsiDcVariable : IMsiDcVariable
    {
        public FakeMsiDcVariable(bool exists, byte gpuByte)
        {
            Exists = exists;
            GpuByte = gpuByte;
        }

        private bool Exists { get; }

        private byte GpuByte { get; }

        public bool TryRead(out MsiDcGpuByte gpu, out string? error)
        {
            gpu = new MsiDcGpuByte(GpuByte);
            error = Exists ? null : "UEFI 变量 MsiDCVarData 不存在";
            return Exists;
        }

        public bool TryWrite(GraphicsMode mode, out string? error) =>
            throw new InvalidOperationException("读回方测试不应发生写入。");
    }
}
