using System.Diagnostics;
using System.Globalization;
using System.Text;
using MsiControl.Core;
using MsiControl.Core.Models;
using MsiControl.Hardware.Msi.Capabilities;
using MsiControl.Hardware.Msi.Transport;
using MsiControl.Hardware.Msi.Transport.PawnIo;
using MsiControl.Monitoring.Nvml;
using MsiControl.Monitoring.Pdh;
using MsiControl.Monitoring.Sources;
using Xunit;
using Xunit.Abstractions;

namespace MsiControl.Monitoring.Tests;

/// <summary>
/// 实机验证与资源占用测量。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认不执行。</b>必须显式设置环境变量 <c>MSI_CONTROL_HW_TESTS=1</c>，
/// 以免在没有目标硬件（或没有 WMI/NVML 的环境）上产生误判。
/// </para>
/// <para>
/// 全部操作为**只读**：不调用任何 <c>Set_*</c>，不修改功耗、风扇或显卡设置。
/// </para>
/// <para>
/// 报告同时写入测试程序集目录下的 <c>hw-scan.txt</c>，便于将原始输出留作交付证据。
/// </para>
/// </remarks>
public sealed class RealHardwareTests
{
    private const string EnableVariable = "MSI_CONTROL_HW_TESTS";

    private readonly ITestOutputHelper _output;

    /// <summary>构造测试。</summary>
    public RealHardwareTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task 实机_传感器覆盖与资源占用测量()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            _output.WriteLine($"已跳过：未设置 {EnableVariable}=1。");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("# MSI-Control 任务 03 实机只读扫描报告");
        report.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine($"机器：{Environment.MachineName}，CLR {Environment.Version}");
        report.AppendLine($"OS：{Environment.OSVersion}");
        report.AppendLine();

        var identity = new DeviceIdentity(
            "MSI Vector 16 HX AI A2XWHG",
            DeviceIdentity.TargetBoardCode);
        var capabilities = CapabilityBaseline.ForDevice(identity);

        report.AppendLine("## 1. 能力基线（用于门控）");
        foreach (var status in capabilities.All.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            report.AppendLine($"- {status.Key} = {status.Level}");
        }

        report.AppendLine();
        report.AppendLine("## 2. 各数据源单次读取耗时（20 次）");

        var sampler = new PdhProcessorSampler();
        var gpuSession = new NvmlSession();
        var gpuSource = new NvmlGpuSource(gpuSession, new GpuPresenceProbe());
        var presenceProbe = new GpuPresenceProbe();
        var battery = new BatteryStatusPowerSource();
        var memory = new MemoryUsageSource();
        var thermal = new AcpiThermalZoneCpuTemperatureSource();
        var frequency = new ProcessorFrequencySource(sampler);
        var transport = new WmiMsiAcpiTransport();
        var ec = new MsiAcpiEcFirmwareSource(transport);

        await MeasureAsync("ACPI 热区温度", 20, () => thermal.ReadAsync(CancellationToken.None), report);
        await MeasureAsync("处理器性能计数（频率）", 20, () => frequency.ReadAsync(CancellationToken.None), report);
        await MeasureAsync("处理器性能计数（利用率）", 20, () => new ProcessorUtilizationSource(sampler).ReadAsync(CancellationToken.None), report);
        await MeasureAsync("内存占用", 20, () => memory.ReadAsync(CancellationToken.None), report);
        await MeasureAsync("电池侧功率", 20, () => battery.ReadAsync(CancellationToken.None), report);
        await MeasureAsync("独显在线性探测", 20, () => Task.FromResult(presenceProbe.Probe()), report);
        await MeasureAsync("独显 NVML 四项", 20, () => gpuSource.ReadAsync(CancellationToken.None), report);
        await MeasureAsync("独显 NVML 扩展项", 20, () => ((IExtendedSensorSource)gpuSource).ReadAsync(CancellationToken.None), report);
        await MeasureAsync("EC 固件响应（首次）", 1, () => ec.ReadAsync(CancellationToken.None), report);

        report.AppendLine();
        report.AppendLine("## 3. 采样快照（真实读数）");

        var provider = MonitoringProviderFactory.CreateDefault(capabilities, MonitoringOptions.Default);
        var snapshot = await provider.ReadAsync(CancellationToken.None);
        DumpSnapshot(snapshot, report);

        report.AppendLine();
        report.AppendLine("## 4. EC 固件响应");
        report.AppendLine($"- 可读：{provider.EcFirmware?.IsAvailable}");
        report.AppendLine($"- 原始文本：{provider.EcFirmware?.RawAscii ?? "(无)"}");
        report.AppendLine($"- 原始字节：{provider.EcFirmware?.RawHex ?? "(无)"}");
        report.AppendLine($"- 说明：{provider.EcFirmware?.Error ?? "(无)"}");

        report.AppendLine();
        report.AppendLine("## 5. 诊断（诊断开关关闭时也应能取到数据源状态）");
        foreach (var source in provider.Diagnostics.Sources)
        {
            report.AppendLine(
                $"- {source.Key}：最近成功={source.LastSuccessAt?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "-"}" +
                $"，耗时={source.LastDuration.TotalMilliseconds:0.##} ms，连续失败={source.ConsecutiveFailures}");
        }

        foreach (var note in provider.Diagnostics.Notes)
        {
            report.AppendLine($"- 备注：{note}");
        }

        report.AppendLine();
        report.AppendLine("## 6. 采样循环资源占用（每 1 秒一次，共 20 次）");

        await MeasureLoopAsync(provider, report);

        provider.Dispose();

        var path = Path.Combine(AppContext.BaseDirectory, "hw-scan.txt");
        await File.WriteAllTextAsync(path, report.ToString(), Encoding.UTF8);
        _output.WriteLine(report.ToString());
    }

    /// <summary>
    /// 实机验证：Intel RAPL 封装能耗计数器可读且换算出的功耗落在合理区间。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>默认不执行</b>，与上面的扫描共用开关 <c>MSI_CONTROL_HW_TESTS=1</c>。
    /// </para>
    /// <para>
    /// 只读：仅读 <c>MSR_RAPL_POWER_UNIT</c>(0x606) 与 <c>MSR_PKG_ENERGY_STATUS</c>(0x611)，
    /// 不写任何 MSR。断言的是「能读、且量级可信」，不是某个具体瓦数——
    /// 空闲与负载下的真实读数随场景变化，把具体数值写死只会制造假失败。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 实机_IntelRAPL封装功耗可读()
    {
        if (Environment.GetEnvironmentVariable(EnableVariable) != "1")
        {
            _output.WriteLine($"已跳过：未设置 {EnableVariable}=1。");
            return;
        }

        var report = new StringBuilder();
        report.AppendLine("# Intel RAPL 封装功耗实机验证");
        report.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        using var access = new IntelMsrAccess();
        var reader = new PawnIoRaplEnergyReader(access);

        if (!reader.TryReadPackageEnergy(out var first, out var error))
        {
            report.AppendLine($"- 首次读取失败：{error}");
            _output.WriteLine(report.ToString());
            Assert.Fail($"RAPL 能耗计数器不可读：{error}");
            return;
        }

        report.AppendLine($"- 能量单位指数：{first.EnergyUnitExponent}（1/{1 << first.EnergyUnitExponent} J）");
        report.AppendLine($"- 累加器初值：0x{first.EnergyUnits:X8}");

        var source = new RaplCpuPowerSource(reader);
        var values = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var reading = await source.ReadAsync(CancellationToken.None);
            report.AppendLine(
                $"- 第 {i + 1} 次：{reading.Status}，" +
                (reading.Value.HasValue
                    ? $"{reading.Value.Value.ToString("0.00", CultureInfo.InvariantCulture)} W"
                    : reading.Detail));

            if (reading.Value.HasValue)
            {
                values.Add(reading.Value.Value);
            }

            await Task.Delay(1000);
        }

        _output.WriteLine(report.ToString());

        Assert.True(values.Count >= 4, $"5 次采样中有效读数不足 4 次：{report}");
        Assert.All(values, watt => Assert.InRange(watt, 0.5, RaplCpuPowerSource.MaxPlausibleWatt));
    }

    private static async Task MeasureAsync<T>(
        string label,
        int iterations,
        Func<Task<T>> action,
        StringBuilder report)
    {
        var samples = new List<double>(iterations);
        object? lastValue = null;

        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            lastValue = await action().ConfigureAwait(false);
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }

        var min = samples.Min();
        var max = samples.Max();
        var avg = samples.Average();

        report.AppendLine(
            $"- {label}：最小 {min:0.###} ms，平均 {avg:0.###} ms，最大 {max:0.###} ms" +
            $"（{iterations} 次）；末次结果类型 {lastValue?.GetType().Name ?? "null"}");
    }

    private static async Task MeasureLoopAsync(MonitoringProvider provider, StringBuilder report)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();

        var cpuBefore = process.TotalProcessorTime;
        var memoryBefore = process.WorkingSet64;

        var samples = new List<double>(20);
        var statuses = new List<string>(20);

        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var snapshot = await provider.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            watch.Stop();
            samples.Add(watch.Elapsed.TotalMilliseconds);
            statuses.Add(
                $"{snapshot.Cpu.Temperature.Status}/{snapshot.Cpu.FrequencyMhz.Status}/" +
                $"{snapshot.Gpu.Temperature.Status}/{snapshot.SystemPower.Reading.Status}");
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }

        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;
        var memoryAfter = process.WorkingSet64;

        report.AppendLine($"- 单次采样耗时：最小 {samples.Min():0.###} ms，平均 {samples.Average():0.###} ms，最大 {samples.Max():0.###} ms");
        report.AppendLine($"- 采样期总 CPU 时间增量：{(cpuAfter - cpuBefore).TotalMilliseconds:0.###} ms（约 {samples.Count} 秒挂钟时间）");
        report.AppendLine($"- 每个采样周期占单核比例：{(cpuAfter - cpuBefore).TotalMilliseconds / samples.Count / 10.0:0.###} %（按 10 段单核 1 秒计）");
        report.AppendLine($"- 工作集变化：{memoryBefore / 1024.0 / 1024.0:0.##} MiB → {memoryAfter / 1024.0 / 1024.0:0.##} MiB");
        report.AppendLine($"- 私有内存：{process.PrivateMemorySize64 / 1024.0 / 1024.0:0.##} MiB");
        report.AppendLine($"- 状态序列（温度/频率/独显温度/整机功耗）：{string.Join(" ", statuses.Distinct())}");
    }

    private static void DumpSnapshot(MonitoringSnapshot snapshot, StringBuilder report)
    {
        report.AppendLine($"- 采样时刻：{snapshot.SampledAt:HH:mm:ss.fff zzz}");
        report.AppendLine($"- 频率口径：{snapshot.Cpu.FrequencyBasis}");
        Dump("CPU 温度", snapshot.Cpu.Temperature, report);
        Dump("CPU 频率(MHz)", snapshot.Cpu.FrequencyMhz, report);
        Dump("CPU 封装功耗(W)", snapshot.Cpu.PackagePowerWatt, report);
        Dump("CPU 风扇 RPM", snapshot.Cpu.Fan.Rpm, report);
        Dump("GPU 温度", snapshot.Gpu.Temperature, report);
        Dump("GPU 核心频率(MHz)", snapshot.Gpu.CoreFrequencyMhz, report);
        Dump("GPU 功耗(W)", snapshot.Gpu.PowerWatt, report);
        Dump("GPU 风扇 RPM", snapshot.Gpu.Fan.Rpm, report);
        Dump("风扇(CPU 通道)", snapshot.Fans.Cpu.Rpm, report);
        Dump("风扇(GPU 通道)", snapshot.Fans.Gpu.Rpm, report);
        report.AppendLine($"- 整机功耗：口径={snapshot.SystemPower.Kind}，可用={snapshot.SystemPower.IsAvailable}");
        Dump("整机功耗", snapshot.SystemPower.Reading, report);

        foreach (var sensor in snapshot.Extended)
        {
            Dump($"扩展·{sensor.DisplayName}（{sensor.Key}）", sensor.Reading, report);
        }
    }

    private static void Dump(string label, SensorReading reading, StringBuilder report)
    {
        var value = reading.Value.HasValue
            ? reading.Value.Value.ToString("0.###", CultureInfo.InvariantCulture)
            : "-";

        report.AppendLine(
            $"- {label}：{reading.Status}，值={value} {reading.Unit}，来源={reading.Source}" +
            $"{(reading.Detail is null ? string.Empty : $"，说明={reading.Detail}")}");
    }
}
