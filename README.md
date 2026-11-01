# MSI-Control

**面向 MSI 笔记本的本地 Windows 控制台** —— 以 G-Helper 的轻量、紧凑和直接操作方式为主要参考，
在完整功能与控制可靠性的前提下，尽量降低内存、CPU、安装体积与启动耗时。
不依赖云账号，不自动上传硬件数据，不提供自动更新检查（design.md §1）。

- **语言 / 技术栈**：C# / .NET 8 / WinForms，单一常驻进程，framework-dependent 发布
- **版本**：v1.00
- **当前状态**：开发中，尚未发布安装包；七项任务已全部交付并通过集成回归，
  Release 构建 0 警告 0 错误，971 / 971 项测试通过（2026-10-31）
- **写入类功能现状**：受能力等级门控约束，目标机型当前仅 `thermal.fallback`
  达到可写入等级（`Verified`）；其余写入入口如实返回「被能力阻止」并说明原因，
  不做任何模拟成功（见 `docs/integration.md`）

---

## 1. 项目定位与核心能力

定位：针对 MSI 笔记本的本地控制工具，**首要适配设备为 MSI Vector 16 HX AI A2XWHG / MS-15M3**。
硬件控制采用可扩展适配器结构，按功能判断能力，不把同品牌或同系列视为全功能支持。

核心能力（design.md §3）：

| 类别 | 内容 |
|---|---|
| 监控 | 实时硬件监控（温度、频率、功耗、风扇、GPU、电池侧功率），采样与窗口生命周期解耦 |
| 风扇 | CPU / GPU 风扇曲线控制、强冷与风扇校准 |
| 性能 | 五档可编辑性能模式、CPU / GPU 功耗限制、CPU 睿频上限 |
| 显卡 | 核显 / 混合 / 独显模式切换（重启待生效状态如实呈现） |
| 界面 | 正常窗口 + 迷你窗口、托盘、开机启动、全局快捷键，简体中文，跟随系统明暗主题与 DPI |
| 配置 | 配置管理、配置档校验与迁移、本地诊断与日志导出 |

明确**不包含**（design.md §3）：电池保护与校准、灯效、屏幕校色、BIOS / EC 刷写、
FPS 叠加层、GPU 超频与 VF 曲线编辑、云账号 / 遥测 / 自动更新。

## 2. 目标设备

| 项目 | 信息 |
|---|---|
| 型号 | MSI Vector 16 HX AI A2XWHG |
| 主板 | MS-15M3 REV:1.0 |
| CPU | Intel Core Ultra 9 275HX |
| 独显 / 核显 | NVIDIA GeForce RTX 5070 Ti Laptop GPU / Intel Graphics |
| BIOS | E15M3IMS.117（本机实测，注册表 SMBIOS 读取） |
| 操作系统 | Windows 11 专业版 x64，Build 26300 |

其他机型可运行，但未适配机型的能力基线为「全禁用」：监控尽量兼容，
写入一律拒绝并说明原因（design.md §2.2）。

## 3. 环境要求

| 用途 | 要求 |
|---|---|
| 开发 / 构建 | Windows 10 或 11（x64）+ .NET SDK 8.0（本仓库用 8.0.426 构建，目标框架 `net8.0` / `net8.0-windows`） |
| 运行 Release 产物 | 系统需安装 `Microsoft.WindowsDesktop.App` 8.0.x 运行时（framework-dependent 桌面壳；缺失时启动器会提示安装） |

无需驱动、无需特权进程、无需额外的 Windows 服务（design.md §13.1）；
是否需要驱动由实际接口决定，当前实现全部通过 WMI / PDH / NVML / ACPI 等系统既有通路完成。

## 4. 构建

在仓库根目录（`MSI-Control.sln` 所在目录）执行：

```bash
dotnet build MSI-Control.sln -c Release --nologo -v q
```

预期结果：0 警告 0 错误，18 个工程全部生成
（构建与测试的实测记录见 `docs/构建验证记录.md`，只追加不覆盖）。

## 5. 运行

```bash
src/MsiControl.Desktop/bin/Release/net8.0-windows/MsiControl.Desktop.exe
```

行为说明：

- **单实例**：重复启动不打开第二个窗口，而是唤醒现有实例并把窗口带到前台。
- **首次启动**会读取设备标识与能力基线并输出装配摘要到轮转日志；
  在未识别机型上以「未适配」状态运行，全部写入入口禁用。
- 退出方式：托盘 / 窗口上的「完全退出」独立入口；隐藏窗口不等于退出
  （隐藏后托盘驻留、采样继续）。

## 6. 测试

```bash
dotnet test MSI-Control.sln -c Release --nologo
```

预期结果：**737 项测试全部通过**（2026-10-16 基线）。
测试纪律：硬件写入路径由能力门控先行拦截，因此绝大多数用例**不需要真实硬件**；
涉及真实数据的用例集中在 `RealHardwareTests`（只读）并按门控条件跳过而非失败。
多会话并发写 `bin/` 时偶发 hostpolicy 进程竞态（非源码问题），可用 `--no-build` 复跑确认。

## 7. 目录结构

```text
F:\Codex programs\msi控制台\
├─ MSI-Control.sln               解决方案（18 工程）
├─ Directory.Build.props         全局编译属性（Nullable / ImplicitUsings / InvariantGlobalization 等）
├─ MSI-Control-开发总流程与任务书.md   开发交接依据（v1.2，含修订目标 G1–G5 与文件归属矩阵）
├─ src/
│  ├─ MsiControl.Core            共享契约：模型、接口、能力等级、结果类型（任务 01）
│  ├─ MsiControl.Hardware.Msi    MSI 硬件适配：能力基线、只读 Discovery/Transport（任务 01 骨架）
│  ├─ MsiControl.Persistence     设置文件、配置档校验、轮转日志（任务 02）
│  ├─ MsiControl.Desktop         WinForms 桌面壳：正常/迷你窗口、托盘、快捷键（任务 02）
│  ├─ MsiControl.Monitoring      统一采样与数据有效期（任务 03）
│  ├─ MsiControl.Fans            曲线、校准、强冷与恢复（任务 04）
│  ├─ MsiControl.Performance     档位、功耗与睿频（任务 05）
│  ├─ MsiControl.Graphics        显卡模式与待生效状态（任务 06）
│  └─ MsiControl.Application     协调器、启动/供电/退出策略、模块状态表、组合根（任务 07）
├─ tests/                        9 个测试工程，与生产工程一一对应
└─ docs/                         设计、接口、实测证据、构建记录等（见第 11 节索引）
```

## 8. 快速上手

**路径 A：只想跑起来看效果**

```bash
dotnet build MSI-Control.sln -c Release
src/MsiControl.Desktop/bin/Release/net8.0-windows/MsiControl.Desktop.exe
```

**路径 B：在自己的程序里装配宿主（组合根即 API）**

新建控制台工程，引用 `src/MsiControl.Application`：

```csharp
using MsiControl.Application.Host;
using MsiControl.Application.Integration;

using var host = ApplicationHost.CreateDefault();
host.Start();                                   // 供电监听与启动恢复评估
Console.WriteLine(host.Device.Model);           // Vector 16 HX AI A2XWHG
Console.WriteLine(host.Capabilities.Count);     // 15（本机）
Console.WriteLine(ModuleRegistry.ToMarkdownTable(host.Modules));  // 模块状态表
```

这段代码与桌面壳共用同一条组合根链：设备识别 → 能力基线 → 协调器 → 自动策略。
所有写入经协调器能力门控；在未确证的能力上会得到 `BlockedByCapability`
而不是「假装成功」。

## 9. 核心纪律（改代码前必读）

1. **能力等级四档**：`Verified`（已确证）/ `Detected`（已探测）/ `Unknown`（未知）/
   未适配（不存在）。`AllowsWrite` 的唯一条件是 `Verified`；`AllowsRead` 为
   `Verified` 或 `Detected`。没有证据不升格。
2. **读回未确认不称成功**：命令返回成功、模拟数据、低负载读数都不算控制生效。
3. **不可用项如实呈现**：没有可靠来源的读数返回 `Unavailable` 并附原因，
   绝不用不可混用的口径互相顶替（如不用 CPU+GPU 功耗之和顶替整机功耗）。
4. **不猜测硬件语义**：不猜 EC 寄存器地址、方法参数与数值含义；WMI 类存在
   只是线索，不是证据。
5. **噪声类排除**：`MSiSCSI_*`、`MSIde*`、`ISR_MSI` 等与本品牌无关的项不得作为能力证据。

## 10. 维护与贡献

- **任务分工与文件归属**：任务书 §15.1 的文件归属矩阵是权威；跨任务修改前先读它，
  不覆盖他人交付物。
- **构建 / 测试记录**：`docs/构建验证记录.md` **只追加不覆盖**；每次有意义的
  构建、修复、实测都应留一条带日期与结论的记录。
- **文档纪律**：术语与代码保持一致；不确定信息标注「待补充」，不臆造；
  模块状态表之类由代码生成的表格不得手写维护（见 `docs/integration.md`）。
- **测试门槛**：提交前 `dotnet test MSI-Control.sln -c Release` 全绿；
  新功能补回归测试，硬件相关用例走只读门控。
- **许可证**：待补充（design.md §15：复用第三方代码前确认许可证、署名与分发义务，
  项目许可证在完成依赖审查后确定）。
- **CI**：待补充（仓库当前无 CI 配置文件）。
- **版本控制 / Issue 流程**：待补充（当前工作目录尚未初始化为 git 仓库；
  设计目标为公开开源）。

## 11. 文档索引

| 文档 | 内容 |
|---|---|
| `MSI-Control-开发总流程与任务书.md` | 开发总流程、七项任务分工、修订目标 G1–G5 |
| `docs/design.md` | 设计定稿（正文冻结，变更须用户批准） |
| `docs/hardware-evidence.md` | 本机硬件实测证据（14 节，全只读） |
| `docs/接口说明.md`、`docs/接口与工程约定.md` | 共享接口与工程约定（能力等级制度、单位口径） |
| `docs/integration.md` | 模块集成说明（状态表由 `ModuleRegistry` 生成） |
| `docs/research.md` | 调研与选型记录 |
| `docs/resource-baseline.md` | 资源基线（CPU / 内存 / 耗时 / 体积 / 依赖数） |
| `docs/构建验证记录.md` | 构建与测试记录（只追加） |
| `docs/监控实机扫描记录-2026-10-16.txt` | 监控层实机扫描原始记录 |
| `docs/七个任务提示词.md`、`docs/prompts/` | 七项任务提示词原件 |

## 12. 已知限制与待补充项

- 尚未发布安装包；当前以 `dotnet build` 产物直接运行（安装包体积待补充）。
- 「最近成功应用配置」的记录产生与落盘（`ILastAppliedConfigSource`）尚未实现。
- `SnapshotStream.Stop()` 存在已知竞态（P2，待处理）。
- 五场景资源矩阵（正常窗口 / 迷你窗口 / 托盘待机 / 风扇编辑 / 校准过程）尚未完整测量，
  已测部分见 `docs/resource-baseline.md`。
- WinForms 与 WPF 的对照实测未执行（design.md §13.2 的选型结论以「优先验证 C# + WinForms」
  的设计决定为准，对照数据待补充）。
