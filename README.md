# PencilBox — 嵌入式产品的 AI 时代上位机

> 为「语音翻页笔」系列产品打造的 PC 上位机：USB HID/EP4 命令通道、BLE 扫描与绑定、串口日志统一时间轴、OTA 在线升级、DSP 信号分析、音频采集与扩音直通。
> 技术栈：**C# · .NET 10 · WPF · HidSharp · NAudio · WinRT BLE · Python DSP**。

***

## 这是什么

PencilBox 不是一个通用的 USB/串口调试工具，而是**为自家嵌入式产品深度定制的控制中枢**。

它是整个「笔端(Pencil) → 基站(Dongle) → PC」三端系统中 PC 侧的那个节点，负责：

- 对 **产品**：给最终用户一个干净的桌面 UI（设备连接、配对状态、升级入口）
- 对 **开发者**：一套完整的 **CLI 命令行工具**（`pencilbox`），覆盖调试、测试、部署全流程
- 对 **AI Agent**：同一套 CLI 接口就是 AI 的操作面 —— 退出码、JSON 输出、可组合的命令序列，让 AI 能自主完成"构建 → 部署 → 测试 → 日志捞取 → 结论输出"的闭环

**第一性原理：UI 是给人看的，CLI 是给 AI 用的。**

***

## 我们怎么开发的 — AI 时代的三端协作

这是这个项目最想表达的东西。

### 背景

这个项目背后是一整套嵌入式产品 —— 一个 **笔端**（HS6621，BLE 发射端）、一个 **基站**（AC6323，USB 接收器）、加上 **PC 上位机**。三个端，三种技术栈（C BLE 协议栈 / C 音频 DSP / C# .NET），各自有独立的仓库和 Git 分支。

传统做法是：一个团队按模块分工，写代码 → 联调 → 测试 → 迭代。

**我们的做法是：三端各配一个 AI，一个人当总指挥+AI秘书。**

### 三端独立化

```
┌─────────────────────────────────────────────────────┐
│  指挥者（人）+AI秘书                                   │
│  - 裁决协议口径                                        │
│  - 调度各端 AI                                         │
│  - 掌握全局，不写一行代码                               │
└────────┬──────────────┬──────────────┬────────────────┘
         │              │              │
    ┌────▼────┐    ┌────▼────┐    ┌────▼────┐
    │ 笔端 AI │    │ 基站 AI │    │ 上位机 AI │
    │ (C/BLE) │    │ (C/DSP) │    │ (C#/.NET)│
    └────┬────┘    └────┬────┘    └────┬────┘
         │              │              │
    ┌────▼────┐    ┌────▼────┐    ┌────▼────┐
    │ pencil  │    │ dongle  │    │ PencilBox│
    │ 仓库     │    │ 仓库     │    │ 仓库      │
    └─────────┘    └─────────┘    └─────────┘
```

每个 AI 只动自己端的代码仓和文档目录，**不越界**。跨端协议的变更不允许各端 AI 各自解读，必须走一个「项目级仓库」（我们叫它 `voice-paging-respose`，一个软链接注入的上下文仓库），在那里冻结口径、传播到实施权威、全库复核 —— 三段闭环。

### 上下文以树的形式开枝散叶

那个项目级仓库里有每个端的 `docs/`、`issues/`、`memory/`。**软链接直接注入**到各端本地，所以笔端 AI 能看到上位机的联调报告，上位机 AI 能看到基站的 OTA 固件版本 —— 但都只看、不改。

```
voice-paging-respose/          ← 项目级上下文仓库（软链接注入）
├── agent/
│   ├── box/docs/              ← 上位机文档（软链到 PencilBox/_share/box）
│   ├── dongle/docs/           ← 基站文档
│   └── pencil/docs/           ← 笔端文档
├── issues/                    ← 跨端议题（协议、联调、待办）
├── link/                      ← 三端协议链路的事实源
└── knowledge/                 ← 领域知识沉淀（USB/BLE/OTA/...）
```

### 多分支 + 多环境 + 虚拟机

一个端的 AI 可能同时在开发两个功能（比如上位机 AI 同时在改 OTA 流程和串口日志管线），这就开两个 Git 分支，每个分支跑自己的构建/测试命令。遇到需要多机器验证的场景（比如一台机器跑基站、一台跑上位机），就上虚拟机。

我们几乎做到了 **一个人运筹帷幄，决胜千里之外** —— 不写一行代码，但掌握全局。

***

## AI 不是万能的 — 让 AI 落地，但别把理解交给 AI

这是我们最深的体会，也是最想分享的一个观点。

这个项目涉及的东西：USB HID 报告描述符、BLE GATT 服务与特征值、mSBC 音频编码、OTA 双区升级与 Bank 切换、FSM 状态机的时序与重传……AI 都能写。**但如果你作为调度 AI 的人不懂这些，AI 写出来的东西你也不知道对不对。**

举个例子：AI 能帮你写出一个 USB HID 收发的实现，但它可能默认用 64B 报告长度 —— 如果你的产品走的是 EP4 无 RID 通道、需要补零到 64B 再发，AI 不一定能自己意识到这个细节。这时候你需要一眼就能看出「这个帧长度不对」。

**所以我们的原则是：**

| 谁负责          | 什么                         |
| ------------ | -------------------------- |
| **人（指挥者）**   | 理解原理、裁决口径、设计架构、识别边界条件      |
| **AI（端负责人）** | 按指令落地代码、执行测试、生成文档、在既定框架内优化 |
| **CLI 工具**   | 把人的判断变成可执行的命令面，让 AI 能自主闭环  |

CLI 在这里的角色特别关键 —— 它是「人的判断」和「AI 的执行能力」之间的桥梁。人把"怎么验证 OTA 升级真的生效"固化成一套命令序列（`identify → ota → wait → identify → diff logs`），AI 只需要调用这些命令、判读退出码、捞日志、给结论。**人设计验证方法，AI 执行验证动作。**

***

## 设计理念

### 产品强绑定 + AI 时代裁剪观

**我们不做通用工具。**

市场上已经有很多好用的 USB 调试助手、串口终端、BLE 扫描器了。我们不需要再造一个 "万能 HID 浏览器" —— UI 不把系统所有 USB 设备都铺出来，只过滤显示我们自己的产品 VID/PID；串口工具不做通用收发终端，而是自动解码我们的 F5 协议帧、按设备日志分域（`[ota_usb]`/`[f5]`/`[pair]`）、双口统一时间轴对拍。

> "如果一个功能只能做到市面通用工具的水平，自研它就是浪费时间。" — 这是我们的裁剪准则。

裁剪只做在**应用层**（UI 要不要展示、怎么展示），不做在**底层能力**（协议帧格式、传输通道抽象）。底层保留结构和可扩展性，产品化时再决定哪些露出来给用户。

### CLI-first：命令行是一等公民

PencilBox 有两套交互层：**CLI（`pencilbox`）和 WPF Desktop**。我们的开发优先级是 CLI 先行。

为什么？因为 CLI：

- **零 UI 依赖** — 不加载 WPF、不需要 STA 线程，AI 能直接在后台执行
- **可组合** — 退出码 + JSON 输出，脚本和 Agent 能串联成自动化流水线
- **可验证** — 每个命令的输入输出都是确定的，写单元测试和集成测试都方便
- **跨环境** — 未来要移植到 Linux/Mac，CLI 层几乎不用改

UI 反而是后来的事 —— 先用 CLI 把功能跑通、协议对齐、联调闭环，再把产品级的功能做个好看的桌面界面。

### 分层架构：Core 纯净，Infrastructure 实现

```
交互层    │  CLI (pencilbox) / Desktop (WPF)
         ─┼───────────────────────────────────
契约层    │  Core — 纯接口 + 纯逻辑，零 UI/零平台依赖
         │  IDeviceService · ICommandChannel · FSM · Protocol codec
         ─┼───────────────────────────────────
实现层    │  Infrastructure — Windows 平台实现
         │  HidSharpTransport · WinRtBleScanner · PythonProcessBridge
         ─┼───────────────────────────────────
平台层    │  Windows HID · USB · UAC · WinRT BLE · Python Runtime
```

**Core 是纯净的。** 协议帧编解码（`CompanyCodec`）、FSM 状态机（`CompanyOtaFsm`）、BLE 命名逻辑（`BleNaming`）这些都是纯函数 —— 入参是字节，出参是对象或错误码，不做任何 IO、不依赖时间、不需要真实硬件。

这带来一个巨大的好处：**离线单测**。我们在 Core 层写了上百个单元测试，FSM 的超时/重发/块级重查可以在假通道上全时序跑通，不需要接真机。协议帧的编解码有黄金向量守护，改了一眼就知道对不对。

***

## 快速上手

### 构建

```powershell
dotnet build PencilBox.slnx -v q --nologo
```

### CLI 命令面（给 AI 用的）

```powershell
# 设备识别（USB 基站 / 串口笔端 / 透传轨）
pencilbox company identify
pencilbox company identify --port COM6          # 直连笔端串口
pencilbox company identify --target pen          # 经基站 B1 透传

# OTA 在线升级（部署主通道）
pencilbox company ota --fw dongle_v0.44.68.bin --target bs --sver 0.44.68

# 工厂快配对（串口读笔端 → USB 写基站 → 回读校验）
pencilbox company pair factory --port COM6

# 串口日志统一时间轴（排障主力）
pencilbox serial log --port COM6 --baud 1000000 --name DONGLE
pencilbox serial log --port COM7 --name PENCIL --port2 COM6 --name2 DONGLE

# 自动对拍：PC 收发帧 × 设备日志 → 缺失/重查/终态谁先谁后
pencilbox serial diff
```

> 完整命令参考（含退出码/JSON 输出/坑点）见 [命令行操作总文档](_share/issues/0-待处理/命令行操作与OTA技能化/01-Box命令行操作总文档.md)

### 桌面 UI（给人看的）

```powershell
dotnet run --project PencilBox.Desktop/PencilBox.Desktop.csproj
```

产品主页：设备连接 + 配对状态 + 升级入口 + 串口日志。调试工具面板（Audio/Serial/DSP/BLE）在 Debug 构建默认开启，Release 发布时默认关闭（`-p:EnableDebugTools=false`）。

### 发布单文件成品

```powershell
dotnet publish PencilBox.Desktop/PencilBox.Desktop.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -o output

dotnet publish PencilBox.Cli/PencilBox.Cli.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -o output
```

产物：`output/PencilBox.Desktop.exe`（\~200MB）和 `output/pencilbox.exe`（\~144MB），目标机器免装 .NET。

***

## 项目结构

```
PencilBox.slnx
├── PencilBox.Core/              # 契约层 — 纯接口 + 纯逻辑，可离线单测
│   ├── Protocol/Company/          #   F5 帧编解码：CompanyFrame / CompanyCodec / CRC16
│   ├── Fsm/                       #   状态机：CompanyOtaFsm / CompanyPairFsm / IClock
│   ├── Communication/             #   通道抽象：ICommandChannel / IBleScanner
│   ├── Ble/                       #   BLE 纯逻辑：BleNaming / MacAddress / KnownPen
│   ├── SerialScope/               #   串口日志管线：解码/分域/对拍/会话组装
│   ├── Dsp/                       #   DSP 分析：AEC 静音门限 / 包络 / 节拍统计
│   └── Services/ Models/          #   接口定义 + 业务模型
├── PencilBox.Infrastructure/    # 实现层 — Windows 平台
│   ├── Hid/                       #   HidCommandChannel（EP4 补零 64B）
│   ├── Serial/                    #   SerialCommandChannel / 日志读写轮转
│   ├── Ble/                       #   WinRtBleScanner
│   ├── Audio/                     #   NAudio 采集 + WASAPI 直通 + Speex 回声
│   └── Dsp/                       #   PythonProcessBridge（进程调用 dsp/ 脚本）
├── PencilBox.Cli/                # 命令行 — AI/脚本入口
├── PencilBox.Desktop/            # WPF 桌面 — 产品 UI
├── PencilBox.Tests/              # 单元测试（129+，FSM/编解码/BLE命名/DSP）
├── dsp/                          # Python DSP 脚本（FFT/VAD/滤波/AGC）
└── _share/                       # 项目级上下文（软链自 voice-paging-respose）
    └── box/
        ├── docs/                   # 架构/角色/GitFlow
        ├── issues/                 # 议题与待办
        └── memory/                 # 坑点与设计观
```

***

## 技术栈速查

| 类别  | 技术                                              | 用途                                    |
| --- | ----------------------------------------------- | ------------------------------------- |
| 运行时 | .NET 10                                         | Core / Infrastructure / Cli / Desktop |
| HID | [HidSharp](https://github.com/OpenTap/HidSharp) | EP4 命令通道（64B 补零收发）                    |
| 音频  | [NAudio](https://github.com/naudio/NAudio)      | USB Audio 采集 / WASAPI 直通              |
| 蓝牙  | WinRT Bluetooth                                 | BLE 广播扫描 / GATT 直连压测                  |
| CLI | System.CommandLine                              | 命令行解析                                 |
| DI  | Microsoft.Extensions.DependencyInjection        | 全项目依赖注入                               |
| 日志  | Serilog                                         | 结构化日志                                 |
| DSP | Python 3.13 + NumPy + SciPy                     | 进程调用方式（`dsp/` 目录）                     |
| 测试  | xUnit + Moq                                     | 单元 + 集成测试                             |

***

## 我们踩过的坑（节选）

| 坑              | 一句话                                               |
| -------------- | ------------------------------------------------- |
| OTA 包头 key 不匹配 | 设备侧 lib 直接崩溃不回错误帧，PC 端表现像"卡死"；必须确保包与设备同配置同源       |
| EP4 命令通道要补零    | 64B HID 报告不满时必须补零到 64B 再发，否则 Dongle 不认            |
| 三条波特率别混        | 命令口 115200 / 日志口 1M / 通用口默认 115200，日志读错波特率全乱码     |
| `[WARN]` 不是失败  | OTA 过程中 R1/R2 自动恢复走黄色 WARN，只有终态非 success 才是 ERROR |
| 未提交 = 版本不变     | 固件仓 commit 数决定版本三元组，没 commit 直接出包 → 同版本跳过升级       |

> 更多坑点见 `_share/box/docs/memory/01-坑点速查.md`

***

## 关联仓库

本项目是三端系统的 PC 侧节点。另外两个端：

| 端            | 仓库                            | 负责人        |
| ------------ | ----------------------------- | ---------- |
| 笔端（HS6621）   | `voice-paging-pencil`         | Pencil AI  |
| 基站（AC6323）   | `jl_dongle`                   | Dongle AI  |
| **上位机（本项目）** | **`host_computer/PencilBox`** | **Box AI** |

项目级上下文（跨端协议、联调报告、领域知识）集中在 `voice-paging-respose`，通过软链接注入各端本地。

***

## 许可证

本项目采用 **Polyform Noncommercial License 1.0.0**。

简单来说：
- ✅ **个人/学习/研究/教育** —— 随便用、随便改、随便分享
- ✅ **慈善/教育/科研/政府机构** —— 随便用
- ❌ **商业使用** —— 卖产品、做商业服务、嵌进商业项目，需要版权持有人书面授权

完整条款见仓库根目录的 [LICENSE](LICENSE) 文件，或访问 https://polyformproject.org/licenses/noncommercial/1.0.0 。

> **关于公司关联**：本项目是作者在公司工作范围之外、业余时间独立完成的作品。项目中涉及的产品协议（F5 V0.9）、命令语义、硬件规格等归属其原公司所有，仅作为实现依据存在于代码和文档中。
