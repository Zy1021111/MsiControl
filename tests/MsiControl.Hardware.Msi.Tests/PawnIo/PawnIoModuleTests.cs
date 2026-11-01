using System.Security.Cryptography;
using System.Text;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests.PawnIo;

/// <summary>
/// PawnIO 层的离线回归：嵌入模块的供应链校验、执行请求打包协议。
/// </summary>
public class PawnIoModuleTests
{
    // ---------- 供应链校验（证据清单 §15.2） ----------

    [Fact]
    public void EmbeddedModule_MatchesRecordedSha256()
    {
        var binary = LpcAcpiEcModule.ReadEmbeddedModule();

        var actual = Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant();
        Assert.Equal(LpcAcpiEcModule.ExpectedSha256, actual);
        Assert.Equal(2612, binary.Length); // 与证据记录的模块大小一致
    }

    [Fact]
    public void VerifyModuleBinary_RejectsTamperedBinary()
    {
        var tampered = (byte[])LpcAcpiEcModule.ReadEmbeddedModule().Clone();
        tampered[0] ^= 0xFF;

        var exception = Assert.Throws<PawnIoException>(
            () => LpcAcpiEcModule.VerifyModuleBinary(tampered));

        Assert.Contains("哈希校验失败", exception.Message);
    }

    // ---------- 执行请求打包（IOCTL 协议事实） ----------

    [Fact]
    public void BuildExecuteInput_NamePaddingAndCellLayout()
    {
        var buffer = PawnIoDriver.BuildExecuteInput("ioctl_pio_read", new long[] { 0x66 });

        Assert.Equal(PawnIoDriver.FunctionNameLength + sizeof(long), buffer.Length);

        var name = new byte[PawnIoDriver.FunctionNameLength];
        Array.Copy(buffer, name, PawnIoDriver.FunctionNameLength);
        Assert.Equal("ioctl_pio_read", Encoding.ASCII.GetString(name).TrimEnd('\0'));
        Assert.All(name[^9..], b => Assert.Equal(0, b)); // 名字后 NUL 补齐

        var cell = BitConverter.ToInt64(buffer, PawnIoDriver.FunctionNameLength);
        Assert.Equal(0x66, cell);
    }

    [Fact]
    public void BuildExecuteInput_RejectsOverlongNames()
    {
        Assert.Throws<ArgumentException>(
            () => PawnIoDriver.BuildExecuteInput(new string('a', 32), Array.Empty<long>()));
    }

    [Fact]
    public void BuildExecuteInput_RejectsEmptyName()
    {
        Assert.Throws<ArgumentException>(
            () => PawnIoDriver.BuildExecuteInput(" ", Array.Empty<long>()));
    }

    // ---------- 协议常量（与公开实现事实一致，改动须同步方案文档） ----------

    [Fact]
    public void ProtocolConstants_AreStable()
    {
        // 41394<<16 | (0x821<<2) / | (0x841<<2)：pawnio.eu 公开协议。
        // 41394 = 0xA1B2，故 DeviceType = 0xA1B20000。
        Assert.Equal(0xA1B22084u, PawnIoDriver.IoctlLoadBinary);
        Assert.Equal(0xA1B22104u, PawnIoDriver.IoctlExecute);
        Assert.Equal(32, PawnIoDriver.FunctionNameLength);
        Assert.Equal(0x62, LpcAcpiEcModule.AcpiEcDataPort);
        Assert.Equal(0x66, LpcAcpiEcModule.AcpiEcCommandPort);
    }
}
