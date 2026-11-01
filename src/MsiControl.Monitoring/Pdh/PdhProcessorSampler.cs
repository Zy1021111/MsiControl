using System.Globalization;
using System.Runtime.InteropServices;

namespace MsiControl.Monitoring.Pdh;

/// <summary>单个处理器实例的性能计数采样。</summary>
/// <param name="Name">实例名，例如 <c>0,3</c>（第 0 组第 3 核）或 <c>_Total</c>。</param>
/// <param name="PercentPerformance">相对标称频率的性能百分比（<c>% Processor Performance</c>）。</param>
/// <param name="PercentUtility">处理器利用率百分比（<c>% Processor Time</c>）。</param>
/// <param name="NominalMhz">标称频率（MHz），即该实例的参考基准（<c>Processor Frequency</c> 计数器）。</param>
public sealed record ProcessorCounterSample(
    string Name,
    double PercentPerformance,
    double PercentUtility,
    double NominalMhz)
{
    /// <summary>是否为「核」实例（形如 <c>组号,序号</c>）。</summary>
    public bool IsCore => !Name.StartsWith('_') && Name.Contains(',');

    /// <summary>是否为「总计」实例（<c>_Total</c> 或 <c>组号,_Total</c>）。</summary>
    public bool IsTotal => Name.EndsWith("_Total", StringComparison.Ordinal);

    /// <summary>该核的有效频率（MHz）。标称频率 × 性能百分比。</summary>
    public double EffectiveMhz => NominalMhz * PercentPerformance / 100.0;
}

/// <summary>一次处理器性能计数采样的结果。</summary>
/// <param name="Samples">全部实例。</param>
/// <param name="Error">采样失败原因；成功为 <see langword="null"/>。</param>
public sealed record ProcessorCountersSnapshot(IReadOnlyList<ProcessorCounterSample> Samples, string? Error)
{
    /// <summary>采样是否成功且至少返回一个实例。</summary>
    public bool HasData => Error is null && Samples.Count > 0;
}

/// <summary>
/// 基于 PDH 的处理器性能计数采样器（CPU 有效频率、利用率与标称频率）。
/// </summary>
/// <remarks>
/// <para>
/// 通过 <c>pdh.dll</c> 以通配符实例读取
/// <c>\Processor Information(*)\% Processor Performance</c>、
/// <c>\Processor Information(*)\% Processor Time</c> 与
/// <c>\Processor Information(*)\Processor Frequency</c> 三个计数器。
/// 热路径单次读取在亚毫秒级，远快于 WMI 性能计数类（实测约 270 ms/次）。
/// </para>
/// <para>
/// <b>预热：</b>前两类计数器需要两次采样才有值。构造时做两次间隔采样的预热，
/// 因此首次 <see cref="Sample"/> 即可返回有效数据。
/// </para>
/// <para>
/// <b>线程模型：</b>供单一采样循环使用；内部加锁以防御误用。
/// </para>
/// </remarks>
public sealed class PdhProcessorSampler : IDisposable
{
    /// <summary>数据来源说明。</summary>
    public const string SourceName = "PDH:\\Processor Information";

    private const string PerfPath = @"\Processor Information(*)\% Processor Performance";
    private const string TimePath = @"\Processor Information(*)\% Processor Time";
    private const string FreqPath = @"\Processor Information(*)\Processor Frequency";

    private readonly object _gate = new();
    private IntPtr _query;
    private IntPtr _performance;
    private IntPtr _time;
    private IntPtr _frequency;
    private string? _initError;
    private bool _disposed;

    /// <summary>构造并预热采样器。</summary>
    public PdhProcessorSampler()
    {
        lock (_gate)
        {
            Initialize();
        }
    }

    /// <summary>是否初始化成功。</summary>
    public bool IsAvailable => _initError is null;

    /// <summary>初始化失败原因。</summary>
    public string? Error => _initError;

    /// <summary>执行一次采样。</summary>
    public ProcessorCountersSnapshot Sample()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return new ProcessorCountersSnapshot(Array.Empty<ProcessorCounterSample>(), "采样器已释放。");
            }

            if (_initError is not null)
            {
                return new ProcessorCountersSnapshot(Array.Empty<ProcessorCounterSample>(), _initError);
            }

            var collect = PdhNative.PdhCollectQueryData(_query);
            if (collect is not (PdhNative.ErrorSuccess or PdhNative.CStatusNewData or PdhNative.CStatusInvalidData))
            {
                return new ProcessorCountersSnapshot(
                    Array.Empty<ProcessorCounterSample>(),
                    $"PdhCollectQueryData 失败：{PdhNative.Describe(collect)}");
            }

            var performance = ReadArray(_performance);
            var time = ReadArray(_time);
            var frequency = ReadArray(_frequency);

            if (performance.Count == 0)
            {
                return new ProcessorCountersSnapshot(
                    Array.Empty<ProcessorCounterSample>(),
                    "PDH 未返回任何有效的 % Processor Performance 实例（可能首次采样尚不可用）。");
            }

            var samples = performance
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new ProcessorCounterSample(
                    kv.Key,
                    kv.Value,
                    time.TryGetValue(kv.Key, out var t) ? t : 0.0,
                    frequency.TryGetValue(kv.Key, out var f) ? f : 0.0))
                .ToArray();

            return new ProcessorCountersSnapshot(samples, null);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_query != IntPtr.Zero)
                {
                    PdhNative.PdhCloseQuery(_query);
                }
            }
            catch (Exception)
            {
                // 释放阶段失败无补救价值。
            }

            _query = IntPtr.Zero;
            _performance = IntPtr.Zero;
            _time = IntPtr.Zero;
            _frequency = IntPtr.Zero;
        }
    }

    private void Initialize()
    {
        try
        {
            var open = PdhNative.PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out _query);
            if (open != PdhNative.ErrorSuccess)
            {
                _initError = $"PdhOpenQuery 失败：{PdhNative.Describe(open)}";
                return;
            }

            var perf = PdhNative.PdhAddEnglishCounterW(_query, PerfPath, IntPtr.Zero, out _performance);
            if (perf != PdhNative.ErrorSuccess)
            {
                _initError = $"添加计数器失败（{PerfPath}）：{PdhNative.Describe(perf)}";
                return;
            }

            var time = PdhNative.PdhAddEnglishCounterW(_query, TimePath, IntPtr.Zero, out _time);
            if (time != PdhNative.ErrorSuccess)
            {
                _initError = $"添加计数器失败（{TimePath}）：{PdhNative.Describe(time)}";
                return;
            }

            var freq = PdhNative.PdhAddEnglishCounterW(_query, FreqPath, IntPtr.Zero, out _frequency);
            if (freq != PdhNative.ErrorSuccess)
            {
                _initError = $"添加计数器失败（{FreqPath}）：{PdhNative.Describe(freq)}";
                return;
            }

            // 预热两次采样，使首个 Sample() 即可给出有效百分比。
            PdhNative.PdhCollectQueryData(_query);
            Thread.Sleep(16);
            PdhNative.PdhCollectQueryData(_query);
        }
        catch (DllNotFoundException)
        {
            _initError = "未找到 pdh.dll，无法读取处理器性能计数器。";
        }
        catch (Exception ex)
        {
            _initError = $"{ex.GetType().Name}：{ex.Message}";
        }
    }

    private static Dictionary<string, double> ReadArray(IntPtr counter)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);

        try
        {
            uint bufferSize = 0;
            var first = PdhNative.PdhGetFormattedCounterArrayW(
                counter,
                PdhNative.FmtDouble,
                ref bufferSize,
                out _,
                Array.Empty<byte>());

            if (first != PdhNative.MoreData || bufferSize == 0)
            {
                return result;
            }

            var buffer = new byte[(int)bufferSize];
            var second = PdhNative.PdhGetFormattedCounterArrayW(
                counter,
                PdhNative.FmtDouble,
                ref bufferSize,
                out var itemCount,
                buffer);

            if (second is not (PdhNative.ErrorSuccess or PdhNative.CStatusNewData))
            {
                return result;
            }

            var itemSize = Marshal.SizeOf<PdhFmtCounterValueItem>();
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var basePtr = handle.AddrOfPinnedObject();
                for (var i = 0; i < itemCount; i++)
                {
                    var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(
                        IntPtr.Add(basePtr, i * itemSize));

                    if (item.Value.CStatus is not (PdhNative.CStatusValidData or PdhNative.CStatusNewData))
                    {
                        continue;
                    }

                    var name = Marshal.PtrToStringUni(item.Name);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    result[name] = item.Value.DoubleValue;
                }
            }
            finally
            {
                handle.Free();
            }
        }
        catch (Exception)
        {
            // 读取失败按无数据处理，由上层统一转为「不可用」。
        }

        return result;
    }

    /// <summary>格式化实例数（供诊断输出）。</summary>
    public static string FormatSampleCount(int count)
        => count.ToString(CultureInfo.InvariantCulture);
}
