# R1PBleAudio — 自研虚拟麦克风驱动（立项）

> 目标：Windows 系统里出现名为 **R1PBleAudio** 的录音设备（虚拟麦克风），第三方软件可直接选择，
> 内容=PencilBox 软件接收器解码出的 R1 人声 PCM。替代"桥接 VB-Cable"路线（见主仓 `THIRD_PARTY.md`）。
> 本目录**独立于上位机源码树**（指挥官 11-02 指示：驱动级文件不与应用代码混放）。

## 一、为什么需要驱动（一句话版）

Windows 上"一个音频设备"只能由**内核驱动**创造——应用层进程无论怎么写都造不出系统级的录音设备。
所以"自己的麦克风名称"= 自己写一个虚拟声卡驱动，这是唯一路径。

## 二、技术路线

- **蓝本：微软官方 sysvad 示例**（虚拟声卡适配器，MIT 许可，可自由修改，需保留版权注记）。
  路径 https://github.com/microsoft/Windows-driver-samples （audio/sysvad）。
- **裁剪为单端点**：只要"录音（capture）"一个端点，16kHz/mono/16bit（与软件接收器解码 PCM 同规格，
  兼容声明 48k/stereo 由后续迭代决定）。
- **数据通道：IOCTL 环形缓冲**——PencilBox 用户态把 PCM 块 `DeviceIoControl` 写进驱动的内核环形缓冲，
  miniport 流从环里取数进 WaveRT 缓冲。接口契约=`driver/common.h`（两侧唯一对齐点）。
- **多应用并发取流的定案**：Windows 音频引擎在**共享模式**下是驱动的唯一 capture 消费者，
  各应用拿到的拷贝由引擎扇出——内核环保持 **SPSC 单写者（IOCTL）单读者（miniport）**即可，
  **勿**为"多应用同时录"把环改成多读者（引擎层已解决）；独占模式抢流属例外，M2 后实测再议。

```
PencilBox 软件接收器 → 解码 PCM → [用户态写入端 app/] --IOCTL--> [内核环形缓冲]
                                                                        ↓
第三方软件（录音/会议/识别）→ 选择麦克风 R1PBleAudio → PortCls ← minwave.c 取数
```

## 三、目录结构

```
R1PBleAudioDriver/
├── README.md          # 本文：路线/前提/里程碑
├── docs/
│   └── signing.md     # 签名三档详解：为什么与"商不商用"无关 + 本机零成本跑通步骤
├── driver/            # 内核驱动源码（C，WDM + PortCls）
│   ├── common.h       # ★ 用户态↔内核接口契约（IOCTL 号/环形缓冲布局/设备名）
│   ├── adapter.c      # DriverEntry / AddDevice / StartDevice（PortCls 适配器）
│   ├── minwave.c      # capture miniport + WaveRT 流（从环形缓冲取数）
│   ├── mintopo.c      # 拓扑 miniport（录音引脚/音量静音节点）
│   ├── pcmring.c      # 内核环形缓冲 + IOCTL 写入路径
│   └── R1PBleAudio.inf # 设备安装信息文件（root 枚举）
└── app/               # PencilBox 侧写入端（M4 接入 srx 桥三路漏斗）
```

> `driver/*.c` 当前为 **v0.1 结构骨架**：类型/接口/流程与 DDI 调用点齐全、注释到行，
> 但**尚未经过编译**——构建前提见下节，编译迭代从 M1 开始。勿在 M1 前当作可工作代码引用。

## 四、构建前提（本机 2026-11-02 实测）

| 项 | 状态 |
|---|---|
| Visual Studio | ✅ Community **2026**（18.7.11911.148）@ `C:\IDE\VS\Community` |
| MSBuild/Roslyn | ✅ 在位（`C:\IDE\VS\Community\MSBuild\Current\Bin\MSBuild.exe`） |
| VC++ 工具集（C++ 桌面工作负载） | ❌ 未装（无 cl.exe）——**M1 缺口①** |
| Windows Driver Kit（WDK） | ❌ 未装——**M1 缺口②** |

→ 安装步骤、装后验证、sysvad 工程搭建与迭代守则全在 **[docs/M1-build-checklist.md](docs/M1-build-checklist.md)**（接手者先读它）。

## 五、里程碑

| # | 内容 | 状态 |
|---|---|---|
| M0 | 路线文档 + 接口契约 + 源码骨架（本目录） | ✅ 本次 |
| M1 | VS+WDK 装机，骨架编译通过（.sys 产出） | 待构建环境 |
| M2 | 自签测试证书签名 + root 枚举安装 → 声音面板出现 R1PBleAudio（静音源） | 待 M1 |
| M3 | IOCTL 环形缓冲写通：测试工具写 PCM → 录音机录到声 | 待 M2 |
| M4 | PencilBox `srx` 桥加驱动出口（R1PBleAudio 对外供音，VB-Cable 桥保留为备选） | 待 M3 |
| M5 | （可选，产品化分发）微软证明签名 attestation——见 docs/signing.md 第三档 | 待裁决 |

## 六、许可与署名

- 本驱动源码随主仓 **Polyform Noncommercial 1.0.0**（与上位机同 License）。
- 改编自 Microsoft Windows-driver-samples（sysvad），**MIT License © Microsoft**——
  M1 起每个改编文件头保留原版权注记与修改说明（MIT 要求）。
