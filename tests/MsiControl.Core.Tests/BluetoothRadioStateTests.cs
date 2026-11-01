using MsiControl.Core.Models;
using Xunit;

namespace MsiControl.Core.Tests;

/// <summary>
/// 蓝牙射频状态模型测试。
/// </summary>
/// <remarks>
/// 判据重点：中间态（Disabled/Unknown）<b>不得</b>被当成「已关闭」。
/// 把「被系统策略禁用」说成「用户关掉了」会让界面给出的结论直接错，属假状态。
/// </remarks>
public sealed class BluetoothRadioStateTests
{
    [Theory]
    [InlineData("On", true)]
    [InlineData("Off", false)]
    public void IsOn_KnownStates_AreBoolean(string stateText, bool expected)
    {
        var state = BluetoothRadioState.Create(stateText, "蓝牙");
        Assert.Equal(expected, state.IsOn);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("Unknown")]
    public void IsOn_IntermediateStates_AreNull(string stateText)
    {
        var state = BluetoothRadioState.Create(stateText, "蓝牙");
        Assert.Null(state.IsOn);
    }

    [Fact]
    public void Describe_KeepsOriginalReason()
    {
        // 原因必须来自系统上报的状态，不能用「切换失败」这类通用话术盖掉。
        Assert.Contains("系统禁用", BluetoothRadioState.Create("Disabled", "蓝牙").Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_EmptyName_FallsBackToChineseLabel()
    {
        var state = BluetoothRadioState.Create("On", "   ");
        Assert.Equal("蓝牙", state.Name);
    }

    [Fact]
    public void Create_EmptyStateText_Throws()
        => Assert.Throws<ArgumentException>(() => BluetoothRadioState.Create("", "蓝牙"));
}
