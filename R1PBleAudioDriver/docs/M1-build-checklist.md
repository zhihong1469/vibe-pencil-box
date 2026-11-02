# M1 构建环境与编译迭代清单（交接给下一位 AI / 开发者）

> 更新：2026-11-02。本机实测结论在此，接手者**先读本页再动手**，别重复探测。

## 一、本机构建环境实测（2026-11-02 探明）

| 项 | 状态 | 明细 |
|---|---|---|
| Visual Studio | ✅ **Community 2026**（18.7.11911.148） | 安装在 `C:\IDE\VS\Community`（非默认盘路径），vswhere 可见、isComplete=True |
| MSBuild/Roslyn | ✅ 在位 | `C:\IDE\VS\Community\MSBuild\Current\Bin\MSBuild.exe`（csc.exe 已确认存在） |
| **VC++ 工具集（C++ 桌面开发工作负载）** | ❌ **未装** | `VC\Tools\MSVC` 不存在、无 cl.exe——驱动编译**必需** |
| **Windows Driver Kit（WDK）** | ❌ **未装** | `C:\Program Files (x86)\Windows Kits\10` 不存在（只有 Installer/NETFXSDK） |
| dotnet SDK | ✅（.NET 10，主仓日常构建用） | 与驱动构建无关，勿混淆 |

**M1 缺口 = 两件安装：① VS2026 勾选"使用 C++ 的桌面开发"工作负载（含 Spectre 缓解库可选项）
② WDK（与 VS2026 配套版本以微软 WDK 下载页为准——VS2026 较新，若正式版 WDK 尚不支持 VS2026，
用 WDK Insider Preview 或 EWDK；装完 WDK 后 VS 会自动装 WDK VSIX 扩展）。**

## 二、安装后验证（全过才算 M1 环境齐）

```powershell
# ① cl.exe 在位（版本号随 WDK/VS 而异，有输出即可）
dir "C:\IDE\VS\Community\VC\Tools\MSVC" -Recurse -Filter cl.exe | Select-Object -First 1 FullName
# ② WDK 头文件与库在位
dir "C:\Program Files (x86)\Windows Kits\10\Include"      # 应有 10.0.xxxxx.0 目录
dir "C:\Program Files (x86)\Windows Kits\10\Lib"          # 同上（km\ 下有 ntoskrnl.lib 等）
# ③ WDK VSIX：VS 关于页出现 "Windows Driver Kit" 或新建项目向导出现 "Kernel Mode Driver" 模板
```

## 三、工程搭建（推荐顺序）

1. **克隆微软示例仓**（蓝本+工程属性模板，~几百 MB）：
   ```powershell
   git clone https://github.com/microsoft/Windows-driver-samples E:\src\Windows-driver-samples
   ```
   参考 `audio/sysvad/`（虚拟声卡适配器）。注意 sysvad 工程在 VS 里以 **Solutions/sysvad.sln** 组织。
2. **新建工程**（不要直接改 sysvad）：`R1PBleAudioDriver/driver/` 现有骨架文件为主体建
   "Kernel Mode Driver, WDM" 空工程（或复制 sysvad 的 .vcxproj 改名），把本目录 5 个文件挂入：
   - `adapter.c` ← 对位 sysvad `adapter.cpp`（DriverEntry/AddDevice/StartDevice 骨架已写）
   - `minwave.c` ← 对位 `minipavc.cpp/minwave*.cpp`（capture miniport + WaveRT 流）
   - `mintopo.c` ← 对位 `mintopo.cpp`
   - `pcmring.c/h` ← 本驱动特有（无 sysvad 对位，纯内核 SPSC 环）
   - `common.h` ← 接口契约，两侧共享
3. **先编 sysvad 原版**（不改一行）：证明环境通——这一步产出 sysvad.sys 并能装机出"虚拟音频设备"，
   是 M1→M2 之间最可靠的中间验收点。sysvad 原版编不过=环境问题，别在骨架上空耗。
4. **再编本骨架**：迭代顺序=pcmring（独立纯函数，先过）→ adapter（入口/装配）→ minwave/mintopo
   （PortCls DDI 细节最多，对着 sysvad 同名方法逐个收敛）。每个文件的 M1 迭代项已写在该文件注释里。

## 四、迭代守则（给 AI 的硬约束）

1. **骨架≠能编**：`driver/*.c` 全部标注 v0.1 未编译；第一轮编译**必然**报类型/包含/IRQL 错，属预期，
   按 sysvad 同名代码收敛，**不要**为了过编译删契约（common.h 的 IOCTL 号/环布局是两侧行为对齐点）。
2. **每个改编自 sysvad 的文件头保留 MIT 版权注记**（Microsoft © + 本仓修改说明）——许可证义务。
3. **WPP/Trace**：起步可用 DbgPrintEx，M2 后切 WPP（sysvad 同款）。
4. **禁止 git add -A**（主仓铁律）；驱动构建产物已被 .gitignore 根锚定（sys/cat/pdb/cer/pfx/pvk）。
5. 每轮迭代汇报走【已完成】/【未完成】双栏 + 编译错误原文（别只说"还有错"）。
