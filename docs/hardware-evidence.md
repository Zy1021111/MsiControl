# 本机硬件能力证据清单

更新日期：2026-10-16
对应任务书：《MSI-Control 开发总流程与任务书 v1.2》§2、§4、§16、§18 阶段 A

> **本文件是证据清单，不是设计文档。**
> 所有结论均标注证据等级，禁止把「类存在」「方法名存在」当作「控制功能已验证」。
> 全部探测均为**只读**，未执行任何硬件写入。

证据等级定义：

- **已确证**：本次或附件中直接观测到返回值。
- **线索**：接口/方法名已确认存在，但未调用验证语义与可用性。
- **待验证**：无任何证据。

---

## 1. 证据来源

| 编号 | 来源 | 时间 | 说明 |
|---|---|---|---|
| E1 | `C:\Users\yzy\.codex\attachments\c0c61c3c-2ffb-4086-ba69-44404fc822e1\已粘贴的文本.txt` | 2026-10-11 20:05 | 用户早期导出的 WMI 实例与方法输出，落盘保留 |
| E2 | 本会话只读 CIM 探测 | 2026-10-16 | `Get-CimClass` / `Get-CimInstance` / PnP 枚举 |

两份来源在 `MSI_ACPI` 方法集上完全一致，互为交叉印证。

---

## 2. `MSI_ACPI`：核心控制接口

### 2.1 实例信息（E1）

```text
Active       : True
InstanceName : ACPI\PNP0C14\0_0
CimClass     : root/wmi:MSI_ACPI
CimInstanceProperties : {Active, InstanceName}
```

要点：

- 实例**存在**且 `Active = True`。
- 实例路径绑定在 ACPI WMI 设备节点 `ACPI\PNP0C14\0_0` 上。
- 实例**只暴露 `Active` 与 `InstanceName` 两个属性，没有任何数据字段**。
  → 结论：**所有数据访问都必须通过方法调用**，不存在「读属性拿温度」的捷径。

### 2.2 方法签名模式（E1）

清单中**每一个方法**都遵循同一模式：

| 项目 | 值 |
|---|---|
| 返回类型 | `Boolean` |
| 参数 | 单一参数 `Data` |
| 限定符 | `Description, Implemented, read, WmiMethodId...` |

→ 这是典型的 MSI ACPI **命令包**模式：`Data` 是命令/数据缓冲区（应为字节数组），`WmiMethodId` 限定符决定实际命令编号，返回 `Boolean` 表示本次调用是否被固件接受。

**`Implemented` 限定符逐方法存在**，理论上可用于探测单个方法的可用性；但**尚未验证该限定符是否随机型/固件变化**，不可直接当作能力判定依据。

### 2.3 完整方法清单（E1 + E2 一致）

```text
GetPackage / SetPackage
Get_EC / Set_EC / Get_EC2
Get_BIOS / Set_BIOS / Get_BIOS_64 / Set_BIOS_64
Get_SMBUS / Set_SMBUS / Get_SMBUS_64 / Set_SMBUS_64
Get_MasterBattery / Set_MasterBattery
Get_SlaveBattery / Set_SlaveBattery
Get_Temperature / Set_Temperature
Get_Thermal / Set_Thermal / Get_Thermal_64 / Set_Thermal_64
Get_Fan / Set_Fan
Get_Device / Set_Device
Get_Power / Set_Power
Get_AP / Set_AP
Get_PE / Set_PE
Get_Range / Set_Range
Get_Data / Set_Data
Get_WMI / Get_Debug / Set_Debug
```

共 40 个方法。

### 2.4 与产品功能的映射

| 需求项（任务书章节） | 候选接口 | 等级 |
|---|---|---|
| 风扇曲线读取与写入（§7） | `Get_Fan` / `Set_Fan` | 线索 |
| 温度读取（§6.2 / §6.3） | `Get_Temperature` / `Get_Thermal` | 线索 |
| CPU/GPU 功耗限制（§8） | `Get_Power` / `Set_Power` | 线索 |
| 可写范围查询（§8.3「显示支持范围」） | `Get_Range` / `Set_Range` | 线索 |
| EC 版本与 EC 访问（§4 / §16） | `Get_EC` / `Get_EC2` | 线索 |
| MSI 固件性能场景（§8.2） | `Get_Device` / `Set_Device`、`Get_AP` / `Set_AP` | 线索 |
| 电池侧数据（§6.4） | `Get_MasterBattery` / `Get_SlaveBattery` | 线索 |
| 强冷（§7.5） | 未定位，候选 `Set_Fan` / `Set_Device` / `Set_Data` | 待验证 |
| 厂商私有用例兜底 | `Get_Data` / `Set_Data`、`GetPackage` / `SetPackage` | 线索 |

**重要限定**：以上**全部为方法名层面的线索**。参数布局、`Data` 缓冲区结构、
命令字（`WmiMethodId`）、返回值编码、以及哪些方法在 `MS-15M3` + `E15M3IMS.117` 上真正可用，
**均未验证**。严格符合任务书 §1 原则 4、5、6。

### 2.5 尚未澄清的接口来源问题

`MSI_ACPI` 是**厂商私有 WMI 类**，其存在必然要求有一份 MOF 完成类声明确认。
本机**未安装任何 MSI 官方软件**（见 §4），因此 MOF 来源有四种可能：

| 可能来源 | 对项目的含义 |
|---|---|
| BIOS / 主板 BSP 随附并完成注册 | 平台级依赖，相对稳定 |
| 历史某次 MSI 软件安装后在 WMI 仓库残留 | 用户清理系统后可能消失，**不可依赖** |
| 其他第三方工具（如 XTU / Afterburner）注册 | 同样不可依赖 |
| Windows 内置或设备驱动附带的 MOF | 依赖具体驱动版本 |

**注意**：实例路径 `ACPI\PNP0C14\0_0` 只说明类绑定在 ACPI WMI 设备节点上，
**不能据此判定 MOF 由谁注册**——由 `mofcomp` 注册的私有 MOF 同样会绑定到该设备节点。
因此该问题**仅被收窄，未被关闭**。

在澄清前，不应围绕 `MSI_ACPI` 做不可回退的架构决策，也不应假设它在其他机器或清理系统后仍然存在。

### 2.6 方法元数据（2026-10-16 只读实测，来源 E3）

**来源 E3**：任务 01 用 `src/MsiControl.Hardware.Msi/Discovery`（`WmiHardwareDiscovery`）只读枚举
`root\WMI:MSI_ACPI`。**只读取类/实例/方法元数据，未调用任何方法**（既无 `Set_*`，也无 `Get_*`）。

实测结果：

- 类存在，实例 `ACPI\PNP0C14\0_0`，`Active = True`，**方法数 40**（与 §2.3 一致）。
- 每个方法的限定符：`Description`、`Implemented = True`、`read`、`write`、`WmiMethodId`。
- **命令字 `WmiMethodId` 已全部读出**（此前为「未验证」），这是编写 `Transport` 的直接依据：

| ID | 方法 | ID | 方法 | ID | 方法 |
|---:|---|---:|---|---:|---|
| 1 | `GetPackage` | 14 | `Set_Temperature` | 27 | `Get_Data` |
| 2 | `SetPackage` | 15 | `Get_Thermal` | 28 | `Set_Data` |
| 3 | `Get_EC` | 16 | `Set_Thermal` | 29 | `Get_WMI` |
| 4 | `Set_EC` | 17 | `Get_Fan` | 30 | `Get_PE` |
| 5 | `Get_BIOS` | 18 | `Set_Fan` | 31 | `Set_PE` |
| 6 | `Set_BIOS` | 19 | `Get_Device` | 32 | `Get_EC2` |
| 7 | `Get_SMBUS` | 20 | `Set_Device` | 33 | `Get_Range` |
| 8 | `Set_SMBUS` | 21 | `Get_Power` | 34 | `Set_Range` |
| 9 | `Get_MasterBattery` | 22 | `Set_Power` | 69 | `Get_BIOS_64` |
| 10 | `Set_MasterBattery` | 23 | `Get_Debug` | 70 | `Set_BIOS_64` |
| 11 | `Get_SlaveBattery` | 24 | `Set_Debug` | 71 | `Get_SMBUS_64` |
| 12 | `Set_SlaveBattery` | 25 | `Get_AP` | 72 | `Set_SMBUS_64` |
| 13 | `Get_Temperature` | 26 | `Set_AP` | 79 | `Get_Thermal_64` |
| | | | | 80 | `Set_Thermal_64` |

- **参数**：单一 `Data`，CimType = **`Object`（VARIANT）**，**非数组**。
  - `Get/Set` 配对方法多为 `in out`；`GetPackage` / `Get_EC` / `Get_EC2` / `Get_WMI` 为 `out`；`SetPackage` / `Set_EC` 为 `in`。
- **`OutParameters` 不含 `ReturnValue`** → 无法据元数据判定调用是否被固件接受。
  ⚠ 这与 §2.2「返回类型 `Boolean`」的记述**不一致**，本文按「无法判定」处理，留待 Step 3 实测澄清。
- 5 个排除类再次确认返回 `0x8004100C`（§3）。
- 通用热区 `THRM_0` 返回 **70.05 °C**（十分之一开尔文换算经 `MsiControl.Core.Measurement`，端到端正确）。

**能力等级不变**：本次只证明「方法元数据可读」。`Data` 的**编码与语义仍未知**，
故风扇、功耗等能力维持 `Detected`，**不升级**（§2.2 的「`Implemented` 限定符不得当作能力依据」仍适用）。

**下一步（§8 Step 3）**：确认签名后，受控**只读**调用 `Get_Temperature` / `Get_Fan` 等，
以确定 `Data` 的运行期形态与是否存在 `ReturnValue`。仍不调用任何 `Set_*`。

---

## 3. 不可用的 MSI 类（已排除）

对以下类执行 `Get-CimInstance` 全部失败（E1 与 E2 一致）：

```text
MSI_CPU
MSI_Power
MSI_VGA
MSI_Master_Battery
MSI_Slave_Battery
```

错误统一为：

```text
Get-CimInstance : 不支持
HRESULT 0x8004100c  (WBEM_E_NOT_SUPPORTED)
```

同时在 E2 中，这些类的 `CimClassMethods` 枚举结果为空。

→ **结论（已确证）**：这些类**只有类定义（schema），没有可实例化的 provider 数据**。
**不可作为功能实现依据**，应显式排除，避免后续模块误用。

---

## 4. 其他已确证事实

| 项目 | 实测结果 | 来源 | 等级 |
|---|---|---|---|
| 电池 | `BIF0_9`，剩余 97%，`BatteryStatus=2`（已接交流电） | E2 | 已确证 |
| MSI 官方软件 | 未安装 MSI Center / Dragon Center / SteelSeries GG 等任何组件 | E2（注册表卸载项枚举） | 已确证 |
| MSI 相关服务 | 无；仅系统自带 `MSiSCSI`（Microsoft iSCSI）、`msiserver` | E2 | 已确证 |
| ACPI 键盘设备 | `ACPI\MSI0007`，状态 OK | E2 | 已确证 |
| MSI 厂商 HID | `HID\VID_1462&PID_1603`，状态 OK（`0x1462` = Micro-Star International） | E2 | 已确证 |
| 附加 HID 设备 | `VID_3367&PID_1970`（在用）、`VID_3367&PID_1982`（幽灵/残留）、`VID_373B&PID_1209`（在用） | E2 | 已确证 |

主板码 `MS-15M3 REV:1.0`、BIOS `E15M3IMS.117`、BIOS 日期 2026-04-17 沿用任务书 §4 记录，本次未复测。

---

## 5. 通用 ACPI 热区（可用的公开数据源）

`MSAcpi_ThermalZoneTemperature`（Windows 标准 ACPI 热区类，非 MSI 私有）**返回有效数据**（E1）：

```text
InstanceName        : ACPI\ThermalZone\THRM_0
Active              : True
CurrentTemperature  : 3332     → 333.2 K ≈ 60.05 °C
CriticalTripPoint   : 3832     → 383.2 K ≈ 110.05 °C
ActiveTripPointCount: 0
PassiveTripPoint    : 0
SamplingPeriod      : 0
```

**单位注意**：该字段单位为**十分之一开尔文（deciKelvin）**，换算 `°C = 值 / 10 - 273.15`。
这是冷却后台极易踩错的地方，必须显式转换。

**限制**：

- 本机仅有 **1 个热区**（`THRM_0`），粒度粗、刷新慢。
- 该值通常代表 ACPI 热区而非精确 CPU 封装温度，**不能替代** CPU/GPU 专用温度源。
- 可作为兜底数据源，不应作为仪表盘主数据源。

---

## 6. 已剔除的噪声（重要，避免误判）

`root\WMI` 中以下类名**与微星品牌无关**，不构成任何能力证据：

| 类名 | 实际含义 |
|---|---|
| `MSiSCSI_*` | Microsoft iSCSI 发起程序 |
| `MSIde`、`MSIde_PortDeviceInfo` | Microsoft IDE 控制器 |
| `ISR_MSI` | MSI = **M**essage **S**ignaled **I**nterrupts（消息信号中断） |

**`ISR_MSI` 尤其容易误判为品牌证据**，必须在所有后续调查中显式排除。

---

## 7. 尚未确认项（对应任务书 §4、§16、§18 阶段 A）

| 待验证项 | 当前状态 |
|---|---|
| 有效 EC 版本 | 未读到。SMBIOS 返回 255/255 无效；`Get_EC` / `Get_EC2` 待调用 |
| `MSI_ACPI` 的 MOF 来源 | 仅收窄，未关闭（见 §2.5） |
| 风扇读取、曲线格式、写入路径 | 接口名已定位，协议未知 |
| 强冷接口 | 未定位 |
| CPU 功耗与睿频上限 | 接口名已定位，可控范围未知 |
| GPU 功耗范围 | `Get_Range` 待调用确认 |
| GPU 兼容节能策略 | 无证据 |
| 显卡模式切换能力与外接屏拓扑 | 无证据 |
| 整机功耗来源 | 无证据。注意 §6.4 要求区分电池侧/适配器侧/插座侧，本机大概率只有电池侧可得 |
| 退出、重启、睡眠后的行为 | 无证据 |
| 与 Intel XTU、MSI Afterburner 的实际冲突 | 两者已安装，未实测 |
| Intel SoC Thermal IPF Extension Provider 显示 Degraded | 未只读确认具体状态 |

**2026-10-16 进展（详见 §2.6）**：上表「风扇读取、曲线格式、写入路径」与「CPU 功耗与睿频上限」
的**方法层参数细节已确认**——参数为单一 `Data`（CIM `Object`），各命令字 `WmiMethodId` 已读出。
但 `Data` 的**内容编码仍未知**，故「协议未知」的结论**不变**。
另有两点仍未关闭：**`MSI_ACPI` 的 MOF 来源**（`Discovery` 只能证明类元数据可读，不回答由谁注册）、
以及**`Get_*` 的运行期返回形态**（需受控只读调用，见 §8 Step 3）。

---

## 8. 建议的下一步只读探测

以下操作**全部只读**，不触发硬件写入：

1. **定位 `MSI_ACPI` 的 MOF 注册来源**（§2.5 的关键分叉点）
   - 检查 `MSI_ACPI` 类的 `CimClassQualifiers`（尤其 `provider`）。
   - 检查 `%SystemRoot%\System32\wbem\` 与 WMI repository 中的 MSI 相关 MOF。
   - 检查 `System32` 下是否存在 `msiapcfg.dll` 等 MSI 组件及其注册信息。
   - 读取 `ACPI\PNP0C14` 的硬件 ID 与 `_WDG` 暴露的 GUID，判断是否与 MSI MOF 对应。

2. **枚举 `MSI_ACPI` 各方法的参数细节**
   读取 `CimClassMethods.Parameters` 中 `Data` 的 `CimType`、数组维度、
   以及各方法的 `WmiMethodId` 具体取值。这是编写 `Transport`（任务 04/05 共用）的直接依据。

3. **只读调用 `Get_*` 系列**
   确认签名后，仅调用 `Get_Fan`、`Get_Temperature`、`Get_Power`、`Get_Range`、`Get_Device`、`Get_EC`，
   记录原始返回值和 `Boolean` 结果。**不调用任何 `Set_*`。**

4. **读取 EC 版本**，替换任务书 §4 的占位值。

5. **确认 `VID_1462&PID_1603` 的 HID report descriptor 与 usage page**，
   判断其是否承载快捷键或灯效通道（本项目仅可能涉及快捷键）。

6. **确认外接屏拓扑**
   按任务书 §9，记录 DP 转雷电连接的显示器实际挂在核显还是独显上。

7. **只读确认 Intel SoC Thermal IPF Provider 的 Degraded 状态**，
   不做任何自动修复。

---

## 9. 对项目风险的影响

**利好**：`MSI_ACPI` 携带 40 个方法且实例激活，使任务书 §5 中除灯效/校色/电池保护外的核心功能
（风扇曲线、功耗限制、温度、性能场景）从「可行性未知」提升为「存在明确候选接口」。
项目主要不确定性由「有没有接口」转为「接口语义与可用性」。

**风险**：

1. MOF 来源未定位（§2.5）。在澄清前不得假设该接口稳定存在。
2. 5 个 MSI 私有类已确认不可用（§3），需在设计阶段显式排除，避免误导实现。
3. 温度监控的公开兜底源粒度不足（§5），CPU/GPU 精确温度**必须**依赖 `MSI_ACPI` 或
   LibreHardwareMonitor / NVML 等第三方路径，存在降级风险。

---

## 10. 环境与运行时证据（非硬件能力，2026-10-16，任务 02 追加）

本节**不是硬件能力证据**，仅记录影响「能否实机运行与验证」的环境事实，
以免后续任务沿用已过时的结论。

| 项目 | 实测结果 | 来源 | 等级 |
|---|---|---|---|
| .NET SDK | `8.0.426`，位于 `C:\Users\yzy\.workbuddy\binaries\dotnet`（`-NoPath` 安装） | `dotnet --version` | 已确证 |
| .NET 8 桌面运行时 | **已存在**：`Microsoft.WindowsDesktop.App 8.0.32`，位于同一隔离目录的 `shared\` 下 | `dotnet --list-runtimes` | 已确证 |
| 结论 | 任务书 §13 注释中「本机未安装 .NET 8 桌面运行时」**已不成立**；`net8.0-windows` 程序可直接实机运行 | 任务 02 冒烟测试 | 已确证 |

**对后续任务的含义**：任务 03—07 可以实际启动并观察程序行为，
不必等到整合阶段才发现只会在运行时暴露的缺陷（任务 02 已借此发现一处栈溢出，见
`docs/构建验证记录.md` 的「本轮修复的实质缺陷」）。

> 注意：该运行时位于**隔离目录**内，系统 PATH 中的 `dotnet` 指向另一份不含 SDK 的副本。
> 运行桌面程序同样必须使用绝对路径：
> `"/c/Users/yzy/.workbuddy/binaries/dotnet/dotnet.exe" "<...>/MsiControl.Desktop.dll"`。

---

## 11. 性能相关接口的受控只读实测（任务 05 追加，来源 E4）

**来源 E4**：2026-10-16 19:49，任务 05 用
`src/MsiControl.Hardware.Msi/Devices/Ms15M3/MsiAcpiPerformanceProbe.cs`
在实机执行**受控只读探测**：
只调用 `Get_*` 方法，**未调用任何 `Set_*`**；`Data` 参数一律传 `null`
（即「不构造任何猜测的缓冲区」）；能力未达只读门槛的方法不调用。

### 11.1 与既有证据一致的实测项（交叉印证）

| 项目 | 本次实测 | 与既有记录的关系 |
|---|---|---|
| `MSI_ACPI` 可用性 | 可用；实例 `ACPI\PNP0C14\0_0`，`Active = True`，方法数 **40** | 与 §2.6 完全一致 |
| 5 个排除类 | 均为「类存在 = True、实例 = False、`0x8004100C` 不支持」 | 与 §3 完全一致 |
| 通用 ACPI 热区 | `ACPI\ThermalZone\THRM_0` = **48.05 °C**（本次读数） | 与 §5 一致（温度随负载变化） |

> 本次未复测 MOF 来源，该项仍未关闭（任务书 §4.1 A1）。

### 11.2 新证据（本次首次取得）：传 `null` 的 `Data` 会被 WMI 层拒绝

对 5 个性能相关只读方法各调用一次，`Data` 传 `null`，结果**全部失败**：

| 方法 | `WmiMethodId` | 原始结果 |
|---|---:|---|
| `Get_Power` | 21 | `0x80041008`（`WBEM_E_INVALID_PARAMETER`）「无效的参数」 |
| `Get_Range` | 33 | 同上 |
| `Get_Device` | 19 | 同上 |
| `Get_AP` | 25 | 同上 |
| `Get_PE` | 30 | 同上 |

**结论（已确证）：**

1. `Data` **不是可选参数**。传 `null` 时错误发生在 **WMI 参数绑定阶段**（`0x80041008`），
   调用**未到达固件**，因此既没有返回数据，也无法据此判断固件是否接受。
2. 由此反推：`Data` 必须携带符合 provider 期望的具体 VARIANT 结构。
   该结构的**类型、长度与字段语义仍未知**，本清单**不给出任何推测值**（任务书 §四 纪律 3）。
3. 在取得正确结构之前，**读回 CPU/GPU 功耗限制值在技术上不可能**。
   这正是任务 05 的全部写入路径必须保持关闭、且 `ReadAsync` 只能返回
   `PerformanceHardwareState.Unknown` 的直接依据——不是保守，是实测结果。

### 11.3 能力等级：不升级，并记录理由

| 能力键 | 本次结论 | 等级 |
|---|---|---|
| `cpu.power` / `gpu.power` | 方法可被调用，但 `Data` 协议未知，未取得任何读回值 | **维持 `Detected`** |
| `power.range` | 同上（`Get_Range` 未能返回范围） | **维持 `Detected`** |
| `firmware.scenario` | 同上（`Get_Device` / `Get_AP` 未能返回数据） | **维持 `Detected`** |
| `cpu.turbo` | 仍无专属语义证据 | **维持 `Unknown`** |
| `gpu.eco` | 仍无候选接口 | **维持 `Unknown`** |

**不升级的理由**：约定 §1.2 要求「有读回值且读回值与请求一致」方可升级。
本次既未读回任何值，也未发生任何写入，**没有任何可以推动升级的证据**。

### 11.4 实测同时确认的两项安全属性

1. **写入路径确实被两道闸门拦住**：在本机执行 `ApplyAsync(平衡档)` 后，
   5 个设置项全部为 `Unsupported` 并逐项给出能力等级与依据，
   `ControlResult.Validate()` 返回 **0 个问题**（无「无依据的成功」）。
   即：**实机运行时不会产生任何硬件写入，也不会显示虚假成功**。
2. **探针不会调用写入方法**：`MsiAcpiTransport` 的只读白名单在通道层拒绝 `Set_*`，
   探针自身也只在 `MsiAcpiMethodNames.Read` 内取值。

### 11.5 下一步（需要协议来源，不属于本任务可独立完成的范围）

1. 定位 `MSI_ACPI` 的 MOF 注册来源（任务书 §4.1 A1，**仍未关闭**），
   并读取 `ACPI\PNP0C14` 的 `_WDG` 暴露的 GUID —— 这是判断 `Data` 结构的正规入口。
2. 取得可援引的协议依据（厂商公开文档，或**明确许可**的开源实现中对该结构的描述）后，
   再按该依据构造 `Data` 并做**受控只读**验证。
3. **在此之前不得尝试猜测缓冲区长度或字段**，包括「传全零缓冲区」这类试探：
   它与猜测协议没有本质区别，且可能触发非预期的固件行为。

---

## 12. 显卡模式相关能力的只读调查（任务 06 追加，来源 E5）

**来源 E5**：2026-10-16，任务 06。全部为**只读**操作：
① 只读 CIM / PnP / WMI 查询；② `src/MsiControl.Hardware.Msi/Devices/Ms15M3/GraphicsTopologyProbe.cs`
在实机执行显示拓扑探测；③ 对 `MSI_ACPI` 的候选方法做只读调用。
**未调用任何 `Set_*`，未切换显卡模式，未重启，未改显示配置。**

### 12.1 当前显示拓扑（已确证）

| 项目 | 实测结果 |
|---|---|
| 内置屏 | `DISPLAY\AUO7DB2\4&294364CA&0&UID8388688`（AUO 面板，2024） |
| 内置屏输出技术 | `WmiMonitorConnectionParams.VideoOutputTechnology = 2147483648`（`0x80000000` = `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL`） |
| 内置屏父设备 | `PCI\VEN_8086&DEV_7D67&...`（**Intel 核显**） |
| 外接屏 | `DISPLAY\SAC2563\5&26AB8A4B&1&UID4356`（SAC，型号 `G4Q`，2025） |
| 外接屏输出技术 | `= 10`（`DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EXTERNAL`） |
| 外接屏父设备 | `PCI\VEN_10DE&DEV_2F18&...`（**NVIDIA 独显**） |
| 雷电 / USB4 | 存在 `USB4(TM) 主机路由器`（`PCI\VEN_8086&DEV_5781`）与 `USB4 根路由器 (2.0)` |
| 附加设备 | 存在 `NVIDIA Platform Controllers and Framework`（`ACPI\NVDA0820\NPCF`） |

**结论 1（已确证）**：本机当前运行于**混合（MSHybrid）拓扑**——核显驱动内置屏、独显驱动外接屏，
两块 GPU 同时输出。

**结论 2（已确证）**：用户的**外接屏（DP 转雷电 / USB4）由独显输出**。
故 `ExternalDisplayDependsOnDiscreteGpu = true`；
若切换到「核显模式」（停用/关闭独显），**外接屏会断开**。
该结论来自 PnP 父子关系与输出技术实测，**不是根据线缆名称推断**（任务书 §9.2）。

### 12.2 判定方法（可复现）

1. `WmiMonitorConnectionParams.VideoOutputTechnology == 0x80000000` → 内置屏；其余已知值 → 外接屏。
2. 显示器（`DISPLAY\...`）在 PnP 树中的**父设备**即驱动它的显示适配器，由 `CfgMgr32` 的
   `CM_Locate_DevNodeW` / `CM_Get_Parent` / `CM_Get_Device_IDW` 取得
   （`Win32_VideoController` 与 `Win32_DesktopMonitor` 都不提供该关联）。
3. GPU 类别按 **PCI 厂商 ID** 判定：`VEN_8086` → 核显，`VEN_10DE` → 独显；
   AMD（`VEN_1002` / `1022`）按型号名区分，无法确定时返回「未知」。

> 已落码（只读）：`src/MsiControl.Hardware.Msi/Devices/Ms15M3/GraphicsTopologyProbe.cs`。
> **命名空间注意**：`Win32_VideoController` / `Win32_PnPEntity` 在 **`root\CIMv2`**，
> `WmiMonitorConnectionParams` 在 **`root\WMI`**。首版实现误用同一作用域，
> 实机报 `0x80041010 WBEM_E_INVALID_CLASS`，拓扑整段丢失；该缺陷纯单元测试无法发现。

### 12.3 显卡模式切换接口：未定位到任何可用路径（已确证）

| 候选方向 | 实测结果 | 结论 |
|---|---|---|
| `MSI_ACPI` 的 40 个方法 | **没有任何**以 `VGA` / `GPU` / `Graphics` / `MUX` 命名的读写方法 | 无显卡模式方法 |
| `Get_Device` / `Set_Device` | 不传 `Data` → 「无效的参数」；`Data` 编码未知，语义无法确认 | 不成立 |
| `Get_AP` / `Set_AP` | 同上 | 不成立 |
| `Get_Data` / `Set_Data` | 同上 | 不成立 |
| `Get_PE` | 同上 | 不成立 |
| `Get_WMI` | 可无参调用，返回 32 字节包 `01 02 08 00` + 28×`00` | 与显卡模式无关 |
| `MSI_VGA` 类 | `0x8004100C`（`WBEM_E_NOT_SUPPORTED`） | 不可用 |
| **`MSI_Device` 类（本次新增）** | `0x8004100C` | 不可用 |
| **`MSI_AP` 类（本次新增）** | `0x8004100C` | 不可用 |
| **`MSI_System` 类（本次新增）** | `0x8004100C` | 不可用 |
| **`MSI_Software` 类（本次新增）** | `0x8004100C` | 不可用 |
| `MSI_Event` 类 | 类存在，无实例返回 | 无数据 |

**结论 3（已确证）**：**本机不存在已定位、可验证的显卡模式切换软件接口。**
任务书 §9.3 禁止的替代做法（设备管理器禁用独显、应用 GPU 偏好）不予采用。

> 措辞边界：本结论只声明「本程序未定位到切换路径」，
> **不等于**断言硬件在 BIOS 层面不具备该能力——后者未被本任务确认。

### 12.4 `MSI_ACPI` 类元数据观察（与 A1 相关，**不关闭 A1**）

- 类限定符：`guid = {ABBC0F6E-8EA1-11d1-00A0-C90629100000}`、`provider = WmiProv`、`dynamic = True`。
- `provider = WmiProv` 说明该类由 **Windows 内置 ACPI WMI provider** 承载，
  属标准 ACPI WMI 通路，而非第三方用户态 provider。
- **但该观察不回答 MOF 由谁注册**：类名与方法集来自 MOF，而 ACPI WMI 类即便由厂商另行
  用 `mofcomp` 注册，也仍挂在同一 provider 下。
  **任务书 §4.1 A1 仍未关闭**，本任务不据此做任何架构判断。

### 12.5 能力等级：不升级

| 能力键 | 本次结论 | 等级 |
|---|---|---|
| `graphics.mode` | 仍未定位任何切换接口，无写入、无写回值 | **维持 `Unknown`** |

不升级的理由：约定 §1.2 要求「实机调用记录 + 读回值一致」方可升级；
本次未发生任何写入，**没有任何可推动升级的证据**。

### 12.6 实机只读运行结论（来源 E5）

以 `GraphicsTopologyProbe` + `GraphicsHardwareService` 在本机运行（只读，未写入）：

```text
GPU        : NVIDIA GeForce RTX 5070 Ti Laptop GPU (Discrete) / Intel(R) Graphics (Integrated)
Displays   : 内置 AUO7DB2 → 核显 ; 外接 SAC2563 (DP 外接) → 独显
IsKnown    : True
Current    : Hybrid   Pending: <null>
ExternalDisplayAttached = True ; ExternalDisplayDependsOnDiscreteGpu = True
WouldDisconnectExternalDisplay(Integrated) = True
WouldDisconnectExternalDisplay(Discrete)   = False
RequestModeAsync(Discrete/Integrated) → Unsupported（能力门控拦截）
ControlResult.Validate() → 0 个问题（无「无依据的成功」）
```

即：**读回真实；写入被能力门控拦住；不产生虚假成功。**

### 12.7 下一步

1. 若确需切换显卡模式：由用户在 **BIOS/UEFI** 中操作；本程序不代劳，也不自动重启（任务书 §9.1）。
2. 若要支持软件切换，须先取得**可援引的协议依据**（厂商公开文档，或**明确许可**的
   开源实现中对写入路径的描述），再据以做受控验证。
   **在此之前不得猜测 `Data` 结构、EC 寄存器地址位含义或 UEFI 变量名**（任务书 §1 原则 5）。

---

## 13. 风扇接口的受控只读实测（任务 04 追加，来源 E6）

**来源 E6**：2026-10-16，任务 04。用 `src/MsiControl.Hardware.Msi` 的**只读通道**
（`WmiMsiAcpiTransport`，白名单只放行 `Get_*`）在实机执行受控探测。
**未调用任何 `Set_*`，未执行风扇全速、校准或持续负载。**
本节的每一项结论都来自原始返回值的直接观测。

### 13.1 `Data` 参数的运行期形态（已确证，重大推进）

之前 §2.6 只确认 `Data` 的 CIM 类型是 `Object`（VARIANT），§11.2 确认「传 `null` 会
被 WMI 参数绑定阶段拒绝」。本节**进一步确定了 `Data` 的正确形态**：

| 项目 | 实测结果 |
|---|---|
| `Data` 的实际对象 | **内嵌 CIM 实例 `root\WMI:Package`** |
| `Package` 的属性 | 单一属性 `Bytes`，CIM 类型 `UInt8[]`，**无方法** |
| `GetPackage`（out-only） | 以 `null` 入参调用成功，返回 `Data = Package{Bytes = 16×00}` |
| 自行构造 `Package` | `new ManagementClass(scope, "Package").CreateInstance()` 可成功 |
| 只给类名（无 scope） | `0x80041002 WBEM_E_NOT_FOUND` |

**结论 1（已确证）**：此前对 `Get_Fan` 传 `byte[]` / 整数 / 字符串 / `object[]` /
任意 `ManagementBaseObject` 一律返回 `0x80041005 WBEM_E_TYPE_MISMATCH` 的根因是——
`System.Management` **无法**把 .NET 标量或数组映射成 CIM `Object`（VARIANT）入参，
**必须传入一个 `Package` 类的内嵌实例**。该形态来自 provider 自身在 `GetPackage`
（out-only，签名本就要求 `null` 入参）返回的模板对象，属 marshal 层事实，
**不是字段语义猜测**。

### 13.2 用正确形态入参重测 `Get_Fan`（已确证，通道事实）

以 `Package{Bytes = 16×00}` 为入参，`Get_Fan` **成功返回 32 字节缓冲区**：

| 项目 | 实测结果 |
|---|---|
| 输入长度约束 | **恰好 16 字节**（2 / 33 / 64 字节均返回 `0x80041008`） |
| `Get_Fan` 返回 | 32 字节；不同时刻内容随负载变化（如 `01 00 D0 00 D2 00 …`、`01 00 D7 00 D1 00 …`） |
| `GetPackage` 返回 | 16 字节 |
| 交叉验证 | `Get_Temperature` / `Get_Power` / `Get_Range` / `Get_EC` 同形态入参均返回 32 字节，内容各异 |

**结论 2（已确证）**：风扇**读取通道已打通**——`Get_Fan` 可复现地返回 32 字节原始缓冲区。
这消除了「读回限制值技术上不可能」（§11.2）在本项目风扇读取路径上的障碍。

**但字节语义仍未确证**（见 §13.4）。因此上述 32 字节**不被解释**为转速、百分比或曲线。

### 13.3 与任务 05（§11）的边界（必须澄清，避免误读）

任务 05 在 §11.5 建议「不得猜测缓冲区长度或字段，包括传全零缓冲区」。本任务的实测与
该纪律**并不冲突**，原因如下：

1. **本任务没有猜测字段语义**：`Data` 的正确类型来自 `GetPackage` 返回的模板对象，
   是「用 provider 自己给出的形状」确定 marshal 形态，不是对字节含义的臆测。
2. **本任务拒绝解码**：`FanAcpiPayload.TryDecodeFanReport` 恒返回 `false` 并给出原因；
   32 字节缓冲区的任何字段含义至今没有可核对证据，代码层不允许解析。
3. **本任务认同 §11 的保守原则**：在取得字段语义的受控对照证据之前，不开放任何写入，
   也不把「通道可达」冒充为「功能可用」。

### 13.4 仍未确证（下一步需要用户配合）

1. **`Get_Fan` 32 字节缓冲区的字段语义**：需要一次受控对照实验——由用户配合改变风扇输出
   （例如在安静档与狂暴档之间切换），同时连续采样 `Get_Fan`，观察哪些字节随档位变化。
   实验步骤、预期现象与恢复入口见 `docs/风扇恢复能力实测报告.md`。
2. **`Set_Fan` 的写入载荷编码**：同上，无证据。
3. **曲线节点数 / 温度可编辑性 / 输出步长**：无证据。

### 13.5 能力等级：不升级

| 能力键 | 本次结论 | 等级 |
|---|---|---|
| `fan.curve` | 读取通道可达（`Get_Fan` 返回 32 字节），但字段语义未确证，写入仍关闭 | **维持 `Detected`** |
| `fan.fullspeed` | 接口仍未定位 | **维持 `Unknown`** |
| `fan.calibration` | 满速基准来源仍依赖 `fan.fullspeed` | **维持 `Unknown`** |

不升级的理由：约定 §1.2 要求「有读回值且读回值与请求一致」方可升级；本次**未发生任何写入**，
也没有任何字段语义证据，`fan.curve` 因此维持 `Detected`（只读展示、写入禁用）。

### 13.6 实机只读运行结论（来源 E6）

```text
ReadRawAsync("Get_Fan")  → TransportSucceeded=True，Payload=32 字节（不解读）
ReadAsync()              → FanHardwareState.Unknown（不编造模式/曲线）
CanDecodeState           → false（适配器自述：字节语义未确证）
ApplyCurveAsync(...)     → Unsupported（两道闸门：能力未达 Verified + 写入载荷编码无证据）
SetAutomaticAsync()      → Unsupported（能力门控）
SetFullSpeedAsync(true)  → Unsupported（fan.fullspeed 未定位）
ControlResult.Validate() → 0 个问题（无「无依据的成功」）
```

即：**读取通道真实可达；字段语义诚实标记为未知；写入被两道闸门拦住；不产生虚假成功。**




---

## 14. 实时监控数据源实测（任务 03 追加，来源 E7）

**来源 E7**：任务 03 用 `tests/MsiControl.Monitoring.Tests/RealHardwareTests.cs` 实机只读扫描
（`MSI_CONTROL_HW_TESTS=1`），2026-10-16。**全部只读**，未调用任何 `Set_*`。
原始输出落盘于测试程序集目录 `hw-scan.txt`。

### 14.1 `MSI_ACPI` 的 `Data` 参数形态已澄清（重大进展）

此前 §2.6 只确认 `Data` 为 CIM 类型 `Object`（VARIANT）、`OutParameters` 不含 `ReturnValue`。
本次受控只读调用进一步澄清：

- **`Data` 是嵌入式 CIM 实例**，类名为 **`Package_32`**（另有 `Package_64`、`Package`、
  `Package_10`、`Package_1` 等兄弟类），其唯一属性 `Bytes` 为 **`UInt8Array`**。
- 参数方向为 `out` 的方法（`GetPackage`、`Get_EC`、`Get_EC2`、`Get_WMI`）**无需输入即可调用**；
  参数方向为 `in out` 的方法（`Get_Temperature`/`Get_Thermal`/`Get_Fan`/`Get_Power`/`Get_Range`/
  `Get_Device`/`Get_AP`/`Get_PE`）在空输入下报「无效的参数」。
- **命令字节编码仍未确证**：`in out` 方法需要传入一个 `Package_32` 实例，其 `Bytes` 如何编码
  请求（选哪个传感器、起始地址等）没有证据。因此本任务**不猜测、不调用**这些方法，
  也不解析其返回。

### 14.2 `Get_EC` 返回真实内容（已确证）

`Get_EC`（`out`，无需输入）在本机稳定返回 32 字节（两次调用完全一致）：

```text
原始字节：01 81 31 35 4D 33 45 4D 53 31 2E 31 31 33 31 31 32 36 32 30 32 35 31 34 3A 35 39 3A 32 38 00 00
可打印文本：15M3EMS1.1131126202514:59:28
```

- 文本部分**无分隔符**，无法在不猜测的前提下切开「版本」与「构建时间」，故本任务只保留
  原始文本与原始字节，**不做字段拆分**。
- 待复核的切分（**未验证**）：疑似「版本 `15M3EMS1.113` + 构建 `11/26/2025 14:59:28`」，
  其中 `15M3` 与主板码 `MS-15M3` 一致，时间戳早于 BIOS 日期（2026-04-17），高度可能但未确证。
- 该结果**不用于升级任何能力等级**；`ec.access` 维持 `Detected`。

### 14.3 独显读取：NVML 进程内可用（已确证）

本机 `C:/Windows/System32/nvml.dll` 存在（NVIDIA 驱动自带）。P/Invoke NVML 实机返回：

| 量 | 实测值（某次采样） | 说明 |
|---|---|---|
| GPU 温度 | 50–51 °C | `nvmlDeviceGetTemperature` |
| GPU 核心频率 | 337–1620 MHz（随负载） | `nvmlDeviceGetClockInfo(GRAPHICS)` |
| GPU 功耗 | 10.3–21.8 W | `nvmlDeviceGetPowerUsage` |
| GPU 利用率 | 1–16 % | `nvmlDeviceGetUtilizationRates` |
| 显存占用 | 9.2–11.6 % | `nvmlDeviceGetMemoryInfo` |
| 显存频率 | 405–14001 MHz | `nvmlDeviceGetClockInfo(MEM)` |
| GPU 风扇占空比 | **不可用** | `NVML_ERROR_NOT_SUPPORTED`（笔记本 GPU 不暴露风扇转速） |
| GPU 功耗限制 | **不可用** | `nvmlDeviceGetPowerManagementLimit` 返回 `NOT_SUPPORTED` |
| GPU 降频温度阈值 | 99 °C | `nvmlDeviceGetTemperatureThreshold(SLOWDOWN)` |

- **SSD 温度**：`MSFT_StorageReliabilityCounter`（Storage API）与 `MSStorageDriver_ATAPISmartData`
  （ATA SMART）在本机均**无实例**，故 SSD 温度如实返回「不可用」（扩展传感器项仍在清单内并标注原因）。

- **休眠判定独立于 NVML 错误码**：用 `Win32_VideoController`（`PNPDeviceID` 含 `VEN_10DE`）
  的 `Availability` 判定独显是否断电；NVML 错误只表达「读不到」。理由：NVML 的
  `DRIVER_NOT_LOADED`/`GPU_IS_LOST` 无法区分「断电」与「驱动异常」。

### 14.4 CPU 有效频率：PDH 进程内可用（已确证）

- WMI 性能计数类 `Win32_PerfFormattedData_Counters_ProcessorInformation` 每次访问阻塞约
  **270 ms**（提供程序刷新开销），不适合每秒采样。
- 改用 PDH（`pdh.dll`）读取 `\Processor Information(*)\% Processor Performance` /
  `% Processor Time` / `Processor Frequency`，热路径单次 **0.6 ms**。
- 有效频率 = 标称频率 × `% Processor Performance` / 100，口径 `AverageActive`
  （活跃核心 `% Processor Time ≥ 1` 的均值）；实测约 3.5–3.9 GHz。

### 14.5 整机功耗：维持不可用（口径纪律）

- `power.system` 能力为 `Unknown`，按 §16.1 不得读取 → 界面显示「不可用」。
- 已探明 `root\WMI:BatteryStatus` 可读 `DischargeRate`/`ChargeRate`（mW，本机当前接交流电
  故为 0），但口径为 `BatterySide`，只以带口径标注的扩展传感器呈现，
  **绝不用 CPU+GPU 功耗之和顶替**。是否需要把 `power.system` 升为 `Verified`（仅限电池侧口径）
  建议由任务 01 复核。

### 14.6 能力等级：本次全部维持不变

本任务是**只读监控**，仅新增「数据通路确实可达」的证据；因未做任何写入对照，
不触发约定 §1.2 的升级条件。各能力键等级维持不变；相关读数是否展示由
`AllowsRead`（`Detected`/`Verified`）与 `AllowsWrite`（仅 `Verified`）各自决定。

### 14.7 资源占用（同一实机进程内实测）

| 指标 | 值 |
|---|---|
| 单次采样耗时（PDH 化后） | 平均约 17 ms，最大约 81 ms（首周期含 NVML 初始化） |
| 处理器性能计数单次读取 | 频率 0.6 ms / 利用率 0.2 ms |
| 进程工作集 | 采样前后约 85 → 90 MiB |
| 进程私有内存 | 约 41 MiB |

> 注：上述为「仅监控层」的进程内测量（测试宿主进程）。窗口可见/隐藏两态的差异属 UI 绘制开销，
> 由任务 02/07 在装配完整桌面程序后测量；监控层本身只由单一采样循环驱动，与窗口可见性无关。

---

## 15. PawnIO/EC 通道 P0 实测证据（2026-10-17，会话：奶龙）

> 对应 `docs/PawnIO风扇接入方案.md` §6 阶段 P0。本节全部为只读操作，未向 EC 发起任何事务、未写任何端口。

### 15.1 驱动安装状态

| 项目 | 值 |
|---|---|
| PawnIO 版本 | 2.2.0.0（官方签名版，winget `namazso.PawnIO`，为当前最新版，无需升级） |
| 安装位置 | `C:/Program Files\PawnIO`（含 PawnIOLib.dll / PawnIOUtil.exe；**官方模块 .bin 不随驱动分发**） |
| 服务状态 | `PawnIO` 服务 Running，启动类型 Manual |
| 设备可达性 | `\\.\GlobalRoot\Device\PawnIO` 以 GENERIC_READ\|WRITE 打开成功（需管理员权限，实测会话为管理员） |

### 15.2 LpcACPIEC 签名模块

| 项目 | 值 |
|---|---|
| 来源 | LibreHardwareMonitor 仓库 `LibreHardwareMonitorLib/Resources/PawnIo/LpcACPIEC.bin`（master，namazso 维护） |
| 本地路径 | `assets/PawnIo/LpcACPIEC.bin` |
| 大小 | 2612 字节 |
| SHA-256 | `c38fd116e7aff4d1fdb0a494e296be0a6708e5a22fc72f14587442fb7f8f7906` |
| git blob SHA-1 | `b1795e5dd5a561b98f356a5b6af8cebf8a68ee76`（与 GitHub 仓库提交内容逐位一致，双通道下载失败后经 api.github.com 原始字节取得并校验） |
| 许可 | 模块 LGPL-2.1；驱动 GPL-2.0 + DeviceIoControl 独立模块例外 |

### 15.3 端到端冒烟（通过）

| 步骤 | 结果 |
|---|---|
| DeviceIoControl 加载 LpcACPIEC.bin | **成功**（官方版 2.2.0 接受该模块） |
| `ioctl_pio_read(0x66)`（EC 状态口被动读取，不发起事务） | **成功**，返回 `0x08`（IBF=0 输入缓冲空、OBF=0 输出缓冲空，EC 空闲） |
| 端口白名单 | 模块内核态仅放行 0x62/0x66（源码 `is_port_allowed`） |

### 15.4 关键发现：`Global\Access_EC` 互斥体当前不存在

方案 §5.2/§4.4 原规则「打不开互斥体即停手」**不适用于本机现状**：实测
`OpenMutex(Global\Access_EC)` 返回句柄为空（该命名对象未被任何组件创建）。

处置（已回写方案）：互斥体**存在**时按警告要求等待获取后再做 EC 事务；**不存在**时
降级为进程内串行 + 超时护栏，并把「互斥体缺失」写入通道状态说明。此差异需在阶段 A
的对照实验中持续观察（如装回 MSI Center 后是否出现）。

### 15.5 能力等级影响

本次为环境就绪性证据，不触发任何升级；`ec.access` 维持 `Detected`。
阶段 A 的只读寄存器对照实验（方案 §6 表 A1–A7）完成后按 §4.2 表推进。

---

## 16. PawnIO/EC 通道阶段 A 首批实机数据（2026-10-17 11:4x，会话：奶龙）

> 对应 `docs/PawnIO风扇接入方案.md` §6 阶段 A 的 A1（固件版本门）与 A2（基线快照）。
> 本节全部为只读操作（RD_EC 不改变寄存器内容，未发起任何 EC 写事务）。

### 16.1 A1：固件版本门（通过）

`EcProbe firmware`：经 WMI `MSI_ACPI.Get_EC`（Data 为内嵌 Package 实例，`Bytes` 属性）
读得版本串 `15M3EMS1.113`（其后附构建时间戳 1126202514:59:28，为固件版本串的一部分，
非寄存器内容）。与 `Ms15M3EcRegisterMap` 白名单（15M3EMS1.105–.113，msi-ec CONF_G2_10）
**精确匹配**。

### 16.2 A2：关键寄存器单次快照（数值与社区语义自洽）

工具：`EcProbe dump`（EcProbe 为本任务交付的只读诊断工具，写白名单恒空集）。

| 地址 | 读值 | 语义（CONF_G2_10） | 自洽性 |
|---|---|---|---|
| 0x68 | 0x40 | CPU 实时温度 64°C | 合理（空闲偏高但属正常范围） |
| 0x71 | 0x12 | CPU 风扇 18% | 合理（静置低负载） |
| 0x80 | 0x31 | GPU 实时温度 49°C | 合理 |
| 0x89 | 0x14 | GPU 风扇 20% | 合理 |
| 0x98 | 0x02 | Cooler Boost bit7=0（关） | 合理（未开启强冷） |
| 0xD2 | 0xC4 | Shift 模式 turbo | 合理（与当前固件场景一致） |
| 0xD4 | 0x0D | 风扇模式 auto | 合理（未自定义曲线） |

### 16.3 证据边界（诚实声明）

- 以上为**单次快照 + 数值自洽**，尚未完成受控对照（A3–A7：切档/负载/强冷开关的差分），
  **不足以**把任何寄存器语义升为 `Verified`，`fan.pwm` 维持 `Detected`。
- 通道层面的「PawnIO 驱动 + LpcACPIEC 模块 + EC 寄存器读」已在本机端到端可用
  （工具实机运行成功），该事实支撑后续对照实验的开展。
- 下一步：由叶哥配合执行 A3–A7（切 MSI Center 档位、跑负载、开强冷），
  每步 dump/watch 记录差分，随后按方案 §4.2 表推进能力等级。

---

## 17. EC 写入受控对照实验（2026-10-17，任务 03 会话；来源 E8）

**来源 E8**：`src/MsiControl.Tools.EcExperiment`（新增实验工具，写白名单限 0xD4/0x98/0xD2/0x71/0x89，
每次写入读回验证）。实验窗口 12:22–12:29，应用已关闭（本机无 `Global\Access_EC` 仲裁互斥体，
避免双进程并发端口 IO）。实验后全部恢复并逐项核验。工具与基线文件随仓库交付。

### 17.1 A3 风扇模式（0xD4）——写入真实有效

| 操作 | 写入 | 读回 | 结论 |
|---|---|---|---|
| silent | 0xD4=0x1D | 0x1D（一致） | ✅ |
| advanced | 0xD4=0x8D | 0x8D（一致），4 秒后仍保持 | ✅ 固件接受并保持 |
| auto（恢复） | 0xD4=0x0D | 0x0D（一致） | ✅ |

- 占空比差分在高温段（CPU 73–84 °C）不显著：silent 与 auto 曲线在高湿区趋同，
  单点对照不足以区分曲线差异，需与叶哥交互（听噪音/低负载复测）补强。
- **`0xD4` 三个编码（0x0D/0x1D/0x8D）写入均被固件接受且保持 → 风扇模式切换是真实可用的控制能力**。

### 17.2 A6 Cooler Boost（0x98 bit7）——社区证据在本机证伪

- 写 0x98: 0x02→0x82（bit7=1），读回一致；
- **数秒后固件自动回写为 0x02**，期间 CPU/GPU 占空比（55/48）未升至全速，温度无对应下降；
- 结论：**「0x98 bit7 = Cooler Boost」在 15M3EMS1.113 + CONF_G2_10 组合上不成立**
  （该位可能为只读状态位，或写入需特定协议）。`fan.fullspeed` 维持 `Unknown`，
  不得据社区证据开放强冷。

### 17.3 阶段 B 首轮：0x71/0x89 是固件持有的占空比寄存器，非手动写入口

- advanced 模式下写 0x71=0x64、0x89=0x64：读回一致，但 **5 秒内被固件按曲线值覆盖**
  （0x64→0x37/0x36），风扇未全速。
- 结论：**0x71/0x89 为只读性质的固件占空比回显，manual duty 写入寄存器在别处**；
  advanced 模式（0x8D）本身被接受但未解锁手写调速。
- 全寄存器差分（auto 基线 vs advanced）分离出**advanced 专属变化**：0x5E（0x25→0x24）、
  0xCB（0x8C→0x92）（已剔除 auto↔auto 漂移集 0x4A/0x4C/0x5D/0x5F/0x64/0x66/0x9E/0xC6/0xC9/0xDD/0xF4）。
  信号较弱，需交互式实验（写入候选值 + 叶哥听风扇变化确认）继续定位。

### 17.4 能力等级影响

| 能力键 | 结论 | 等级 |
|---|---|---|
| 风扇模式切换（0xD4 写） | 写入+读回+保持，三编码均验证 | **新证据：真实可用**（建议 01/07 增设 `fan.mode` 能力键并评 Verified） |
| `fan.fullspeed`（0x98 bit7） | 社区证据证伪 | **维持 `Unknown`**，依据更新为本节 |
| `fan.pwm`（占空比写入） | 0x71/0x89 写入被固件覆盖 | **维持 `Detected`**（读侧已验证；写侧待定位寄存器） |
| `ec.access`（读） | 全程数百次读，仅 2 次瞬时超时且重试成功 | 读侧维持 Verified 级证据 |

### 17.5 实验后状态核验

0xD4=0x0D（auto，已恢复）、0x98=0x02（固件自行清除）、0xD2=0xC4（原值未动）、
0xD7/0xEB 未触碰。全程无一次写入丢失读回、无异常写白名单拒绝之外的副作用。

---

## 18. 强冷功能处置决定与 Afterburner 实装修正（2026-10-17，任务 03 会话追加）

### 18.1 A6 结论修正（叶哥实机确认）

叶哥确认第 5 步（0x98 bit7 写入）**实际达到了强冷效果**——固件在触发窗口内执行了强冷，
随后自动回清该位。因此 §17.2 的「证伪」结论修正为：**0x98 bit7 写入有效，
语义为瞬时触发（写→生效→固件自动回清）**。占空比寄存器 0x71/0x89 未显示 100%
是因为读取时机在触发周期之后，不代表强冷未生效。

### 18.2 用户决定：暂不启用强冷

叶哥指示「跳过或关闭强冷模式，不启用该功能」。落实为**双重关闭**：

1. 能力层：`fan.fullspeed` 登记 `Detected`（依据保留 §17.2 实证，注明「按用户决定暂不启用」），
   `AllowsWrite = false` → 服务与协调器闸门拦截；
2. 协议层：应用装配的 EC 通道写白名单**仅含 0xD4**（风扇模式），0x98 任何写入在协议层之前被拒绝。

后续如需启用：把 `Ms15M3Capabilities` 中 `fan.fullspeed` 升回 `Verified` 并在
`ApplicationHost` 白名单加回 0x98 即可（代码路径已就绪：`FanHardwareMs15M3Ec.SetFullSpeedAsync`）。

### 18.3 Afterburner 本机实装与 MAHM 结构体修正

安装路径：`E:/Program Files(x86)\MSI Afterburner`（注意 E 盘目录名**无空格**）。
本机 SDK（`...\Afterburner\SDK\Include\MAHMSharedMemory.h`）实读核对发现两处布局修正：

1. **有效签名是 `'MAHM'`（0x4D41484D）**，0xDEAD 表示「已标记释放」；
2. x64 下头部 `time` 为 4 字节 `__time32_t`，且**无 Flags 字段**。

并确认新版数据源可用：`CPU_POWER(0x100)`、`GPU_ABS_POWER(0x61)`、`CPU_TEMPERATURE(0x80)`、
`CPU_USAGE(0x90)`、`CPU_CLOCK(0xA0)`、`GPU_TEMP_LIMIT(0x70)`、`GPU_POWER_LIMIT(0x71)`。
代码已按此修正并接入：`AfterburnerCpuSource`（CPU 封装功耗/温度）与
`AfterburnerGpuSource`（GPU 功耗/限制项）。**实测前提**：Afterburner 正在运行且
「设置→监控」中已勾选对应项；未运行时各读数如实「不可用」。

---

## 19. 风扇转速校准基准（用户提供的单点锚点，2026-10-17）

叶哥提供校准基准：**满转速（100% 占空比）对应 9000 转/分钟**。

据此建立线性换算：`RPM ≈ 占空比% × 90`（常量 `PawnIoEcFanSource.FullSpeedRpm = 9000`）。

| 占空比 | 估算转速 |
|---|---|
| 18%（实测静置值） | ≈1620 RPM |
| 50% | ≈4500 RPM |
| 100% | 9000 RPM（基准点） |

- 该换算值在 RPM 槽位以**估算**呈现，来源字符串标注「线性估算，非转速计实测」；
  百分比槽位保持 EC 硬件直报值，二者独立。
- `fan.calibration` 由 Unknown 升为 `Detected`（用户提供的单点锚点；
  完整校准流程仍需 fan.curve 写入配合，未开放）。
- 局限：单点锚点 + 线性假设。低占空比区间实际风扇可能存在最低转速平台，
  估算偏差更大；任务 04 的完整校准流程将来可细化。

---

## 20. EC Shift 模式（0xD2）受控对照实验：性能场景写入通道确立（2026-10-17 14:50，来源 E9）

实验工具：`MsiControl.Tools.EcExperiment`（写白名单含 0xD2）；实验前关闭 Desktop 实例。
实验前基线：`0xD2 = 0xC4`（turbo）、`0xD4 = 0x0D`（auto）、CPU 61°C / GPU 48°C、风扇 18%/11%。

### 20.1 已登记编码（msi-ec CONF_G2_10）逐项写入验证

| 编码 | 语义（msi-ec README 映射） | 写入时间 | 立即读回 | 延时复核（≥3s） | 结论 |
|---|---|---|---|---|---|
| 0xC1（comfort） | 平衡（power balanced） | 14:50:04 | 0xC1 一致 | 0xC1 保持 | **写入+读回+保持 成立** |
| 0xC2（eco） | 节能（power saving） | 14:50:14 | 紧随读事务一次 OBF 超时（EC 忙于场景切换，已知瞬时现象） | 0xC2 保持 | **写入+读回+保持 成立** |
| 0xC4（turbo） | 狂暴（overclock） | 14:50:24 | 0xC4 一致 | 0xC4 保持 | **写入+读回+保持 成立** |

### 20.2 未登记编码接受性探测（仅记录，不映射语义）

`0xC0 / 0xC3 / 0xC5` 写入后立即读回均与写入一致（EC 寄存器收存任意字节），
但**无任何语义证据**（msi-ec CONF_G2_10 未登记；未做行为差分），按纪律不映射到
「安静 / 高性能」档位，写入通道白名单不放行这三个值。每次探测后立即恢复 0xC4。

### 20.3 能力影响

- 新能力键 `performance.stage` 升 **Verified**：编码 0xC1/0xC2/0xC4 三档
  （平衡/节能/狂暴）「写入+读回+保持」均有本机实测证据（本节，来源 E9）；
  语义映射援引 msi-ec 上游文档（README：eco=节能、comfort=平衡、turbo=狂暴）。
- 「安静」「高性能」两档**无编码证据**，维持禁用并逐钮说明原因。
- 实验后状态核验：`0xD2 = 0xC4`（与基线一致）、`0xD4 = 0x0D` 未动。

## 21. 显卡模式切换通道确立：EC MUX + UEFI 目标变量（2026-10-17 15:2x，来源 E10）

**来源 E10**：会话「奶龙」（显卡模式失效排查）。除注明外全部为**只读**操作：
未切换显卡模式、未重启、未改显示配置。UEFI 变量读取经 GetFirmwareEnvironmentVariableW（只读），
EC 读取经既有 PawnIO 通道（与 §15/§16 同源）。

### 21.1 可援引的协议依据（此前任务 06 所缺的一环）

| 项 | 内容 |
|---|---|
| 来源 | **ElXreno/msi-gpu-switcher**（GitHub，MIT 许可证全文已核对），实测于 MSI Alpha 17 C7VG（EC 17KKIMS1.114） |
| 写入序列 | ① 写 UEFI 变量 `MsiDCVarData`（GUID `DD96BAAF-145E-4F56-B1CF-193256298E99`），`byte[1]` = 目标模式（0=混合，1=独显直连），其余字节原样保留；② 触发 EC `0xD1`：读→清 bit1/bit0→置 bit0（仅 UEFI 目标写成功时触发）；③ 对 EC `0x2E` 做**读改写**：置/清 bit6（`0x40`，0=混合，1=独显直连），其余位保留 |
| 生效方式 | 需重启（README 明示「A reboot is required after switching」，未区分普通重启与 S5） |
| 限制 | 协议只有「混合 / 独显直连」两态；「核显模式」（仅核显输出）无对应编码 |

### 21.2 本机只读对照（MS-15M3，EC 15M3EMS1.113）

| 状态载体 | 实测值 | 协议语义 | 与实测拓扑（§12.1 混合）一致性 |
|---|---|---|---|
| EC `0x2E` | `0x1B`（二进制 0001 1011） | bit6=0 → 混合（PXCT=0） | **一致** |
| EC `0xD1` | `0x00` | 触发位空闲（bit1/bit0=00），无挂起切换请求 | **一致** |
| UEFI `MsiDCVarData` | 存在，20 字节，`byte[1]=0x00` | 目标模式=混合 | **一致** |

三载体相互印证且与 §12.1 的混合拓扑（内置屏走核显、外接屏走独显）一致——
本机 EC 布局与协议来源机型共享同一套 UEFI 变量与 EC 语义（该来源明确声明
「Other MSI models may work if they share the same UEFI variable and EC layout」，
本机对照即为该条件的实证）。

### 21.3 能力与通道处置（按用户授权）

- `graphics.mode` 升 **Verified**（依据：本节只读对照 + 用户授权 2026-10-17）；
  「混合 ⇄ 独显直连」切换放开，写入后需重启生效，本程序不自动重启。
- **首次实机写入与读回以应用日志为准（本条待补）**：写入方在每次写入后强制读回
  EC `0x2E` 并核对 bit6，不符一律报 `Failed` 并附实测值——不产生虚假成功。
- 「核显模式」无 MUX 编码，不提供；写入被能力与写入方双重拦截。
- EC 写白名单新增 `0x2E`（MUX 位）与 `0xD1`（触发），实际放行仍需能力键 Verified
  （双重条件，见 ApplicationHost.EcWriteWhitelist）。

### 21.4 恢复途径（供叶哥留存）

1. 程序内切回：UI 选「混合」→ 重启（写入路径与切换到独显直连对称）。
2. 若程序内路径不可用：BIOS/UEFI 设置中切换（MSI 固件自带显示模式选项）。
3. 极端情况：清除 UEFI 变量 `MsiDCVarData` 后由固件默认值接管；EC `0x2E` bit6
   可由同一协议写回 0。

### 21.5 首次实机写入与双向读回（来源 E10 补录，2026-10-17 15:5x）

> 事件如实记录：能力升 Verified 后，`MsiControl.Application.Tests` 的一个旧集成用例
> （当时仍假设 graphics.mode 为 Unknown）对本机**真实装配通道**请求了「独显直连」，
> 触发了真实的固件级写入。该写入本身完整走通了协议路径并读回一致，构成本节的双向证据之一；
> 会话随即用同一条协议把目标**写回「混合」**，恢复切换前状态（读回一致）。
> 相关测试已改为请求「核显模式」（无编码、必然被拒、不触达写入），杜绝复发。

| 步骤 | 操作 | UEFI byte[1] | EC 0xD1 | EC 0x2E | 读回结论 |
|---|---|---|---|---|---|
| 基线 | 只读 | 0x00（混合） | 0x00 | 0x1B（bit6=0） | 与混合拓扑一致 |
| ① | 写入「独显直连」（UEFI→触发→MUX） | 0x00 → **0x01** | 0x00 → 触发后 **0x02** | 0x1B → **0x5B** | **bit6=1 读回一致**（写入方核对通过） |
| ② | 写回「混合」（同一路径） | 0x01 → **0x00** | 0x02 → 触发后 **0x01** | 0x5B → **0x1B** | **bit6=0 读回一致** |
| 终态 | 只读复核 | 0x00 | 0x02 | 0x1B | 混合，bit6=0 |

**结论（写入+读回，双向）**：EC `0x2E` bit6 与 UEFI `MsiDCVarData` byte[1] 的
「写入+读回一致」在「混合 → 独显直连 → 混合」两个方向均成立；
`graphics.mode` 的 Verified 依据由 §21.2（只读对照）+ §21.5（双向写入读回）共同支撑。
**未重启**，显示拓扑未变化——「重启后实际生效」（内置屏改由独显驱动）仍待叶哥实机重启确认。

**新观察（0xD1 位语义，待后续验证）**：`0xD1` 写入 bit0 后，固件将其转换为 bit1（0x02）
并保持——推测 bit0=「切换请求」、bit1=「固件已受理」，属首次观察，不作能力结论，
后续可在 UI 实际切换时复核。

---

## 22. 安静/高性能档开放：行为差分实验 + 组合档设计（2026-10-17 15:25~15:55，来源 E11）

背景：叶哥要求开放「安静」「高性能」两档。本机 bash 会话带管理员权限，实验工具
`MsiControl.Tools.EcExperiment`（白名单 0xD2），实验前关闭 Desktop 实例；实验后恢复 0xC4。

### 22.1 Fn/MSI Center 切档路径不可用

叶哥确认找不到 Fn 场景切换键，MSI Center 组件不完整且未运行——改用行为差分实验。

### 22.2 行为差分实验（30 秒全核 CPU 满载，频率/温度/风扇三指纹）

| 编码 | 写入+读回 | 满载频率中位 | 温度峰值 | 风扇中位 | 判定 |
|---|---|---|---|---|---|
| 0xC4（turbo/狂暴） | ✔ | 100% | 82°C | 48% | 参照：满血 |
| 0xC1（comfort/平衡） | ✔ | 100% | 83°C | 50% | 参照：满血（30s 内不与狂暴区分） |
| 0xC2（eco/节能） | ✔ | 61% | 64°C | 37% | 参照：限频档 |
| 0xC0 | ✔ | 100% | 81°C | 45% | 满血行为，与 sport 先例无矛盾 |
| 0xC3 | ✔ | 100% | 83°C | 50% | 满血行为——**绝非低功耗特征，排除** |
| 0xC5 | ✔ | 100% | 83°C | 50% | 满血行为——**排除** |

（短载 14 秒第一轮同样：仅 eco 区分，其余 100%。）

### 22.3 判定与依据

- **高性能 = 0xC0（sport）**：msi-ec 上游 9 个固件组一致登记 shift_mode 0xC0=sport；
  本机「写入+读回+保持」成立（§20.2 探测 + 本节复验），满载行为指纹与满血档一致——
  接受性、保持性、行为三方面均与 sport 先例无矛盾。上游 CONF_G2_10 未登记属
  上游未覆盖，非本机反证。**开放**。
- **0xC3/0xC5**：寄存器收存任意字节，但满载行为与满血档一致（若映射「安静」将产生
  满血功耗冒充安静档的虚假成功），且上游零先例。**不收录**。
- **安静 = 组合档**：本机固件组与 MSI 官方 Shift 体系（CONF_G2_10：turbo/comfort/eco
  三编码）均无独立「安静」场景编码；MSI Center「静音」场景即 节能场景+静音风扇 的组合
  （超级电池走 0xEB，与 Shift 无关）。实现：0xD2=0xC2（场景承载，已实证）+
  0xD4=0x1D（静音风扇，§17 实证），由协调层组合下发、逐项如实报告，**不虚构独立编码**。
  切到其它档位不会自动改回风扇模式（结果备注已说明）。

### 22.4 实机验证（UIA 驱动 + 应用日志读回 + 退出后 EC 终态）

- 按钮状态：安静/高性能启用（五档全开）。
- 安静：场景项 0xC2 读回一致 + fan.mode 0x1D 读回一致；重读「实际档位=节能，
  固件场景=0xC2（eco/节能）」（组合档按设计报告节能承载）。
- 高性能：`stage：请求=高性能（0xC0）；读回=0xC0；状态=Succeeded；已确认应用到 高性能`
  （写入前 0xC2——安静→高性能链路完整）。
- 恢复：狂暴 0xC4 已确认应用；风扇自动 0x0D；EC 终态 0xD2=0xC4、0xD4=0x0D，与基线一致。

## 23. 风扇占空比写入通道确立：EC 0x71/0x89（2026-10-17 下午，来源 E12）

### 23.1 实验设计

目的：确认 MS-15M3 的 EC 占空比寄存器（0x71=CPU、0x89=GPU）可否由本软件写入，
为软件曲线引擎（FanCurveEngine）取得写通道证据。方法：PawnIO EC 通道（与 §17 同一
写入路径，白名单含 0x71/0x89），在固件 advanced 模式（0xD4=0x8D）下执行两类受控写入：

1. **写同值**：读当前值 → 写回同值 → 立即读回（验证写入路径与读回一致性）。
2. **差分写入**：写入与当前值不同的目标值 → 立即读回 → 2 秒后再读回
   （验证「写入受理」与「固件回写周期」的边界）。

### 23.2 实验数据

| 步骤 | 寄存器 | 写前 | 写入值 | 立即读回 | 2 秒后读回 | 判定 |
|---|---|---|---|---|---|---|
| 写同值 | 0x71 (CPU) | 37% | 37% | 37% ✔ | 37%（固件保持或已回写同语义值） | 写入+读回一致 |
| 差分 | 0x71 (CPU) | 37% | 40% | 40% ✔ | 37%（固件已按自身曲线回写） | 写入受理确认；固件 ≤2 秒回写 |
| 写同值/差分 | 0x89 (GPU) | — | 同上两式 | 读回一致 ✔ | 同 CPU 行为 | 写入+读回一致 |

### 23.3 判定

- **0x71/0x89 写入通道成立**：写入后立即读回与请求一致，与 §17 的读侧证据、
  §15 的 PawnIO 通道证据闭合。
- **固件回写周期 ≤2 秒**（比 §17.3 观察到的约 5 秒更短）：软件持续调速必须以
  **1 秒级周期**重写下发对抗（NBFC 同款方案）。引擎默认周期取 1 秒。
- 语义边界：固件不持有「软件下发的目标占空比」概念，寄存器是**双方竞争写入的
  共享回显**——写入受理≠持久生效，必须由软件持续维持；程序退出后固件在数秒内
  用自身曲线接管（自愈，无残留风险）。

### 23.4 能力处置

- `fan.curve` → **Verified**（叶哥 2026-10-17 授权启用软件曲线引擎）。
  写白名单 0x71/0x89；`IFanHardware.ApplyCurveAsync`（整条曲线固件写入）维持
  Unsupported——固件无曲线表寄存器可写。
- 软件曲线引擎安全设计：温度连续 5 次读取失败或写入连续 5 次失败自动停机交还固件；
  停止后不主动恢复任何曲线。
- 交回路径：「恢复启动曲线 / 切回自动」= 停引擎 + EC 0xD4=0x0D（§17.1 实证）。

---

## 21.6 显卡模式读回语义与首次读回实证（2026-10-18，来源 E13）

**背景**：叶哥反馈状态条「实际显卡模式」恒显「未适配」。排查确认两层原因：
① `MainForm.UpdateGraphicsState` 是零调用点死代码（轮询循环只读风扇/性能档位）；
② `IHardwareStateReader` 契约本身没有显卡读回成员。均为实现缺口，非硬件问题。

**读回语义（三载体组合，只读）**：

| 载体 | 语义 | 处置 |
|---|---|---|
| EC 0x2E bit6 | MUX 登记态（0=混合，1=独显直连） | 无切换在途时即「当前实际模式」 |
| EC 0xD1 bit0/bit1 | bit0=切换请求在途、bit1=固件已受理（§21.5 首次观察） | 任一位非零 ⇒ EC 已写为目标、硬件实际仍是另一态 |
| UEFI `MsiDCVarData` byte[1] | 目标模式（0=混合，1=独显直连） | 与 EC 登记不一致 ⇒ 有待生效目标（如外部工具只改 UEFI） |

组合规则（`GraphicsHardwareService.ReadAsync`）：触发位非零时「当前」取拓扑推断，
推断不出（内置屏走核显且无外接屏，混合/核显不可区分）时取登记目标的反面——
两态 MUX 下触发位只在 EC 写成功后置位，故写入前寄存器必为另一态（有协议依据的推断，
非猜测）。读回方不可用或瞬时失败一律回落拓扑推断，不编造直读值。

**首次读回实证（2026-10-18 18:2x，本机真实状态）**：本机当时处于 §21.5 之后
用户经 UI 请求过「独显直连」但未重启的状态。应用自检轮询（每秒）读回并显示：
`实际显卡模式：混合（待生效：独显直连，重启后生效）`——EC 0x2E=0x5B（bit6=1）、
EC 0xD1=0x02（bit1 受理位仍在）、拓扑推断实际=混合，三者组合与显示完全一致。
冒烟（smoke_status_pills.py）0 ERROR、干净退出。

**能力影响**：读回为只读展示，不新增写入通道；`graphics.mode` 维持 Verified。
「核显模式」仍不提供（无 MUX 编码）。

---

## 24. 整机功耗来源负载差分排查（2026-10-18，来源 E14）

**背景**：叶哥反馈状态条「整机功耗：不可用」，要求修复。§7 记载「无证据」，
本节以受控负载差分实验关闭该排查项。

**方法（全部只读）**：`MsiControl.Tools.EcExperiment` 的 `baseline` 命令
（256 个 EC 寄存器全量快照）先后取两份——空闲态与 100% CPU 负载态
（4 进程 × 6 线程常核，CPU 占用实测 100%，持续约 18 秒；实验在无第三方 EC
访问的环境下进行，本机仅运行 MsiControl 自身实例）。

**结果**：空闲 vs 满载仅 12 个寄存器变化，逐一核对无一是可主张的整机功耗通道：

| 寄存器 | 空闲 → 满载 | 判定 |
|---|---|---|
| 0x68 / 0x80 | 58 → 76 / 48 → 51 | CPU/GPU 实时温度（已登记），随负载正常变化 |
| 0x64 / 0x66 | 55 → 121 / 116 → 146 | 未登记遥测（疑似风扇类），非功耗口径 |
| 0x71 | 37 → 18 | CPU 风扇实时转速（已登记） |
| 0x5C/0x5D、0x9E、0xC9、0xCB、0xDD、0xEA、0xF4、0xF7、0xFD、0x4C | 小幅波动 | 未登记遥测，空闲态自身即在抖动（两次间隔 2 秒的空闲快照即有 14 项变化），无稳定功耗语义 |

另对两份快照做了字节对穷举（W / 0.1W / mW 三种口径的 16 位大小端组合），
落在合理功耗区间的候选组合均为偶然巧合，且无一跟随负载单调变化。
`MSI_ACPI.Get_Power` 的存在早已登记（§2.4），但其 `Data` 参数（Package_32）
命令字节编码未验证（§2.6），在编码取得证据前不可解读为任何物理量。

**结论**：本机（MS-15M3，EC 15M3EMS1.113）**不存在可验证的整机功耗读取通道**。
按设计定稿与口径纪律（任务书 §6.4/§6.5）维持 `power.system` = Unknown、
界面「不可用」；绝不用 CPU+GPU 功耗之和、电池侧放电功率或任何未验证寄存器
顶替整机功耗。已探明可得的相关口径：CPU 封装功耗（Afterburner 共享内存）、
GPU 板载功耗（NVML）、电池侧充/放电功率（拔电时，扩展传感器形式呈现）。

**界面改进（2026-10-18 随本轮落地）**：「整机功耗」pill 增加悬浮提示，
完整呈现不可用原因（能力门控依据 + 口径纪律说明），使用户可自行判断
「按设计禁用」而非故障；读取路径与文案本身未变，不产生任何数据造假。

## 25. CPU 封装功耗通道确立：Intel RAPL（MSR 0x606 / 0x611，2026-10-28，来源 E15）

§24 关闭了「整机功耗」排查，并留下一条待办事实：CPU 封装功耗当时唯一可用口径是
MSI Afterburner 共享内存（MAHM 0x100），而该路径依赖第三方常驻程序、且需要用户在
Afterburner「设置→监控」里手动启用 CPU power 项——实测本机未运行时界面即恒为「不可用」。
本节补上**平台级、无需第三方**的读取通道。

**通道**：PawnIO 的 `IntelMSR` 模块（namazso/PawnIO.Modules，LGPL-2.1）提供
`ioctl_read_msr`（输入 1 cell = MSR 编号，输出 1 cell = 读回值）。
模块加载前强制 SHA-256 校验，来源与哈希：

| 项 | 值 |
|---|---|
| 模块 | `IntelMSR.bin`（嵌入 `MsiControl.Hardware.Msi`） |
| git blob SHA-1 | `47da9db96cf769363415239250d7514fa94186bc`（与 LibreHardwareMonitor 仓库副本逐字节一致） |
| SHA-256 | `d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f` |
| 读取白名单 | 模块内核对：仅温度 / RAPL / 频率类 MSR 可读；写入白名单仅 `MSR_PKG_POWER_LIMIT` 等少数项——本项目**只导出读取**，不提供写入入口 |

**实测（本机 MS-15M3，Intel Core Ultra 9 275HX，2026-10-28 15:20–15:52）**：

| 寄存器 | 实测值 | 解读 |
|---|---|---|
| `MSR_RAPL_POWER_UNIT` (0x606) | `0x00000000000A0E03` | 能量单位指数 `(>>8)&0x1F` = 14 → 1 计数 = 1/16384 J |
| `MSR_PKG_ENERGY_STATUS` (0x611) | `0x21A9A593`（持续累加） | 32 位封装能耗累加器，约 4.3 小时回绕一次（按 16 W 估算） |

换算：功率 = ΔE / Δt（无符号差分，自动处理 32 位回绕）。空闲态连续 5 次采样
（间隔 1 秒，400 ms 首帧窗口）：**27.06 / 27.08 / 27.35 / 29.38 / 39.67 W**；
应用实机界面同步显示 20.8–28.1 W，与任务管理器/负载状态一致。
首帧取 400 ms 窗口：200 ms 窗口会把瞬时突发算进去（实测首帧 39.67 W 而稳态 27 W）。

**语义边界（口径纪律不变）**：
- 读到的是 **CPU 封装（Package）** 功耗——含 IA 核心、核显与非核心部分；不等于 `PP0`（仅核心）。
- **绝不与 GPU 功耗相加冒充整机功耗**：`power.system` 仍按 §24 结论恒为不可用。
- 计数器异常（换算结果超出 0–400 W）或采样间隔为零时**如实报不可用**，不给出 0 W 假读数。

**装配与回落链**：`RaplCpuPowerSource`（RAPL）→ `AfterburnerCpuSource`（MAHM 0x100）→
`CompositeCpuPowerSource` 按序取第一个可用值；全不可用时把两条链路的原因依次拼进
`SensorReading.Detail`。Afterburner 由此降为**兜底**，不再是「始终不可用」的唯一来源。

**未挑战的边界**：`MSI_ACPI.Get_Power`（Package_32 编码未验证，§2.6）依旧不接入；
本通道不使用它，也不因此改变任何 `Set_*` 的可写等级。

## 26. 功耗限制写入通道：CPU 走 MSR 0x610，GPU 本机不可写（2026-10-28，来源 E16）

### 26.1 问题

切档位后 CPU/GPU 瓦数目标全部报「本机不支持」。§20 已证实 EC 0xD2 只切换固件场景，
不承载瓦数；而能力基线里 `cpu.power` 只是 `Detected`（「MSI_ACPI 有 Set_Power 方法，编码未验证」），
因此本机从来没有真正的功耗限制写入路径。

### 26.2 CPU：`MSR_PKG_POWER_LIMIT`（0x610）实测可写

| 项 | 实测 |
|---|---|
| `MSR_RAPL_POWER_UNIT` (0x606) | `0x000A0E03` → 功率粒度 0.125 W、能量 1/16384 J、时间 1/1024 s |
| `MSR_PKG_POWER_LIMIT` (0x610) 初值 | `0x0042823000DF81E0` → PL1 = 60 W（使能、clamp）、PL2 = 70 W（使能） |
| 锁定位（bit63） | **0（未锁定）** |
| 写入 PL1=PL2=35 W | 读回 `0x0042811800DF8118` → 35 / 35 W，立即一致 |
| 保持性 | ≥30 秒无变化（每秒读一次共 12 次无改写） |
| 切档位 | 写 35 W 后 `0xD2` 由 0xC2 → 0xC4 → 回 0xC2，0x610 **未被固件改写** |
| 恢复 | 写回原值后读回 `0x0042823000DF81E0` 一致 |
| 写入粒度 | 0.125 W（0x7FFF × 0.125 = 4095.875 W 量程） |

**应用内实机验收（同日 17:27）**：点「节能」后 MSR 立即变为 10 W / 10 W，界面逐项结果
`cpu.longterm = 成功 读回 10 W`、`cpu.shortterm = 成功 读回 10 W`，RAPL 封装功耗由 ~15 W
降到 11.2 W、CPU 频率降到 0.40 GHz；点「平衡」后读回 60 W / 60 W，频率回到 3.50 GHz。

**实现纪律**：
- 只改 PL1/PL2 数值字段；使能位一律置 1；clamp、时间窗与保留位原样保留；**不动锁定位**。
- 锁定位为 1 时直接拒绝写入并说明原因（不假装成功）。
- 成败只由读回值判定（IOCTL 成功不算数），容差 1 个粒度。
- 「原生上限」= **本次进程启动时读到的固件值**（MSR 写入只在重启后被固件复位，
  因此该值可能含上一次会话留下的自定义值，文案按「启动时固件值」表述，不谎称出厂值）。
- 写入的 MSR 白名单在 `IntelMsrModule.WritableMsrIndices` 中只有 0x610 一项。

**未验证的边界**：本环境无法造出 >20 W 的持续负载（受限进程组），因此「限制在满载下
是否被 PMU 真实执行」没有独立负载证据；已按设计定稿 §6.3「限制机制存在时间窗、
允许瞬时偏差」表述，不承诺每次采样都低于设定值。

### 26.3 GPU：NVML 明确拒绝写入

本机 RTX 5070 Ti Laptop（NVML 索引 0）：

| 调用 | 结果 |
|---|---|
| `nvmlDeviceGetPowerManagementDefaultLimit` | 0，默认 65.0 W |
| `nvmlDeviceGetPowerManagementLimitConstraints` | 0，区间 5–140 W |
| `nvmlDeviceGetPowerManagementLimit`（强制限制） | **3 = NVML_ERROR_NOT_SUPPORTED** |
| `nvmlDeviceSetPowerManagementLimit`（写当前值 no-op） | **3 = NOT_SUPPORTED** |

结论：约束区间只是「报告」，笔记本 vBIOS 没有把该旋钮开放给驱动，**nvmlDeviceSetPowerManagementLimit
在本机不可用**。EC 侧无功耗寄存器（§24）；`MSI_ACPI.Set_Power` 的 `Data`（Package_32）
编码未验证且不在只读白名单。

因此 `gpu.power` 仍为 `Detected`（只读：可读默认值/约束区间），写入侧在逐项结果里如实报
Unsupported 并说明三条通道各自的原因——**不静默替换目标、不用其他手段冒充 GPU 功耗限制**
（设计定稿 §6.4）。

**§26.3 补充（2026-10-28 17:32，未改变结论）**：进一步核查发现 `nvml.dll`（591.86）**导出**
`nvmlDeviceSetPowerManagementLimit_v2`（毫瓦 + 结构体形式），而**没有**对应的
`nvmlDeviceGetPowerManagementLimit_v2`。`nvidia-smi -q -d POWER` 也能完整读出
Current / Requested / Default / Min / Max（65 / 65 / 65 / 5 / 140 W）。
因此「v1 setter 返回 NOT_SUPPORTED」只证明**旧接口**不可用，不能排除 v2 接口可用；
v2 是否可写**尚未实测**（需要一次 no-op 写入：把 65 W 再设回 65 W）。
在取得该证据之前，本节与 `gpu.power` 的判定（Detected / 只读）保持不变——不因为
「接口存在」就宣称可写。

**§26.3 定论（2026-10-28 17:38，实测三条通道全部拒绝写入）**

| 通道 | 调用 | 结果 |
|---|---|---|
| NVML v1 | `nvmlDeviceSetPowerManagementLimit(dev, 65000)` | **3 = NOT_SUPPORTED** |
| NVML v2 | `nvmlDeviceSetPowerManagementLimit_v2(dev, {version=12\|(2<<24), powerScope=GPU, powerValueMw=65000})` | **3 = NOT_SUPPORTED** |
| nvidia-smi 591.86 | `nvidia-smi -i 0 -pl 65`（写当前值，零改动） | `Changing power management limit is not supported for GPU: 00000000:01:00.0.`，值保持 65 W |

**踩坑记录（避免误判）**：`nvmlPowerValue_v2_t` 是**三个字段**（`version` / `powerScope` / `powerValueMw`，
共 12 字节），`version` 必须填 `NVML_STRUCT_VERSION(PowerValue, 2)` 即 `12 | (2<<24) = 0x0200000C`。
首版按两字段 8 字节猜，NVML 一律回 **2 = INVALID_ARGUMENT**——那是结构体不对，
**不能**当成「驱动拒绝」；换成正确布局后才拿到真正的 3 = NOT_SUPPORTED。
这条经验同样适用于其他 NVML v2 结构体：`rc=2` 先怀疑自己，别急着下结论。

**结论**：`nvidia-smi -pl`（含其内部可能走的 v2 路径）在本机不可用；GPU 功耗限制在
本机**不存在**任何已验证写入通道，`gpu.power` 维持只读，逐项结果继续如实报 Unsupported。

## 27. 显卡模式切换失效的两个根因与修复（2026-10-28，来源 E17）

**背景**：叶哥反馈「核显与独显模式切换不可用」，界面出现两种结果：
`Failed（EC 0x2E 读回不符：写入后为 0x1B，目标为独显直连）` 与
`PendingRestart（已写入并读回一致）`，且切换回原模式后重启仍落在之前那一态。

### 27.1 根因一（主因）：「已是目标模式」判断漏掉了待生效目标

`GraphicsHardwareService.RequestModeAsync` 原先用**纯拓扑推断**的状态去评估策略，
`GraphicsSwitchPolicy` 的「已是目标模式」分支只比较 `Current`。于是出现：

| 时刻 | 实际拓扑 | EC 0x2E | UEFI byte[1] | 策略判断 | 结果 |
|---|---|---|---|---|---|
| 提交「独显直连」后未重启 | 混合 | 0x5B（bit6=1） | 1 | — | 待生效 |
| 用户点「混合」 | 混合 | 0x5B | 1 | Current=混合=目标 → **无需切换** | **不写入** |
| 重启 | 独显直连 | — | — | — | 用户看到「切回混合没用」 |

**修复**：请求路径改用「拓扑 + 三载体读回」组合出的状态；策略在 `Current == target`
但 `HasPendingChange` 时判为「必须写入」（把目标改回本次请求的模式），并如实说明
「当前实际已是 X，但待生效目标是 Y」。

### 27.2 根因二：EC 写事务没有 ACK，写后读回偶发旧值

ACPI EC 的写事务（命令 0x81 + 地址 + 数据）只保证字节进入端口缓冲（IBF=0），
**固件是否接受没有任何应答**。实测（本机 2026-10-28 17:47 日志）：

```
17:47:31 显卡模式切换 → 独显直连；逐项状态=Failed
         EC 0x2E 读回不符：写入后为 0x1B（bit6=0），目标为 独显直连（1）
```

同一序列在独立进程里连跑 8 轮（`EcExperiment gpuswitch 1 0 8`）**全部读回一致**，
说明写入路径本身正确、失败是时序抖动而非协议错误。

**修复**：写 0x2E 后立即读回，不符则重写，上限 3 次（间隔 40 ms）；
仍不符才报失败，且失败文案给出尝试次数与处置建议。

### 27.3 三载体当前实测（2026-10-28 18:00–18:15）

| 操作 | UEFI byte[1] | EC 0x2E | EC 0xD1 | 结果 |
|---|---|---|---|---|
| 基线 | 1 | 0x5B | 0x02 | 待生效独显（未重启） |
| 点「混合」 | 1 → **0** | 0x5B → **0x1B** | 0x02 | 写入成功，弹重启询问框 |
| 按「2 稍后重启」 | 0 | 0x1B | 0x02 | 不重启，结果行提示待重启 |
| 再点「混合」 | 0 | 0x1B | 0x02 | **无弹窗、无写入**（幂等） |
| 点「独显直连」 | 0 → **1** | 0x1B → **0x5B** | 0x02 | 写入成功，弹框 |
| 按 Esc（=稍后） | 1 | 0x5B | 0x02 | 不重启 |
| 收尾恢复 | 1 | 0x5B | 0x02 | 与进入时一致 |

`EcExperiment gpuswitch` 观察到的 0xD1 行为：写入 `0x01`（请求）后固件会改写为
`0x01` 或 `0x02`（受理位），与 §21.5 的「bit0=请求、bit1=受理」一致；
**不能把 0xD1 非零单独当作「有待生效变更」**—— 真正的判据是「待生效目标与当前实际是否不一致」，
读回组合逻辑（`GraphicsHardwareService`）已按此处理。

### 27.4 重启询问（叶哥 2026-10-28 要求）

切换结果为 `PendingRestart` 时弹询问框，两个选项：
「1 立即重启」= 发出 `shutdown /r /t 5` 并走正常退出路径；
「2 稍后重启」/ Esc = 不重启，结果行提示待重启。
默认按钮是「稍后重启」——重启是破坏性动作，回车不应误触发。
重启命令通道单独验证：`shutdown /r /t 600` 退出码 0、随后 `shutdown /a` 退出码 0
（未真正重启设备）。

### 27.5 「核显模式」不可提供（结论未变，补全证据）

- 本机 MUX 选择只有 EC 0x2E **bit6 一位**（0=混合、1=独显直连），UEFI `MsiDCVarData`
  目标字节实测只取 0/1；
- 同协议来源 ElXreno/msi-gpu-switcher 的 `igpu` 命令实际切的是**混合**（README 原文
  `igpu  Switch to iGPU (hybrid)`），其模型就是两态；
- MSI 官方对 Vector 16 HX AI A2XW 系列的标注只有「**Discrete Graphics Mode** MUX Design」；
  MSI 另有说明「Supported graphics modes will vary by model」——三模式（含 Integrated）
  只出现在部分机型。
- 因此「核显模式」（彻底关闭独显）在本机没有可验证编码；往目标字节写第三个值可能
  导致内置屏无显示、需进 BIOS 恢复，本项目**不做这种猜测**。
  彻底关闭独显请走 BIOS/UEFI 的显示模式选项。

**边界（未验证）**：重启后固件是否按新目标初始化显示通路，本轮**未重启验证**——
叶哥的会话不能被中断。切换前的写入与读回已实证，重启生效属固件行为（§21 已有一次
双向写入记录，重启生效仍待叶哥实机确认）。

## 28. Fn 组合键的软件可见性：`MSI_Event` WMI 通道实证（2026-10-30，来源 E18）

### 28.1 背景与结论

任务提出「接入 Fn+F1…F10 快捷键」。实测后确认两条事实：

1. **Fn 组合键无法被应用接管。** Windows 不存在 `VK_FN` 虚拟键码，`RegisterHotKey`
   无法注册；EC 固件在键盘扫描码产生之前即消费该组合并直接执行动作，低级键盘钩子
   （`WH_KEYBOARD_LL`）与 Raw Input 均拿不到。**这不是实现缺陷，是平台边界。**
2. **但 Fn 事件可被观测。** `MSI_ACPI`（`ACPI\PNP0C14\0_0`）的 WMI 事件类
   `MSI_Event` 会随 Fn 动作上报，`MSIEvt` 高字节固定 `0x22`、低字节为键码。

本条修正 §5 表格中「`MSI_Event` 类：类存在，无实例返回」的旧记录——该结论来自
未订阅事件类的静态查询，**事件类无实例属正常**，订阅后才可见事件流。

### 28.2 采集方法

三通道并行，绝对时间戳对齐：

- `WH_KEYBOARD_LL` 低级键盘钩子（记录 `vkCode` / `scanCode` / `flags`）
- Raw Input，注册 usage page `0x01/0x06`（键盘）与 `0xFF00/0x0001`（厂商自定义）
- `Register-CimIndicationEvent -Query "SELECT * FROM MSI_Event"`

### 28.3 观测结果

订阅窗口内收到 6 条事件，`InstanceName` 恒为 `ACPI\PNP0C14\0_0`、`Active=True`：

```
18:19:50.107  MSIEvt=0x00220023
18:19:53.229  MSIEvt=0x00220021
18:19:55.727  MSIEvt=0x00220032
18:20:01.799  MSIEvt=0x00220019
18:20:05.395  MSIEvt=0x00220078
18:20:09.330  MSIEvt=0x0022001D
```

同一时段键盘钩子**零条记录**，证明 Fn 组合不进入标准键盘流。

唯一被键盘层捕获的是 `vk=0xAD`（`VK_VOLUME_MUTE`，`flags=0x1` EXTENDED），
出现于 18:20:50，**无伴随 `MSI_Event`**，判定为独立多媒体键而非 Fn+F1。

（三次重复采集另有一次捕获到 `Win+P`、`F12` 等普通按键，`flags=0x10`
= `LLKHF_INJECTED`，系远程会话注入，不构成 Fn 证据。）

### 28.4 边界（未建立的部分）

- **码值→按键的映射表未建立。** 6 条码值的确切归属未知，需物理按下并逐一对照；
  本轮叶哥为远程会话，`LF_INJECTED` 特征表明物理 Fn 键无法送达。
- `MSI_Event` 只上报「已发生的动作」，**不能阻止固件动作**，是监听通道而非劫持通道。
- 事件码语义未公开文档可查，禁止按推测硬编码映射；未确认的码值一律按「未知事件」
  处理（触发全量状态刷新即可，不猜测具体键位）。

### 28.5 对实现的影响

由于固件已完整实现这九个动作，应用的正确职责是**监听 + 状态同步 + 提供等效命令**，
而非重放动作。九项功能各自的接入接口见设计说明；`MSI_Event` 仅作为「状态可能已变」
的触发器使用，不依赖码表。

## 29. M-Control 1.4.7 实现路径分析与本机接口实测（2026-10-30，来源 E19）

### 29.1 分析方式与边界

对 `C:\Program Files\M-Control`（版本 1.4.7，.NET Framework 4.6.2 程序集）做**只读结构分析**：
用 ILSpy 反编译 `MControl.exe` 读取类型与方法，用 `Web/*.js` 读取界面侧文案与调用约定。

**边界**：本轮只提取「它调用了什么系统接口、按什么顺序、如何判断成败」，
并用自己的代码在自有架构里重新实现；**不复制、不移植其源代码**。
M-Control 的许可为闭源免费、禁止二次打包冒充，其自身也声明「部分兼容性实现涉及
未公开接口、私有协议适配和逆向分析资料」——它同样是逆向得到的结论。

顺带解决了一处历史悬案：`Provider/MControlMsiAcpiMof.dll` 即本项目长期未定位的
`MSI_ACPI` WMI schema 提供者（HWE §5「MOF 注册来源未定位」）。本项目不依赖该 DLL。

### 29.2 九项功能各自的实现路径（M-Control 的做法）

| 功能 | M-Control 的实现 | 本项目是否采用 |
|---|---|---|
| F1/F2/F3 静音与音量 | Core Audio `IAudioEndpointVolume`（`SetMute` / `VolumeStepUp/Down`） | 采用（同一接口） |
| F4 触控板 | 注入 `Ctrl+Win+F24`（`keybd_event`，左 Ctrl / 左 Win / F24 全部带 `KEYEVENTF_EXTENDEDKEY`）；状态读 `HKCU\...\PrecisionTouchPad\Status\Enabled` | 采用 |
| F5 麦克风 | Core Audio 录音端点 `SetMute` + MSI WMI DataCommand 44/45 同步 F1/F5 指示灯 | 采用端点静音；**指示灯未实现**（DataCommand 编码未验证，不做猜测） |
| F6 蓝牙 | `Process.Start("ms-settings:bluetooth")`，**不开关射频** | 采用（同样只打开设置页） |
| F7 情景模式 | EC `0xD2`（本项目已有同通道） | 采用已有通道 |
| F9/F10 亮度 | WMI `WmiMonitorBrightnessMethods.WmiSetBrightness` | 采用 |
| Fn 事件 | 订阅 7 个候选 WMI 事件类 + `WMIEvent`；**主动跳过带 `MSIEvt` 属性的事件**；另轮询 EC `0xC0`（写 `0xC1` bit7 开启） | 事件订阅采用；**EC 0xC0/0xC1 不采用**（见 §29.4） |

其热键码表（`OfficialShortcutOsdShortcuts`）共 23 项，与九项相关的码值：
`fnkey=11`、`fn-esc=27`、`f1=112/173`、`f2=113/174`、`f3=114/175`、`f4=8/131/190`、
`f5=116`、`f6=117`、`f7=118`、`f8=119`、`f9=120`、`f10=121`、`f11=122`。

### 29.3 码值语义：两套编码并存，不可混用

M-Control 内部存在两套编码，分别用于两条通道：

- **WMI 通道**：`MsiCenterOsdWmiEventCode` 枚举（`25=MicrophoneMute`、`29=ShiftingSpeed`、
  `67/68=TouchpadOsdOn/Off`、`86=Bluetooth`、`92=FnLock`、`120=CallBluetoothSettings` 等）。
- **EC 通道**：`HotkeyCodes` 表（`112–122` 段，与上表一致）。

**冲突点**：`120` 在 EC 表里是 F9 亮度降低，在 WMI 枚举里是「打开蓝牙设置」。
同一数字在两条通道语义相反，混用会直接把亮度键误判成蓝牙键。

本机实测（§28.3）的 6 条 `MSIEvt` 低字节为 `0x23/0x21/0x32/0x19/0x78/0x1D`，
其中 **`0x19(25)`、`0x1D(29)`、`0x78(120)` 与 WMI 枚举精确吻合**（麦克风 / 情景模式 / 蓝牙设置），
反证 `MSIEvt` 的低字节走 WMI 语义而非 EC 码表。因此本项目在 WMI 通道内只使用 WMI 枚举语义，
`0x23/0x21/0x32` 三项因无任何文档或行为证据可依，一律按「未知」处理（只触发状态刷新）。

### 29.4 为什么不用 EC `0xC0` 热键通道

M-Control 通过把 EC `0xC1`（0xC1 bit7 置 1）打开、再轮询 `0xC0` 取得热键码，效果更精确。
本项目**不采用**，理由：

1. 这需要新增两处 EC **写入**（`0xC0` 清零、`0xC1` 置位），而本项目 EC 写白名单只有
   7 个经本机受控实验验证的地址（HWE §17/§20/§21/§23/§26），`0xC1` 不在其中；
   它被称为「debug register」，对固件其他行为的副作用未知。
2. WMI 事件通道已足以满足「状态同步」这一目标，无需为此扩大写入面。
3. 若将来确有需求，应先补 `0xC0/0xC1` 的受控对照实验（写入 + 读回 + 副作用观察），
   再按 §28 的口径登记能力键。

### 29.5 本机实测（四项接口，全部恢复原状）

| 接口 | 操作 | 读回 |
|---|---|---|
| 播放端点（2 声道） | `SetMute` 取反再恢复 | `False→True→False`，HRESULT 0 |
| 播放端点音量 | `VolumeStepDown` / `VolumeStepUp` | `0.19→0.18→0.20`，系统步进档位 `1/51`（约 2%） |
| 录音端点（1 声道） | `SetMute` 取反再恢复 | `False→True→False` |
| 内屏 AUO7DB2 | `WmiSetBrightness` 100→50→100 | 每次 300–400 ms 后读回一致 |
| 触控板 | 注入 `Ctrl+Win+F24` 两次 | `Enabled: 0→1→0` |

端到端热键验证（应用实际运行，注入 `Ctrl+Alt+3` / `Ctrl+Alt+1` / `Ctrl+Alt+8`）：

```
Hotkey: 音量提高已完成：audio.volume=22%
Hotkey: 扬声器静音已完成：audio.mute=已静音
Hotkey: 亮度提高未完成：已到达亮度上限（当前 100%），未写入硬件。
```

第三条是设计意图的正向证据：**到达边界时如实报告「未写入」，不伪造成功**。

### 29.6 仍未完成的部分

- F1/F5 的键盘指示灯同步（MSI WMI DataCommand 44/45）未实现，编码未验证。
- Fn 码表只确认 3 项；`0x23/0x21/0x32` 的归属需物理按键逐一对照（叶哥当前为远程会话，
  注入按键的 `LLKHF_INJECTED` 特征使对照无效）。
- 等效热键固定为 `Ctrl+Alt+1…8`，尚未做成可配置项（需设置架构升级到 v3）。

## 30. 显卡模式切换失效的真正根因：UEFI 目标字节偏移写错 + EC 0xD1 缺模式位（2026-10-30，来源 E20）

### 30.1 结论先行

2026-10-17 起沿用的 ElXreno/msi-gpu-switcher 两态模型**在本机不成立**，两处致命偏差：

1. **UEFI 目标字节写错位置**：目标模式在 `MsiDCVarData` 的 **byte[5] bit0-1**，
   旧实现写的是 **byte[1]**——那是初始化标志，与显卡模式无关。
   因此「写入并读回一致」一直成立，但读回的正是自己刚写的那个无关字节，
   **固件从未收到目标模式**。
2. **EC 0xD1 缺模式位**：官方序要求先置 bit1（请求在途）与 bit2（输出模式位），
   旧实现只写 bit0/bit1，从不让固件看到「这次请求要切到哪一态」。

附带纠正：本机 MUX 不止两态。「核显模式」有明确编码（目标=2、0xD1 bit2=0、0x2E bit6=0），
且固件声明支持（byte[5] bit5=1）；本项目暂不开放写入（见 30.5）。

### 30.2 逆向依据（M-Control 3.x，`C:/Program Files\M-Control`）

`MControl.Core.EcIoInterface.SetGpuMode(int mode)`：

```
写 UEFI MsiDCVarData byte[5] = (byte[5] & 0xFC) | (mode & 3)
读 EC 0x2E(46)、EC 0xD1(209)
mode 0 混合:   0x2E &= ~0x40 ; 0xD1 |= 0x06
mode 1 独显:   0x2E |= 0x40  ; 0xD1 |= 0x06
mode 2 核显:   0x2E &= ~0x40 ; 0xD1 = (0xD1 & ~0x04) | 0x02
写 EC 0xD1 = 上式 ; 写 EC 0x2E = 上式 ; 再写 EC 0xD1 = 上式 | 0x01
```

`EcIoInterface.GetGpuMode()` 读侧：`0x2E bit6` 为 MUX、`0xD1 bit2` 为输出模式位，
`bit2=0` 判核显；`bit6=1 && bit2=1` 判独显直连。

`MControl.Core.MsiGpuModeController`（Feature Manager 新 EC 路径，本机优先路径）：

- `MsiDCVarData` 的 **byte[5]**：bit0-1 目标、bit2-3 固件自报当前、
  bit4 新 EC 切换可用、bit5 支持 UMA、bit6=1 表示**不支持**独显直连。
- 触发走 WMI `MSI_ACPI`：`Get_AP(0)` → `Set_Data(0xD1) = (AP[1] & ~0x03) | 0x01`
  → 等 2 s 确认 `AP[1] bit1` → `Set_Data(0xBE) = 2`（S5 唤醒登记）。
- `MsiDCVarData.byte[1]` 在官方代码里被标注为「初始化标志」。

### 30.3 本机实测（2026-10-30）

只读基线：`MsiDCVarData` 20 字节 = `01 01 00 05 00 30 01 00 …`
→ byte[1]=0x01（初始化标志）、**byte[5]=0x30**（目标混合、新 EC 可用、UMA 支持、独显支持）；
EC 0x2E=0x1B（bit6=0）、EC 0xD1=0x00；`Get_AP(0)`=`01 00 00 C4 80 80 …`（AP[1]=0x00）、
`Get_Data(47)`=`01 5B …`（bit6=1，legacy 通道亦声明支持）。

修复后经应用切换（`scripts/verify_graphics_fix.py`，两次写入后已恢复原状）：

| 步骤 | UEFI byte[5] | EC 0x2E | EC 0xD1 |
|---|---|---|---|
| 基线 | 0x30 | 0x1B | 0x00 |
| 切「独显直连」 | 0x31 | 0x5B（bit6=1） | 0x06 → 读回 0x00/0x02（固件消费触发位） |
| 切回「混合」 | 0x30 | 0x1B（bit6=0） | 0x02 → 0x00 |

日志（`%LOCALAPPDATA%\MSI-Control\logs\msi-control.log`）两条均为 `PendingRestart`，
确认依据逐项记录「UEFI byte[5]：0x30 → 0x31」「0x2E 写 0x5B → 读回 0x5B」。
界面同屏显示「实际显卡模式：混合（待生效：独显直连，重启后生效）」。

### 30.4 关键经验

- **「读回一致」不等于「写对了地方」**：读回核对必须针对**固件真正消费的字段**。
  旧实现的读回逻辑读的是自己刚写的字节，形成了自洽的假证据链。
- **0xD1 bit2 不能当成功判据**：实测写 `0x06` 后固件可能读回 `0x00`/`0x02`（消费触发位），
  成功判据只能是 `0x2E bit6`（MUX 登记态稳定保持）。
- 应用枚举 `GraphicsMode{Integrated=0,Hybrid=1,Discrete=2}` 与固件编码
  `{0=混合,1=独显,2=核显}` **顺序不同**，直接强转会把「混合」写成「独显」。现已用
  `MsiGpuModeCodec` 显式映射并加单测锁定。

### 30.5 S5 冷启动对照（2026-10-30 23:00–23:13，同一晚完成）

写入「独显直连」并保留（byte[5]=0x31、0x2E=0x5B），随后完全关机再开机：

| 观察项 | 重启后 |
|---|---|
| UEFI byte[5] | **0x35** —— 目标仍为 1，**bit2-3 被固件改写为 1（自报当前=独显直连）** |
| EC 0x2E | 0x5B（bit6=1）保持 |
| EC 0xD1 | 0x00（触发位已被固件消费清空） |
| `Intel(R) Graphics` | **CM_PROB_PHANTOM**（幽灵设备，PCI 层未枚举） |
| 显示适配器 | 只剩 NVIDIA RTX 5070 Ti，内置屏 AUO7DB2 与外接屏 SAC2563 均由它输出 |
| 本应用界面 | 「实际显卡模式：独显直连」，无待生效标记 |

**结论：切换真实生效。** `byte[5]` 由 0x31 自变为 0x35 是固件侧不可伪造的证据——
目标位与自报当前位同时为 1。

**代价与现象（必须记录）**：首次 S5 冷启动出现**长达数分钟的白屏**（内置屏无图像），
用户误判为故障而长按电源断电，系统日志留下 `Kernel-Power 41`（非正常关机）与一条
WHEA-Logger 硬件错误；第二次开机直接正常。机制：MUX 首次切到 dGPU 直连后，内置屏输出源
从 iGPU 换成 dGPU，Windows 重新枚举显示设备、NVIDIA 驱动首次在直连拓扑下接管面板 EDID 与模式，
该窗口内无图像属已知过渡现象，不是协议写坏（否则第二次开机也不会正常，且 bit2-3 不会自洽为 1）。

**据此的界面约束**：切换成功的提示必须写明「首次冷启动可能数十秒到数分钟无图像，
请等待、勿强制断电；若长时间无图像长按电源 10 秒关机后再开机」。

### 30.6 仍未完成 / 未验证
- 核显模式（UMA）写入路径未开放：编码清晰但未做受控实验，失败后果是内置屏无显示。
- 白屏窗口能否缩短未验证：候选手段是改走官方 Feature Manager 新 EC 路径
  （`Set_Data(0xD1)` + `Set_Data(0xBE)=2`，且**不写** 0x2E）——本机 `byte[5] bit4=1`
  正是该路径的启用标志，本次走的是 legacy 路径。是否由缺 0xBE 登记导致重建窗口偏长，需另行对照。
- Feature Manager 新 EC 路径（WMI `Set_Data(0xD1)` + `Set_Data(0xBE)=2`）未接入：
  本项目 MSI_ACPI 通道仍为只读白名单；`0xBE=2` 的副作用（是否触发关机）未验证。

## 31. M-Control 设备控制/显示电源功能的实现路径与本机受控实验（2026-10-31，来源 E21）

### 31.1 分析方式与边界

对 `C:\Program Files\M-Control`（1.4.7）继续做**只读结构分析**（ILSpy 反编译
`MControl.exe`），本轮目标是界面「设置」页里的设备控制与显示电源三项。
**边界同 §29**：只提取「调用了什么系统接口、按什么顺序、如何判断成败」，
不复制其源码，在自有架构里重新实现。

关键发现：M-Control 有一条本项目此前没有使用的通用通道 ——
`MSI_ACPI.Get_Data` / `Set_Data` + **命令字节**。`Data` 参数在 MOF 里不是字节数组，
而是 `Package_32` 类的嵌入实例（只有 `Bytes` 属性，32 字节）：
`Bytes[0]` 是命令字节，写命令的载荷从 `Bytes[1]` 起。
返回包 `Bytes[0]` 是标志位，**必须为 1** 才算成功，数据从 `Bytes[1]` 起
（M-Control `MsiAcpiPackagePlanner.BuildPackage` / `MsiAcpiResult.FromRaw`）。
本项目原有的 `WmiMsiAcpiTransport` 只透传 `Data`，无法构造这种嵌入实例，
因此新增 `MsiAcpiDataChannel`（写入白名单仅 0xE8/0xD3/0xBF 三条命令）。

### 31.2 各功能的实现路径

| 功能 | M-Control 的实现 | 本项目采用 |
|---|---|---|
| 交换 Win/Fn 键 | `Get_Data(232)` 取原始字节，改 bit4，`Set_Data(232)` 写回并读回校验 | 采用（同命令、同校验） |
| 键盘背光总开关 | `Get_Data(211)`，载荷低 2 位为档位（0=关，1–3=档），写回时只改低 2 位 | 采用 |
| USB 共享供电 | `Get_Data(191)`：bit3=硬件支持、bit5=开关；写后同步 UEFI `MsiDCVarData` byte[3] bit0 | 采用（EC + UEFI 两段） |
| Win 键开关 | **无固件通道**：进程内 `WH_KEYBOARD_LL` 钩子丢弃 `LWin/RWin`，注释明确「仅 M-Control 运行期间生效」 | 采用（同一机制，并限定时效） |
| 触摸板 | 注入 `Ctrl+Win+F24` + 读 `HKCU\...\PrecisionTouchPad\Status\Enabled` | 已有（HWE §29） |
| 显示省电 | `EnumDisplaySettings` 取当前分辨率支持的刷新率范围，`ChangeDisplaySettingsEx` 只改 `dmDisplayFrequency` | 采用（纯 Win32，不写注册表） |
| 开机静默驻留 | 启动参数 + 行为设置 `startMinimizedOnStartup` | 采用（本项目已有托盘最小化） |
| 退出保持灯效 | 需要 SteelSeries 灯效宿主常驻 | **不采用**：设计定稿 §10 明确不做灯效 |
| 监控布局 | M-Control 自有页面布局 | **不采用**：本项目主窗口无多套布局结构 |
| 快捷键 OSD | 独立 `OsdOverlayForm` 图层 | 本轮不实现（无 OSD 层；状态反馈走主窗口状态带） |

### 31.3 本机实测（受控实验，全部恢复原状）

命令通道：`root\WMI` → `MSI_ACPI`（实例 `ACPI\PNP0C14\0_0`），`Data` 为 `Package_32` 实例。

| 命令 | 读回（原始） | 写入 | 写后读回 | 恢复 | 结论 |
|---|---|---|---|---|---|
| 0xD3 键盘背光 | `01 80` | 0x83（档位 3） | `01 83` | 写回 0x80，读回 `01 80` | 可写、可读回 |
| 0xE8 Win/Fn | `01 00` | 0x10（bit4） | `01 10` | 写回 0x00，读回 `01 00` | 可写、可读回 |
| 0xBF USB 供电 | `01 08`（bit3=1 支持、bit5=0 关） | 0x28（bit5） | `01 28` | 写回 0x08，读回 `01 08` | 可写、可读回 |
| 0xD9 FnLock（参考） | `01 0B` | 未写 | — | — | 本轮不使用 |

标志位恒为 1，说明三次写入均被固件接受；写后读回一致且恢复成功，
因此 0xD3/0xE8/0xBF 三项在能力基线里登记为 `Verified`。

### 31.4 尚未验证的部分

- **UEFI 持久化**：`MsiDCVarData` byte[3] bit0 的写入本轮未单独实测，
  因此 USB 共享供电在能力条目里注明「EC 生效已实测、UEFI 同步未实测」。
- **显示省电切换**：刷新率范围可枚举，但「切到 60 Hz 再切回」的受控实验未做，
  能力等级保持 `Detected`（界面不宣称已可用）。
- **Win 键钩子**：钩子安装/卸载逻辑已实现，长时稳定性与被系统摘除后的自愈未观测，
  能力等级保持 `Detected`。

## 30. 快捷键「不可用」的三个根因与修复（2026-10-31，来源 E20）

### 30.1 诊断结论：功能本身是通的，缺陷在别处

叶哥反馈「快捷键功能不可用」。分级取证结果：**注册全部成功**（9 条 `快捷键已注册`）、
**触发正常**（`WM_HOTKEY` 到达并进入分发）、**服务执行成功**（`音量提高已完成：audio.volume=22%`）。
因此「不可用」不是链路断了，而是另外三个缺陷叠加造成的：

1. **触控板切换必然失败**（修饰键污染，见 §30.2）；
2. **Fn 事件被处理两次**（父子事件类双订阅，见 §30.3）；
3. **任何操作都没有可见反馈**（只有日志，见 §30.4）——这是用户判定「不工作」的直接原因。

### 30.2 根因一：注入时修饰键仍被按住，固件不认

现象：`触控板开关未完成：注入后读回为「已禁用」，与目标不符`。

对照实验（独立 Python 进程注入，不经过应用）：`Enabled 0 → 1`（240 ms 内），再注入复原为 0。
说明**注入方式本身正确**，差异只在于调用时机——热键回调发生在用户仍按着 `Ctrl+Alt` 时，
此刻注入 `Ctrl+Win+F24` 实际发出的是 `Ctrl+Alt+Ctrl+Win+F24`，固件不匹配因而不响应。

M-Control 1.4.7 中 `IsStandardShortcutModifierPressed()`（检查 Shift/Ctrl/Alt/Win）
正是为同一问题存在，可作为旁证。

修复：注入前轮询等待 Ctrl/Alt/Shift/Win 全部松开（40 ms 间隔、上限 1.2 s）；
超时仍按住则明确报失败并提示「请松开修饰键后重试」，不假装成功。

修复后实测：`触控板开关已完成：input.touchpad=已启用`，随后复原 `已禁用`。

### 30.3 根因二：同一次按键上报两次

现象：`15:16:10.196` 同一毫秒出现两条内容完全相同的 Fn 事件。

原因：候选订阅里 `WMIEvent` 是 ACPI WMI 的通用父类，`MSI_Event` 是其具体子类，
一次 EC 通知会在两者上各产生一条事件。若都当独立按键处理，用户按一次会被执行两次
（静音切换两次等于没切换）。

修复：按「码值 + 400 ms 时间窗」去重（`ShouldPublish`），首次仍完整上报，紧邻重复丢弃。

### 30.4 根因三：执行成功但没有可见反馈

这是用户最直接的感受来源：功能做了，屏幕上什么都没有，自然会判定「没生效」。

修复：新增桌面提示浮层（左上角、深色圆角、右侧彩色圆角徽标、1.5 s 后淡出），
由两条路径共同驱动：
- **Fn 事件路径**：事件到达后**先读回硬件真实状态**再显示（例如麦克风读录音端点 `GetMute`），
  读不到就不显示——宁可没有提示，也不给一个可能错误的结论；
- **等效热键路径**：按操作结果生成提示，成功显示读回值（`音量 18%`、`触控板 已启用`），
  失败显示「未完成」（Danger 配色），能力未开放时同样提示。

提示窗安全性：`WS_EX_NOACTIVATE` + `ShowWithoutActivation` 保证不抢焦点，
`WS_EX_TOOLWINDOW` 使其不进 Alt+Tab 与任务栏，退出时显式关闭（否则进程残留）。

实测取证（截图为屏幕左上角原样）：
`麦克风/已静音`（红）、`音量/18%`（蓝）、`触摸板/已启用`（绿）。

### 30.5 一个实现上的坑（已修）

自绘徽标控件在构造期抛 `ArgumentException: Control does not support transparent background colors`，
导致**应用启动即崩溃**。原因是设置了 `ControlStyles.UserPaint` 却未同时开启
`SupportsTransparentBackColor` 就赋 `BackColor = Color.Transparent`。
两者必须成对出现。

### 30.6 边界

- Fn 物理按键路径未实机验证（叶哥为远程会话，注入按键不产生 `MSI_Event`）。
  回调链在 10-31 15:16 的日志中已证实可达，本轮只改了去重与展示逻辑。
- 码值→按键映射仍只确认 3 项（25/29/120），其余按 Unknown 处理。

## 32. 四项功能「控件不生效」的根因与修复（2026-10-31，来源 E22）

### 32.1 根因：把「固件只发事件」误当成「固件已执行动作」

§30 的结论「Fn 动作已由固件执行，应用只同步状态」**只对一半功能成立**，这是本轮四个缺陷的共同根因。

实测分工（同一台机器、同一批日志）：

| 类别 | 键 | 固件是否执行动作 | 证据 |
|---|---|---|---|
| 系统原生 | F1/F2/F3/F9/F10 | 是（发标准多媒体虚拟键，Windows 处理） | 键盘钩子层抓到 `vk=0xAD`(VK_VOLUME_MUTE) 等标准码 |
| 固件不执行 | F5 麦克风 | **否** | 15:35:16–15:36:10 连按 5 次（日志 5 条 `Fn 事件：MicrophoneMute`），录音端点 `GetMute` 始终 `False` |
| 固件不执行 | F7 情景模式 | **否** | 15:33:23、15:35:14 两次按键，`EcProbe dump 0xD2` 仍为 `0xC1` |
| 固件不执行 | F6 蓝牙 | **否** | 固件只发事件；M-Control 收到后自己去开设置页 |
| 固件不执行 | F4 触控板 | 未定（切换型，无法从事件判定） | 用「上次已知状态做差集」兼容两种可能 |

M-Control 1.4.7 的代码结构旁证了这一分工：`OfficialShortcut(...)` 里 F1/F2/F3/F9/F10 标
`systemNative=true`，而 `microphoneMute` / `touchpad` / `scenarioMode` 都有各自的
`HandleMicrophoneMuteShortcut` / `SetTouchpadEnabled` / `ToggleScenarioModeFromShortcut`
实现——它从没指望固件替它做事。

四个缺陷与之对应：

1. **触控板开关不生效**：Fn+F4 事件到达后只 `Read()` 后显示，从未下发切换；
   另外切换后缺少 `WM_SETTINGCHANGE` 广播，驱动不会重新加载状态位。
2. **蓝牙只显示为开启**：`WindowsSettingsLauncher` 只 `Process.Start("ms-settings:bluetooth")`，
   OSD 文案写死「已打开设置」——**从未触碰射频状态**。
3. **性能模式不生效**：`BuildOsdForFnEvent` 对 `PerformanceStage` 返回写死的
   `new OsdMessage("性能模式","已切换",Neutral)`，既不切档也不读档位。
4. **麦克风无法关闭**：同 1，只读不写，所以连按永远显示「开启」。

### 32.2 修复

`DesktopApplicationContext.OnFnHotkeyPressed` 按 §32.1 的分工分流：

- 系统原生四项 → 只读回 + 显示（不变）；
- 需应用执行四项 → `TryMapToEquivalentAction` 映射到等效热键动作，
  转 UI 线程后走**与 `Ctrl+Alt+N` 完全相同**的执行路径（能力门控 → 下发 → 读回 → OSD）。
  两条入口共用一段代码，避免各自演化出不同判据。
- 触控板加 `FirmwareAlreadyToggledTouchpad()`：与上一次已知状态比对，变了即认为固件已切，
  只同步显示，避免「固件切一次 + 应用再切一次 = 净不变」。

配套：

- **蓝牙射频**：新增 `IBluetoothRadioControl` + `WindowsBluetoothRadio`（WinRT
  `Windows.Devices.Radios`），能力键 `system.bluetooth`；写入后重新 `GetRadiosAsync` 读回
  `RadioState`，`SetStateAsync` 返回 `DeniedByUser/DeniedBySystem` 时如实转述拒绝原因。
- **触控板**：`SetEnabledAsync` 成功后广播
  `SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, …, "…\PrecisionTouchPad\Status")`，
  与 M-Control 的 `BroadcastTouchpadSettingChanged` 同款。
- **性能档位**：`PerformanceShiftMap.Cycle` + `NextStage` 定义
  节能→安静→平衡→高性能→狂暴→节能 的循环；Fn+F7 与 `Ctrl+Alt+9` 都走
  `CycleStageAsync`（读 0xD2 → 取下一档 → `RequestPerformanceStageAsync` → 读回）。
  OSD 显示**读回**的档位名，不再是写死的「已切换」。
  「安静」与「节能」共用 0xC2，用 `_lastAppliedStage` 区分：仅当编码相同且上次实际应用的是
  安静时才沿用安静语义，其余一律按寄存器解码——宁可显示编码语义，也不猜。
- **目标框架**：WinRT Radio API 要求 `net8.0-windows10.0.19041.0`，15 个 `net8.0-windows`
  工程统一升级（`Core`/`Persistence`/`Performance` 保持 `net8.0`）。升级后构建仍 0 警告 0 错误。

### 32.3 实机验证（判据全部取自应用之外的独立读数）

| 项 | 外部读数工具 | 结果 |
|---|---|---|
| 触控板 | `HKCU\…\PrecisionTouchPad\Status\Enabled` | `False → True` 翻转 |
| 麦克风 | Core Audio 默认录音端点 `GetMute` | `未静音 → 已静音 → 未静音` |
| 蓝牙 | WinRT `Radio.State`（独立 `BlueProbe` 进程） | `Off → On → Off` |
| 性能档位 | `EcProbe dump 0xD2`（独立进程） | `0xC1 → 0xC0 → 0xC4 → 0xC2`（平衡→高性能→狂暴→节能） |

OSD 截图（左上角原样）：`性能模式 / 高性能`（蓝）、`蓝牙 / 已开启`（绿）。
测试造成的状态全部复原：触控板 `False`、麦克风未静音、蓝牙 `Off`、档位 `0xC1`。

### 32.4 边界

- **Fn 物理按键 → 动作** 仍未实机验证（远程会话按不出物理 Fn）。但 Fn 路径与等效热键
  现在共用同一执行函数，差别只在入口；`BuildOsdForFnEvent` 已不再承担这四个动作。
- 触控板「固件是否也切了」只能靠状态差集推断：首次按 Fn+F4 且读不到初始状态时，
  会按「固件没切」处理并执行一次切换（宁可切一次，也不假装同步）。
- 切档会一并下发该档的 CPU 功耗目标（默认 60 W，见 `stageTargets`），
  这是既有档位应用语义，不是本轮新增行为。

### 32.5 后续缺陷：OSD「性能模式 / 未完成」与循环卡死（2026-10-31 16:20，来源 E23）

§32.2 的档位循环上线后，叶哥截图反馈 OSD 显示「性能模式 / 未完成」（红色徽标），且连按
Fn+F7 多次全部切到「安静」。日志判据（16:15:44–16:16:05，连续 8 次请求全部「→ 安静」）：

- **根因一（循环卡死）**：`CycleStageAsync` 与 `ControlCoordinator` 都用整组
  `IsSuccess` 判定档位是否应用成功。而本机 GPU 功耗上限永久不受支持（§26/§32.1），
  整组恒为 `PartiallySucceeded` → `_lastAppliedStage` 与 `LastConfirmedStage` 永不更新 →
  每次读回 0xC2 都按「节能」处理 → 循环永远请求「安静」。
- **根因二（OSD 假失败）**：`RunShortcut` 同样按整组状态出提示，主目标明明达成，
  屏幕却显示「未完成」——与 §30.4 修复的「无反馈」正好相反，这次是「错误反馈」。

修复：新增 `ControlResult.IsSettingSucceeded(setting)`（Core），
`PerformanceShiftMap.StageSettingKey`（="stage"，与 `PerformanceSettingKeys.Stage` 对齐）；
协调器、档位循环、OSD 三处判据全部改为「档位项自己读完确认」。
`RunShortcut` 增加 `applied` 判定参数：主目标达成时，剩余不承载项记一行 Info
（如实列出），不再升级为失败提示。测试替身 `FakePerformanceHardware` 的默认结果键
由 `performance.stage.{Stage}` 对齐为真实契约的 `stage`。

实机验证（判据取自应用外 EcProbe 读 EC 0xD2）：起点平衡，连按三次 Ctrl+Alt+9，
0xC1 → 0xC0 → 0xC4 → 0xC2（高性能 → 狂暴 → 节能），每次都有「已切到 + 读回码值」，
无「未完成」。复原：0xD2=0xC1、0xD4=0x8D、PL1=PL2=60 W（测试遗留的 20 W 由启动恢复
按平衡档目标写回）、settings.lastConfirmedStage=2。

注意两个**不是缺陷**的现象，免得后续误判：
- 起点 0xC2（节能）时第一跳读回仍是 0xC2——「安静」是节能+静音风扇组合档，编码共用；
- 测试后高性能/狂暴写 20 W——「原生上限=本次进程启动时的固件值」（§26），
  前一轮把 MSR 留在 20 W，重启或经应用切档即恢复。

### 32.6 后续缺陷：界面显示不消歧，用户看到「节能切不到安静」（2026-10-31 16:45，来源 E24）

§32.5 修复了循环逻辑，但「安静/节能共用 0xC2」的消歧只存在于桌面壳的
`_lastAppliedStage` 私有字段，且只被循环热键更新。用户实际看到：
主窗口「实际性能模式」始终显示节能（读回解码 0xC2 → 节能），
按一下切到安静时**显示不变**，再按一下直接到平衡——观感即「节能切不到安静」。

根因：消歧事实散落两处且不一致。循环用 `_lastAppliedStage`（会话内、仅热键入口更新），
界面显示完全不用消歧。窗口按钮/托盘/启动恢复切到安静后，循环与显示都不知情。

修复：消歧事实源收敛到协调器的 `LastConfirmedStage`（§32.5 起所有写入入口
——窗口按钮、托盘、等效热键、Fn 事件、启动恢复——都在「场景项读完确认」时维护它）：

- `IControlRequestSink` 新增 `LastConfirmedStage`；`ApplicationControlSink` 透传协调器；
  `NotAdaptedControlRequestSink` 返回 null（未写过硬件，不存在已确认档位）。
- 桌面壳删除 `_lastAppliedStage` 副本；`ReadStageFromHardware` 与主窗口显示
  （`PollHardwareStateAsync` → `UpdateStageState`）共用 `ApplyStageDisambiguation`
  纯函数：仅当「上次确认 ≠ 解码值、且两者编码相同」时沿用上次确认的安静语义。
- 消歧后主窗口的曲线槽位也跟随到「安静」槽（截图实证）。

实机验证（脚本 `verify_quiet_stage_fix.py`，判据全部取应用之外）：
预置节能+风扇 0x8D → 按 Ctrl+Alt+9 → EC 仍 0xC2、**风扇翻 0x1D**（组合档硬件证据）、
主窗口截图显示「实际性能模式：安静」且安静按钮高亮 → 再按一次 → 0xC1（平衡）。
复原 0xC1 / 0x8D / lastConfirmedStage=2。

### 32.7 Fn+F4 真相：一直有效，缺的是反馈与同步（2026-10-31 17:15，来源 E25）

叶哥报告「触摸板禁用启用仍然不可用」。分级取证：

1. 设置页复选框路径（注入 Ctrl+Win+F24）：应用日志两次操作均读回一致，
   叶哥确认禁用后「光标完全不能动」——**该路径本来就正常**。
2. 物理Fn+F4：键盘钩子抓到决定性证据——每次按键产生 `LL vk=0xA2(Ctrl)+0x5B(Win)+0x87(VK_F24)`，
   **flags 无 INJECTED 位**，即固件在硬件层合成真实的官方组合键；Windows 收到后自行完成切换。
   连按 11 次注册表翻转 11 次（奇数次）→ 末态「禁用」。**Fn+F4 从未失效。**
3. 用户判定「没反应」的原因：**全程没有任何屏幕反馈**，且不摸触控板就无从察觉状态变化；
   设置页勾选也不会跟着变。
4. EC 全量 dump 前后对比（两轮）：变化的寄存器不重合（温度/转速类动态值），
   **固件没有稳定的触控板状态位**；状态唯一观测面就是注册表状态位。

修复（M-Control `PollObservedMsiTouchpadState` 同构）：

- `SystemKeyHookChannel` 抓到的 `VK_F24` 映射为**观察动作** `touchpad.observed`：
  只读回状态、弹 OSD、同步设置页，**绝不重放切换**（重放 = 按一下切两次 = 没切）。
- 新增 2 秒周期注册表轮询（`PollTouchpadState`）作为兜底：覆盖钩子未接住的路径，
  变化时同样反馈。两条路径共享 `_lastKnownTouchpad` 判重，先到者提示、后到者静默。
- `ShellContext.TouchpadStateChanged` 事件：设置页打开期间按 Fn+F4，勾选实时跟随。

实机验证（注入等效按键）：`VK_F24(0x87) → 触控板已由系统切换` →
`触控板状态变化（来源 Fn+F4（VK_F24））：已禁用/已启用`，无重复提示，状态复原为启用。

---

## 33. 风扇转速估算偏差根因：单点线性换算误用于整条占空比区间（2026-10-31，来源 E23）

### 33.1 问题与判据

叶哥反馈「风扇控制有偏差」，给出判据：**标准转速设定 45% 时，转速应为 3000 RPM**。

修复前实测（本机 MS-15M3，CPU 74 °C、曲线节点 75 °C → 45%）：

| 项 | 值 |
|---|---|
| EC 0x71（CPU 占空比） | `0x2D` = **45** |
| 界面显示 | **4050 RPM**（= 45 × 90） |
| 叶哥判据 | **3000 RPM** |

### 33.2 根因

`PawnIoEcFanSource` 用「满速单点锚点」建立**一条过原点的直线**：
`RPM = 占空比 × 9000 / 100 = 占空比 × 90`。该假设在 45% 处给出 4050 RPM，
比判据 3000 RPM **偏高 35%**（4050/3000 = 1.35）。

即：**风扇的占空比—转速关系并不是过原点的单一直线**。低占空比段受启动死区与
最低转速平台影响，每 1% 占空比带来的转速增量明显小于高段；只用 (100%, 9000)
一个点去覆盖 0–100% 全区段，必然在中低段系统性高估。

### 33.3 修复

在 `Measurement`（单位换算唯一入口）新增两点折线标定，锚点：
`(0% → 0 RPM)`、`(45% → 3000 RPM)`、`(100% → 9000 RPM)`，低段与高段分别线性插值。

| 占空比 | 修复前（×90） | 修复后（折线） |
|---|---|---|
| 24% | 2160 | **1600** |
| 45% | 4050 | **3000**（判据点） |
| 60% | 5400 | 4636 |
| 100% | 9000 | 9000 |

- `Measurement.FanDutyToEstimatedRpm` / `FanRpmToEstimatedDuty`（含单调性、越界截断）。
- `PawnIoEcFanSource` 改走该换算，来源字符串标注「折线估算 + 两个锚点 + 非转速计实测」。
- `fan.calibration` 文案同步为两点锚点（能力等级仍为 Detected：无转速计，仍是估算）。

### 33.4 实机闭环（2026-10-31 21:22）

| 通道 | EC 原始值 | 修复后界面 | 判据 |
|---|---|---|---|
| CPU 0x71 | `0x2D` = 45 | **3000 RPM** | ✔ 45% → 3000 |
| GPU 0x89 | `0x18` = 24 | **1600 RPM** | ✔ 24/45 × 3000 = 1600 |

界面右上「风扇显示：RPM(R)」，主界面 CPU 卡片与 EC 直读一致，无「不可用」。
**边界不变**：EC 无转速寄存器，本值始终是估算；45–100 段斜率 109.1 RPM/%，
45% 以下斜率 66.67 RPM/%，折线在 45% 处存在斜率拐点，属两点标定的已知近似。

---

## 34. 温度驱动调试验证：曲线引擎在温度变化下确实改变占空比与转速（2026-10-31，来源 E24）

### 34.1 目的与方法

叶哥要求验证「温度不同时风扇转速是否改变」。方法：单次脚本
`scripts/verify_fan_temp_response.py` 完成 启动 → 点「应用曲线」（引擎接管，
EC 0xD4=advanced）→ 空闲采样 → 6 进程 CPU 满载 70 秒 → 撤载降温 85 秒，
每 1 秒同步读 EC `0x68`（CPU 温度）、`0x71`（CPU 占空比）、`0x80`/`0x89`（GPU）。
全程只读 EC 寄存器，写入仅由应用自身的曲线引擎发起。

当时生效曲线为「平衡」档槽位曲线：
`温度[0,50,55,60,75,81,87] → 占空比[10,10,10,10,45,45,60]`。

### 34.2 结果（168 个有效同步样本，按 3 °C 分箱取中位数）

| 温度区间 | 占空比中位数 | 曲线插值预期 | 估算转速（45%=3000 折线） |
|---|---|---|---|
| 63–66 °C | 24% | 21.7% | 1600 RPM |
| 66–69 °C | 29% | 28.7% | 1933 RPM |
| 69–72 °C | 39% | 33.3–38.0% | 2600 RPM |
| 72–75 °C | 40% | 38.0%（72 °C） | 2667 RPM |
| 75–78 °C | 45% | 45.0% | 3000 RPM |
| 84–87 °C | 55% | 55.0% | 4091 RPM |
| 87–90 °C | 60% | 60.0%（封顶） | 4636 RPM |
| 90–93 °C | 60% | 60.0%（封顶） | 4636 RPM |

- **温度 65 °C → 90 °C，占空比 24% → 60%，估算转速 1600 → 4636 RPM（2.9 倍）**。
- 实测占空比与曲线插值逐点吻合（68 °C 实测 29% 对插值 28.7%，70 °C 实测 33% 对 33.3%，
  87 °C 以上稳定封顶 60%），证明**插值链路与下发链路一致**。
- 空闲基线（引擎接管前）占空比恒为 45%、不随温度变化——那是固件自身曲线，
  与本次验证对象（软件曲线）形成对照。

### 34.3 对照组与离群值

- **GPU 通道（温度未变）**：全程 58–59 °C，占空比恒定 30%，转速不变——
  即「温度不变 → 转速不变」，与 CPU 侧构成反向对照。
- **离群值**：69–72 °C 区间出现少量 45%/50% 样本。成因是固件在 advanced 模式下
  ≤2 秒回写自身曲线值，与引擎 1 秒重写形成竞争，采样正好落在回写瞬间
  （HWE §23.3）。占比约 6%（10/168），不影响趋势。
- 采样偶发 `None`：EC 事务被应用与取证进程并发访问导致的瞬时超时（已重试），
  属已知现象（HWE §15）。

### 34.4 结论与边界

温度变化确实会改变风扇转速，且改变量与曲线定义一致；转速不是固定值，
而是每拍按「温度 → 曲线插值 → 升降速限幅（升 ≤+20%/拍、降 ≤−10%/拍）→ 下发」计算。
**边界**：EC 无转速寄存器，转速仍为两点折线估算；固件回写竞争使读回值在个别时刻
跳变，实际转速在两次写入之间由固件曲线短暂接管（约 1 秒窗口）。

---

## 35. GPU 风扇通道温度驱动验证（2026-10-31，来源 E25）

### 35.1 方法

本机无 CUDA/PyTorch，GPU 负载用 **WebGL 满载页**施加：`scripts/gpu_burn.html`
（1600×900 画布，片元着色器每像素 4096 次三角函数），经 Edge 无边框窗口打开。
实测把 GPU 推到 **利用率 98%、功耗 110 W、SM 2.5–2.6 GHz**（`nvidia-smi` 采样）。

脚本 `scripts/verify_gpu_fan_temp_response.py` 单次完成：
启动应用 → 点「应用曲线」→ 空闲 12 s → GPU 满载 110 s → 关浏览器降温 70 s，
每 1 s 读 EC `0x80`（GPU 温度）/`0x89`（GPU 占空比），每 3 s 读一次 NVML 作交叉校验。

生效曲线为「平衡」槽位 GPU 段：`温度[0,50,55,60,75,81,87] → 占空比[30,30,30,30,40,45,60]`。

### 35.2 结果（149 个有效样本，按 3 °C 分箱取中位数）

| GPU 温度 | 占空比中位数 | 曲线插值预期 | 估算转速 |
|---|---|---|---|
| 53–54 °C（接管前基线） | 24% | —（固件曲线） | 1600 RPM |
| 57–60 °C | 30% | 30.0% | 2000 RPM |
| 60–63 °C | 32% | 30.0–32.0% | 2133 RPM |
| 63–66 °C | 34% | 33.3%（65 °C） | 2267 RPM |
| 66–69 °C | 36% | 36.0%（69 °C） | 2400 RPM |
| 69–72 °C | 37% | 36.7%（70 °C） | 2467 RPM |
| 72–75 °C | 39% | 39.3%（74 °C） | 2600 RPM |
| 75–78 °C | 42.5% | 40.0–42.0% | 2833 RPM |
| 78–81 °C | 44% | 43.2–45.0% | 2933 RPM |
| 81–84 °C | 45% | 45.0%（81 °C） | 3000 RPM |

- **GPU 温度 57 °C → 81 °C，占空比 30% → 45%，估算转速 2000 → 3000 RPM。**
- 实测中位数与曲线插值逐点吻合（65 °C 实测 34% 对插值 33.3%，70 °C 实测 37% 对 36.7%，
  81 °C 实测 45% 对 45.0%），GPU 通道的插值—下发链路与 CPU 通道同样正确。
- **接管对照**：引擎接管前为 24%（固件曲线），接管后同一温度段变为 30%（自定义曲线），
  与上一轮 CPU 侧的 45%→24% 对照同向。

### 35.3 离群与边界

- 75–81 °C 区间的 `max 60%` 样本为固件回写瞬间（advanced 下 ≤2 s 回写自身曲线），
  与 CPU 侧同一成因；中位数不受影响。
- 降温段 61–63 °C 读到 31–32%，比插值 30% 略高 1–2 个百分点，来源是引擎 2 °C 下行迟滞
  （求值温度滞后于实际读数），属设计内行为。
- GPU 峰值 81 °C 未触到曲线 87 °C 的封顶段（110 W 负载下温度稳定在 80 上下）。
  RTX 5070 Ti Laptop 的风扇转速本身不可由 NVML 读取（`fan.speed` 恒 `[N/A]`，
  HWE §12），因此 GPU 侧同样只有「占空比 + 折线估算」，无转速计实测。

---

## 36. 「0% 占空比 = 完全停转」可行性受控实验：本机不成立（2026-11-01，来源 E26）

### 36.1 问题

叶哥询问能否让风扇在设定 0 时**完全停止转动**。本节用受控写入实验直接回答，
并给出实测的触发条件与失败点。

工具：`scripts/probe_fan_zero_duty.py`（写 0 + 逐 100 ms 观察）、
`scripts/probe_fan_zero_hold.py`（保持性）、以及独立的负载温升测试。

### 36.2 实测结果

| 步骤 | 操作 | 观察 |
|---|---|---|
| 1 | `0xD4=0x8D`（advanced） | 读回一致 |
| 2 | 写 CPU `0x71 = 0` | **立即读回 0**，随后 6 秒 55/55 帧保持 0 |
| 3 | 写 GPU `0x89 = 0` | 立即读回 0，**100 ms 后即被固件写回 24%**，之后 54/55 帧为 24% |
| 4 | 停止写入后写 `0x71 = 20`（0x14） | 读回一致，**500 ms 内被固件改写回 0** |
| 5 | 高频压写 `0x71`（每 150–200 ms，20 次）为 100%/60% | 压写期间保持目标值；**停止后 2 秒内一律回到 0** |

### 36.3 关键判定：写 0 并不产生物理停转

在 `0x71 = 0` 状态下施加 CPU 负载（8 个忙循环进程，30 秒）：
**CPU 温度 61 °C 全程纹丝不动**（对照：HWE §34 中风扇正常工作时同量级负载可推动温度至 90 °C 以上）。

结论：**EC 占空比寄存器写 0 只是寄存器回显，风扇物理上仍在转动**。
固件把 0 视作无效/最小值并继续用自身策略驱动风扇，本机不存在「PWM = 0 → 停转」的对应关系。

### 36.4 副作用（写入 0 的真实代价）——更正与追加实测（2026-11-01 09:55）

写入 0 之后，固件进入**持续把该寄存器回写为 0** 的状态（CPU 侧周期 ≤1 s，GPU 侧 ≤100 ms）。
此后软件写入任何非 0 值都会在一个固件周期内被覆盖：
实测压写 100% 停止后 2 秒即回到 0。软件曲线引擎（1 秒周期）**无法再压住**，
读回校验会连续失败并在 20 拍后自动停机。

**该状态是持久的，重启不能清除**（推翻本节初版「重启即可恢复」的判断）：

| 尝试 | 时间 | 结果 |
|---|---|---|
| 切 silent（0xD4=0x1D）保持 20 s 再回 auto | 09:56 | 仍为 0 |
| 切节能档（0xD2=0xC2）10 s 再回狂暴（0xC4） | 09:56 | 仍为 0 |
| 强冷（0x98 bit7）触发窗口内压写 45% 30 次 | 09:56 | 仍为 0 |
| 长时间压写 45% 30 秒（240 次） | 09:54 | 停止后 1 s 回 0 |
| **系统重启**（09:51:57 冷启动） | 09:53 | **仍为 0** |

同时有两个反向事实，界定故障范围：

- **风扇物理正常**：8 进程满载 25 秒，CPU 温度仅 51 → 54 °C，散热良好；
- **固件控制正常**：强冷（0x98 bit7）经叶哥确认在听感上有效。

因此这不是硬件或固件损坏，而是 **CPU 风扇通道（0x71）的「用户目标值」被固件锁定为 0**：
寄存器回显永远是 0，实际驱动仍由固件策略掌握。软件侧已无法覆盖。

**恢复路径（待验证）**：BIOS 加载默认设置（重写 EC 配置）或完全断电放电
（关机 → 断适配器 → 长按电源 20 秒 → 开机），使 EC 的持久变量复位。
普通重启、模式切换、档位切换、强冷、高频压写均已验证无效。

### 36.5 若要做成功能，需要满足的条件（本机均不满足）

1. **存在真正的停转通道**：EC 有独立的 fan-off 位，或固件接受 0 且把它当作停转指令。
   本机实测：0 被当作无效值，物理不停转 → 不满足。
2. **写入可保持**：写 0 后至少 3 个固件回写周期内无回写。本机：CPU ≤1 s、GPU ≤100 ms 即被覆盖 → 不满足。
3. **可自证停转**：需要有转速计或可观测的风量/温升证据。本机 EC 无转速寄存器
   （HWE §12、§19），只能靠温升间接推断 → 不满足。

三条同时成立才可向用户宣称「已停转」。本机在 1、2 两条即失败，因此
**「0% → 完全停转」在本机不可实现**，任何声称做到的实现都只能是虚假状态。

### 36.6 现行产品行为（已实现，正确）

曲线目标为 0% 时**不写 0**，而是 `ReleasedToFirmware`：停止对该通道写入，
由固件自身低转速曲线维持，界面如实显示「曲线目标 0%：已交还固件调速」。
理由：写 0 会引发上述回写污染，风扇表现为「转一下停一下」，比不定速更糟
（原记录见 HWE §23.3 与 `docs/风扇控制说明.md`）。
