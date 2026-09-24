# 开发踩坑记录

这份文档记录开发过程中踩到的坑，**给需要修改脚本的人看**（包括未来的自己）。

如果你打算改 `scripts/` 下的两个 bat 脚本，建议先读完这里 —— 能省下几个小时。这三个坑的共同特点是：**出问题时没有任何像样的报错**，表现都是"双击了但什么都没发生"。

---

## 一、批处理文件必须用 GBK 编码 + CRLF 换行

这是 Windows 脚本开发最容易踩、也最难排查的一对坑，而且**两个坑会叠加出现**。

### 1.1 编码：必须 GBK

`cmd.exe` 按代码页解码批处理文件，简体中文系统默认是 **936（GBK）**。若文件存为 UTF-8：

- 单层运行时，第一行的 `chcp 65001` 还能勉强挽救
- 但**提权后的新窗口里会彻底乱码**：中文字节被重新组合，甚至产生 `&`、`|`、`>` 之类的 cmd 元字符，导致命令被拆碎执行

实际观察到的症状（提权后窗口）：

```
'码' is not recognized as an internal or external command
'员分支' is not recognized as an internal or external command
'ocess' is not recognized as an internal or external command
```

**正确做法**：文件存 GBK，代码页写 `chcp 936`。这两个必须配套，一个错另一个也白搭。

### 1.2 换行：必须 CRLF

批处理文件**必须是 CRLF 换行**。若用 LF（Unix 风格），cmd 会吃掉每行的**首字符**：

```
'etsh' is not recognized as an internal or external command
'ession' is not recognized as an internal or external command
'cho' is not recognized as an internal or external command
```

注意看 —— `netsh` 变成了 `etsh`、`echo` 变成了 `cho`、`session` 变成了 `ession`。整个脚本从第一行就崩，后面的命令一条都执行不到。

**这个坑特别阴险**：脚本文件看起来完全正常，用编辑器打开也没问题，只有 cmd 执行时才出错，而且不报"语法错误"，只报一堆莫名其妙的"命令未找到"。

### 1.3 自查方法

```powershell
# 检查换行符（纯 LF > 0 就是坏文件）
$b = [System.IO.File]::ReadAllBytes('script.bat')
$crlf = 0; $lf = 0
for ($i = 0; $i -lt $b.Length; $i++) {
    if ($b[$i] -eq 10) { if ($i -gt 0 -and $b[$i-1] -eq 13) { $crlf++ } else { $lf++ } }
}
"CRLF=$crlf  LF=$lf"

# 检查编码：按 GBK 读，中文应该正常显示
$gbk = [System.Text.Encoding]::GetEncoding(936)
[System.IO.File]::ReadAllText('script.bat', $gbk)
```

### 1.4 正确写法

```powershell
$gbk = [System.Text.Encoding]::GetEncoding(936)
[System.IO.File]::WriteAllLines($path, $lines, $gbk)   # WriteAllLines 自动用 CRLF
```

**注意 `WriteAllLines` 与 `WriteAllText` 的区别**：

- `WriteAllLines` 逐行写入，自动使用 `Environment.NewLine`（Windows 上是 CRLF）✓
- `WriteAllText` 原样写入字符串里的换行符 —— 如果你手里是 LF 字符串，写出来就是坏文件 ✗

这个区别极其容易忽略。本项目的首次提交就踩了这个坑：用 `WriteAllText` 加 LF 字符串生成的脚本，测试时用 `WriteAllLines` 生成对照文件，结果测试全绿、真实文件全坏。

### 1.5 用 .gitattributes 锁死

光靠自觉不够 —— git 的 `core.autocrlf=true` 会在 add 时把 CRLF 规范化成 LF 存入仓库，检出时再转回来。看似无害，但如果有人在 `core.autocrlf=false` 的环境里检出（比如 Linux 上的 CI），拿到的就是 LF 文件。

所以项目根目录有 `.gitattributes`：

```
*.bat text eol=crlf
*.cmd text eol=crlf
```

这保证**任何平台、任何配置下**检出这些文件都是 CRLF。

---

## 二、批处理里的 `timeout` 命令可能被 Git 抢走

如果系统装了 Git for Windows 且 `C:\Program Files\Git\usr\bin` 排在 PATH 前面，批处理里的延时命令会失效：

```
timeout: invalid time interval '/t'
Try 'timeout --help' for more information.
```

`timeout /t 5` 被解析成了 Unix 版 `timeout`（它不认 `/t` 参数）。

**解决办法**：用 `ping` 做延时，它在 `System32` 里，不依赖 PATH 顺序：

```batch
ping -n 5 127.0.0.1 >nul
```

`-n 5` 表示发 5 个包（约 4 秒），这个写法从 Windows 95 时代就有了，兼容性最好。

---

## 三、`net session` 判断管理员权限的返回值

本项目用这个经典写法做自提权检测：

```batch
net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
```

**实测发现**：在非管理员权限下，`net session` 返回的退出码是 **2**，而不是网上常说的 **5**（拒绝访问）。

逻辑上只判断"非零即无权限"，所以不受影响。但如果你按"是不是等于 5"来写判断，就会出错。

另外提醒：`net session` 依赖 `LanmanServer` 服务。如果该服务被停用，即使有管理员权限也会返回非零 —— 这种环境下应该改用：

```batch
whoami /groups | findstr /C:"S-1-16-12288" >nul
```

（`S-1-16-12288` 是"高完整性级别"的 SID，即管理员令牌）

---

## 四、改完一定要做这三步验证

改脚本时很容易出现"测试全绿、真实文件全坏"的情况（见 1.4）。所以每次改完，按这个顺序验证：

**第一步：查文件本身的编码和换行**

```powershell
$gbk = [System.Text.Encoding]::GetEncoding(936)
$b = [System.IO.File]::ReadAllBytes('scripts\apply-campus-config.bat')
$crlf = 0; $lf = 0
for ($i = 0; $i -lt $b.Length; $i++) {
    if ($b[$i] -eq 10) { if ($i -gt 0 -and $b[$i-1] -eq 13) { $crlf++ } else { $lf++ } }
}
"CRLF=$crlf  LF=$lf"
[System.IO.File]::ReadAllText('scripts\apply-campus-config.bat', $gbk) | Select-Object -First 5
```

要求：`LF=0`，且中文正常显示。

**第二步：dry-run 跑一遍流程**

把危险命令（`netsh`、`route`、`ipconfig`）替换成 `echo` 模拟输出，再执行一遍，检查命令顺序和交互分支是否正确：

```powershell
$src = [System.IO.File]::ReadAllText('scripts\apply-campus-config.bat', $gbk)
$src = $src.Replace('netsh interface ip set address name="WLAN" static 172.19.245.138 255.255.255.0 192.168.128.1', 'echo [模拟] 设置静态IP')
# ... 其余危险命令同样替换
$src = $src.Replace('net session >nul 2>&1', 'ver >nul')   # 跳过提权分支
$src = $src.Replace('pause', 'ping -n 1 127.0.0.1 >nul')
[System.IO.File]::WriteAllText("$env:TEMP\dryrun.bat", $src, $gbk)
cmd /c "echo Y| `"$env:TEMP\dryrun.bat`""     # 自动回答 Y
```

**第三步：验证提权链路**

自提权是最容易出问题的一环，务必单独测一次（用一个只写日志、不改配置的探针脚本）：

```batch
@echo off
chcp 936 >nul
net session >nul 2>&1
echo [非提权] 返回码=%errorlevel% >> "%~dp0probe.txt"
if %errorlevel% neq 0 (
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
echo [提权后] 已进入管理员分支 >> "%~dp0probe.txt"
netsh interface ipv4 show config "WLAN" >> "%~dp0probe.txt" 2>&1
```

跑完检查 `probe.txt`：应该看到两次记录（非提权一次、提权一次），且 `netsh` 能正常输出。

---

## 五、关于中文显示与 GitHub 网页

bat 文件是 GBK 编码，这个选择是**为了 cmd 能正确执行**，代价是 **GitHub 网页查看会乱码**（GitHub 前端一律按 UTF-8 渲染文本文件）。

**经实测确认**：

| 途径 | 结果 |
| --- | --- |
| `git clone` 检出 | GBK 正常，CRLF 正常 ✓ |
| zip 下载解压 | GBK 正常，CRLF 正常 ✓ |
| GitHub 网页查看 | 乱码（仅显示问题，文件本身完好） |

因为 git 存的是字节、不做编码转换，`.gitattributes` 也只管换行，所以**下载下来的文件始终是正确的 GBK**。乱码纯粹是网页渲染层面的现象。

如果需要让网页也正常显示，可以给 `.gitattributes` 加 `working-tree-encoding=GBK`（实测三条路径都正常），但要注意 git 官方文档列出的兼容性代价：**2018 年 3 月之前的 git 版本、以及 JGit / libgit2 等替代实现不支持这个属性**，用这些客户端 clone 会拿到 UTF-8 版本的 bat，脚本会失效。

如果这个项目以后要开源，更彻底的做法是**整体迁移到 PowerShell**：`.ps1` 用 UTF-8 with BOM，编码、换行、网页显示、跨客户端全部一致，不存在这个矛盾。
