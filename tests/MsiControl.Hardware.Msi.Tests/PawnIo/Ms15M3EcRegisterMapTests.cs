using MsiControl.Hardware.Msi.Transport.PawnIo;
using Xunit;

namespace MsiControl.Hardware.Msi.Tests.PawnIo;

/// <summary>
/// <see cref="Ms15M3EcRegisterMap"/> 的回归测试：固件版本门、寄存器表完整性、模式值表。
/// </summary>
public class Ms15M3EcRegisterMapTests
{
    // ---------- 固件版本门 ----------

    [Theory]
    [InlineData("15M3EMS1.105")]
    [InlineData("15M3EMS1.113")] // 本机当前固件
    public void IsSupportedFirmware_AcceptsWhitelistedVersions(string version)
    {
        Assert.True(Ms15M3EcRegisterMap.IsSupportedFirmware(version));
    }

    [Theory]
    [InlineData("15M3EMS1.114")] // 超出白名单：禁止套用旧寄存器表
    [InlineData("15M3EMS1.104")]
    [InlineData("16V3EMS1.105")] // 其他机型
    [InlineData("15M3EMS1")]     // 缺次版本
    [InlineData("15M3EMS1.113.1")] // 版本串带尾巴：精确匹配拒绝
    [InlineData("")]
    [InlineData(null)]
    public void IsSupportedFirmware_RejectsUnsupportedVersions(string? version)
    {
        Assert.False(Ms15M3EcRegisterMap.IsSupportedFirmware(version));
    }

    // ---------- 寄存器表 ----------

    [Fact]
    public void All_ContainsElevenRegistersWithUniqueAddresses()
    {
        Assert.Equal(11, Ms15M3EcRegisterMap.All.Count);
        Assert.Equal(
            Ms15M3EcRegisterMap.All.Count,
            Ms15M3EcRegisterMap.All.Select(r => r.Address).Distinct().Count());
    }

    [Fact]
    public void Find_ReturnsRegisteredRegisters()
    {
        Assert.Equal(Ms15M3EcRegisterMap.CpuFanPercent, Ms15M3EcRegisterMap.Find(0x71));
        Assert.Equal(Ms15M3EcRegisterMap.GpuFanPercent, Ms15M3EcRegisterMap.Find(0x89));
        Assert.Equal(Ms15M3EcRegisterMap.FanMode, Ms15M3EcRegisterMap.Find(0xD4));
        Assert.Equal(Ms15M3EcRegisterMap.CoolerBoost, Ms15M3EcRegisterMap.Find(0x98));
        Assert.Equal(Ms15M3EcRegisterMap.GpuMux, Ms15M3EcRegisterMap.Find(0x2E));
        Assert.Equal(Ms15M3EcRegisterMap.GpuSwitchTrigger, Ms15M3EcRegisterMap.Find(0xD1));
        Assert.Null(Ms15M3EcRegisterMap.Find(0xFF)); // 未登记地址
    }

    [Fact]
    public void Registers_MatchConfG2_10CommunityEvidence()
    {
        // msi-ec CONF_G2_10 的关键字段（社区证据，Detected 级；改动须同步证据文档）。
        Assert.Equal(0x68, Ms15M3EcRegisterMap.CpuTemperature.Address);
        Assert.Equal(0x71, Ms15M3EcRegisterMap.CpuFanPercent.Address);
        Assert.Equal(0x80, Ms15M3EcRegisterMap.GpuTemperature.Address);
        Assert.Equal(0x89, Ms15M3EcRegisterMap.GpuFanPercent.Address);
        Assert.Equal(0x98, Ms15M3EcRegisterMap.CoolerBoost.Address);
        Assert.Equal(0xD2, Ms15M3EcRegisterMap.ShiftMode.Address);
        Assert.Equal(0xD4, Ms15M3EcRegisterMap.FanMode.Address);
    }

    // ---------- 模式值表 ----------

    [Fact]
    public void FanModes_MatchConfG2_10Values()
    {
        Assert.Equal("auto", Ms15M3EcRegisterMap.FanModes[0x0D]);
        Assert.Equal("silent", Ms15M3EcRegisterMap.FanModes[0x1D]);
        Assert.Equal("advanced", Ms15M3EcRegisterMap.FanModes[0x8D]);
    }

    [Fact]
    public void ShiftModes_MatchConfG2_10Values()
    {
        Assert.Equal("turbo", Ms15M3EcRegisterMap.ShiftModes[0xC4]);
        Assert.Equal("eco", Ms15M3EcRegisterMap.ShiftModes[0xC2]);
        Assert.Equal("comfort", Ms15M3EcRegisterMap.ShiftModes[0xC1]);
    }

    // ---------- 写候选 ----------

    [Fact]
    public void WriteCandidates_ContainsWriteCandidatesFromEvidence()
    {
        // 0xD4/0x98/0xD2：本机受控实验（HWE §17/§20）；0x71/0x89：占空比（HWE §23）；
        // 0x2E/0xD1：显卡切换协议（HWE §21）。
        Assert.Equal(
            new HashSet<byte>
            {
                Ms15M3EcRegisterMap.FanMode.Address,
                Ms15M3EcRegisterMap.CoolerBoost.Address,
                Ms15M3EcRegisterMap.ShiftMode.Address,
                Ms15M3EcRegisterMap.CpuFanPercent.Address,
                Ms15M3EcRegisterMap.GpuFanPercent.Address,
                Ms15M3EcRegisterMap.GpuMux.Address,
                Ms15M3EcRegisterMap.GpuSwitchTrigger.Address
            },
            Ms15M3EcRegisterMap.WriteCandidates.ToHashSet());
    }
}
