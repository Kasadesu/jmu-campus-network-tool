# JMU 校园网配置工具

集美大学校园网（`JMU-STU`）Windows 客户端的网络配置切换工具与脚本。

用于解决**晚上在宿舍楼区无法完成 ePortal 认证、连接频繁卡顿或降级为 169.254** 的问题。

---

## 🚀 推荐：轻型原生 GUI 客户端 (`JmuNetHelper.exe`)

本项目提供了一个极小体积、零外部依赖的 Windows 原生桌面 GUI 工具 **`JmuNetHelper.exe`**：

- **单文件免安装，仅 ~35 KB**：基于 Windows 10/11 系统自带的 .NET Framework 4.8 编译，内存占用仅约 10 MB，瞬时启动，不依赖任何第三方运行库。
- **Windows 11 DAD 冲突全自动防御**：内置执行 `dadtransmits=0`，彻底根除网关 Proxy-ARP 导致的 IP 冲突假死与 `169.254.x.x` 降级。
- **8 候选 IP 池与一键测速**：预设 8 个实测可用的跨楼层校园网 IP 节点，支持自定义手输与毫秒级 TCP 握手测速。
- **ePortal 一键快捷认证**：支持 DPAPI 本机凭据安全加密，应用静态 IP 后可全自动绑定 WLAN 源地址完成 `10.8.2.2` 网页登录，无需手动打开浏览器。
- **系统托盘驻留**：支持最小化至任务栏托盘，右键一键切换「校园网静态模式」与「日常 DHCP 模式」。

### 自行编译构建

项目根目录下附带了源码与构建脚本：

```cmd
build.bat
```

运行后将自动调用系统内置的 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` 完成编译，生成 `JmuNetHelper.exe`。

---

## 📜 传统批处理脚本使用方法

如果你更倾向于极简的命令行脚本，也可以直接使用 `scripts/` 目录下的批处理：

两个脚本成对使用。**右键管理员身份或双击运行** —— 会自动请求管理员权限，弹窗点「是」，然后按 **Y** 确认。

| 脚本 | 作用 | 什么时候用 |
| --- | --- | --- |
| `scripts/apply-campus-config.bat` | 应用校园网配置 | 在宿舍/学校，要用校园网时 |
| `scripts/restore-dhcp.bat` | 恢复自动获取 | 离开校园网、去连别的 WiFi 时 |

### 应用脚本具体做了什么

| 步骤 | 内容 |
| --- | --- |
| 1 | 清理旧的校园网路由 |
| 2 | 关闭网卡 DAD 检测 (`dadtransmits=0`)，防止 Win11 因网关 Proxy-ARP 误判冲突 |
| 3 | WLAN 设为静态 IP `172.19.245.138/24`，网关 `192.168.128.1` |
| 4 | DNS 设为 `172.17.8.32` / `172.17.8.33` |
| 5 | 写入持久路由：`10.0.0.0/8`、`172.17.0.0/16` → `192.168.128.1` |
| 6 | 打印当前配置，供你核对结果 |

脚本内置 SSID 检测：当前不在 `JMU-STU` 时会先警告并要求二次确认 —— 这套配置在别的网络下应用会直接断网。

### 重启之后

- 静态 IP、DNS、持久路由**都会保留**
- **认证会失效**，需要重新登录：连上 `JMU-STU` 后，在 `JmuNetHelper.exe` 中点「立即登录认证」或打开浏览器访问 `http://10.8.2.2/`。

---

## 🛠️ 遇到问题

认证页打不开、配了还是上不了网、校内资源不通、Win11 出现 IP 冲突 —— 详见 **[docs/troubleshooting.md](docs/troubleshooting.md)**。

---

## 📂 目录结构

```
jmu-campus-network-tool/
├── README.md                       ← 本说明文档
├── LICENSE
├── .gitignore
├── .gitattributes                  ← 锁定 bat 为 CRLF 换行
├── build.bat                       ← 一键编译生成 JmuNetHelper.exe
├── JmuNetHelper.exe                ← 编译生成的小巧绿色单文件工具 (~35 KB)
├── src/
│   ├── JmuNetHelper.cs             ← GUI 工具完整 C# 源码
│   └── app.manifest                ← UAC 管理员提权与高 DPI 感知清单
├── scripts/
│   ├── apply-campus-config.bat     ← 应用校园网配置（含 DAD 防御）
│   ├── restore-dhcp.bat            ← 恢复自动获取（含 DAD 还原）
│   └── test-candidate-ip.bat       ← 批量测试候选 IP 可用性
└── docs/
    ├── troubleshooting.md          ← 排查手册（新增 Win11 DAD 故障剖析）
    └── development-notes.md        ← 开发踩坑记录（改脚本前建议先读）
```

---

## ⚠️ 免责声明

- 本项目**仅调整本机网络配置**（IP、DNS、路由表），不修改、不入侵认证系统。
- 脚本中的 IP 段、网关、DNS 均为本机实测值，**仅适用于集美大学校园网环境**，直接用于其他网络可能导致断网。
- **使用本工具须遵守学校相关管理规定，因使用本工具产生的后果由使用者自行承担。**
- **小范围自用就好，请不要传播。**

---

## 💻 环境要求

- Windows 10 / Windows 11（已实测 23H2 / 24H2）
- 系统自带 .NET Framework 4.8（Win10/11 开箱即自带，无需单独安装）
