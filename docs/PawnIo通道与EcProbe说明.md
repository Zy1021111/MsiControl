# PawnIO EC 通道与 EcProbe 说明（阶段 A 交付）

版本：v1.0
日期：2026-10-17
负责人：PawnIO 风扇接入会话（奶龙）
工程：`src/MsiControl.Hardware.Msi/Transport/PawnIo/`、`src/MsiControl.Tools.EcProbe/`、
`tests/MsiControl.Hardware.Msi.Tests/PawnIo/`

> 方案全文见 `docs/PawnIO风扇接入方案.md`；P0 证据见 `docs/hardware-evidence.md` §15；
> 阶段 A 首批实机数据见 §16。本文件说明代码结构、使用方法与验收状态。

---

## 1. 交付范围

| 产出 | 位置 |
|---|---|
| PawnIO 驱动封装（打开设备 / 加载模块 / 执行方法） | `Transport/PawnIo/PawnIoDriver.cs` |
| LpcACPIEC 模块封装（端口 IO + 供应链哈希校验） | `Transport/PawnIo/LpcAcpiEcModule.cs` |
| EC 协议端口（RD_EC/WR_EC 时序、互斥体仲裁、写白名单） | `Transport/PawnIo/MsiEcPort.cs` |
| MS-15M3 EC 寄存器表（msi-ec CONF_G2_10 + 固件版本门） | `Transport/PawnIo/Ms15M3EcRegisterMap.cs` |
| 通道门面（TryCreate，失败带具体原因） | `Transport/PawnIo/PawnIoEcAccess.cs` |
| 只读诊断工具 | `src/MsiControl.Tools.EcProbe/` |
| 模块二进制（SHA-256 固化于代码与证据） | `Resources/PawnIo/LpcACPIEC.bin`（嵌入资源） |
| 单元测试（65 项，全部离线） | `tests/MsiControl.Hardware.Msi.Tests/PawnIo/` |
| 能力键 `fan.pwm`（Detected） | Core / Hardware.Msi / Desktop 登记 |

**安全设计**（方案 §4.4 全部落地）：

1. 模块哈希校验：加载前 SHA-256 必须等于固化值，不符即拒绝；
2. 端口白名单：模块内核态仅放行 0x62/0x66，用户态 `LpcAcpiEcModule` 再校验一次；
3. 寄存器写白名单：阶段 A 恒为**空集**——任何 `WriteRegister` 在协议层之前被
   `EcWriteBlockedException` 拒绝，一个端口写都不会发生（有单测锁定该行为）；
4. 仲裁互斥体：`Global\Access_EC` 存在则每次事务前等待获取，不存在则降级进程内串行
   并通过 `ArbitrationNotice` 如实说明（P0 实测本机当前不存在，evidence §15.4）；
5. 固件版本门：`IsSupportedFirmware` 只接受 15M3EMS1.105–.113，固件升级出界自动失效；
6. 错误显式化：驱动调用失败抛 `PawnIoException`（带 Win32 错误码），时序失败抛
   `EcTimeoutException`（带阶段与最后状态字），绝不静默返回零数据。

## 2. EcProbe 使用方法（需管理员）

```
EcProbe status                 驱动/模块/仲裁/写白名单状态
EcProbe firmware               读 EC 固件版本并匹配寄存器表白名单
EcProbe dump <地址>            读单个寄存器（如 dump 0xD4）
EcProbe dump-all               全寄存器只读 dump（0x00–0xFF，阶段 B 对照基线）
EcProbe watch <地址> [间隔ms]  周期观察某寄存器（默认 500 ms，Ctrl+C 退出）
```

**本工具没有写命令**，且通道构造时写白名单恒为空集；即使阶段 B 定位出写入寄存器，
写放行也只属于主应用业务层（经能力等级闸门），不会回收给诊断工具。

## 3. 验收状态（2026-10-17）

| 项 | 状态 |
|---|---|
| `Hardware.Msi` / `Core` / `EcProbe` Release 构建 | 0 警告 0 错误 |
| `Hardware.Msi.Tests` | 65/65 通过（新增 PawnIo 系列） |
| 其余 7 个测试工程 | 全部通过（639 项，见构建验证记录） |
| `Desktop` 构建 + `Desktop.Tests` | **待补**：运行中的 MsiControl.Desktop 实例（11:26 启动）锁定 bin 输出目录，退出后重跑全量 sln 构建收口 |
| 实机冒烟（EcProbe） | status / firmware / dump 均通过；A1、A2 证据已入 §16 |
| 实机待办 | A3–A7 受控对照实验（需叶哥配合，方案 §6） |

## 4. 后续路线（对应方案 §6）

1. **A3–A7 对照实验**：切档（0xD4）、场景（0xD2）、负载（0x68/0x71、0x80/0x89）、
   强冷（0x98 bit7）差分记录 → 寄存器语义升证据；
2. **阶段 B**：MSI Center 自定义固定转速 + `dump-all` 差分，定位手写转速寄存器；
3. **阶段 C**：`FanHardwareMs15M3Ec` 写路径 + `fan.curve`/`fan.pwm` 升 Verified +
   ApplicationHost 装配（方案 §4.3）；
4. Desktop 全量构建收口后，本文件与构建验证记录同步补齐。
