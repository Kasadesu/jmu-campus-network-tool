# 排查手册

校园网"连上了但用不了"的完整排查流程。

---

## 〇、先看这个：认证页为什么弹不出来

**症状**：校园网 WiFi 连上了，`ipconfig` 也显示有 IP 地址，但浏览器永远弹不出认证页。

**原因**：Windows 同时存在多条默认路由时，**接口 metric 数字最小的那条胜出**。如果笔记本同时还开着手机热点，它的 metric 通常比校园网低，会把默认路由抢走：

| 链路 | 地址 | 接口 metric | 结果 |
| --- | --- | --- | --- |
| 手机 USB 共享 / 热点 | `172.20.10.10/28` | 25 | **被优先选中** |
| 校园网 WLAN（`JMU-STU`） | `192.168.165.119/17` | 45 | 闲置 |

于是浏览器的 HTTP 请求全部走手机热点出去，**压根不经过校园网**，Dr.COM 的 Portal 劫持无从触发，认证页自然弹不出来。

**这类问题极难自查**：WiFi 图标正常、`ipconfig` 有地址、`ping` 网关也通 —— 一切都"看起来正常"，只是流量走了另一条路。

**快速确认**：

```powershell
tracert -d -h 3 10.8.2.2
# 第 1 跳若是 172.20.10.1（手机热点网关）→ 确认是这个问题
```

**处理方式**：**不要动默认路由**，而是给校园网相关网段加**独立路由**（即 `apply-campus-config.bat` 所做的事）：

```
10.0.0.0/8      → 校园网网关   （认证服 + 校内资源）
172.17.0.0/16   → 校园网网关   （校内 DNS、VPN 网关）
```

这样两条链路各管一摊、互不抢道：普通上网继续走手机热点，校园网流量走校园网。

---

## 一、快速决策树

按顺序执行，每一步都有明确的判读标准。

### 第 1 步：WiFi 真的连上了吗

```powershell
netsh wlan show interfaces
```

看 `State` 和 `SSID`：

```
State    : connected
SSID     : JMU-STU
Signal   : 79%
```

`State` 不是 `connected` → 无线关联本身有问题（密码、信号、網卡驱动），与本项目无关。

---

### 第 2 步：拿到 IP 了吗

```powershell
netsh interface ipv4 show config "WLAN"
```

正常情况下能看到 `IP Address` 和 `Default Gateway`。

**若显示 `169.254.x.x`** → DHCP 没成功（AP 侧问题，或网卡配置被改坏）。先跑 `restore-dhcp.bat`。

**若 `DHCP enabled: No`** → 当前是静态配置，检查 IP 是否属于该网络。

---

### 第 3 步：默认路由走哪条链路（**最关键的一步**）

```powershell
# 看有几条默认路由、优先级如何
Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0' |
    Select-Object InterfaceAlias, NextHop, RouteMetric

# 看某个目标地址实际会走哪条路
Find-NetRoute -RemoteIPAddress 10.8.2.2 |
    Select-Object IPAddress, InterfaceAlias, NextHop

# 看真实路径的第一跳
tracert -d -h 3 10.8.2.2
```

**判读**：

- 多条默认路由时，`RouteMetric` **最小的那条胜出**
- 若第一跳不是校园网网关（`192.168.128.1`），说明**流量走错链路了** —— 这正是认证页弹不出来的根因
- 典型症状：第一跳是手机热点网关（如 `172.20.10.1`）

**解决办法**：给校园网相关网段加独立路由，而不是动默认路由。

```
10.0.0.0/8      → 校园网网关
172.17.0.0/16   → 校园网网关
```

---

### 第 4 步：Windows 怎么判定连通性

```powershell
Get-NetConnectionProfile
```

| IPv4Connectivity | 含义 |
| --- | --- |
| `Internet` | 已通网（认证已生效） |
| `LocalNetwork` | 只有本地网络（**未认证**或路由不对） |
| `NoTraffic` | 完全没流量（配置有误） |
| `Subnet` / `Disconnected` | 更底层就没通 |

这是 Windows 的官方判定，比肉眼看网站是否打开可靠得多。

---

### 第 5 步：是不是被 Portal 劫持了

```powershell
# 用 http（不是 https），访问任意站点
curl.exe -s -i --max-time 12 http://1.1.1.1/

# 若要强制走校园网链路
curl.exe -s -i --max-time 12 --interface 192.168.165.119 http://1.1.1.1/
```

**判读**：

```
HTTP/1.1 200 ok
Server: Apache
<script>top.self.location.href='http://10.8.2.2/eportal/index.jsp?wlanuserip=...'</script>
```

出现 `10.8.2.2/eportal/index.jsp` → **被劫持了，尚未认证**。参数由认证系统生成。

```
HTTP/1.1 301 Moved Permanently
Server: cloudflare
```

出现真实服务器响应 → **流量已放行，认证生效**。

---

### 第 6 步：认证服可达吗

```powershell
ping -n 2 10.8.2.2
Test-NetConnection 10.8.2.2 -Port 80
```

可达说明校园网链路健康，问题只在"没认证"或"路由走错"。

**不可达** → 检查是否有到 `10.0.0.0/8` 的路由指向校园网接口。

---

### 第 7 步：认证了但校内资源不通

```powershell
# 校园网 DNS 应能解析出校内地址（公网 DNS 给不出）
Resolve-DnsName www.jmu.edu.cn -Server 172.17.8.32

# 应返回 10.14.0.10 这类内网地址

# 测试内网服务
curl.exe -s -o NUL -w "%{http_code}" --max-time 10 http://10.14.0.10/
```

**若解析出内网地址但访问不通** → 缺路由。参考第 3 步给 `10.0.0.0/8` 和 `172.17.0.0/16` 加路由。

---

## 二、典型误判

### 误判一："认证系统坏了"

**实际**：流量没经过校园网，Portal 拦截根本没触发。

**证据**：`Get-NetConnectionProfile` 显示 `LocalNetwork`，且 `tracert` 第一跳不是校园网网关。

### 误判二："改本机 IP 就能绕过认证"

**不成立。** Dr.COM 的准入绑定是 **接入 IP + MAC + NAS 端口/VLAN** 三件套。把网卡地址改成别的网段：

- 改成同 VLAN 他人的地址 → IP 冲突，且对方绑定不认你
- 改成其他 VLAN 的地址 → 二层不通，网关直接丢弃
- 认证前的 HTTP 劫持与本机地址无关，改什么都会被跳转

**实测验证**：把网卡地址从 `192.168.165.119` 改成校园网地址空间之外的 `172.19.245.138` 后，Portal 劫持行为**完全不变**，`mac`、`nasportid`、`vid` 三个参数一字未改 —— 证明认证系统识别的是接入端口而非本机自设地址。

### 误判三：`SIN` 之类的 CDN 节点代码 = 我的位置

`CF-RAY: xxxx-SIN` 里的 `SIN` 是 **Cloudflare 边缘节点所在机房**（新加坡樟宜机场 IATA 码），**不是你的位置**。`1.1.1.1` 是 anycast 地址，运营商路由把你送到拓扑上最近的节点，中国大陆用户常被送到香港/新加坡/东京。

**验证自己的真实出口 IP**：

```powershell
curl.exe -s --max-time 15 "http://ip-api.com/json/?fields=query,country,regionName,city,isp"
```

---

## 三、恢复与回滚

### 一键恢复

```powershell
netsh interface ip set address name="WLAN" dhcp
netsh interface ip set dns name="WLAN" dhcp
route delete 10.0.0.0
route delete 172.17.0.0
route delete 0.0.0.0 mask 0.0.0.0 192.168.128.1
```

**顺序有讲究**：先恢复 DHCP（它会自己拿回默认路由），再删持久路由。反过来会短暂失去默认路由。

也可直接运行 `scripts/restore-dhcp.bat`。

### 只删路由，保留静态 IP

```powershell
route delete 10.0.0.0
route delete 172.17.0.0
```

### 检查残留

```powershell
# 持久路由（重启后仍在）
route print -4 | Select-String "Persistent" -Context 0,5

# 接口配置
netsh interface ipv4 show config "WLAN"
```

### 换网络前的注意事项

静态 IP 是**绑定在网卡上**的，与连哪个 SSID 无关。所以：

- 带着笔记本去别的 WiFi 前，**必须先跑 `restore-dhcp.bat`**，否则连不上
- 持久路由中的 `10.0.0.0/8` 范围很大，若目标网络也用 `10.x` 网段可能干扰
  （网关不可达时 Windows 通常会忽略该路由，但不保证）

---

## 四、常用命令速查

```powershell
# 网络总览
Get-NetIPConfiguration -Detailed

# 所有接口地址与来源
Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notmatch '^169\.254' }

# 默认路由
Get-NetRoute -AddressFamily IPv4 -DestinationPrefix '0.0.0.0/0'

# 持久路由
route print -4

# 接口 metric
Get-NetIPInterface -AddressFamily IPv4 | Select-Object ifIndex,InterfaceAlias,Dhcp,InterfaceMetric

# 连通性判定
Get-NetConnectionProfile

# DNS 配置
Get-DnsClientServerAddress -AddressFamily IPv4

# WiFi 状态
netsh wlan show interfaces

# WiFi 配置文件列表
netsh wlan show profiles

# 当前出口公网 IP
curl.exe -s --max-time 15 "http://ip-api.com/json/?fields=query,country,regionName,city,isp"
```
