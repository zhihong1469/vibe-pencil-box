# 驱动签名：为什么、三档怎么办（含本机零成本跑通）

> 回答指挥官 11-02 的疑问："我们又不商用、纯赠送，为什么还要证书？"

## 一、先拆清一个误解：签名 ≠ 商用许可

驱动签名是 **Windows 内核的安全准入规则**，不是收费项目，也不是"商用水印"：

- Windows Vista 起，x64 系统**只加载带可信签名的内核驱动**——目的是防 Rootkit（指挥官熟悉的
  驱动级病毒正是当年无签名/盗签名泛滥逼出来的这道墙）；
- 微软**不区分**你是收费软件、免费赠送还是开源——免费开源驱动一样要签；
- 我们仓库的 Polyform Noncommercial 许可证管的是"**人**可以拿代码做什么"，
  签名管的是"**Windows 内核**肯不肯加载这个 .sys"——两套体系互不相干。

所以问题从来不是"要不要证书"，而是"**哪一档**"。

## 二、三档签名

| 档 | 场景 | 成本 | 效果 |
|---|---|---|---|
| **① 测试签名（自签）** | **自己电脑跑通流程（现在要的）** | **零**（自签证书，命令行生成） | 桌面右下角"测试模式"水印；只在自己开了测试模式的机器上加载 |
| ② 证明签名 attestation | 发给客户/随机赠送的产品化分发 | 硬件开发者中心注册需购一张 **EV 代码签名证书**（验公司身份，约 ¥2000~4000/年）；上传 .sys 后微软免费给你签 | 装上就能用，无水印，Secure Boot/HVCI 全兼容 |
| ③ WHQL 认证 | 大规模分发/渠道信心 | ②之上再加测试申报流程 | 微软徽标认证（暂不需要） |

**结论：现在走①，零成本；将来产品化再议②。**

## 三、本机跑通（档①）操作手册

前提：M1 编译出 `R1PBleAudio.sys` 之后。

### 1. 生成自签测试证书（PowerShell 管理员）

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject "CN=PencilBox Test Signing" `
    -CertStoreLocation Cert:\CurrentUser\My
$pwd = ConvertTo-SecureString -String "<口令自己定>" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath .\R1PBleAudioTest.pfx -Password $pwd
```

### 2. 签名 .sys 和 .cat

```powershell
signtool sign /fd SHA256 /a /f .\R1PBleAudioTest.pfx /p <口令> R1PBleAudio.sys
signtool sign /fd SHA256 /a /f .\R1PBleAudioTest.pfx /p <口令> R1PBleAudio.cat
```

### 3. 机器开测试模式（一次性）

按顺序（管理员命令行）：

```cmd
:: ① BIOS 里先关 Secure Boot（开着的 testsigning 会被拒绝）
bcdedit /set testsigning on
shutdown /r /t 0
```

重启后桌面右下角出现"**测试模式 Test Mode**"水印=生效。

### 4. 关闭内核隔离（HVCI 会拒测试签名驱动）

设置 → Windows 安全中心 → 设备安全性 → 内核隔离 → **内存完整性 = 关** → 重启。

### 5. 安装设备

设备管理器 → 添加过时硬件 → 从列表指定 `R1PBleAudio.inf`（root 枚举）；
或 `pnputil /add-driver R1PBleAudio.inf /install`。
声音设置里出现 **R1PBleAudio** 录制设备 = M2 达成。

### 恢复原状（想退出测试模式时）

```cmd
bcdedit /set testsigning off
```

## 四、为什么产品化挡不住要档②

客户机器不可能替你开测试模式（安全水印+降保护）。届时：注册
[硬件开发者中心](https://developer.microsoft.com/windows/hardware/dashboard)（用 EV 证书验证公司身份一次）
→ 上传签好 EV 的 .cab → 微软 attestation 签名回传 → 随产品分发，客户机器免开任何开关。
**就绪包=软件接收器全部功能对客户可用（R1PBleAudio 走档②）或先用 VB-Cable 桥顶替（客户装免费驱动）。**
