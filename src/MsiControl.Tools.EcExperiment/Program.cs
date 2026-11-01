using System.Globalization;
using MsiControl.Hardware.Msi.Transport.PawnIo;

namespace MsiControl.Tools.EcExperiment;

/// <summary>
/// EC 受控对照实验工具。
/// </summary>
/// <remarks>
/// <para>
/// <b>安全边界：</b>写白名单仅含寄存器表候选（0xD4 风扇模式、0x98 Cooler Boost、0xD2 性能场景）
/// 与两个风扇占空比寄存器（0x71/0x89，阶段 B 定位假设的直接写入测试）。
/// 每次写入后立即读回验证；<c>fanmode</c>/<c>cooler</c>/<c>shiftmode</c> 命令附带恢复入口。
/// </para>
/// <para><b>需要管理员权限</b>；实验期间不得运行其他可能访问 EC 的软件（MSI Center、Afterburner 等）。</para>
/// </remarks>
internal static class Program
{
    /// <summary>实验用写白名单（寄存器表候选 + 阶段 B 直接占空比测试地址）。</summary>
    private static readonly HashSet<byte> WritableAddresses = new()
    {
        Ms15M3EcRegisterMap.FanMode.Address,        // 0xD4
        Ms15M3EcRegisterMap.CoolerBoost.Address,    // 0x98
        Ms15M3EcRegisterMap.ShiftMode.Address,      // 0xD2
        Ms15M3EcRegisterMap.CpuFanPercent.Address,  // 0x71（阶段 B 假设）
        Ms15M3EcRegisterMap.GpuFanPercent.Address,  // 0x89（阶段 B 假设）

        // 显卡 MUX 与切换触发（HWE §21/§27）：应用内 graphics.mode 已是 Verified 写通道，
        // 这里放行是为了在**同一进程外**复现「写 0x2E 后被固件回滚」的时序，
        // 逐帧观察三载体的先后变化。写入序列与 Ms15M3GraphicsModeWriter 完全一致。
        Ms15M3EcRegisterMap.GpuMux.Address,         // 0x2E
        Ms15M3EcRegisterMap.GpuSwitchTrigger.Address // 0xD1
    };

    private static PawnIoEcChannel? _channel;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        var creation = PawnIoEcAccess.TryCreate(WritableAddresses);
        if (!creation.Succeeded)
        {
            Console.WriteLine($"[错误] EC 通道创建失败：{creation.Error}");
            return 1;
        }

        using var channel = creation.Channel!;
        _channel = channel;

        try
        {
            return args[0] switch
            {
                "status" => CmdStatus(channel),
                "read" => CmdRead(args),
                "baseline" => CmdBaseline(args),
                "diff" => CmdDiff(args),
                "fanmode" => CmdFanMode(args),
                "shiftmode" => CmdShiftMode(args),
                "cooler" => CmdCooler(args),
                "write" => CmdWrite(args),
                "watch" => CmdWatch(args),
                "fanzero" => CmdFanZero(channel, args),
                "gpuswitch" => CmdGpuSwitch(args),
                _ => Unknown(args[0])
            };
        }
        catch (EcTimeoutException ex)
        {
            Console.WriteLine($"[错误] EC 时序超时：{ex.Message}");
            return 1;
        }
        catch (EcWriteBlockedException ex)
        {
            Console.WriteLine($"[错误] 写入被白名单拒绝：{ex.Message}");
            return 1;
        }
        catch (PawnIoException ex)
        {
            Console.WriteLine($"[错误] 驱动调用失败：{ex.Message}");
            return 1;
        }
        finally
        {
            _channel = null;
        }
    }

    private static int CmdStatus(PawnIoEcChannel channel)
    {
        Console.WriteLine($"仲裁说明：{channel.Port.ArbitrationNotice ?? "（无）"}");
        Console.WriteLine($"写白名单：{string.Join(' ', WritableAddresses.Select(a => $"0x{a:X2}").OrderBy(a => a, StringComparer.Ordinal))}");
        foreach (var register in Ms15M3EcRegisterMap.All)
        {
            var value = channel.Port.ReadRegister(register.Address);
            Console.WriteLine(
                $"0x{register.Address:X2} = 0x{value:X2} ({value,3})  {register.Description}（{register.Unit}）");
        }

        return 0;
    }

    private static int CmdRead(string[] args)
    {
        if (args.Length < 2 || !TryParseHex(args[1], out var address))
        {
            Console.WriteLine("用法：read <十六进制地址>");
            return 2;
        }

        var value = _channel!.Port.ReadRegister(address);
        Console.WriteLine($"0x{address:X2} = 0x{value:X2} ({value})");
        return 0;
    }

    private static int CmdBaseline(string[] args)
    {
        var path = args.Length > 1 ? args[1] : "ec-baseline.txt";
        var dump = DumpAll();
        File.WriteAllLines(path, dump.Select(entry => $"0x{entry.Address:X2}=0x{entry.Value:X2}"));
        Console.WriteLine($"已写入 {dump.Count} 项基线到 {Path.GetFullPath(path)}");
        return 0;
    }

    private static int CmdDiff(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：diff <基线文件>");
            return 2;
        }

        var baseline = File.ReadAllLines(args[1])
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2
                && parts[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && byte.TryParse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)
                && byte.TryParse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            .ToDictionary(
                parts => byte.Parse(parts[0].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                parts => byte.Parse(parts[1].AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        var current = DumpAll();
        var changed = 0;

        foreach (var entry in current)
        {
            if (baseline.TryGetValue(entry.Address, out var before) && before != entry.Value)
            {
                var descriptor = Ms15M3EcRegisterMap.Find(entry.Address);
                Console.WriteLine(
                    $"0x{entry.Address:X2}: 0x{before:X2} → 0x{entry.Value:X2}" +
                    (descriptor is null ? string.Empty : $"  ({descriptor.Description})"));
                changed++;
            }
        }

        Console.WriteLine(changed == 0 ? "与基线无差异。" : $"共 {changed} 个寄存器发生变化。");
        return 0;
    }

    private static int CmdFanMode(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：fanmode <auto|silent|advanced>");
            return 2;
        }

        var target = args[1].ToLowerInvariant();
        if (!Ms15M3EcRegisterMap.FanModes.TryGetValue(
                target switch
                {
                    "auto" => 0x0D,
                    "silent" => 0x1D,
                    "advanced" => 0x8D,
                    _ => 0x00
                },
                out var _))
        {
            Console.WriteLine("未知模式；可选 auto / silent / advanced。");
            return 2;
        }

        var value = target switch { "auto" => (byte)0x0D, "silent" => (byte)0x1D, _ => (byte)0x8D };
        var register = Ms15M3EcRegisterMap.FanMode;

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 写 {register.Description} 0x{register.Address:X2} = 0x{value:X2}（{target}）");
        _channel!.Port.WriteRegister(register.Address, value);

        var readBack = _channel.Port.ReadRegister(register.Address);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 读回 0x{register.Address:X2} = 0x{readBack:X2}（{(readBack == value ? "一致" : "不一致！")}）");
        return readBack == value ? 0 : 1;
    }

    private static int CmdShiftMode(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：shiftmode <eco|comfort|turbo>");
            return 2;
        }

        var value = args[1].ToLowerInvariant() switch
        {
            "eco" => (byte)0xC2,
            "comfort" => (byte)0xC1,
            "turbo" => (byte)0xC4,
            _ => byte.MinValue
        };

        if (value == 0)
        {
            Console.WriteLine("未知场景；可选 eco / comfort / turbo。");
            return 2;
        }

        var register = Ms15M3EcRegisterMap.ShiftMode;
        var before = _channel!.Port.ReadRegister(register.Address);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 原 0x{register.Address:X2} = 0x{before:X2}；写 0x{value:X2}（{args[1]}）");
        _channel.Port.WriteRegister(register.Address, value);

        var readBack = _channel.Port.ReadRegister(register.Address);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 读回 0x{register.Address:X2} = 0x{readBack:X2}（{(readBack == value ? "一致" : "不一致！")}）");
        return readBack == value ? 0 : 1;
    }

    private static int CmdCooler(string[] args)
    {
        if (args.Length < 2 || args[1] is not ("on" or "off"))
        {
            Console.WriteLine("用法：cooler <on|off>");
            return 2;
        }

        var register = Ms15M3EcRegisterMap.CoolerBoost;
        var mask = Ms15M3EcRegisterMap.CoolerBoostBitMask;
        var before = _channel!.Port.ReadRegister(register.Address);
        var target = args[1] == "on"
            ? (byte)(before | mask)
            : (byte)(before & ~mask);

        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 原 0x{register.Address:X2} = 0x{before:X2}；写 0x{target:X2}（Cooler Boost {args[1]}）");
        _channel.Port.WriteRegister(register.Address, target);

        var readBack = _channel.Port.ReadRegister(register.Address);
        var bit = (readBack & mask) != 0;
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 读回 0x{register.Address:X2} = 0x{readBack:X2}，bit7={(bit ? 1 : 0)}");
        return 0;
    }

    private static int CmdWrite(string[] args)
    {
        if (args.Length < 3
            || !TryParseHex(args[1], out var address)
            || !TryParseHex(args[2], out var value))
        {
            Console.WriteLine("用法：write <十六进制地址> <十六进制值>");
            return 2;
        }

        if (!WritableAddresses.Contains(address))
        {
            Console.WriteLine($"[拒绝] 0x{address:X2} 不在实验写白名单内（{string.Join(' ', WritableAddresses.Select(a => $"0x{a:X2}").OrderBy(a => a, StringComparer.Ordinal))}）。");
            return 1;
        }

        var descriptor = Ms15M3EcRegisterMap.Find(address);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 写 0x{address:X2} = 0x{value:X2}{(descriptor is null ? string.Empty : $"（{descriptor.Description}）")}");
        _channel!.Port.WriteRegister(address, value);

        var readBack = _channel.Port.ReadRegister(address);
        Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 读回 0x{address:X2} = 0x{readBack:X2}（{(readBack == value ? "一致" : "不一致！")}）");
        return readBack == value ? 0 : 1;
    }

    /// <summary>
    /// 按应用的写入时序复现一次显卡模式切换，多次运行时逐次记录「写后读回」结果。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 序列与 <c>Ms15M3GraphicsModeWriter</c> 完全一致（HWE §30，M-Control 逆向）：
    /// UEFI <c>MsiDCVarData</c> byte[5] 低两位 → EC 0xD1 |= 0x06（bit1 请求 + bit2 模式位）
    /// → EC 0x2E 读改写 bit6 → EC 0xD1 |= 0x01 触发。
    /// </para>
    /// <para>
    /// <c>delayMs</c> 是「触发 0xD1 之后、写 0x2E 之前」的等待：应用当前为 0（紧邻写入）。
    /// 用它检验「固件处理切换请求期间 EC 写入被丢弃」这一假设——若失败率随延迟下降，
    /// 假设成立，修复方向就是写入后读回校验 + 有限重试（而不是改协议顺序）。
    /// </para>
    /// </remarks>
    private static int CmdGpuSwitch(string[] args)
    {
        if (args.Length < 2 || !byte.TryParse(args[1], out var requested) || requested > 1)
        {
            Console.WriteLine("用法：gpuswitch <0|1> [delayMs] [repeat]（0=混合 1=独显直连）");
            return 2;
        }

        var delay = args.Length > 2 && int.TryParse(args[2], out var parsedDelay)
            ? Math.Clamp(parsedDelay, 0, 2000)
            : 0;
        var repeat = args.Length > 3 && int.TryParse(args[3], out var parsedRepeat)
            ? Math.Clamp(parsedRepeat, 1, 20)
            : 1;

        if (!UefiVariableProbe.TryEnablePrivilege(out var privilegeError))
        {
            Console.WriteLine($"[错误] 无法启用 UEFI 变量特权：{privilegeError}");
            return 1;
        }

        var baseline = UefiVariableProbe.Read(out var readError);
        if (baseline is null)
        {
            Console.WriteLine($"[错误] {readError}");
            return 1;
        }

        var baseUefiMode = baseline[UefiVariableProbe.ModeByteOffset];
        var baseMux = _channel!.Port.ReadRegister(Ms15M3EcRegisterMap.GpuMux.Address);
        var baseTrigger = _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address);

        Console.WriteLine("基线：" + Describe(baseline, baseMux, baseTrigger));
        Console.WriteLine($"变量字节：{string.Join(' ', baseline.Select(b => $"{b:X2}"))}");
        Console.WriteLine($"时序：UEFI → 0xD1 触发 → 等待 {delay} ms → 写 0x2E；重复 {repeat} 次。");

        var failures = 0;
        var target = requested;

        try
        {
            for (var round = 1; round <= repeat; round++)
            {
                var muxBefore = _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuMux.Address);
                var triggerBefore = _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address);

                if (!UefiVariableProbe.WriteMode(target, out var writeError))
                {
                    Console.WriteLine($"[{round}] UEFI 写入失败：{writeError}");
                    return 1;
                }

                var requestValue = (byte)(triggerBefore
                    | Ms15M3EcRegisterMap.GpuSwitchModeBit
                    | Ms15M3EcRegisterMap.GpuSwitchAckBit);
                _channel.Port.WriteRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address, requestValue);

                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }

                var targetMux = target == 1
                    ? (byte)(muxBefore | Ms15M3EcRegisterMap.GpuMuxBitMask)
                    : (byte)(muxBefore & ~Ms15M3EcRegisterMap.GpuMuxBitMask);

                var wrote = targetMux != muxBefore;
                if (wrote)
                {
                    _channel.Port.WriteRegister(Ms15M3EcRegisterMap.GpuMux.Address, targetMux);
                }

                // 触发位最后置：固件消费后会自行清 bit0（实测读回 0x00/0x02）。
                var fireValue = (byte)(requestValue | Ms15M3EcRegisterMap.GpuSwitchTriggerRequest);
                _channel.Port.WriteRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address, fireValue);

                var muxAfter = _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuMux.Address);
                var triggerAfter = _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address);
                var ok = ((muxAfter & Ms15M3EcRegisterMap.GpuMuxBitMask) != 0) == (target == 1);

                if (!ok)
                {
                    failures++;
                }

                Console.WriteLine(
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] 第 {round} 轮 → {(target == 1 ? "独显直连" : "混合")}："
                    + $"0x2E 0x{muxBefore:X2} → 写 0x{targetMux:X2}{(wrote ? string.Empty : "（已就位）")} → 读回 0x{muxAfter:X2}"
                    + $"，0xD1 0x{triggerAfter:X2} → {(ok ? "一致" : "不一致！")}");

                // 交替方向：下一次切回另一态，保证每轮都是真实切换而不是空操作。
                target = target == 1 ? (byte)0 : (byte)1;
            }
        }
        finally
        {
            UefiVariableProbe.WriteMode(baseUefiMode, out _);
            _channel.Port.WriteRegister(
                Ms15M3EcRegisterMap.GpuSwitchTrigger.Address,
                baseTrigger);
            _channel.Port.WriteRegister(Ms15M3EcRegisterMap.GpuMux.Address, baseMux);
            var restored = _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuMux.Address);
            var restoredUefi = UefiVariableProbe.Read(out _);
            Console.WriteLine("恢复基线：" + (restoredUefi is null
                ? $"UEFI 读失败 / 0x2E=0x{restored:X2}"
                : Describe(restoredUefi, restored, _channel.Port.ReadRegister(Ms15M3EcRegisterMap.GpuSwitchTrigger.Address))));
        }

        Console.WriteLine(failures == 0
            ? $"全部 {repeat} 轮写入读回一致。"
            : $"失败 {failures} / {repeat} 轮（延迟 {delay} ms）。");
        return failures == 0 ? 0 : 1;
    }

    private static string Describe(byte[] uefi, byte mux, byte trigger)
        => $"UEFI byte[5]=0x{uefi[UefiVariableProbe.ModeByteOffset]:X2} / "
           + $"EC 0x2E=0x{mux:X2}（bit6={((mux & Ms15M3EcRegisterMap.GpuMuxBitMask) != 0 ? 1 : 0)}） / "
           + $"EC 0xD1=0x{trigger:X2}";

    private static int CmdWatch(string[] args)
    {
        if (args.Length < 2 || !TryParseHex(args[1], out var address))
        {
            Console.WriteLine("用法：watch <十六进制地址> [间隔ms]（Ctrl+C 退出）");
            return 2;
        }

        var interval = args.Length > 2 && int.TryParse(args[2], out var parsed)
            ? Math.Clamp(parsed, 100, 5000)
            : 500;

        var descriptor = Ms15M3EcRegisterMap.Find(address);
        Console.WriteLine($"watch 0x{address:X2} 每 {interval} ms（{descriptor?.Description ?? "未登记寄存器"}），Ctrl+C 退出。");

        while (true)
        {
            var value = _channel!.Port.ReadRegister(address);
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] 0x{address:X2} = {value,3} (0x{value:X2})");
            Thread.Sleep(interval);
        }
    }

    private static List<(byte Address, byte Value)> DumpAll()
    {
        var result = new List<(byte, byte)>(256);

        for (var address = 0; address <= 0xFF; address++)
        {
            // 密集事务会超出 EC 的命令速率（实测偶发 OBF 等待超时）：
            // 逐次加 2 ms 间隔，单次超时退避 50 ms 后重试，最多 3 次。
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    result.Add(((byte)address, _channel!.Port.ReadRegister((byte)address)));
                    break;
                }
                catch (EcTimeoutException) when (attempt < 3)
                {
                    Thread.Sleep(50);
                }
            }

            Thread.Sleep(2);
        }

        return result;
    }

    private static bool TryParseHex(string text, out byte value)
        => byte.TryParse(
            text.Replace("0x", string.Empty).Replace("0X", string.Empty),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out value);

    private static int Unknown(string command)
    {
        Console.WriteLine($"未知命令 {command}。");
        PrintUsage();
        return 2;
    }

    /// <summary>
    /// 风扇 PWM 控制子命令：预检 / 保持性验证 / 停转协议。
    /// </summary>
    /// <remarks>
    /// 三者的共同前提是同一条 PawnIO EC 通道（<c>LpcACPIEC</c> 模块 + 0x62/0x66 端口）。
    /// 所有子命令在退出路径（含异常与看门狗）都会把基线与风扇模式写回，不留「手动模式」残留。
    /// </remarks>
    private static int CmdFanZero(PawnIoEcChannel channel, string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法：fanzero <probe|hold|zero> ...");
            return 2;
        }

        using var controller = new FanZeroController(channel);
        return args[1].ToLowerInvariant() switch
        {
            "probe" => FanZeroProbe(controller),
            "hold" => FanZeroHold(controller, args),
            "zero" => FanZeroZero(controller, args),
            _ => Unknown(args[1])
        };
    }

    private static int FanZeroProbe(FanZeroController controller)
    {
        Console.WriteLine("== PWM 通道预检（只读，除污染探测外不改状态）==");
        Console.WriteLine($"  EC 0x68 CPU 温度      = {controller.Read(FanZeroController.CpuTemperature),3} °C");
        Console.WriteLine($"  EC 0x71 CPU 占空比    = {controller.Read(FanZeroController.CpuDuty),3} %");
        Console.WriteLine($"  EC 0x80 GPU 温度      = {controller.Read(FanZeroController.GpuTemperature),3} °C");
        Console.WriteLine($"  EC 0x89 GPU 占空比    = {controller.Read(FanZeroController.GpuDuty),3} %");
        Console.WriteLine($"  EC 0xD4 风扇模式      = 0x{controller.Read(0xD4):X2}"
            + $"（0x{FanZeroController.AutoMode:X2}=auto / 0x{FanZeroController.ManualMode:X2}=advanced）");

        var pollution = controller.CheckFirmwarePollution();
        Console.WriteLine();
        Console.WriteLine("== 固件回写探测 ==");

        if (pollution.Error is not null)
        {
            Console.WriteLine($"  [无法判定] 探测过程异常：{pollution.Error}");
            Console.WriteLine("  EC 事务偶发超时（实测约 20%），可重试一次；持续失败请检查是否有其他程序占用 EC。");
            return 1;
        }

        Console.WriteLine($"  写 0x71 = {pollution.Probe}% → 立即读回 {pollution.ImmediateReadBack}%"
            + $" → 1.5 秒后读回 {pollution.After1500Ms}%");

        if (pollution.Polluted)
        {
            Console.WriteLine("  [污染] 固件正在把占空比强制回写为 0，且写非 0 无效。");
            Console.WriteLine("         该状态持久化、跨重启保留；需 BIOS 加载默认设置或完全断电放电复位（HWE §36.4）。");
            return 1;
        }

        Console.WriteLine("  [正常] 未检测到固件回写 0 的污染状态。");
        return 0;
    }

    private static int FanZeroHold(FanZeroController controller, string[] args)
    {
        if (args.Length < 4
            || !byte.TryParse(args[2], out var percent)
            || percent > 100
            || !double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            Console.WriteLine("用法：fanzero hold <占空比 0-100> <秒数>");
            return 2;
        }

        var pollution = controller.CheckFirmwarePollution();
        if (pollution.Error is not null)
        {
            Console.WriteLine($"[中止] 无法判定固件状态：{pollution.Error}");
            return 1;
        }

        if (pollution.Polluted)
        {
            Console.WriteLine("[中止] 固件处于回写 0 的污染状态（写 0x71 后 1.5 秒被改回 0）。");
            Console.WriteLine("       该状态持久化且跨重启保留（HWE §36.4），需 BIOS 加载默认设置或完全断电放电复位。");
            return 1;
        }

        if (!controller.EnterManual(out var entered))
        {
            Console.WriteLine("[失败] " + entered);
            return 1;
        }

        Console.WriteLine("  " + entered);

        try
        {
            Console.WriteLine($"  开始以 {FanZeroController.SamplePeriodMs} ms 周期写入 {percent}%，持续 {seconds:0.#} 秒…");
            var report = controller.Hold(percent, seconds);
            Console.WriteLine("  " + FanZeroController.Describe(report));

            if (report.FirstCpuMismatch is not null)
            {
                Console.WriteLine($"  CPU 首个被回写的样本：{report.FirstCpuMismatch}");
            }

            if (report.FirstGpuMismatch is not null)
            {
                Console.WriteLine($"  GPU 首个被回写的样本：{report.FirstGpuMismatch}");
            }

            if (report.Watchdog)
            {
                Console.WriteLine("  [看门狗] " + report.WatchdogReason);
                return 1;
            }

            var verdict = report.CpuHoldRatio >= 0.95 && report.GpuHoldRatio >= 0.95;
            Console.WriteLine(verdict
                ? $"  [结论] 两个通道都以 ≥95% 的保持率维持 {percent}%，该周期下 PWM 可被视为受控。"
                : "  [结论] 至少一个通道未能维持目标值：固件回写快于写入周期，需要缩短周期或接受固件接管。");
            return verdict ? 0 : 1;
        }
        finally
        {
            Console.WriteLine("  回滚：" + controller.Restore());
        }
    }

    private static int FanZeroZero(FanZeroController controller, string[] args)
    {
        var allowZero = args.Any(a => string.Equals(a, "--allow-zero", StringComparison.OrdinalIgnoreCase));
        if (args.Length < 3
            || !double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            Console.WriteLine("用法：fanzero zero <秒数> --allow-zero");
            return 2;
        }

        if (!allowZero)
        {
            Console.WriteLine("[拒绝] 停转协议需要显式开关 --allow-zero。");
            Console.WriteLine("       原因（本机实测，HWE §36）：写 0 之后固件会持续回写 0，");
            Console.WriteLine("       此后写 20/60/100% 都在 ≤2 秒内被覆盖，且重启、切模式、切档位、");
            Console.WriteLine("       强冷窗口压写、30 秒高频压写全部无效——只能靠 BIOS 加载默认或断电放电复位。");
            Console.WriteLine("       另：写 0 并未让风扇物理停转（8 进程满载 30 秒温度不变）。");
            return 3;
        }

        var pollution = controller.CheckFirmwarePollution();
        if (pollution.Error is not null)
        {
            Console.WriteLine($"[中止] 无法判定固件状态：{pollution.Error}");
            return 1;
        }

        if (pollution.Polluted)
        {
            Console.WriteLine("[中止] 固件已处于回写 0 的污染状态，无法区分「本次写入生效」与「固件自身回写」。");
            Console.WriteLine("       该状态持久化且跨重启保留，需 BIOS 加载默认设置或完全断电放电复位。");
            return 1;
        }

        if (!controller.EnterManual(out var entered))
        {
            Console.WriteLine("[失败] " + entered);
            return 1;
        }

        Console.WriteLine("  " + entered);

        try
        {
            Console.WriteLine($"  写入 0% 并观察 {seconds:0.#} 秒（周期 {FanZeroController.SamplePeriodMs} ms）…");
            var report = controller.Hold(0, seconds);
            Console.WriteLine("  " + FanZeroController.Describe(report));

            if (report.FirstCpuMismatch is not null)
            {
                Console.WriteLine($"  CPU 首个被回写的样本：{report.FirstCpuMismatch}");
            }

            if (report.FirstGpuMismatch is not null)
            {
                Console.WriteLine($"  GPU 首个被回写的样本：{report.FirstGpuMismatch}");
            }

            var held = report.CpuHoldRatio >= 0.99 && report.GpuHoldRatio >= 0.99;
            if (!held)
            {
                Console.WriteLine("  [判定] 停转不可实现：固件在观察窗口内改写了 0%，寄存器值不能维持。");
                return 1;
            }

            Console.WriteLine("  [判定] 寄存器层面 0% 已维持，但「物理停转」未被证实。");
            Console.WriteLine("         本机 EC 无转速寄存器，只能靠温升差分验证：");
            Console.WriteLine("         请在另一窗口施加稳定 CPU 负载并对比温升；温度不升说明风扇仍在转。");
            return 0;
        }
        finally
        {
            Console.WriteLine("  回滚：" + controller.Restore());
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("EC 受控对照实验工具（需要管理员；实验期间关闭 MSI Center / Afterburner）");
        Console.WriteLine("  status                       通道状态与寄存器表快照");
        Console.WriteLine("  read <addr>                  读单个寄存器");
        Console.WriteLine("  baseline [file]              全寄存器基线（默认 ec-baseline.txt）");
        Console.WriteLine("  diff <file>                  与基线比对，打印变化寄存器");
        Console.WriteLine("  fanmode <auto|silent|advanced>  写风扇模式并读回");
        Console.WriteLine("  shiftmode <eco|comfort|turbo>   写性能场景并读回");
        Console.WriteLine("  cooler <on|off>              Cooler Boost 位（0x98 bit7）读改写");
        Console.WriteLine("  write <addr> <val>           白名单内寄存器写入并读回");
        Console.WriteLine("  watch <addr> [ms]            周期观察");
        Console.WriteLine("  gpuswitch <0|1> [delayMs] [repeat]  按应用时序切换显卡模式并逐轮读回");
        Console.WriteLine("  fanzero probe                PWM 通道预检（驱动/模块/权限/污染状态，只读）");
        Console.WriteLine("  fanzero hold <pct> <sec>     周期写入目标占空比并统计保持率，退出自动回滚");
        Console.WriteLine("  fanzero zero <sec> --allow-zero  停转协议（危险：实测会让固件锁死 0，需重启恢复）");
    }
}
