using System.Runtime.InteropServices;
using MsiControl.Core;

namespace MsiControl.Application.Host;

/// <summary>
/// 当前供电来源的读取器。
/// </summary>
/// <remarks>
/// 抽象出来有两个目的：① 让供电策略可以在测试中精确构造「拔电 / 插电」时序；
/// ② 允许宿主替换为事件驱动实现（例如桌面壳用 <c>WM_POWERBROADCAST</c> 订阅），
/// 而不改动策略与协调器。
/// </remarks>
public interface IPowerStatusProbe
{
    /// <summary>读取当前供电来源。无法判定时返回 <see cref="PowerSupplyKind.Unknown"/>。</summary>
    PowerSupplyKind Read();
}

/// <summary>
/// 基于 <c>kernel32!GetSystemPowerStatus</c> 的供电来源读取器。
/// </summary>
/// <remarks>
/// <para>
/// <c>ACLineStatus</c>：0 = 电池供电，1 = 交流电，255 = 未知。
/// <b>255 必须映射为 <see cref="PowerSupplyKind.Unknown"/> 而不是猜测</b>——
/// 未知供电下不得自动改动功耗限制（任务书 §11）。
/// </para>
/// <para>
/// 只读取供电状态字段，不读取电池电量、剩余时间等与调度无关的信息。
/// 该调用不需要管理员权限，也不启动任何外部进程。
/// </para>
/// </remarks>
public sealed class Win32PowerStatusProbe : IPowerStatusProbe
{
    /// <inheritdoc />
    public PowerSupplyKind Read()
    {
        // 返回 0 表示调用失败；此时按「未知」处理，不猜测供电来源。
        if (GetSystemPowerStatus(out var status) == 0)
        {
            return PowerSupplyKind.Unknown;
        }

        return status.ACLineStatus switch
        {
            0 => PowerSupplyKind.Battery,
            1 => PowerSupplyKind.Ac,
            _ => PowerSupplyKind.Unknown
        };
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary><c>SYSTEM_POWER_STATUS</c> 结构（只保留供电状态字段的完整布局）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        /// <summary>0 = 电池供电，1 = 交流电，255 = 未知。</summary>
        public byte ACLineStatus;

        /// <summary>电池充电状态标志。</summary>
        public byte BatteryFlag;

        /// <summary>电量百分比。</summary>
        public byte BatteryLifePercent;

        /// <summary>系统状态标志。</summary>
        public byte SystemStatusFlag;

        /// <summary>剩余秒数。</summary>
        public int BatteryLifeTime;

        /// <summary>满电秒数。</summary>
        public int BatteryFullLifeTime;
    }
}

/// <summary>供电状态变化的监听器。</summary>
public interface IPowerSupplyWatcher : IDisposable
{
    /// <summary>最近一次读到的供电状态。</summary>
    PowerSupplyKind Current { get; }

    /// <summary>供电状态发生变化时触发。可能在任意线程触发。</summary>
    event EventHandler<PowerSupplyKind>? Changed;

    /// <summary>开始监听。重复调用为空操作。</summary>
    void Start();

    /// <summary>停止监听。重复调用为空操作。</summary>
    void Stop();
}

/// <summary>
/// 轮询式供电监听器。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是轮询：</b>事件式订阅（<c>SystemEvents.PowerModeChanged</c>）需要
/// Windows 桌面框架依赖，而应用层刻意不依赖 WinForms/WPF。供电变化是个低频事件，
/// 默认 5 秒的只读探测开销可忽略；同时探测本身不启动任何外部进程，
/// 符合任务书 §20「不周期性启动外部命令进程」。
/// </para>
/// <para>
/// 宿主可在装配时注入自己的读取器（<see cref="IPowerStatusProbe"/>），
/// 由桌面壳实现事件驱动版本时无需改动策略与协调器。
/// </para>
/// <para>
/// <b>触发条件：</b>只有状态值真正改变才触发 <see cref="Changed"/>。
/// 重复读数不触发事件，避免「同一个状态被反复处理」导致的重复写入（任务书 §11）。
/// </para>
/// </remarks>
public sealed class PollingPowerSupplyWatcher : IPowerSupplyWatcher
{
    /// <summary>允许的最小轮询间隔。</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>默认轮询间隔（秒）。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly IPowerStatusProbe _probe;
    private readonly TimeSpan _interval;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>构造监听器。</summary>
    /// <param name="probe">供电读取器。为空时使用 <see cref="Win32PowerStatusProbe"/>。</param>
    /// <param name="interval">轮询间隔。小于 <see cref="MinInterval"/> 时取最小值。</param>
    public PollingPowerSupplyWatcher(IPowerStatusProbe? probe = null, TimeSpan? interval = null)
    {
        _probe = probe ?? new Win32PowerStatusProbe();
        var requested = interval ?? DefaultInterval;
        _interval = requested < MinInterval ? MinInterval : requested;
        Current = SafeRead();
    }

    /// <inheritdoc />
    public PowerSupplyKind Current { get; private set; }

    /// <inheritdoc />
    public event EventHandler<PowerSupplyKind>? Changed;

    /// <summary>实际使用的轮询间隔。</summary>
    public TimeSpan Interval => _interval;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => LoopAsync(_cts.Token), CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            if (_cts is null)
            {
                return;
            }

            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            _cts.Dispose();
            _cts = null;
            _loop = null;
        }
    }

    /// <summary>立即读取一次并按需触发事件。供宿主在启动与唤醒时调用。</summary>
    /// <returns>本次读取到的状态。</returns>
    public PowerSupplyKind Refresh()
    {
        var value = SafeRead();
        if (value != Current)
        {
            Current = value;
            Changed?.Invoke(this, value);
        }

        return value;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    Refresh();
                }
                catch (Exception)
                {
                    // 单次探测失败不影响后续探测：供电策略对 Unknown 本身就有明确处理。
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private PowerSupplyKind SafeRead()
    {
        try
        {
            return _probe.Read();
        }
        catch (Exception)
        {
            return PowerSupplyKind.Unknown;
        }
    }
}
