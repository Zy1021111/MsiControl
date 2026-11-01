# MSI-Control 资源基线

本文登记 MSI-Control 的**资源占用基线**：CPU、内存、耗时、发布体积、依赖数量。
所有数值均为目标机型实机实测，注明采集环境、采集方式与口径；未执行的测量在
第 6 节列为待补充，不用估计值填充。

---

## 1. 采集环境

| 项目 | 值 |
|---|---|
| 采集日期 | 2026-10-16（采样扫描 20:29；Release 构建 20:47；GUI 启动测量 21:10） |
| 机型 | MSI Vector 16 HX AI A2XWHG / MS-15M3（Intel Core Ultra 9 275HX，NVIDIA GeForce RTX 5070 Ti Laptop GPU） |
| BIOS | E15M3IMS.117 |
| 操作系统 | Windows 11 专业版 x64，Build 26300 |
| 运行时 | .NET 8（CLR 8.0.32；SDK 8.0.426 构建） |
| 构建配置 | Release；目标框架 `net8.0` / `net8.0-windows`；`Deterministic=true` |
| 供电状态 | 采样期间接交流电（电池侧读数因此为 0 / 不可得） |
| 并发因素 | 采样期间 Intel XTU 服务（`XtuService`）在运行（第三方占用，见 `docs/integration.md` §4） |

## 2. 采集方式与来源

| 指标 | 采集方式 | 原始记录 |
|---|---|---|
| 采样耗时 / CPU / 内存 | 监控宿主进程内跑 20 次采样循环（每 1 秒一次），统计单次耗时与进程计数器增量 | `docs/监控实机扫描记录-2026-10-16.txt` §2、§6 |
| 各数据源读取耗时 | 同一循环内对每个数据源单独计时，20 次取最小 / 平均 / 最大 | 同上 §2 |
| 启动耗时 | 直接启动 `MsiControl.Desktop.exe`（framework-dependent，冷启动），以日志时间戳分段 | 本节 §3.5 |
| 发布体积 | 对 Release 输出目录逐目录 `du -sk`；对关键程序集取字节数 | 本节 §3.6 |
| 依赖数量 | 扫描 18 个工程的 csproj（`PackageReference` / `ProjectReference`） | 本节 §3.7 |
| 测试规模 | `dotnet test MSI-Control.sln -c Release` | `docs/构建验证记录.md` |

## 3. 基线数值

### 3.1 CPU 占用（仅监控宿主，每秒一次采样）

| 指标 | 值 | 口径 |
|---|---|---|
| 采样期总 CPU 时间增量 | 375 ms | 约 20 秒挂钟时间（20 次循环） |
| 每采样周期占单核比例 | **1.875 %** | 375 ms ÷ 20 s；含全部数据源的读取 |
| 单核满载参照 | 100 % | —— |

### 3.2 内存占用（同一宿主进程）

| 指标 | 值 |
|---|---|
| 工作集（采样前 → 采样后） | 85.05 MiB → 89.57 MiB（+4.52 MiB） |
| 私有内存 | 40.42 MiB |

> 口径：以上为**监控层宿主进程**测量，不含窗口 / 托盘等 UI 常驻开销；
> 界面两态差异属 UI 绘制开销，尚未单独测量（见第 6 节）。

### 3.3 采样耗时

| 指标 | 值 |
|---|---|
| 单次采样耗时（20 次） | 最小 2.742 ms / 平均 16.018 ms / 最大 79.072 ms |

> 最大值的构成：首周期含 NVML 初始化。hardware-evidence.md §14.7 的早期
> 「仅监控层」测量为平均约 17 ms、最大约 81 ms，与本次同量级，互为交叉印证。

### 3.4 各数据源单次读取耗时（20 次统计）

| 数据源 | 最小 | 平均 | 最大 | 返回类型 |
|---|---|---|---|---|
| ACPI 热区温度 | 1.418 ms | 3.099 ms | 31.731 ms | SensorReading |
| 处理器性能计数（频率） | 0.143 ms | 0.544 ms | 5.843 ms | SensorReading |
| 处理器性能计数（利用率） | 0.078 ms | 0.295 ms | 1.164 ms | ExtendedSensor[] |
| 内存占用（Win32_OperatingSystem） | 2.71 ms | 3.128 ms | 6.901 ms | ExtendedSensor[] |
| 电池侧功率（BatteryStatus） | 1.483 ms | 1.711 ms | 2.881 ms | SystemPowerReading |
| 独显在线性探测 | 12.485 ms | 15.738 ms | 64.096 ms | GpuPresence |
| 独显 NVML 四项 | 0.001 ms | 2.486 ms | 49.674 ms | GpuSnapshot |
| 独显 NVML 扩展项 | 0.03 ms | 0.198 ms | 3.274 ms | ExtendedSensor[] |
| EC 固件响应（首次） | 17.307 ms | 17.307 ms | 17.307 ms | EcFirmwareReading |

> 对照：WMI 性能计数类 `Win32_PerfFormattedData_Counters_ProcessorInformation`
> 单次访问阻塞约 270 ms（hardware-evidence.md §14.4），已因此在选型中否决，
> 改用 PDH（热路径约 0.6 ms）。

### 3.5 启动耗时（桌面壳，冷启动）

| 区段 | 值 | 说明 |
|---|---|---|
| 进程启动 → 首条日志 | 611 ms | 外部计时，100 ms 轮询粒度（±100 ms） |
| 首条日志 → 窗口显示 | 2.02 s | 日志时间戳 21:10:51.842 → 21:10:53.862 |
| 进程启动 → 窗口显示（合计） | ≈ 2.63 s | 含 CLR 初始化、宿主装配（设备标识 + 能力基线 + 协调器）、窗口创建 |

> 首条日志之前的 611 ms 主要是 apphost / CLR 启动；装配摘要（设备、能力数、
> 模块状态、第三方检测）即首条日志之后输出。

### 3.6 发布体积（Release 输出目录）

| 目录（`bin/Release/`） | 体积 |
|---|---|
| `MsiControl.Desktop`（net8.0-windows） | **1465 KiB**（运行时需要的最小完整目录） |
| `MsiControl.Application`（net8.0-windows） | 696 KiB |
| `MsiControl.Monitoring`（net8.0-windows） | 352 KiB |
| `MsiControl.Fans`（net8.0-windows） | 336 KiB |
| `MsiControl.Graphics`（net8.0-windows） | 256 KiB |
| `MsiControl.Hardware.Msi`（net8.0-windows） | 220 KiB |
| `MsiControl.Persistence`（net8.0） | 176 KiB |
| `MsiControl.Performance`（net8.0） | 164 KiB |
| `MsiControl.Core`（net8.0） | 97 KiB |

关键单文件：`MsiControl.Desktop.exe`（apphost）152,576 B；`MsiControl.Desktop.dll`
121,344 B；`MsiControl.Application.dll` 101,888 B。

> 口径说明：
> 1. 每个目录包含其**全部传递依赖副本**，因此目录体积不可跨工程相加；
>    实际分发只取 Desktop 目录（1465 KiB）+ 系统已装的 WindowsDesktop 8.0.x 运行时。
> 2. 另有 `MsiControl.Hardware.Msi/bin/Release/net8.0`（120 KiB）为构建过程残留的
>    旧 TFM 产物目录，不参与运行，可在清理构建时移除。
> 3. 安装包（自包含 / MSIX / Setup）体积**待补充**（第 6 节）。

### 3.7 依赖数量

| 类别 | 数量 | 明细 |
|---|---|---|
| 生产代码外部 NuGet 包 | **1** | `System.Management` 8.0.0（`MsiControl.Hardware.Msi` 与 `MsiControl.Monitoring` 各引用一份） |
| 测试代码外部 NuGet 包 | 3 | `Microsoft.NET.Test.Sdk` 17.11.1、`xunit` 2.9.2、`xunit.runner.visualstudio` 2.8.2 |
| 工程间 ProjectReference | 16 条 | Application 6；Desktop 3；Monitoring 2；Fans 2；Graphics 2；Hardware.Msi 1；Persistence 1；Performance 1；Core 0 |
| 系统 P/Invoke | 3 项 | `pdh.dll`（CPU 计数）、`nvml.dll`（NVIDIA 驱动提供，进程内）、WMI（系统组件，经 System.Management） |

> 生产代码零第三方硬件库：未引入 LibreHardwareMonitor 等外部硬件依赖，
> 硬件访问全部经由系统既有通路与自研适配层完成。

### 3.8 测试规模与构建

| 指标 | 值 |
|---|---|
| 测试通过数 | 737 / 737（2026-10-16，Release） |
| 构建告警 / 错误 | 0 / 0（18 个工程） |
| 硬件相关测试 | 走只读门控（能力未达 `Detected` 以上即跳过并登记，不伪造通过） |

## 4. 口径与限制

1. **单机单次测量**：全部数据来自上述环境的一次实测，不代表其他机型 / BIOS / 驱动版本。
2. **监控层 vs 常驻进程**：§3.1–§3.4 是监控宿主（无窗口）的数值；桌面壳常驻内存
   应高于此（窗口、托盘、日志），差异未测（第 6 节）。
3. **冷启动路径**：启动计时为冷启动；NVML 首次初始化开销计入首周期采样最大值。
4. **交流电状态**：电池侧放电 / 充电功率在采样期间不可得，属口径限制而非缺陷。
5. **并发噪声**：XTU 服务在采样期间运行，CPU 数值含其影响；未做静默环境对照测量。

## 5. 复测方法

```bash
# 1) 构建（应与基线一致：0 警告 0 错误）
dotnet build MSI-Control.sln -c Release --nologo -v q

# 2) 全量测试（应全绿；当前基线 737 项）
dotnet test MSI-Control.sln -c Release --nologo

# 3) 体积（Release 输出目录逐项目统计）
du -sk src/*/bin/Release/net8.0*

# 4) 采样与各数据源耗时：重放任务 03 的实机只读扫描（见 docs/监控实机扫描记录-2026-10-16.txt
#    的生成方式：监控宿主 20 次循环，逐数据源计时 + 进程计数器增量）

# 5) 启动耗时：直接运行 MsiControl.Desktop.exe，比对轮转日志首条时间戳
#    与窗口显示时刻
```

## 6. 待补充项

| 事项 | 说明 |
|---|---|
| 五场景资源矩阵 | design.md §14.3 要求分别测量：正常窗口 / 迷你窗口 / 托盘待机 / 风扇编辑 / 校准过程；当前仅有监控宿主与冷启动两态数据 |
| UI 常驻增量 | 桌面壳（窗口 + 托盘 + 日志）相对监控宿主的内存 / CPU 增量 |
| 安装包体积 | 自包含或安装包制作后的分发体积 |
| 长稳漂移 | 数小时连续运行后的内存与句柄漂移 |
| 电池供电特征 | 拔电状态下的采样与功耗表现 |
| 静默环境对照 | 关闭 XTU 等第三方进程后的 CPU 基线对照 |
