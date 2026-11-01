using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Core.Results;
using MsiControl.Graphics;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using Xunit;

namespace MsiControl.Graphics.Tests;

/// <summary>
/// MS-15M3 显卡模式写入方的回归测试。
/// </summary>
/// <remarks>
/// 全部使用测试替身，不接触真实硬件。协议依据 M-Control 3.x 逆向（<c>EcIoInterface.SetGpuMode</c>）：
/// UEFI <c>MsiDCVarData</c> byte[5] bit0-1 写目标 → EC 0xD1 置 bit1/bit2 → EC 0x2E 读改写 bit6 →
/// EC 0xD1 再置 bit0 触发。重点覆盖该序列、位保留语义、读回不符必须报失败、核显模式暂不开放。
/// </remarks>
public class Ms15M3GraphicsModeWriterTests
{
    [Fact]
    public async Task WriteAsync_IntegratedTarget_IsUnsupportedWithoutAnyWrite()
    {
        var ec = FakeEcAccess.HybridMachine();
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Integrated, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Settings[0].Status);
        Assert.Empty(ec.Writes);
        Assert.Equal(0, uefi.WriteCount);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task WriteAsync_ToDiscrete_WritesOfficialSequenceAndPreservesBits()
    {
        var ec = FakeEcAccess.HybridMachine();          // 0x2E = 0x1B（bit6=0）、0xD1 = 0x00
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.PendingRestart, result.Settings[0].Status);

        // 官方序：0xD1(请求 0x06) → 0x2E(读改写 bit6) → 0xD1(触发 0x07)。
        Assert.Equal(3, ec.Writes.Count);
        Assert.Equal((0xD1, (byte)0x06), ec.Writes[0]);
        Assert.Equal((0x2E, (byte)0x5B), ec.Writes[1]);   // 0x1B | 0x40，其余位保留
        Assert.Equal((0xD1, (byte)0x07), ec.Writes[2]);   // bit0 触发位最后置

        // UEFI：只改 byte[5] 的低两位（0x30 → 0x31），byte[1] 必须与显卡模式无关。
        Assert.Equal(1, uefi.WriteCount);
        Assert.Equal(0x31, uefi.LastData![5]);
        Assert.Equal(0x01, uefi.LastData[1]);   // byte[1] 是初始化标志，写入不得触碰

        Assert.Contains("读回", result.Settings[0].ConfirmationBasis!, StringComparison.Ordinal);
        Assert.Contains("关机", result.Settings[0].Message!, StringComparison.Ordinal);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task WriteAsync_ToHybrid_ClearsMuxBitAndSetsModeBit()
    {
        var ec = FakeEcAccess.HybridMachine();
        ec.LiveRegisters[0x2E] = 0x5B;                  // bit6=1（独显直连在生效）
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x31);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Hybrid, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.PendingRestart, result.Settings[0].Status);
        Assert.Equal((0x2E, (byte)0x1B), ec.Writes[1]);   // 只清 bit6，其余位保留
        Assert.Equal((0xD1, (byte)0x06), ec.Writes[0]);
        Assert.Equal((0xD1, (byte)0x07), ec.Writes[2]);
        Assert.Equal(0x30, uefi.LastData![5]);            // 0x31 → 目标混合（0）
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task WriteAsync_UefiVariableMissing_StillSwitchesViaEcAndSaysSo()
    {
        var ec = FakeEcAccess.HybridMachine();
        var uefi = new FakeMsiDcVariable(exists: false, gpuByte: 0x00);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.PendingRestart, result.Settings[0].Status);
        Assert.Equal(0, uefi.WriteCount);
        Assert.Equal(3, ec.Writes.Count);
        Assert.Contains("UEFI", result.Settings[0].ConfirmationBasis!, StringComparison.Ordinal);
        Assert.Contains("覆盖", result.Settings[0].Message!, StringComparison.Ordinal);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task WriteAsync_ReadbackMismatchOnce_RetriesWholeSequenceAndSucceeds()
    {
        // HWE §27 实测现象：EC 写事务无 ACK，偶发「写完读回仍是旧值」。
        // 修复后必须整段重写，而不是把一次抖动直接当成失败。
        var ec = FakeEcAccess.HybridMachine();
        // 前 2 次读 0x2E（写入前 + 第 1 次读回）被清掉 bit6，第 2 次尝试恢复正常。
        ec.FailBit6ReadsRemaining = 2;
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.PendingRestart, result.Settings[0].Status);

        var muxWrites = ec.Writes.Where(w => w.Address == 0x2E).ToArray();
        Assert.Equal(2, muxWrites.Length);
        Assert.All(muxWrites, w => Assert.Equal(0x5B, w.Value));

        Assert.Contains("第 2 次", result.Settings[0].ConfirmationBasis!, StringComparison.Ordinal);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task WriteAsync_ReadbackMismatchPersistent_FailsAfterMaxAttempts()
    {
        var ec = FakeEcAccess.HybridMachine();
        ec.FailBit6Readback = true;                     // 每次都读回旧值
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Settings[0].Status);

        // 有上限：不允许无限重写（每次 40 ms，无限重试会把界面卡死）。
        var muxWrites = ec.Writes.Where(w => w.Address == 0x2E).ToArray();
        Assert.Equal(Ms15M3GraphicsModeWriter.MaxWriteAttempts, muxWrites.Length);

        Assert.Contains(
            $"连续写入 {Ms15M3GraphicsModeWriter.MaxWriteAttempts} 次",
            result.Settings[0].Message!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WriteAsync_ReadbackMismatch_ReportsFailureNeverFakeSuccess()
    {
        var ec = FakeEcAccess.HybridMachine();
        ec.FailBit6Readback = true;                     // 读回时 bit6 恒为 0
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.Failed, result.Settings[0].Status);
        Assert.Contains("读回不符", result.Settings[0].Message!, StringComparison.Ordinal);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public async Task WriteAsync_EcChannelUnavailable_ReportsUnsupportedWithReason()
    {
        var ec = FakeEcAccess.HybridMachine();
        ec.AvailabilityError = "PawnIO 驱动未安装";
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.Unsupported, result.Settings[0].Status);
        Assert.Contains("PawnIO 驱动未安装", result.Settings[0].Message, StringComparison.Ordinal);
        Assert.Empty(ec.Writes);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public void Constructor_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new Ms15M3GraphicsModeWriter(null!, new FakeMsiDcVariable(true, 0x30)));
        Assert.Throws<ArgumentNullException>(() =>
            new Ms15M3GraphicsModeWriter(FakeEcAccess.HybridMachine(), null!));
    }

    [Fact]
    public async Task WriteAsync_FirmwareClearsModeBit_StillReportsSuccess()
    {
        // 2026-10-30 实机实测：写 EC 0xD1=0x06 后固件读回 0x02（bit2 被清、bit1 置位）。
        // 成功判据只能是 0x2E bit6，否则会把真实成功的切换判成失败。
        var ec = FakeEcAccess.HybridMachine();
        ec.FirmwareClearsModeBitOnWrite = true;
        var uefi = new FakeMsiDcVariable(exists: true, gpuByte: 0x30);
        var writer = new Ms15M3GraphicsModeWriter(ec, uefi);

        var result = await writer.WriteAsync(
            GraphicsMode.Discrete, CurrentState(), CancellationToken.None);

        Assert.Equal(OperationStatus.PendingRestart, result.Settings[0].Status);
        Assert.Equal((0x2E, (byte)0x5B), ec.Writes[1]);
        Assert.Equal(0x02, ec.LiveRegisters[0xD1]);
        Assert.Empty(result.Validate());
    }

    [Fact]
    public void MsiGpuModeCodec_MapsApplicationEnumToFirmwareCode()
    {
        // 应用枚举 Integrated=0/Hybrid=1/Discrete=2 与 MSI 编码 0=混合/1=独显/2=核显**不同**，
        // 混淆两者会直接把错误值写进固件。
        Assert.Equal(0, MsiGpuModeCodec.ToCode(GraphicsMode.Hybrid));
        Assert.Equal(1, MsiGpuModeCodec.ToCode(GraphicsMode.Discrete));
        Assert.Equal(2, MsiGpuModeCodec.ToCode(GraphicsMode.Integrated));

        Assert.Equal(GraphicsMode.Hybrid, MsiGpuModeCodec.FromCode(0));
        Assert.Equal(GraphicsMode.Discrete, MsiGpuModeCodec.FromCode(1));
        Assert.Equal(GraphicsMode.Integrated, MsiGpuModeCodec.FromCode(2));
        Assert.Null(MsiGpuModeCodec.FromCode(3));
    }

    [Fact]
    public void MsiDcGpuByte_DecodesSupportBitsFromRealMachineValue()
    {
        // 本机实测 byte[5]=0x30：目标混合、新 EC 入口可用、支持 UMA、支持独显直连。
        var gpu = new MsiDcGpuByte(0x30);
        Assert.Equal(GraphicsMode.Hybrid, gpu.Requested);
        Assert.True(gpu.NewSwitchSupported);
        Assert.True(gpu.UmaSupported);
        Assert.True(gpu.DiscreteSupported);
        Assert.Equal(0x31, gpu.WithRequested(GraphicsMode.Discrete));
        Assert.Equal(0x32, gpu.WithRequested(GraphicsMode.Integrated));
        Assert.Equal(0x30, new MsiDcGpuByte(0x32).WithRequested(GraphicsMode.Hybrid));
    }

    private static GraphicsHardwareState CurrentState()
        => GraphicsModeInference.Build(TopologyFactory.HybridWithExternalOnDiscrete());

    /// <summary>脚本化 EC 替身：按地址存取，可注入读回故障。</summary>
    private sealed class FakeEcAccess : IGraphicsEcAccess
    {
        public static FakeEcAccess HybridMachine() => new()
        {
            LiveRegisters = new Dictionary<byte, byte>
            {
                [0x2E] = 0x1B,
                [0xD1] = 0x00
            }
        };

        public required Dictionary<byte, byte> LiveRegisters { get; init; }

        /// <summary>注入后：读 0x2E 时强制清零 bit6（模拟读回不符，永不恢复）。</summary>
        public bool FailBit6Readback { get; set; }

        /// <summary>前 N 次读 0x2E 时清零 bit6，之后恢复正常（模拟「写入被固件丢弃一次」）。</summary>
        public int FailBit6ReadsRemaining { get; set; }

        public string? AvailabilityError { get; set; }

        /// <summary>注入后：写 0xD1 时固件清 bit2/bit0、置 bit1（实测受理行为：0x07 → 0x02）。</summary>
        public bool FirmwareClearsModeBitOnWrite { get; set; }

        public List<(byte Address, byte Value)> Writes { get; } = new();

        public byte ReadRegister(byte address)
        {
            if (AvailabilityError is not null)
            {
                throw new InvalidOperationException(AvailabilityError);
            }

            var value = LiveRegisters.TryGetValue(address, out var v) ? v : (byte)0;
            if (address == 0x2E && FailBit6Readback)
            {
                value = (byte)(value & ~Ms15M3EcRegisterMap.GpuMuxBitMask);
            }
            else if (address == 0x2E && FailBit6ReadsRemaining > 0)
            {
                FailBit6ReadsRemaining--;
                value = (byte)(value & ~Ms15M3EcRegisterMap.GpuMuxBitMask);
            }

            return value;
        }

        public void WriteRegister(byte address, byte value)
        {
            if (AvailabilityError is not null)
            {
                throw new InvalidOperationException(AvailabilityError);
            }

            Writes.Add((address, value));
            if (address == 0xD1 && FirmwareClearsModeBitOnWrite)
            {
                value = (byte)((value & ~0x05) | 0x02);
            }

            LiveRegisters[address] = value;
        }
    }

    private sealed class FakeMsiDcVariable : IMsiDcVariable
    {
        public FakeMsiDcVariable(bool exists, byte gpuByte)
        {
            Exists = exists;
            GpuByte = gpuByte;
            LastData = exists ? new byte[20] : null;
            if (LastData is not null)
            {
                LastData[1] = 0x01;      // 初始化标志：与显卡模式无关，必须保持不变
                LastData[5] = gpuByte;
            }
        }

        private bool Exists { get; }

        private byte GpuByte { get; }

        public int WriteCount { get; private set; }

        public byte[]? LastData { get; private set; }

        public bool TryRead(out MsiDcGpuByte gpu, out string? error)
        {
            gpu = new MsiDcGpuByte(GpuByte);
            error = Exists ? null : "UEFI 变量 MsiDCVarData 不存在";
            return Exists;
        }

        public bool TryWrite(GraphicsMode mode, out string? error)
        {
            error = null;
            if (!Exists || LastData is null)
            {
                error = "UEFI 变量 MsiDCVarData 不存在，跳过 UEFI 写入";
                return false;
            }

            WriteCount++;
            LastData[5] = new MsiDcGpuByte(LastData[5]).WithRequested(mode);
            return true;
        }
    }
}
