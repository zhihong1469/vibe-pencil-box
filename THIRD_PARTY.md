# THIRD_PARTY — 第三方组件与依赖来源登记

> 本文件登记 PencilBox 用到的**第三方组件、贡献者与获取来源**（开源项目依赖透明化）。
> 约定：新增第三方依赖时在对应小节登记"是什么 / 谁写的 / 从哪来 / 许可证 / 我们怎么用"。
> 手工安装类依赖（不随仓库分发）同样登记，并写明获取方式。

***

## VB-CABLE Virtual Audio Device（虚拟声卡驱动）— 软件接收器「虚拟麦克风」的底层设备

| 项 | 内容 |
|---|---|
| **是什么** | Windows 虚拟音频驱动：所有送进 CABLE Input（播放端）的音频，立即出现在 CABLE Output（录音端）。充当一根"虚拟音频线" |
| **作者/版权** | Vincent Burel / VB-Audio（Programmed by V.Burel / JS-Loezic，© 2010-2024） |
| **来源** | 官网 https://vb-audio.com/Cable/ ；直接下载 https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip （Driver Pack 45，zip 时间戳 2024-10-08） |
| **许可证** | Donationware（捐赠免费使用），最终用户许可协议见包内 readme.txt 与 https://vb-audio.com/Services/licensing.htm ；版权归作者保留、非公有领域 |
| **我们怎么用** | `SrxBridgePump`（PencilBox.Infrastructure/Audio）把解码后的 16k mono PCM 泵进 **CABLE Input**；第三方软件选麦克风 **CABLE Output** 即 R1 人声（=「软件接收器」页的"虚拟麦克风桥"，形态③ 对齐硬件基站 UAC 形态）。`SrxBridgePump.SelectPair` 自动发现 CABLE Input↔Output 配对端点（UU 远程外壳同类设备被降级，避免误挂） |
| **安装方式** | **不随本仓库/产物分发**（尊重其分发条款）。由使用者自行从上述官方地址下载，管理员运行 `VBCABLE_Setup_x64.exe` → Install Driver（包内说明建议重启收尾） |
| **设备名说明** | 设备在系统中显示为 "CABLE Input / CABLE Output (VB-Audio Virtual Cable)"——**名字跟驱动走**，不是 PencilBox 命名。产品定名 `R1PBleAudio` 需自研虚拟声卡驱动（sysvad 路线）另立项 |

## mSBC 解码算法（MsbcDecoder）

| 项 | 内容 |
|---|---|
| **是什么** | mSBC 语音帧解码（60B 包装帧 → 240B PCM s16LE 16k mono），软件接收器收流解码的核心 |
| **算法出处** | Bluetooth A2DP 规范附录 B 的 SBC 定点解码算法（规范公开，mSBC=固定参数 SBC：16kHz 单声道、15 blocks、8 subbands、bitpool 26、LOUDNESS） |
| **对拍参考** | **linux-bluez SBC 库（libsbc 2.0）** https://git.kernel.org/pub/scm/bluetooth/sbc.git/ ——仅用作**金标准逐位对拍验证**（3 帧笔端真机帧的 C 参考解码输出作单测基线），**未复制/未分发其代码**；C# 实现为规范算法的独立移植 |
| **许可证** | 规范算法本体不受参考实现许可证约束；libsbc 自身许可证以仓库为准（对拍参考用途） |

## NuGet 包依赖

| 包 | 版本 | 用途 | 许可证 |
|---|---|---|---|
| NAudio | 2.3.0 | WASAPI 采集/渲染、WAV 落盘、重采样 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | WPF MVVM（ObservableProperty/RelayCommand） | MIT |
| HidSharp | 2.6.4 | USB HID 通道（笔端/基站直连） | Apache-2.0 |
| Microsoft.Extensions.DependencyInjection / Logging | 10.0.10 | DI 组合根、日志抽象 | MIT |
| Serilog（+Console/File sink） | 4.4.0 | 结构化日志落盘 | Apache-2.0 |

> 版本以各 `*.csproj` 的 `PackageReference` 为准；许可证以各包官方仓库为准。

## 待登记

- sherpa-onnx（语音识别）：识别链接入时登记（已查证宿主代码尚无实现，仅议题在册）。
