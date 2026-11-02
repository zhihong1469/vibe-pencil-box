# PencilBox — 「语音翻页笔」PC 上位机

> 「语音翻页笔」系列产品的 PC 端配套软件：设备连接、工厂配对、OTA 升级、串口日志、扩音、
> 软件接收器（BLE 直连）、空鼠调参、快捷键映射与连点器，一站全管。
> 技术栈：C# / .NET 10 / WPF / WinRT BLE / NAudio。第三方依赖与来源见 [THIRD_PARTY.md](THIRD_PARTY.md)。

***

## 这是什么

PencilBox 是这套三端系统（**笔端 → 基站 → PC**）里 PC 侧的官方配套软件，交付两件东西：

| 交付物 | 给谁用 | 形态 |
|---|---|---|
| **PencilBox.Desktop.exe** | 产品用户、产线、调试人员 | 桌面软件，双击即用 |
| **pencilbox.exe** | 脚本、自动化、CI、产线治具 | 单文件命令行工具 |

两个都是**绿色免安装**的单文件程序（自包含运行时，目标机器免装 .NET），成套同批发布。

## 支持的硬件

- **R1 笔端**（HS6621）：USB 串口直连 / BLE 直连（软件接收器）
- **R1D 基站**（AC6323，USB 接收器）：HID EP4 命令通道 + 串口日志
- 键盘经基站 BLE 透传轨（0x51/0x52 配对体系）

## 功能一览

| 功能 | 入口 | 说明 |
|---|---|---|
| 设备连接与笔端配对 | Desktop「仪表盘」 | BLE 扫描 + 串口 0x51 读 MAC 双来源目录、一键配对基站 |
| OTA 在线升级 | Desktop「升级」/ CLI `company ota` | 基站/键盘双对象、并行多台、断点终态兜底、读 Hash 校验 |
| 工厂快配对 | CLI `company pair factory` | 串口读笔端 → USB 写基站 → 回读校验，一条命令 |
| 串口日志统一时间轴 | Desktop「串口」/ CLI `serial log/diff` | 双口同录、协议帧与日志同轴、PC×设备自动对拍 |
| 扩音直通 | Desktop「扩音」/ CLI `audio passthrough` | 笔端麦克风 → PC 扬声器实时回路（AEC/ANS/带通/notch） |
| **软件接收器** | Desktop「软件接收器」/ CLI `srx` | PC 经 BLE 直连键盘，绕过硬件基站：语音接收/扩音/录制/延迟实测/**虚拟麦克风桥**（第三方软件可选 R1 人声作麦克风）/**软件键鼠**（按键+空鼠直接控制电脑，按 Keymap 表注入） |
| 空鼠调参与遥测 | Desktop「空鼠」/ CLI `company param/stream` | 11 参调参工作台、快照 A/B、50Hz 遥测流录制与五指标分析 |
| 快捷键映射 / 连点器 / 软触发 | Desktop「仪表盘·Keymap」「连点器」/ CLI `keymap / softtrigger` | 按键表+S1 切换表编辑下发、连点序列、触发键→动作序列编排 |
| 量产烧录 | `output\produce\` 独立工具 | R1D 基站 / R1P 键盘两个产线专用一键烧录工具（治具多口并发） |
| 外发升级小工具 | `output\custom\` 独立工具 | 发给最终用户的极简升级器（CLI+GUI 向导） |
| 功能开关 | CLI `feature on/off` | 可选功能域整域启停（扩音/串口/空鼠/软触发…），GUI 入口联动隐藏 |

## 系统要求

- Windows 10 / 11 x64（自包含运行时，免装 .NET）
- BLE 直连类功能需要本机蓝牙适配器
- 「虚拟麦克风桥」需要安装 [VB-Cable](https://vb-audio.com/Cable/)（免费；来源与许可见 [THIRD_PARTY.md](THIRD_PARTY.md)），装完重开桥自动识别
- 串口类功能需要对应驱动（基站/笔端治具 CH340 等）

## 快速上手

### 桌面软件

双击 `PencilBox.Desktop.exe`：

1. 「仪表盘」连接基站或扫描笔端
2. 按需进入各功能页（软件接收器先扫描选择键盘再连接）
3. 「升级」页选择固件包即可在线升级（无线/串口双通道）

### 命令行

```powershell
pencilbox help                 # 全命令面（--output json 可脚本化）

# 常用示例
pencilbox company identify                 # 识别在位基站（--port COM6 = 串口识别笔端）
pencilbox company ota --fw app.bin --target bs --sver 1.0.1    # OTA 升级基站
pencilbox serial log --port COM6 --baud 1000000 --name DONGLE  # 抓设备日志
pencilbox srx scan                         # 软件接收器：扫广播中的键盘
pencilbox srx receive --actions            # 连接收语音+按键/空鼠软件注入
```

> 注意：OTA 过程中黄色 `[WARN]` 是自动恢复提示，不是失败；只有终态非 success 才按失败处理。
> 完整命令参考见 [命令行操作总文档](_share/issues/0-待处理/命令行操作与OTA技能化/01-Box命令行操作总文档.md)。

### 从源码构建（贡献者）

```powershell
dotnet build PencilBox.slnx -v q --nologo
dotnet test  PencilBox.slnx               # 单元测试
```

单文件发布（CLI+Desktop 必须同批成套）：

```powershell
dotnet publish PencilBox.Cli/PencilBox.Cli.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -o output
dotnet publish PencilBox.Desktop/PencilBox.Desktop.csproj -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -o output
```

***

## 项目结构（速览）

```
PencilBox.Core/            # 纯逻辑（协议/编解码/FSM/BLE），可离线单测
PencilBox.Infrastructure/  # Windows 平台实现（HID/串口/BLE/音频/注入）
PencilBox.Cli/             # 命令行入口
PencilBox.Desktop/         # WPF 桌面软件
PencilBox.Tests/           # xUnit 单元测试
dsp/                       # Python DSP 脚本（FFT/VAD/滤波）
```

## 关联仓库

本项目是三端系统的 PC 侧节点：

| 端 | 仓库 |
|---|---|
| 笔端（HS6621） | `voice-paging-pencil` |
| 基站（AC6323） | `jl_dongle` |
| **上位机（本项目）** | **`host_computer/PencilBox`** |

跨端协议、联调报告等项目级上下文集中在 `voice-paging-respose` 共享区。

***

## 许可证

本项目采用 **Polyform Noncommercial License 1.0.0**。

简单来说：
- ✅ **个人/学习/研究/教育** —— 随便用、随便改、随便分享
- ✅ **慈善/教育/科研/政府机构** —— 随便用
- ❌ **商业使用** —— 卖产品、做商业服务、嵌进商业项目，需要版权持有人书面授权

完整条款见仓库根目录的 [LICENSE](LICENSE) 文件，或访问 https://polyformproject.org/licenses/noncommercial/1.0.0 。

> **关于公司关联**：本项目是作者在公司工作范围之外、业余时间独立完成的作品。项目中涉及的产品协议（F5 V0.9）、命令语义、硬件规格等归属其原公司所有，仅作为实现依据存在于代码和文档中。
