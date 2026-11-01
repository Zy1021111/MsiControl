using System.Text;
using System.Text.RegularExpressions;
using MsiControl.Hardware.Msi.Transport;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Tools.EcProbe;

/// <summary>
/// EcProbe：MS-15M3 EC 寄存器只读诊断工具。
/// </summary>
/// <remarks>
/// 命令：status / firmware / dump &lt;地址&gt; / dump-all / watch &lt;地址&gt; [间隔ms]。
/// <b>本工具没有写命令</b>，且 EC 通道构造时写白名单恒为空集——即使未来寄存器定位
/// 完成（方案阶段 B），写放行也只属于主应用的业务层，不回收给诊断工具。
/// 全部读操作不发起任何 EC 写事务（RD_EC 不改变寄存器内容），但未定义寄存器的
/// 读取行为由固件决定，输出仅作观察证据，不得直接当作「已验证语义」。
/// </remarks>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitError = 1;

    /// <summary>EC 固件版本字符串的提取模式（如 15M3EMS1.113）。</summary>
    private static readonly Regex FirmwareVersionPattern =
        new(@"[0-9A-Za-z]{3,8}EMS[0-9]\.[0-9]{2,3}", RegexOptions.Compiled);

    /// <summary>
    /// MSI_ACPI.Get_EC 方法名。与 MsiControl.Monitoring 的 MsiAcpiEcFirmwareSource
    /// 使用同一方法（本工具不引用 Monitoring 工程，方法名按约定登记于
    /// MsiAcpiMethodNames.Read 白名单，属只读通道允许范围）。
    /// </summary>
    private const string GetEcMethodName = "Get_EC";

    private static int Main(string[] args)
    {
        // 控制台输出中文；输出重定向到非常规编码目标时允许失败。
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch (Exception)
        {
            // 忽略：不影响诊断功能。
        }

        if (args.Length == 0)
        {
            PrintUsage();
            return ExitError;
        }

        try
        {
            return args[0] switch
            {
                "status" => CmdStatus(),
                "firmware" => CmdFirmware(),
                "dump" when args.Length >= 2 => CmdDump(args[1]),
                "dump-all" => CmdDumpAll(),
                "watch" when args.Length >= 2 => CmdWatch(args[1], args.Length >= 3 ? args[2] : null),
                _ => UnknownCommand(args),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[错误] {exception.Message}");
            return ExitError;
        }
    }

    // ── 命令实现 ─────────────────────────────────────────────

    private static int CmdStatus()
    {
        Console.WriteLine("== EcProbe 状态 ==");
        Console.WriteLine($"管理员权限     : {(IsAdministrator() ? "是" : "否（PawnIO 设备将拒绝访问）")}");

        if (!PawnIoDriver.IsInstalled)
        {
            Console.WriteLine("PawnIO 驱动    : 未安装（官方安装：winget install namazso.PawnIO）");
            return ExitError;
        }

        Console.WriteLine($"PawnIO 驱动    : 已安装，版本 {PawnIoDriver.InstalledVersion}");

        using var channel = CreateChannelOrReport();
        if (channel is null)
        {
            return ExitError;
        }

        Console.WriteLine("LpcACPIEC 模块 : 已加载（SHA-256 校验通过）");
        Console.WriteLine($"EC 仲裁        : {channel.Port.ArbitrationNotice ?? "系统级互斥体存在，事务前等待获取"}");
        Console.WriteLine(
            $"写白名单       : {(channel.Port.WritableAddresses.Count == 0
                ? "空集（阶段 A：所有写入被协议层拒绝）"
                : string.Join(", ", channel.Port.WritableAddresses.OrderBy(a => a).Select(a => $"0x{a:X2}")))}");
        return ExitOk;
    }

    private static int CmdFirmware()
    {
        Console.WriteLine("== EC 固件版本（WMI MSI_ACPI.Get_EC）==");

        var transport = new WmiMsiAcpiTransport();
        var result = transport.InvokeReadAsync(GetEcMethodName, null, CancellationToken.None)
            .GetAwaiter().GetResult();

        if (result.Data is null)
        {
            Console.WriteLine($"读取失败：{result.Error ?? "未返回数据。"}");
            return ExitError;
        }

        var text = ExtractText(result.Data);
        Console.WriteLine($"原始返回       : {text}");
        Console.WriteLine($"十六进制       : {ExtractHex(result.Data)}");

        var match = FirmwareVersionPattern.Match(text);
        if (!match.Success)
        {
            Console.WriteLine("版本提取       : 未能从返回文本中提取形如 XXXXEMS1.nnn 的版本串。");
            return ExitError;
        }

        var version = match.Value;
        var supported = Ms15M3EcRegisterMap.IsSupportedFirmware(version);
        Console.WriteLine($"提取版本       : {version}");
        Console.WriteLine(
            supported
                ? $"寄存器表匹配   : 支持（{Ms15M3EcRegisterMap.FirmwareVersionPrefix}"
                  + $"{Ms15M3EcRegisterMap.FirmwareMinorMin}–{Ms15M3EcRegisterMap.FirmwareMinorMax}，msi-ec CONF_G2_10）"
                : "寄存器表匹配   : 不支持！固件超出白名单，EC 通道必须停用（禁止套用旧寄存器表）。");
        return supported ? ExitOk : ExitError;
    }

    private static int CmdDump(string addressText)
    {
        if (!TryParseAddress(addressText, out var address))
        {
            Console.Error.WriteLine($"[错误] 无法解析寄存器地址：{addressText}（示例：0xD4 或 d4 或 212）");
            return ExitError;
        }

        using var channel = CreateChannelOrReport();
        if (channel is null)
        {
            return ExitError;
        }

        try
        {
            var value = channel.Port.ReadRegister(address);
            PrintRegister(address, value);
            return ExitOk;
        }
        catch (Exception exception) when (exception is EcPortException or PawnIoException)
        {
            Console.Error.WriteLine($"[错误] 读寄存器 0x{address:X2} 失败：{exception.Message}");
            return ExitError;
        }
    }

    private static int CmdDumpAll()
    {
        Console.WriteLine("== 全寄存器只读 dump（0x00–0xFF）==");
        Console.WriteLine("说明：RD_EC 不改变寄存器内容；未定义寄存器的读取行为由固件决定，");
        Console.WriteLine("      输出仅作对照实验证据（方案阶段 B），不得当作已验证语义。");
        Console.WriteLine();

        using var channel = CreateChannelOrReport();
        if (channel is null)
        {
            return ExitError;
        }

        var values = new byte[256];
        var failures = new List<byte>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        for (var address = 0; address <= 0xFF; address++)
        {
            try
            {
                values[address] = channel.Port.ReadRegister((byte)address);
            }
            catch (Exception exception) when (exception is EcPortException or PawnIoException)
            {
                failures.Add((byte)address);
            }

            if (address % 0x10 == 0x0F)
            {
                PrintRow((byte)(address & 0xF0), values, failures);
            }
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"耗时 {stopwatch.ElapsedMilliseconds} ms，"
            + (failures.Count == 0
                ? "全部寄存器读取成功。"
                : $"失败 {failures.Count} 个：{string.Join(" ", failures.Select(a => $"0x{a:X2}"))}"));

        PrintKnownRegisters(values);
        return failures.Count == 0 ? ExitOk : ExitError;
    }

    private static int CmdWatch(string addressText, string? intervalText)
    {
        if (!TryParseAddress(addressText, out var address))
        {
            Console.Error.WriteLine($"[错误] 无法解析寄存器地址：{addressText}（示例：0x71）");
            return ExitError;
        }

        var intervalMs = 500;
        if (intervalText is not null &&
            (!int.TryParse(intervalText, out intervalMs) || intervalMs is < 50 or > 60_000))
        {
            Console.Error.WriteLine("[错误] 间隔须为 50–60000 毫秒。");
            return ExitError;
        }

        using var channel = CreateChannelOrReport();
        if (channel is null)
        {
            return ExitError;
        }

        var register = Ms15M3EcRegisterMap.Find(address);
        Console.WriteLine($"== watch 0x{address:X2}"
            + (register is null ? string.Empty : $"（{register.Description}，{register.Unit}）")
            + $"，间隔 {intervalMs} ms，Ctrl+C 退出 ==");
        Console.WriteLine($"时间                  值     语义");
        Console.WriteLine(new string('─', 60));

        var stop = false;
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop = true;
        };

        byte? previous = null;
        while (!stop)
        {
            byte value;
            try
            {
                value = channel.Port.ReadRegister(address);
            }
            catch (Exception exception) when (exception is EcPortException or PawnIoException)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  错误  {exception.Message}");
                Thread.Sleep(intervalMs);
                continue;
            }

            var delta = previous is null
                ? "  "
                : value == previous.Value ? " =" : value > previous.Value ? " ↑" : " ↓";
            Console.WriteLine(
                $"{DateTime.Now:HH:mm:ss.fff}  0x{value:X2}  {DescribeValue(address, value)}{delta}");
            previous = value;

            Thread.Sleep(intervalMs);
        }

        Console.WriteLine("已停止。");
        return ExitOk;
    }

    // ── 公共辅助 ─────────────────────────────────────────────

    private static PawnIoEcChannel? CreateChannelOrReport()
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("[错误] 当前进程没有管理员权限，PawnIO 设备将拒绝访问。请以管理员身份运行。");
            return null;
        }

        // 阶段 A 语义：写白名单恒为空集，任何写入都在协议层被拒绝。
        var creation = PawnIoEcAccess.TryCreate();
        if (!creation.Succeeded)
        {
            Console.Error.WriteLine($"[错误] {creation.Error}");
            return null;
        }

        return creation.Channel;
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool TryParseAddress(string text, out byte address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            var normalized = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? text[2..]
                : text;
            var value = Convert.ToByte(normalized, 16);
            address = value;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            _ = exception;
            return false;
        }
    }

    private static void PrintRegister(byte address, byte value)
    {
        var register = Ms15M3EcRegisterMap.Find(address);
        Console.WriteLine($"0x{address:X2} = 0x{value:X2} ({value,3})"
            + (register is null
                ? "（未登记寄存器）"
                : $"  {register.Description}（{register.Unit}）"));
        if (register is not null)
        {
            Console.WriteLine($"        语义：{DescribeValue(address, value)}");
        }
    }

    private static string DescribeValue(byte address, byte value)
    {
        return address switch
        {
            _ when address == Ms15M3EcRegisterMap.FanMode.Address
                => Ms15M3EcRegisterMap.FanModes.TryGetValue(value, out var fanMode)
                    ? $"风扇模式 {fanMode}"
                    : "风扇模式（未知枚举值，如实记录）",
            _ when address == Ms15M3EcRegisterMap.ShiftMode.Address
                => Ms15M3EcRegisterMap.ShiftModes.TryGetValue(value, out var shiftMode)
                    ? $"性能场景 {shiftMode}"
                    : "性能场景（未知枚举值，如实记录）",
            _ when address == Ms15M3EcRegisterMap.CoolerBoost.Address
                => $"Cooler Boost bit7 = {(value & Ms15M3EcRegisterMap.CoolerBoostBitMask) != 0}",
            _ when address == Ms15M3EcRegisterMap.CpuFanPercent.Address
                || address == Ms15M3EcRegisterMap.GpuFanPercent.Address
                => $"风扇转速 {value}%",
            _ when address == Ms15M3EcRegisterMap.CpuTemperature.Address
                || address == Ms15M3EcRegisterMap.GpuTemperature.Address
                => $"温度 {value}°C",
            _ when address == Ms15M3EcRegisterMap.SuperBattery.Address
                => $"掩码位低 4 位 = 0x{value & 0x0F:X1}",
            _ => string.Empty
        };
    }

    private static void PrintRow(byte rowStart, byte[] values, List<byte> failures)
    {
        var cells = string.Join(' ', Enumerable.Range(0, 0x10)
            .Select(offset => failures.Contains((byte)(rowStart + offset))
                ? "ERR"
                : $"{values[rowStart + offset]:X2}"));
        Console.WriteLine($"0x{rowStart:X2}  {cells}");
    }

    private static void PrintKnownRegisters(byte[] values)
    {
        Console.WriteLine();
        Console.WriteLine("已登记寄存器（msi-ec CONF_G2_10，社区证据，待本机对照确认）：");
        foreach (var register in Ms15M3EcRegisterMap.All)
        {
            var value = values[register.Address];
            var semantics = DescribeValue(register.Address, value);
            Console.WriteLine(
                $"  0x{register.Address:X2}  {register.Key,-18} = 0x{value:X2} ({value,3})"
                + (string.IsNullOrEmpty(semantics) ? string.Empty : $"  {semantics}"));
        }
    }

    private static string ExtractText(object? data)
    {
        if (TryExtractBytes(data, out var bytes))
        {
            var characters = bytes
                .Where(b => b is >= 0x20 and < 0x7F)
                .Select(b => (char)b)
                .ToArray();
            return new string(characters);
        }

        return data?.ToString() ?? string.Empty;
    }

    private static string ExtractHex(object? data)
    {
        return TryExtractBytes(data, out var bytes)
            ? Convert.ToHexString(bytes)
            : $"（无法取出字节：{data?.GetType().Name ?? "null"}）";
    }

    /// <summary>
    /// 从 WMI 返回对象中取出字节（协议事实：Get_EC 的 Data 与 Get_Fan 同形，
    /// 为内嵌 CIM 实例 Package 的 Bytes 属性；证据清单 §2.4、§13）。
    /// </summary>
    private static bool TryExtractBytes(object? data, out byte[] bytes)
    {
        switch (data)
        {
            case byte[] raw:
                bytes = raw;
                return true;

            case System.Management.ManagementBaseObject management:
                if (management.Properties["Bytes"]?.Value is byte[] inner)
                {
                    bytes = inner;
                    return true;
                }

                break;
        }

        bytes = Array.Empty<byte>();
        return false;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("EcProbe —— MS-15M3 EC 寄存器只读诊断工具（需管理员）");
        Console.WriteLine();
        Console.WriteLine("命令：");
        Console.WriteLine("  status                 驱动 / 模块 / 仲裁 / 写白名单状态");
        Console.WriteLine("  firmware               经 WMI Get_EC 读 EC 固件版本并匹配寄存器表");
        Console.WriteLine("  dump <地址>            读单个寄存器（如 dump 0xD4）");
        Console.WriteLine("  dump-all               全寄存器只读 dump（0x00–0xFF，对照实验基线）");
        Console.WriteLine("  watch <地址> [间隔ms]  周期观察某寄存器（默认 500 ms）");
        Console.WriteLine();
        Console.WriteLine("本工具没有写命令；EC 通道写白名单恒为空集（方案 §4.4 护栏 1）。");
        return ExitError;
    }

    private static int UnknownCommand(string[] args)
    {
        Console.Error.WriteLine($"[错误] 未知命令：{args[0]}");
        PrintUsage();
        return ExitError;
    }
}
