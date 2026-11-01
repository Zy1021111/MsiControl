using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Hardware.Msi.Tests.PawnIo;

/// <summary>
/// 脚本化 EC 端口替身：按端口分类响应读请求，并记录全部写请求。
/// </summary>
/// <remarks>
/// 用途：驱动 <see cref="MsiEcPort"/> 的协议时序单测（ACPI EC 的 IBF / OBF
/// 状态机），完全离线，不依赖真实 PawnIO 驱动。
/// </remarks>
internal sealed class ScriptedPortIo : IEcPortIo
{
    private readonly Func<int, byte> _reader;

    /// <summary>记录的全部写请求（端口, 值），按发生顺序。</summary>
    public List<(int Port, byte Value)> Writes { get; } = new();

    /// <summary>记录的全部读请求（端口），按发生顺序。</summary>
    public List<int> Reads { get; } = new();

    private ScriptedPortIo(Func<int, byte> reader)
        => _reader = reader;

    /// <summary>
    /// 构造：状态口（0x66）按脚本逐个出队，耗尽后回到稳态字；数据口（0x62）按脚本逐个出队。
    /// </summary>
    /// <param name="statusResponses">读 0x66 的状态字序列（超时路径测试只需给出前几拍）。</param>
    /// <param name="dataResponses">读 0x62 的数据序列。</param>
    /// <param name="steadyStatus">状态脚本耗尽后的固定返回值（默认 IBF/OBF 均空）。</param>
    public static ScriptedPortIo Scripted(
        Queue<byte> statusResponses,
        Queue<byte> dataResponses,
        byte steadyStatus = 0x00)
    {
        return new ScriptedPortIo(port => port switch
        {
            LpcAcpiEcModule.AcpiEcCommandPort => DequeueOr(statusResponses, steadyStatus),
            LpcAcpiEcModule.AcpiEcDataPort => Dequeue(dataResponses, port),
            _ => throw new InvalidOperationException($"替身未配置端口 0x{port:X2} 的读脚本。"),
        });
    }

    /// <summary>
    /// 构造：状态口恒返回固定状态字（用于超时路径测试）。
    /// </summary>
    public static ScriptedPortIo ConstantStatus(byte status)
    {
        return new ScriptedPortIo(port => port == LpcAcpiEcModule.AcpiEcCommandPort
            ? status
            : throw new InvalidOperationException("恒定状态替身不期望读数据口。"));
    }

    /// <inheritdoc />
    public byte ReadPort(int port)
    {
        Reads.Add(port);
        return _reader(port);
    }

    /// <inheritdoc />
    public void WritePort(int port, byte value)
        => Writes.Add((port, value));

    private static byte Dequeue(Queue<byte> queue, int port)
    {
        if (queue.Count == 0)
        {
            throw new InvalidOperationException($"端口 0x{port:X2} 的读脚本已耗尽。");
        }

        return queue.Dequeue();
    }

    private static byte DequeueOr(Queue<byte> queue, byte fallback)
        => queue.Count > 0 ? queue.Dequeue() : fallback;
}
