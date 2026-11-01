using MsiControl.Hardware.Msi.Transport.PawnIo;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests.PawnIo;

/// <summary>
/// <see cref="MsiEcPort"/> 的协议时序与写白名单回归测试（全部离线，ScriptedPortIo 替身）。
/// </summary>
public class MsiEcPortTests
{
    // ---------- 读事务时序 ----------

    [Fact]
    public void ReadRegister_FollowsAcpiEcSequence()
    {
        // IBF 空 ×2 →（命令字、地址写入）→ OBF 满 → 数据口出值
        var portIo = ScriptedPortIo.Scripted(
            statusResponses: new Queue<byte>(new byte[] { 0x00, 0x00, 0x01 }),
            dataResponses: new Queue<byte>(new byte[] { 0x42 }));
        using var port = CreatePort(portIo);

        var value = port.ReadRegister(0xD4);

        Assert.Equal(0x42, value);
        var expected = new[]
        {
            (LpcAcpiEcModule.AcpiEcCommandPort, MsiEcPort.ReadRegisterCommand),
            (LpcAcpiEcModule.AcpiEcDataPort, (byte)0xD4),
        };
        Assert.Equal(
            expected.Select(w => (w.Item1, w.Item2)),
            portIo.Writes);
    }

    [Fact]
    public void ReadRegister_TimeoutWaitingInputEmpty_SendsNothing()
    {
        // 状态口恒 IBF=1：第一条命令都不会发出。
        var portIo = ScriptedPortIo.ConstantStatus(MsiEcPort.StatusInputBufferFull);
        using var port = CreatePort(portIo, phaseTimeoutMs: 15);

        var exception = Assert.Throws<EcTimeoutException>(() => port.ReadRegister(0xD4));

        Assert.Contains("IBF", exception.Phase);
        Assert.Empty(portIo.Writes);
    }

    [Fact]
    public void ReadRegister_TimeoutWaitingOutputFull_StopsAfterAddressWrite()
    {
        // IBF 空 ×2 放行命令字与地址，随后 OBF 永不置位 → 超时。
        var portIo = ScriptedPortIo.Scripted(
            statusResponses: new Queue<byte>(new byte[] { 0x00, 0x00 }),
            dataResponses: new Queue<byte>());
        using var port = CreatePort(portIo, phaseTimeoutMs: 15);

        Assert.Throws<EcTimeoutException>(() => port.ReadRegister(0x71));

        var expected = new[]
        {
            (LpcAcpiEcModule.AcpiEcCommandPort, MsiEcPort.ReadRegisterCommand),
            (LpcAcpiEcModule.AcpiEcDataPort, (byte)0x71),
        };
        Assert.Equal(
            expected.Select(w => (w.Item1, w.Item2)),
            portIo.Writes);
    }

    // ---------- 写白名单（阶段 A 空集语义） ----------

    [Fact]
    public void WriteRegister_EmptyWhitelist_RejectsBeforeAnyPortWrite()
    {
        var portIo = ScriptedPortIo.ConstantStatus(0x00);
        using var port = CreatePort(portIo);

        var exception = Assert.Throws<EcWriteBlockedException>(
            () => port.WriteRegister(0xD4, 0x0D));

        Assert.Equal(0xD4, exception.Address);
        Assert.Contains("写白名单", exception.Message);
        Assert.Empty(portIo.Writes); // 闸门在协议层之前：一个端口写都不发生
    }

    [Fact]
    public void WriteRegister_Whitelisted_FollowsAcpiEcSequence()
    {
        var portIo = ScriptedPortIo.Scripted(
            statusResponses: new Queue<byte>(new byte[] { 0x00, 0x00, 0x00 }),
            dataResponses: new Queue<byte>());
        var writable = new HashSet<byte> { 0xD4 };
        using var port = CreatePort(portIo, writableAddresses: writable);

        port.WriteRegister(0xD4, 0x0D);

        var expected = new[]
        {
            (LpcAcpiEcModule.AcpiEcCommandPort, MsiEcPort.WriteRegisterCommand),
            (LpcAcpiEcModule.AcpiEcDataPort, (byte)0xD4),
            (LpcAcpiEcModule.AcpiEcDataPort, (byte)0x0D),
        };
        Assert.Equal(
            expected.Select(w => (w.Item1, w.Item2)),
            portIo.Writes);
    }

    [Fact]
    public void WritableAddresses_DefaultsToEmptySet()
    {
        using var port = CreatePort(ScriptedPortIo.ConstantStatus(0x00));

        Assert.Empty(port.WritableAddresses);
    }

    // ---------- 仲裁说明（P0 实测修订语义） ----------

    [Fact]
    public void ArbitrationNotice_ReportsMissingMutex()
    {
        // 唯一名保证不存在：测试不与系统态的 Access_EC 互相污染。
        var uniqueName = $@"Global\Access_EC_Test_{Guid.NewGuid():N}";

        using var port = CreatePort(
            ScriptedPortIo.ConstantStatus(0x00),
            arbitrationMutexName: uniqueName);

        Assert.NotNull(port.ArbitrationNotice);
        Assert.Contains("不存在", port.ArbitrationNotice);
    }

    // ---------- 辅助 ----------

    private static MsiEcPort CreatePort(
        ScriptedPortIo portIo,
        IReadOnlySet<byte>? writableAddresses = null,
        int phaseTimeoutMs = MsiEcPort.DefaultPhaseTimeoutMs,
        string? arbitrationMutexName = null)
    {
        return new MsiEcPort(
            portIo,
            writableAddresses,
            phaseTimeoutMs,
            arbitrationMutexName: arbitrationMutexName);
    }
}
