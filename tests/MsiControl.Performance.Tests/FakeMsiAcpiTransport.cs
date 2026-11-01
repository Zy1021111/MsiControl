using MsiControl.Hardware.Msi.Transport;

namespace MsiControl.Performance.Tests;

/// <summary>
/// 测试用的假只读通道。
/// </summary>
/// <remarks>
/// 只实现只读调用，并记录被调用的方法名——用于断言实现**没有**调用任何 <c>Set_*</c>。
/// 不发生产生任何真实 WMI 访问。
/// </remarks>
internal sealed class FakeMsiAcpiTransport : IMsiAcpiTransport
{
    private readonly Func<string, object?, MsiAcpiInvocationResult> _handler;

    public FakeMsiAcpiTransport(Func<string, object?, MsiAcpiInvocationResult>? handler = null)
        => _handler = handler ?? DefaultHandler;

    /// <summary>被调用的方法名，按调用顺序。</summary>
    public List<string> InvokedMethods { get; } = new();

    /// <summary>被传入的 Data 参数，按键与方法名对齐（便于断言「未传任何猜测缓冲区」）。</summary>
    public List<object?> PassedData { get; } = new();

    /// <inheritdoc />
    public Task<MsiAcpiInvocationResult> InvokeReadAsync(
        string methodName,
        object? data,
        CancellationToken cancellationToken)
    {
        InvokedMethods.Add(methodName);
        PassedData.Add(data);

        if (!MsiAcpiMethodNames.IsReadOnly(methodName))
        {
            return Task.FromResult(MsiAcpiInvocationResult.Blocked(
                $"方法 {methodName} 未登记为只读方法。"));
        }

        return Task.FromResult(_handler(methodName, data));
    }

    private static MsiAcpiInvocationResult DefaultHandler(string methodName, object? data)
        => MsiAcpiInvocationResult.AcceptedUnconfirmed(
            new byte[] { 0x01, 0x02, 0x03, 0x04 },
            $"{methodName}：provider 未暴露 ReturnValue，无法判定固件是否接受该调用。");
}
