# MSI-Control 模块集成说明

本文说明 MSI-Control 的**模块集成范围、集成方式与集成纪律**。核心状态表不是手写维护的，
而是由代码中唯一的来源 `ModuleRegistry` 生成后逐字引用，保证「文档说已接入」与
「代码里真的装配了」永远一致。

> **表格来源声明**：第 2 节的状态表是 `ModuleRegistry.ToMarkdownTable`
> 输出的**逐字复制**（含标点与空格，未做任何改写）。生成与复测方法见第 5 节。

---

## 1. 集成范围

### 1.1 工程与任务对应关系

解决方案 `MSI-Control.sln` 共 18 个工程：9 个生产工程（`src/`）+ 9 个测试工程（`tests/`），
按任务书 §15.1 的文件归属矩阵分工开发：

| 任务 | 生产工程 | 测试工程 |
|---|---|---|
| 01 基础接口 / MSI 硬件适配 | `src/MsiControl.Core`、`src/MsiControl.Hardware.Msi` | `tests/MsiControl.Core.Tests`、`tests/MsiControl.Hardware.Msi.Tests` |
| 02 桌面框架 / 配置持久化 | `src/MsiControl.Desktop`、`src/MsiControl.Persistence` | `tests/MsiControl.Desktop.Tests`、`tests/MsiControl.Persistence.Tests` |
| 03 实时监控 | `src/MsiControl.Monitoring` | `tests/MsiControl.Monitoring.Tests` |
| 04 风扇控制 | `src/MsiControl.Fans` | `tests/MsiControl.Fans.Tests` |
| 05 性能与功耗 | `src/MsiControl.Performance` | `tests/MsiControl.Performance.Tests` |
| 06 显卡切换 | `src/MsiControl.Graphics` | `tests/MsiControl.Graphics.Tests` |
| 07 自动策略与整合 | `src/MsiControl.Application` | `tests/MsiControl.Application.Tests` |

### 1.2 组合根（集成入口）

运行时只有**单一常驻进程**，源码分模块不等于发布后运行多个程序（design.md §13.3）。
集成通过一条组合根链完成（`src/MsiControl.Desktop/Program.cs`）：

```text
Program.Main（单实例互斥体 / 唤醒通道）
  └─ ApplicationHost.CreateDefault      设备识别 → 能力基线 → 控制协调器 → 自动策略
       └─ ShellContextFactory.CreateDefault   桌面壳上下文（窗口、托盘、日志）
            └─ DesktopApplicationContext → Application.Run
```

依赖方向（design.md §13.1）：`Desktop → Application →（各业务模块 / Hardware.Msi）→ Core`；
各业务模块彼此不互相引用，也不引用 `Desktop` 或 `Application`。桌面壳自身
**不探测硬件、不硬编码能力等级**，只消费宿主装配好的协调器与能力基线。

### 1.3 「已接入」的判定标准

`ModuleRegistry` 的纪律原文：**只有真正被装配使用的模块才标记为「已接入」**。
工程存在但未注入协调器时，状态必须是「未接入」并写明原因；界面侧对未接入功能
一律禁用并说明原因，不使用模拟数据。

---

## 2. 模块接入状态表

采集方式：在目标机型（MSI Vector 16 HX AI A2XWHG / MS-15M3）上运行
`ApplicationHost.CreateDefault()` 装配出的组合根，直接取 `host.Modules`
过 `ModuleRegistry.ToMarkdownTable` 的输出。采集时间：2026-10-16。

| 模块 | 负责任务 | 工程 | 接入状态 | 依据 / 阻塞原因 |
|---|---|---|---|---|
| 基础接口 Core | 01 | `src/MsiControl.Core` | 已接入 | 共享契约（模型、接口、能力等级、结果类型）已被应用层与桌面壳使用。 |
| MSI 硬件适配层 Hardware.Msi | 01（骨架）/ 04 / 05 / 06 | `src/MsiControl.Hardware.Msi` | 只读接入 | 能力基线与只读 Discovery/Transport 已被应用层使用；写入通道（Transport 的 Set_*）按约定保持关闭，需对应能力达「已确证」且由 04/05/06 公告后开启。 |
| 配置持久化 Persistence | 02 | `src/MsiControl.Persistence` | 已接入 | 设置文件、配置档校验与轮转日志已被桌面壳使用。 |
| 桌面壳 Desktop | 02 | `src/MsiControl.Desktop` | 已接入 | 正常/迷你窗口、托盘、快捷键已接入；控制出口由本任务的协调器提供。 |
| 实时监控 Monitoring | 03 | `src/MsiControl.Monitoring` | 已接入 | 已提供采样实现并注入共享采样流。 |
| 风扇 Fans | 04 | `src/MsiControl.Fans` | 已接入 | 风扇适配层已注入协调器。 |
| 性能与功耗 Performance | 05 | `src/MsiControl.Performance` | 已接入 | 性能适配层已注入协调器。 |
| 显卡 Graphics | 06 | `src/MsiControl.Graphics` | 已接入 | 显卡适配层已注入协调器。 |
| 应用层 Application（本任务） | 07 | `src/MsiControl.Application` | 已接入 | 协调器、启动/供电/退出策略、模块状态表、第三方共存检测与宿主装配已交付。 |

同一份数据经 `ModuleRegistry.Summarize` 生成的一行摘要（逐字复制，启动日志同款）：

```text
模块接入状态：已接入 8，只读接入 1
```

---

## 3. 集成方式要点

1. **组合根装配语义**：`ApplicationHost.CreateDefault` 对监控、风扇、性能、显卡四个模块参数
   采用「为空时装配真实实现」的语义（任务 07 提示词（六）「先接可用的模块」），
   即任务 02–06 交付后由宿主统一装配，而不是留给界面层各自创建。
2. **共享只读通道**：四个业务模块共享同一个 `WmiMsiAcpiTransport` 实例。
   Transport 无状态（每次调用新建 `ManagementScope`），共享可避免重复初始化；
   其白名单只放行 `Get_*`，从机制上不可能写入硬件。
3. **释放所有权分离**：由宿主内部创建的监控 provider 由宿主负责释放
   （`ownsMonitoringProvider`，内部持有 NVML 会话等资源）；调用方显式传入的
   实现以传入为准，释放责任留给调用方。
4. **能力门控先于适配层调用**：所有写入先经 `ControlCoordinator` 的能力门控
   （`DeviceCapabilities.AllowsWrite`，仅 `CapabilityLevel.Verified` 放行），
   未达「已确证」的能力键一律拒绝；门控发生在调用硬件适配层**之前**，
   因此相关回归测试不触碰硬件也能验证安全语义。
5. **装配失败降级**：宿主装配抛异常时，桌面壳记录原因并以「未适配」状态运行，
   全部写入入口保持禁用，不会出现「界面打不开」或「假装能用」两种极端。
6. **桌面壳零硬件接触**：窗口只订阅采样结果、只发出控制请求
   （任务书 §16），`IFanHardware` / `IPerformanceHardware` / `IGraphicsHardware`
   的实现从不被界面层直接调用。

---

## 4. 注意事项

- **状态表是某一时点的快照，代码是唯一来源。** 若本文表格与代码输出的
  `ModuleRegistry.ToMarkdownTable` 不一致，以代码为准，并按第 5 节重新生成后更新本文。
- **「只读接入」不等于「不可用」。** Hardware.Msi 的能力基线与只读
  Discovery/Transport 已被应用层使用；只是其写入通道（`Set_*`）按约定保持关闭，
  开启条件是「对应能力达『已确证』且由 04/05/06 公告」——这是约定而非缺陷。
- **本机写入现状**：目标机型能力基线共 15 个能力键，当前仅 `thermal.fallback`
  为 `Verified`（可写入）；其余为 `Detected` / `Unknown`。因此性能档位、风扇曲线、
  显卡模式等写入入口当前被能力门控阻止，协调器返回 `BlockedByCapability`
  并以原因说明呈现，界面不提供「假成功」。
- **第三方共存检测只报告不干预**：宿主启动时做一次进程扫描。本机实测样本为
  检测到 Intel Extreme Tuning Utility（进程 `XtuService`）——检测到重叠时只报告，
  不修改或删除第三方配置、不自动停止或卸载，也不通过重复写入争夺控制权（任务书 §11）。
- **遗留项（如实登记，不臆造进度）**：
  - `ILastAppliedConfigSource`：当前实现为「无记录」占位；「最近成功应用配置」的
    记录产生与落盘尚未实现（待补充）。
  - `SnapshotStream.Stop()` 存在已知竞态（P2，待处理）。
  - 五场景资源矩阵（正常窗口 / 迷你窗口 / 托盘待机 / 风扇编辑 / 校准过程）
    尚未完整测量，见 `docs/resource-baseline.md` 第 6 节待补充项。

---

## 5. 表格的生成与复测

状态表由以下任一途径取得，三条途径**共用同一份代码来源**，结果必然一致：

1. **启动日志**：桌面壳启动时输出 `ModuleRegistry.Summarize(host.Modules)`
   一行摘要与 `Coordinator.AdapterAvailability.Describe()`（轮转日志，任务 02 提供）。
2. **诊断 harness**（复测本文表格用）：新建控制台工程引用
   `src/MsiControl.Application`，调用组合根后打印：

   ```csharp
   using var host = ApplicationHost.CreateDefault();
   Console.WriteLine(ModuleRegistry.ToMarkdownTable(host.Modules));
   ```

3. **回归测试**：`tests/MsiControl.Application.Tests/IntegrationTests.cs`
   断言 `ModuleRegistry.NotIntegratedNames(host.Modules)` 为空、
   03–06 四个模块均为 `ModuleIntegrationState.Integrated`，
   并验证能力门控在组装真实模块后仍阻止写入
   （`ControlOutcomeKind.BlockedByCapability`）。

> 说明：harness 只做只读装配与打印，不向硬件写入；`CreateDefault()` 会真实读取
> 注册表 SMBIOS 与进程列表，均为只读操作。
