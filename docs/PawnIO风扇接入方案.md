# PawnIO 风扇接入方案（调研 + 实现 + 测试）

版本：v1.0（提案，待叶哥确认后进入实施）
日期：2026-10-17
关联：`MSI-Control-设计定稿.md`、`docs/风扇控制说明.md`、`docs/风扇恢复能力实测报告.md`、`docs/hardware-evidence.md`
目标：基于开源项目 **PawnIO**（https://github.com/namazso/PawnIO ）打通泰坦16 AI 2025
（MS-15M3，EC 固件 15M3EMS1.113）的风扇转速读取与 PWM/转速调节。

---

## 0. 一句话结论

**路线可行，且有强社区证据背书**：msi-ec 内核驱动（BeardOverflow/msi-ec）的 `CONF_G2_10`
配置白名单**精确包含 `15M3EMS1.105 ~ .113`**（.113 正是本机当前固件），其 EC 寄存器布局
已由社区定论——风扇模式 `0xD4`、 Cooler Boost `0x98 bit7`、CPU/GPU 实时转速（百分比）
`0x71/0x89`、CPU/GPU 温度 `0x68/0x80`。本方案用 PawnIO 的 **LpcACPIEC 模块**
（仅放行 EC 端口 0x62/0x66）从用户态安全地读写这些寄存器，替换当前"通道可达但语义未确证"
的 `Get_Fan` 黑盒路径。

按项目铁律，**写入不能凭社区证据直接开放**：本方案分四个阶段（P0 预装 → A 只读实证 →
B 写入定位 → C 开放调节），每一步用本机受控实验把证据等级从 Detected 推到 Verified，
与现有能力门控制度完全兼容。

---

## 1. PawnIO 调研结果

### 1.1 PawnIO 是什么

- **可编程的通用 Windows 内核驱动**（作者 namazso），驱动内嵌 Pawn 脚本虚拟机
  （PawnPP 解释器），加载编译后的 Pawn 模块（`.bin`）在内核态沙箱执行。
- 定位是 **WinRing0 / InpOut / Rusty 等危险 ring0 驱动的安全替代品**：
  - 驱动本体经微软签名，仅导入 ntoskrnl.exe / HAL.dll / ksecdd.sys；
  - 模块能力被**白名单约束**（如 LpcACPIEC 只能访问 0x62/0x66 两个端口），
    不像 WinRing0 那样无限制开放全部端口/内存/MSR；
  - FanControl（V238+）、LibreHardwareMonitor 已全线采用，生态成熟。
- 版本：官方安装包 PawnIO_setup.exe（最新 2.2.0，winget id `namazso.PawnIO`）。
  - **官方版**：驱动签名，且**校验所加载模块的签名**（驱动源码含 `signature.cpp`）；
  - **Unrestricted 版**：不校验模块签名（不建议本项目使用）。

### 1.2 用户态调用方式（无需官方 NuGet 包）

PawnIO **没有官方 C# 绑定**；调用方式是对设备 `\\?\GLOBALROOT\Device\PawnIO` 做
`DeviceIoControl`。LibreHardwareMonitor 的 `PawnIo.cs` 给出了参考实现（**协议常量是事实，
代码须自行重写**，LHM 为 MPL-2.0，不可复制进本项目）：

| 项目 | 值 |
|---|---|
| 打开设备 | `CreateFile("\\\\?\\GLOBALROOT\\Device\\PawnIO", ReadWrite, SHARE_READ|WRITE, OPEN_EXISTING)` |
| 加载模块 | IOCTL = `(41394u << 16) \| (0x821u << 2)`，输入为模块 `.bin` 全部字节 |
| 执行方法 | IOCTL = `(41394u << 16) \| (0x841u << 2)`，输入 = `32 字节 ASCII 方法名 + long[] 输入 cell`，输出 = `long[] cell`（每 cell 8 字节，`bytesRead/8` 为有效 cell 数） |
| 版本探测 | 注册表 `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO` 的 `DisplayVersion` |

方法执行失败时 `DeviceIoControl` 返回 false，错误码经 `Marshal.GetLastWin32Error()` 取得；
**不得**像 LHM 的 `Execute` 那样静默返回全 0 数组（本项目要求显式错误）。

### 1.3 官方模块体系（PawnIO.Modules 仓库）

- 模块源码为 `.p`（Pawn 源），LGPL-2.1，官方 CI 编译；**官方版驱动只接受签名模块**。
- 预编译且可用的签名 `.bin` 可从 LibreHardwareMonitor 仓库获取
  （`LibreHardwareMonitorLib/Resources/PawnIo/`，由 namazso 维护提交，FanControl
  生态在官方版驱动上实际使用，如 IntelMSR.bin / LpcIO.bin / **LpcACPIEC.bin**）。
- 与本任务相关的三个模块：

| 模块 | 功能 | 适用性 |
|---|---|---|
| `LpcACPIEC` | **ACPI EC 端口读写**：`ioctl_pio_read`（in=[端口]，out=[值]）、`ioctl_pio_write`（in=[端口,值]），**仅放行 0x62 / 0x66** | ✅ **本方案采用** |
| `LpcIO` | Super I/O 发现与端口 IO（`ioctl_find_bars`/`ioctl_pio_inb`/`ioctl_pio_outb`，封禁 PCI 配置端口） | ❌ 笔记本风扇由 EC 管理，不挂在 Super I/O 风扇通道上，不适用 |
| `LpcCrOSEC` | ChromiumOS EC（memmap 只读） | ❌ 不适用 |

### 1.4 LpcACPIEC 模块关键事实（源码摘录）

```pawn
is_port_allowed(port) { return port == 0x62 || port == 0x66; }

DEFINE_IOCTL_SIZED(ioctl_pio_read, 1, 1)   // in: [port]        out: [value]
DEFINE_IOCTL_SIZED(ioctl_pio_write, 2, 0)  // in: [port, value] out: 无
// 两者均带注释级警告：
// @warning You should acquire the "\BaseNamedObjects\Access_EC" mutant before calling this
```

要点：
1. 端口白名单写死在内核态模块内——即使用户态代码失控，也只能碰到 EC 的两个端口，
   无法打 PCI 配置空间或其它 IO，这正是选它而非通用端口 IO 模块的原因。
2. `DEFINE_IOCTL_SIZED` 使驱动在校验阶段强制 in/out cell 数**精确匹配**（读=1进1出，
   写=2进0出），传错直接失败，天然防呆。
3. **调用前必须持有 `\BaseNamedObjects\Access_EC` 互斥体**（.NET 侧为
   `Mutex.OpenExisting("Global\\Access_EC")`），与 ACPI/电池驱动等仲裁 EC 访问；
   打不开就报错停手，不得无仲裁直写。

### 1.5 许可证合规

| 组件 | 许可证 | 对本项目的影响 |
|---|---|---|
| PawnIO 驱动 | GPL-2.0 + 特殊例外 | 本项目仅通过设备 IOCTL 与驱动通信，属例外条款认可的"独立模块"，无需 GPL 化 |
| LpcACPIEC 等模块 | LGPL-2.1 | 以 `.bin` 形式加载、保留来源与许可声明即可；修改模块才需开源修改部分 |
| LibreHardwareMonitor 代码 | MPL-2.0 | **只参考协议事实，不复制代码**；IOCTL 常量与 EC 时序属协议/事实，不受版权约束 |

`.bin` 供给侧：从 LHM 仓库 raw URL 下载 `LpcACPIEC.bin`，**把 SHA-256 记入
`docs/hardware-evidence.md` 并在构建/测试中做哈希校验**，杜绝供应链篡改。

---

## 2. 当前风扇调节为什么不可用（现状复盘）

任务 04 的交付状态（`docs/风扇控制说明.md`）：

1. **读取**：`MSI_ACPI.Get_Fan`（WMI）通道可达——输入 16 字节、返回 32 字节缓冲区，
   但**字节语义无实机证据**，`FanAcpiPayload.TryDecodeFanReport` 恒拒绝解码；
   监控层转速源为显式 `UnavailableFanSpeedSource`。
2. **写入**：三道闸门全关——
   - 能力闸门：`fan.curve = Detected`（`AllowsWrite == false`）；
   - 载荷闸门：`FanAcpiPayload.CanComposeWriteRequest => false`；
   - 通道闸门：`WmiMsiAcpiTransport` 白名单只放行 21 个 `Get_*`，`Set_Fan` 等全 Blocked。
3. **根因**：WMI 这条路只给一个不透明的 32 字节缓冲区，字段语义需要大量受控对照实验
   才能确证，证据获取成本极高——这是它一直停在 Detected 的结构性原因。

**PawnIO/EC 路线改变了这个结构**：EC 寄存器是单字节、语义可逐个对照验证的
（0x71 就是 CPU 风扇百分比），证据获取从"解码黑盒缓冲区"降级为"逐寄存器对照实验"，
且 msi-ec 的 `CONF_G2_10` 已经把候选答案摆在桌面上。

---

## 3. 本机 EC 证据链（msi-ec CONF_G2_10，固件白名单含 15M3EMS1.105–.113）

| 功能 | EC 地址 | 值/语义 | 备注 |
|---|---|---|---|
| 风扇模式 | `0xD4` | `0x0D`=auto，`0x1D`=silent，`0x8D`=advanced | advanced 档允许外部设定转速 |
| Cooler Boost（强冷） | `0x98` bit7 | 1=开，0=关 | 读写该字节须先读出、按位或/清零后回写，**不得整字节覆盖** |
| CPU 实时风扇转速 | `0x71` | 百分比 0–100 | 只读观察值 |
| GPU 实时风扇转速 | `0x89` | 百分比 0–100 | 只读观察值 |
| CPU 实时温度 | `0x68` | 摄氏度 | 可与现有 WMI/ nhiệt度源交叉验证 |
| GPU 实时温度 | `0x80` | 摄氏度 | 同上 |
| Shift 模式 | `0xD2` | `0xC4`=turbo（注释：个别固件 0xC0），`0xC2`=eco，`0xC1`=comfort | 与 `firmware.scenario` 能力相关 |
| 电池充电阈值 | `0xD7` | — | 本任务不用，登记备用 |

**必须如实标注的不确定性**：
- 以上是**社区证据**（msi-ec 源码，配对固件版本精确匹配），按本项目等级制只能作为
  `Detected` 依据；升级 `Verified` 必须有本机对照实验（§6 阶段 A/B）。
- "advanced 档下手写转速的寄存器" msi-ec **未定义**（其 fan_mode 只切档不调值）。
  老机型（ISW/MControlCenter 时代）候选为 `0x72–0x7D` 区间的档位/占空比寄存器，
  本机具体地址**必须靠阶段 B 的差分实验定位**，不得凭旧机型经验直写。
- RPM（转/分）读数在 `CONF_G2_10` 中无对应寄存器；EC 读出的是**百分比**。
  `FanReading` 在证据落地前不得编造 RPM。

---

## 4. 总体设计

### 4.1 分层

```
Desktop（不接触硬件，不变）
   │
Application（组合根：按 PawnIO 可用性装配 EC 版风扇硬件，不变业务逻辑）
   │
Fans（曲线校验/应用/快照/恢复：完全复用，零改动）
   │
Core（IFanHardware / CapabilityKeys：仅新增 1 个键）
   │
Hardware.Msi
   ├─ Transport/（WMI 通道，保持只读，不动）
   ├─ Transport/PawnIo/  ← 新增
   │    ├─ PawnIoDriver        —— 设备打开/模块加载/方法执行（自写，~150 行）
   │    ├─ LpcAcpiEcModule     —— ioctl_pio_read/write 封装 + Access_EC 互斥
   │    ├─ MsiEcPort           —— EC 时序状态机（IBF/OBF 轮询、RD_EC/WR_EC/Burst）
   │    └─ IEcPortAccess       —— byte ReadRegister(byte addr) / WriteRegister(byte addr, byte value)
   ├─ Devices/Ms15M3/
   │    ├─ Ms15M3EcRegisterMap —— CONF_G2_10 寄存器表（按 EC 固件版本匹配）
   │    ├─ FanHardwareMs15M3Ec —— IFanHardware 的 EC 实现（新增）
   │    └─ FanHardwareMs15M3   —— 原 WMI 实现，保留作诊断/交叉验证
   └─ Capabilities/Ms15M3Capabilities.cs —— 等级演进（见 4.3）
```

设计原则对齐定稿：硬件访问仍全部收在 `Hardware.Msi`；`Core.Measurement` 之外不做单位
换算（EC 百分比 → `FanReading` 直接用百分比字段）；写入串行（EC 通道内置
`SemaphoreSlim` + OS 互斥体双重串行）；不支持即明示原因，绝不假成功。

### 4.2 新增能力键与等级演进

| 键 | 现状 | 阶段 A 后 | 阶段 B/C 后 | 证据来源 |
|---|---|---|---|---|
| `ec.access` | Detected（Get_EC 通道） | Detected → **Verified（读）**：本机经 PawnIO 读出 EC 固件版本/温度与 WMI 交叉一致 | 同左（写仍单独看 fan 键） | 阶段 A 实验记录 |
| `fan.curve` | Detected | 维持 | **Verified**：写入→读回→一致性 + 热效应观察 | 阶段 B/C 实验 |
| `fan.fullspeed` | **Unknown** | **Detected**（Cooler Boost=0x98 bit7 有社区定论 + 本机读回该字节可解释） | Verified（本机实测强冷开/关及退出恢复） | 阶段 A/B |
| `fan.calibration` | Unknown | Unknown（依赖 fullspeed） | 随 fullspeed 走 | — |
| 新增 `fan.pwm` | —（现无此键） | Detected（advanced 模式 0xD4=0x8D 可读） | Verified（档位/占空比寄存器定位并验证后） | 阶段 B |

`CapabilityKeys` 新增 `FanPwm = "fan.pwm"` 并加入 `All` 列表；`Ms15M3Capabilities.Statuses`
同步登记；Desktop 的 `CapabilityPresenter` 补显示名"风扇输出调节"。

**EC 固件版本门**：`Ms15M3EcRegisterMap.ForFirmware("15M3EMS1.113")` 只接受白名单
`.105–.113`；读到其它版本 → 返回 null → 通道整体 Unknown（固件升级后自动失效，
防止把旧寄存器表套到新固件上）。

### 4.3 组合根接线（ApplicationHost.CreateDefault）

```csharp
// PawnIO 通道可用性探测（不抛异常，失败返回 null 并带原因）
var ecAccess = PawnIoEcAccess.TryCreate();   // 驱动未装/互斥体不可用/固件不在白名单 → null+reason
IFanHardware fanHardware = ecAccess is not null && capabilities.AllowsRead(...)
    ? new FanHardwareMs15M3Ec(ecAccess, capabilities, transport)   // EC 版
    : new FanHardwareMs15M3(transport, capabilities);              // 原 WMI 版（诊断兜底）
```

装配必须落在 `CreateDefault` 的现有可注入参数体系内（外部可传 fake），ApplicationHost
不新增对 PawnIO 的硬依赖——`Hardware.Msi` 内部实现，Application 只见接口。

### 4.4 安全护栏（写入路径的硬约束）

1. **寄存器白名单**：`MsiEcPort.WriteRegister` 只接受 `RegisterMap` 登记过的地址，
   白名单外一律 `AccessDenied`——把"写错地址"的爆炸半径压到零。
2. **位级写保护**：`0x98`（Cooler Boost）等"位语义"寄存器必须走
   `ReadModifyWrite(address, mask, value)`，禁止整字节覆盖。
3. **Access_EC 互斥体**：模块源码要求 EC 事务前持有 `\BaseNamedObjects\Access_EC`。
   **P0 实测修订**：本机当前该命名互斥体**不存在**（见 evidence §15.4）。实现策略改为：
   `OpenExisting` 成功 → 每次 EC 事务前 `WaitOne(500ms)`，超时按错误处理；
   不存在 → 降级为进程内串行 + 时序护栏，并在通道状态里如实注明"无系统级 EC 仲裁互斥体"。
   阶段 A 实验期间持续复查（如 MSI Center 运行时是否创建）。
4. **单实例通道**：EC 通道全局唯一（组合根保证），内部 `SemaphoreSlim(1,1)` 串行，
   与 SnapshotStream 的采样循环共享同一实例。
5. **写后读回核对**：沿用 `FanApplicationService` 现有流程（写入→读回→
   `AreEquivalent`→`ConfirmationBasis`），EC 版读回即 `0x71/0x89` 实时百分比。
6. **ExitStrategy**：进程退出/异常时提供"恢复固件自动档"（写 `0xD4=0x0D`），
   失败则界面上明示"未恢复成功"，不静默。
7. **共存风险**：MSI Center / Afterburner 运行时会与本项目争抢 EC（尽管有互斥体，
   但语义层会互相覆盖）。检测到 MSI Center 进程 → 界面警示并建议关闭；
   文档写明互斥规则。

---

## 5. 关键实现细节（代码级）

### 5.1 PawnIoDriver（IOCTL 层，示意）

```csharp
internal sealed class PawnIoDriver : IDisposable
{
    private const uint DeviceType = 41394u << 16;
    private const uint IoctlLoadBinary = DeviceType | (0x821u << 2);
    private const uint IoctlExecute = DeviceType | (0x841u << 2);
    private const int FnNameLength = 32;

    private readonly SafeFileHandle _handle;

    public static bool IsInstalled { get; } = /* 注册表 Uninstall\PawnIO DisplayVersion */;

    public static PawnIoDriver Open() { /* CreateFile(GLOBALROOT\Device\PawnIO) */ }

    public void LoadModule(ReadOnlySpan<byte> moduleBin) { /* DeviceIoControl(LoadBinary) */ }

    /// <summary>执行模块方法。失败显式抛 PawnIoException(Win32Error)，不静默。</summary>
    public int Execute(string name, long[] input, int inputCells, long[] output, int outputCells)
    {
        // totalInput = 32 字节方法名 + inputCells*8 字节
        // DeviceIoControl(Execute, ...) → bytesRead/8 个有效输出 cell
        // false → throw new PawnIoException(Marshal.GetLastWin32Error())
    }
}
```

需要 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（或全部用 `fixed`/`Span` 规避 unsafe
——建议后者，与全库现状一致；`fixed` 语句不需要 unsafe 关键字的场景足够覆盖本实现）。

### 5.2 MsiEcPort（EC 时序状态机，示意）

```csharp
// 标准 ACPI EC 协议（命令口 0x66，数据口 0x62）
// 状态口（读 0x66）：bit0=OBF 输出缓冲满，bit1=IBF 输入缓冲满
private const byte RdEc = 0x80;   // 读寄存器命令
private const byte WrEc = 0x81;   // 写寄存器命令

public byte ReadRegister(byte address)
{
    using var _ = _ecMutex.EnterOptional();  // Global\Access_EC：存在则等待(500ms)，不存在降级（P0 实测修订）
    lock (_gate)                             // 进程内串行
    {
        WaitForInputBufferEmpty();           // 轮询 IBF=0，超时 50ms
        _module.WritePort(CmdPort, RdEc);
        WaitForInputBufferEmpty();
        _module.WritePort(DataPort, address);
        WaitForOutputBufferFull();           // 轮询 OBF=1，超时 50ms
        return _module.ReadPort(DataPort);
    }
}
```

轮询节奏：`SpinWait` 起步（前 ~200µs），再退化为 `Thread.Sleep(1)`，总超时 50ms/阶段、
单寄存器操作总超时 150ms。这些参数在阶段 A 实测后固化到常量并写注释。

### 5.3 FanHardwareMs15M3Ec（IFanHardware 实现）

| IFanHardware 方法 | EC 实现 |
|---|---|
| `ReadAsync` | 读 `0xD4`（模式）、`0x71`/`0x89`（CPU/GPU 百分比）；`FanHardwareState` 直接可解码（不像 Get_Fan 黑盒） |
| `ApplyCurveAsync` | 阶段 C 开放：写 `0xD4=0x8D`（advanced）→ 写档位/占空比寄存器（地址来自阶段 B 实验）→ 读回核对 |
| `SetAutomaticAsync` | 写 `0xD4=0x0D`，读回核对 |
| `SetFullSpeedAsync` | `ReadModifyWrite(0x98, 0x80, 0x80)`；退出为清位。依赖 `fan.fullspeed` 达 Verified |

监控侧：新增 `EcFanSpeedSource : IFanSpeedSource`（Key=`"fan.rpm.ec"`），从 EC 读百分比；
`FanReading.Rpm` 保持空（无 RPM 证据），界面回落显示百分比——与现有"校准未完成不显示
百分比"规则相反方向的诚实：**有什么显示什么，没有的不编**（届时核对 FanReading 契约后
定稿字段语义）。

---

## 6. 分阶段实施计划

### P0 预装与可达性验证 —— ✅ 已完成（2026-10-17，证据见 `docs/hardware-evidence.md` §15）

| # | 步骤 | 结果 |
|---|---|---|
| 1 | 安装 PawnIO | ✅ 2.2.0.0 官方签名版（最新），服务 Running |
| 2 | 驱动可达性 | ✅ 设备句柄以读写方式打开成功（需管理员，实测会话为管理员） |
| 3 | 获取 LpcACPIEC.bin | ✅ `assets/PawnIo/LpcACPIEC.bin`，2612 字节，SHA-256 `c38fd1…f7906`，git blob SHA-1 与仓库提交逐位一致 |
| 4 | 端到端冒烟 | ✅ 模块加载成功；`ioctl_pio_read(0x66)` 被动读状态口返回 `0x08`（EC 空闲） |
| 5 | 互斥体探测 | ⚠ `Global\Access_EC` 当前不存在 → 方案 §4.4/§5.2 已修订为"存在则遵守，不存在则降级并注明" |
| 6 | 关闭 MSI Center / Afterburner | 阶段 A 实验窗口开始前执行（本次冒烟为被动读，无冲突） |

### 阶段 A：只读实证（代码与工具已交付，2026-10-17；A1/A2 已过，A3–A7 待叶哥配合）

> 交付清单与验收状态见 `docs/PawnIo通道与EcProbe说明.md`；证据见 evidence §15/§16。

代码清单：
- `src/MsiControl.Hardware.Msi/Transport/PawnIo/`：`PawnIoDriver`、`LpcAcpiEcModule`、
  `MsiEcPort`（仅 ReadRegister 编译进此阶段；WriteRegister 编译但被白名单+能力双闸拦住）、
  `Ms15M3EcRegisterMap`。
- 新控制台诊断工具 `src/MsiControl.Tools.EcProbe`（net8.0-windows，不进主 sln 依赖图，
  仅被 sln 收纳以便一键构建）：`EcProbe dump <addr>` / `EcProbe watch` / `EcProbe firmware`。
- 单元测试：`MsiControl.Hardware.Msi.Tests` 新增 `PawnIoEcTests`（FakePortIo 注入：
  时序状态机、互斥体超时、白名单拒绝、固件版本门）。

**只读实验协议（本机执行，全程零写入）**：

| # | 步骤 | 记录 |
|---|---|---|
| A1 | `EcProbe firmware` 读 EC 固件版本字符串，与 WMI `Get_EC` 文本比对 | 版本一致 → `ec.access`(读) 可升 Verified |
| A2 | 静置时 dump `0x68/0x71/0x80/0x89/0xD2/0xD4/0x98` | 基线表 |
| A3 | MSI Center 切静音档 → dump `0xD4`（预期 0x1D）；切回 auto（预期 0x0D） | 风扇模式寄存器实证 |
| A4 | 用户场景 silent↔turbo 切换，watch `0xD2` | shift 模式实证 |
| A5 | 跑 Cinebench 1 分钟，watch `0x68/0x71`；跑 GPU 负载 watch `0x80/0x89` | 温度/百分比随负载变化实证 |
| A6 | 开/关 Cooler Boost（Fn+ cooler 键或 MSI Center），watch `0x98` bit7 | 强冷位实证 → `fan.fullspeed` 升 Detected |
| A7 | 连续采样 `Get_Fan`（WMI 32 字节）与 EC `0x71/0x89` 对齐 | **用 EC 真值反推 Get_Fan 字段语义**（黑盒解码的钥匙） |

通过标准：A1–A6 中每个寄存器的响应与 CONF_G2_10 预期一致，无一次时序超时/校验失败。
全部原始 dump 值 + 时间戳追加进 `docs/hardware-evidence.md`。

### 阶段 B：写入定位（高危，须叶哥逐条确认后执行）

1. `EcProbe dump` 全量 256 寄存器基线。
2. MSI Center 自定义曲线设为固定低值（如 30%）→ dump diff，定位变化的寄存器
   （候选 `0x72–0x7D` 区间）；恢复 auto 再 diff 回去。
3. 固定高档（如 90%）重复 diff，确认"同一寄存器、值随目标百分比单调变化"。
4. 每一步写入都由 **MSI Center 完成**（我们只读观察）——阶段 B 我方代码仍然零写入。
5. 产出：`docs/hardware-evidence.md` 新增"写入寄存器定位"节，写明地址、编码
   （单字节百分比还是档位索引）、副作用观察。定位失败则如实记录，`fan.pwm` 停在 Detected，
   交付降级方案：仅 auto/silent/advanced 档切换 + Cooler Boost。

### 阶段 C：开放调节（写入通路 + 能力升级）

1. `FanHardwareMs15M3Ec` 写路径按阶段 B 定位结果实现；
   `Ms15M3Capabilities` 把 `fan.curve`/`fan.pwm` 升 Verified（引证据条目）。
2. ApplicationHost 按 4.3 装配；Desktop 曲线编辑器启用（文案带上"经 EC 通道"）。
3. 走 `FanApplicationService` 全流程（快照→写入→读回→恢复），复用现有恢复机制代码。
4. 恢复能力实测报告 §3 的实验（写入→读回→恢复→睡眠/重启行为）此时才真正可执行。

---

## 7. 测试步骤

### 7.1 单元测试（每阶段随代码交付）

```bash
"C:\Users\yzy\.workbuddy\binaries\dotnet\dotnet.exe" build "F:\Codex programs\msi控制台\MSI-Control.sln" -c Release
"C:\Users\yzy\.workbuddy\binaries\dotnet\dotnet.exe" test  "F:\Codex programs\msi控制台\MSI-Control.sln" -c Release
```

- `PawnIoEcTests`：FakePortIo 驱动时序状态机（IBF 超时/OBF 超时/正常读写）；
  `Access_EC` 打不开 → 通道 Unavailable + 原因文案；写白名单外地址 → 拒绝；
  `0x98` 只能 ReadModifyWrite；固件版本不在白名单 → RegisterMap 为 null；
  能力未 Verified 时写方法一律 Unsupported（沿用闸门测试范式）。
- `FanApplicationService` 现有 143 项测试必须全绿（业务层零改动是验收标准之一）。

### 7.2 实机测试（每阶段收口）

1. **构建冒烟**：Release 构建后实机启动 Desktop（用 WMI `Win32_Process.Create` 启动，
   规避作业对象回收坑），确认 Ctrl+Alt+M 显隐、干净退出、日志 0 ERROR。
2. **EcProbe 冒烟**：`EcProbe firmware` 与 `Get_EC` 一致；`EcProbe dump 0xD4` 稳定复现。
3. **通道切换冒烟**（阶段 C 后）：装/卸 PawnIO 各启动一次，确认装配降级到 WMI 版风扇
   硬件且界面如实显示降级原因。
4. **调节闭环**（阶段 C 后，叶哥在场）：
   - 曲线 40%@60°C 下发 → `EcProbe watch 0x71` 读回 ≈40 → 恢复启动曲线 → 回 auto；
   - Cooler Boost 开→关，确认 bit7 复位且转速回落；
   - 睡眠唤醒后曲线是否保留 → 决定启动恢复语义（照抄恢复能力报告 §3.2 的判定表）；
   - 全程温度监控，任何异常（风扇停转、读数冻结）立即终止实验并恢复 auto。
5. **回归**：`docs/构建验证记录.md` 追加本阶段构建/测试输出（只追加不覆盖）。

---

## 8. 风险与回滚

| 风险 | 缓解 |
|---|---|
| EC 写入未知寄存器致硬件异常 | 阶段 B 之前我方代码零写入；写白名单硬约束；位级 RMW；写入前快照 + 一键恢复 auto |
| 与 MSI Center/Afterburner 互踩 | 实验期关闭；运行期进程检测警示；互斥体保证事务级不撕裂 |
| PawnIO 驱动被 Defender/策略拦 | 官方签名版通常无碍；文档记录白名单方法备用 |
| 固件升级（>.113）致寄存器表失配 | 版本门自动降级 Unknown，明示"EC 固件已变更，风扇调节停用" |
| EC 时序在低配 CPU 上超时 | 超时参数阶段 A 实测固化；超时按错误处理不重试写入 |
| LpcACPIEC.bin 供应链 | SHA-256 记录 + 构建期校验 |

---

## 9. 待叶哥决策的问题

1. **是否接受安装 PawnIO 内核驱动**（官方签名版，winget `namazso.PawnIO`）？
   这是整个路线的前置条件。
2. 阶段 B 的写入定位实验需要你手动操作 MSI Center 并在场观察——安排一个方便的时间窗口。
3. 曲线语义：EC 只接受"百分比/档位"，`FanCurveLimits.Ms15M3` 的输出步长约束
   将按阶段 B 实测结果修订（可能不是现有假设的 3% 步进）。
4. `fan.pwm` 新键及等级演进表（4.2 节）是否认可，认可后我按多会话纪律提交代码
   （本方案落地预计跨 2–3 个任务会话，归属文件清单见各阶段"代码清单"）。
