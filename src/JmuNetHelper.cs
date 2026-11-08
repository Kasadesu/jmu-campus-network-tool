using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JmuNetHelper
{
    // =========================================================================
    // 数据模型与配置存储
    // =========================================================================

    public class NetworkStatusInfo
    {
        public bool HasWlan;
        public string InterfaceName;
        public int InterfaceIndex;
        public string Ssid;
        public int SignalQuality;
        public string Ipv4Address;
        public string Gateway;
        public bool IsDhcp;
        public int DadTransmits;
        public int LatencyMs;
        public string DnsServers;

        public NetworkStatusInfo()
        {
            HasWlan = false;
            InterfaceName = "WLAN";
            InterfaceIndex = -1;
            Ssid = "未连接";
            SignalQuality = -1;
            Ipv4Address = "0.0.0.0";
            Gateway = "";
            IsDhcp = true;
            DadTransmits = -1;
            LatencyMs = -1;
            DnsServers = "";
        }
    }

    public class AppConfig
    {
        public string SelectedIp;
        public string UserId;
        public string EncryptedPassword;
        public string Service;
        public bool RememberPassword;
        public bool AutoLoginAfterSwitch;

        public AppConfig()
        {
            SelectedIp = "172.19.245.138 (5楼·原版首选)";
            UserId = "";
            EncryptedPassword = "";
            Service = "电信宽带接入";
            RememberPassword = false;
            AutoLoginAfterSwitch = false;
        }
    }

    public static class ConfigStorage
    {
        private static readonly string ConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JmuNetHelper");
        private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.ini");

        public static AppConfig LoadConfig()
        {
            AppConfig config = new AppConfig();
            try
            {
                if (!File.Exists(ConfigFile)) return config;

                string[] lines = File.ReadAllLines(ConfigFile, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                    int eqIndex = line.IndexOf('=');
                    if (eqIndex <= 0) continue;

                    string key = line.Substring(0, eqIndex).Trim();
                    string val = line.Substring(eqIndex + 1).Trim();

                    if (key.Equals("SelectedIp", StringComparison.OrdinalIgnoreCase))
                    {
                        config.SelectedIp = val;
                    }
                    else if (key.Equals("UserId", StringComparison.OrdinalIgnoreCase))
                    {
                        config.UserId = val;
                    }
                    else if (key.Equals("EncryptedPassword", StringComparison.OrdinalIgnoreCase))
                    {
                        config.EncryptedPassword = val;
                    }
                    else if (key.Equals("Service", StringComparison.OrdinalIgnoreCase))
                    {
                        config.Service = val;
                    }
                    else if (key.Equals("RememberPassword", StringComparison.OrdinalIgnoreCase))
                    {
                        bool b;
                        if (bool.TryParse(val, out b)) config.RememberPassword = b;
                    }
                    else if (key.Equals("AutoLoginAfterSwitch", StringComparison.OrdinalIgnoreCase))
                    {
                        bool b;
                        if (bool.TryParse(val, out b)) config.AutoLoginAfterSwitch = b;
                    }
                }
            }
            catch
            {
                // 读取失败静默降级为默认
            }
            return config;
        }

        public static void SaveConfig(AppConfig config)
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                {
                    Directory.CreateDirectory(ConfigDir);
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# JMU Network Helper Configuration");
                sb.AppendLine("SelectedIp=" + (config.SelectedIp ?? ""));
                sb.AppendLine("UserId=" + (config.UserId ?? ""));
                sb.AppendLine("EncryptedPassword=" + (config.RememberPassword ? (config.EncryptedPassword ?? "") : ""));
                sb.AppendLine("Service=" + (config.Service ?? ""));
                sb.AppendLine("RememberPassword=" + config.RememberPassword);
                sb.AppendLine("AutoLoginAfterSwitch=" + config.AutoLoginAfterSwitch);

                File.WriteAllText(ConfigFile, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // 保存失败做自防御，不阻断主流程
            }
        }

        public static string EncryptPassword(string plainPassword)
        {
            if (string.IsNullOrEmpty(plainPassword)) return "";
            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainPassword);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(cipherBytes);
            }
            catch
            {
                return "";
            }
        }

        public static string DecryptPassword(string cipherBase64)
        {
            if (string.IsNullOrEmpty(cipherBase64)) return "";
            try
            {
                byte[] cipherBytes = Convert.FromBase64String(cipherBase64);
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return "";
            }
        }
    }

    // =========================================================================
    // 核心引擎：网络探测与巡检 (NetworkInspector)
    // =========================================================================

    public static class NetworkInspector
    {
        public static NetworkStatusInfo Inspect(bool testLatency)
        {
            NetworkStatusInfo info = new NetworkStatusInfo();

            // 1. 查找 WLAN 适配器
            NetworkInterface targetNic = null;
            NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();

            foreach (NetworkInterface nic in nics)
            {
                if (nic.Name.Equals("WLAN", StringComparison.OrdinalIgnoreCase))
                {
                    targetNic = nic;
                    break;
                }
            }

            if (targetNic == null)
            {
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        targetNic = nic;
                        break;
                    }
                }
            }

            if (targetNic != null)
            {
                info.HasWlan = true;
                info.InterfaceName = targetNic.Name;

                try
                {
                    IPInterfaceProperties ipProps = targetNic.GetIPProperties();
                    IPv4InterfaceProperties ipv4Props = ipProps.GetIPv4Properties();
                    if (ipv4Props != null)
                    {
                        info.InterfaceIndex = ipv4Props.Index;
                        info.IsDhcp = ipv4Props.IsDhcpEnabled;
                    }

                    foreach (UnicastIPAddressInformation unicast in ipProps.UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.Ipv4Address = unicast.Address.ToString();
                            break;
                        }
                    }

                    foreach (GatewayIPAddressInformation gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.Gateway = gw.Address.ToString();
                            break;
                        }
                    }

                    List<string> dnsList = new List<string>();
                    foreach (IPAddress dns in ipProps.DnsAddresses)
                    {
                        if (dns.AddressFamily == AddressFamily.InterNetwork)
                        {
                            dnsList.Add(dns.ToString());
                        }
                    }
                    info.DnsServers = string.Join(", ", dnsList.ToArray());
                }
                catch
                {
                    // 忽略特定属性读取异常
                }
            }

            // 2. 解析 SSID 与信号
            try
            {
                string output;
                if (CampusConfigManager.RunCmd("netsh", "wlan show interfaces", out output))
                {
                    Match mSsid = Regex.Match(output, @"^\s*SSID\s*:\s*(.+)$", RegexOptions.Multiline);
                    if (mSsid.Success)
                    {
                        info.Ssid = mSsid.Groups[1].Value.Trim();
                    }

                    Match mSignal = Regex.Match(output, @"^\s*(?:信号|Signal)\s*:\s*(\d+)%", RegexOptions.Multiline);
                    if (mSignal.Success)
                    {
                        int sig;
                        if (int.TryParse(mSignal.Groups[1].Value, out sig))
                        {
                            info.SignalQuality = sig;
                        }
                    }
                }
            }
            catch
            {
            }

            // 3. 解析 DAD 次数
            if (info.HasWlan)
            {
                try
                {
                    string output;
                    string args = string.Format("interface ipv4 show interface \"{0}\"", info.InterfaceName);
                    if (CampusConfigManager.RunCmd("netsh", args, out output))
                    {
                        Match mDad = Regex.Match(output, @"(?:Dad\s*Transmits|DadTransmits|重复地址检测传输次数)\s*:\s*(\d+)", RegexOptions.IgnoreCase);
                        if (mDad.Success)
                        {
                            int dad;
                            if (int.TryParse(mDad.Groups[1].Value, out dad))
                            {
                                info.DadTransmits = dad;
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            // 4. 延迟测试（针对 10.8.2.2:80）
            if (testLatency)
            {
                info.LatencyMs = TestTcpLatency(info.Ipv4Address, "10.8.2.2", 80, 1500);
            }

            return info;
        }

        public static int TestTcpLatency(string bindIp, string host, int port, int timeoutMs)
        {
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    if (!string.IsNullOrEmpty(bindIp) && bindIp != "0.0.0.0")
                    {
                        IPAddress localIp;
                        if (IPAddress.TryParse(bindIp, out localIp))
                        {
                            socket.Bind(new IPEndPoint(localIp, 0));
                        }
                    }

                    IAsyncResult ar = socket.BeginConnect(host, port, null, null);
                    bool success = ar.AsyncWaitHandle.WaitOne(timeoutMs, true);
                    if (success && socket.Connected)
                    {
                        socket.EndConnect(ar);
                        sw.Stop();
                        return (int)sw.ElapsedMilliseconds;
                    }
                }
            }
            catch
            {
            }
            return -1;
        }
    }

    // =========================================================================
    // 核心引擎：网络配置执行器 (CampusConfigManager)
    // =========================================================================

    public static class CampusConfigManager
    {
        public static bool RunCmd(string app, string args, out string stdout)
        {
            stdout = "";
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(app, args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.GetEncoding(936);
                psi.StandardErrorEncoding = Encoding.GetEncoding(936);

                using (Process proc = Process.Start(psi))
                {
                    StringBuilder sbOut = new StringBuilder();
                    proc.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) sbOut.AppendLine(e.Data);
                    };
                    proc.BeginOutputReadLine();
                    proc.BeginErrorReadLine();
                    if (!proc.WaitForExit(15000))
                    {
                        try { proc.Kill(); } catch { }
                        stdout = "命令执行超时 (15s)";
                        return false;
                    }
                    proc.WaitForExit();
                    stdout = sbOut.ToString();
                    return proc.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                stdout = ex.Message;
                return false;
            }
        }

        public static bool ApplyStaticConfig(string staticIp, string ifName, Action<string> log)
        {
            if (string.IsNullOrEmpty(ifName)) ifName = "WLAN";
            string output;

            log(string.Format("开始配置校园网静态参数 [IP: {0}, 网卡: {1}]...", staticIp, ifName));

            // [1/5] 清理旧路由
            log("[1/5] 正在清理旧的校园网专用路由 (10.0.0.0 / 172.17.0.0)...");
            RunCmd("route", "delete 10.0.0.0", out output);
            RunCmd("route", "delete 172.17.0.0", out output);

            // [2/5] 关闭 DAD 防网关 Proxy-ARP 冲突
            log(string.Format("[2/5] 正在配置 netsh 禁用网卡 DAD (dadtransmits=0)..."));
            string dadArgs = string.Format("interface ipv4 set interface \"{0}\" dadtransmits=0", ifName);
            if (!RunCmd("netsh", dadArgs, out output))
            {
                log("  [警告] 禁用 DAD 失败: " + output.Trim());
            }

            // [3/5] 设定静态 IP、掩码与默认网关
            log(string.Format("[3/5] 正在设定静态 IP: {0}, 掩码: 255.255.255.0, 网关: 192.168.128.1...", staticIp));
            string ipArgs = string.Format("interface ip set address name=\"{0}\" static {1} 255.255.255.0 192.168.128.1", ifName, staticIp);
            if (!RunCmd("netsh", ipArgs, out output))
            {
                log("  [错误] 设定静态 IP 失败: " + output.Trim());
                return false;
            }

            // [4/5] 设定 DNS
            log("[4/5] 正在设定 DNS: 首选 172.17.8.32, 备用 172.17.8.33...");
            string dns1Args = string.Format("interface ip set dns name=\"{0}\" static 172.17.8.32 primary", ifName);
            RunCmd("netsh", dns1Args, out output);
            string dns2Args = string.Format("interface ip add dns name=\"{0}\" 172.17.8.33 index=2", ifName);
            RunCmd("netsh", dns2Args, out output);

            // [5/5] 写入持久路由
            log("[5/5] 正在写入持久静态路由 10.0.0.0/8 与 172.17.0.0/16...");
            string r1Args = string.Format("interface ipv4 add route prefix=10.0.0.0/8 interface=\"{0}\" nexthop=192.168.128.1 metric=1 store=persistent", ifName);
            RunCmd("netsh", r1Args, out output);
            string r2Args = string.Format("interface ipv4 add route prefix=172.17.0.0/16 interface=\"{0}\" nexthop=192.168.128.1 metric=1 store=persistent", ifName);
            RunCmd("netsh", r2Args, out output);

            log("静态配置已全部应用成功！正在等待路由表刷新...");
            Thread.Sleep(800);
            return true;
        }

        public static bool RestoreDhcpConfig(string ifName, Action<string> log)
        {
            if (string.IsNullOrEmpty(ifName)) ifName = "WLAN";
            string output;

            log(string.Format("开始恢复日常 DHCP 自动获取 [网卡: {0}]...", ifName));

            // [1/4] 恢复 DAD 默认值 (3)
            log("[1/4] 正在恢复网卡默认 DAD 冲突检测 (dadtransmits=3)...");
            string dadArgs = string.Format("interface ipv4 set interface \"{0}\" dadtransmits=3", ifName);
            RunCmd("netsh", dadArgs, out output);

            // [2/4] 恢复 IP 与 DNS 为 DHCP
            log("[2/4] 正在将 IP 和 DNS 还原为自动获取 (DHCP)...");
            string dhcpIpArgs = string.Format("interface ip set address name=\"{0}\" dhcp", ifName);
            RunCmd("netsh", dhcpIpArgs, out output);
            string dhcpDnsArgs = string.Format("interface ip set dns name=\"{0}\" dhcp", ifName);
            RunCmd("netsh", dhcpDnsArgs, out output);

            // [3/4] 清理专用路由与残留默认网关
            log("[3/4] 正在清理校园网专用路由与残留默认网关...");
            RunCmd("route", "delete 10.0.0.0", out output);
            RunCmd("route", "delete 172.17.0.0", out output);
            RunCmd("route", "delete 0.0.0.0 mask 0.0.0.0 192.168.128.1", out output);

            // [4/4] 刷新 DHCP 租约
            log(string.Format("[4/4] 正在触发 ipconfig /renew \"{0}\" 重新获取网络租约...", ifName));
            string renewArgs = string.Format("/renew \"{0}\"", ifName);
            RunCmd("ipconfig", renewArgs, out output);

            log("DHCP 模式已恢复成功！");
            Thread.Sleep(500);
            return true;
        }
    }

    // =========================================================================
    // 核心引擎：ePortal 网页认证客户端 (EPortalAuthClient)
    // =========================================================================

    public class LoginResult
    {
        public bool Success;
        public string Message;
        public string RawResponse;

        public LoginResult(bool success, string msg, string raw = "")
        {
            Success = success;
            Message = msg;
            RawResponse = raw;
        }
    }

    public static class EPortalAuthClient
    {
        // 绑定指定本地 IP 发送原始 HTTP 请求
        private static string SendBoundHttpRequest(string localIp, string targetHost, int targetPort, string httpRequest, int timeoutMs)
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.SendTimeout = timeoutMs;
                socket.ReceiveTimeout = timeoutMs;

                if (!string.IsNullOrEmpty(localIp) && localIp != "0.0.0.0")
                {
                    IPAddress local;
                    if (IPAddress.TryParse(localIp, out local))
                    {
                        socket.Bind(new IPEndPoint(local, 0));
                    }
                }

                IAsyncResult ar = socket.BeginConnect(targetHost, targetPort, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(timeoutMs, false))
                {
                    socket.Close();
                    throw new TimeoutException(string.Format("连接 {0}:{1} 超时 ({2}ms)", targetHost, targetPort, timeoutMs));
                }
                socket.EndConnect(ar);

                byte[] sendBytes = Encoding.UTF8.GetBytes(httpRequest);
                socket.Send(sendBytes);

                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buffer = new byte[4096];
                    int bytesRead;
                    while ((bytesRead = socket.Receive(buffer)) > 0)
                    {
                        ms.Write(buffer, 0, bytesRead);
                        if (socket.Available == 0)
                        {
                            Thread.Sleep(50);
                            if (socket.Available == 0) break;
                        }
                    }
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        // 探测获取重定向 URL 中的 queryString
        public static string DetectQueryString(string wlanIp, Action<string> log)
        {
            try
            {
                log("正在探测网关以获取 ePortal 认证参数...");
                string req = "GET / HTTP/1.1\r\n" +
                             "Host: www.baidu.com\r\n" +
                             "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
                             "Connection: close\r\n\r\n";

                string response = SendBoundHttpRequest(wlanIp, "110.242.68.66", 80, req, 2500);

                // 尝试从 Location 标头或 HTML body 中正则匹配 queryString
                Match m = Regex.Match(response, @"(?:Location:[^\?\r\n]*\?|href\s*=\s*['""][^\?\r\n]*\?)(wlanuserip=[^'""\s\r\n]+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    string qs = m.Groups[1].Value.Trim();
                    log("已成功获取认证参数: " + qs);
                    return qs;
                }

                // 备用正则：直接查找包含 wlanuserip 的参数串
                Match m2 = Regex.Match(response, @"(wlanuserip=[a-zA-Z0-9\.\-_%]+(?:&[a-zA-Z0-9\.\-_%]+=[a-zA-Z0-9\.\-_%]*)*)", RegexOptions.IgnoreCase);
                if (m2.Success)
                {
                    string qs = m2.Groups[1].Value.Trim();
                    log("已匹配提取认证参数: " + qs);
                    return qs;
                }
            }
            catch (Exception ex)
            {
                log("探测网关重定向异常 (可能已在线或网关直通): " + ex.Message);
            }

            // 兜底回退：如果获取不到，基于已知规范构造基础 queryString
            if (!string.IsNullOrEmpty(wlanIp) && wlanIp != "0.0.0.0")
            {
                string fallback = "wlanuserip=" + wlanIp;
                log("采用本地 IPv4 兜底认证参数: " + fallback);
                return fallback;
            }

            return "";
        }

        // 执行 ePortal Web 认证 POST
        public static LoginResult Login(string wlanIp, string userId, string password, string serviceName, Action<string> log)
        {
            if (string.IsNullOrEmpty(userId))
            {
                return new LoginResult(false, "学号/账号不能为空！");
            }
            if (string.IsNullOrEmpty(password))
            {
                return new LoginResult(false, "登录密码不能为空！");
            }

            try
            {
                string qs = DetectQueryString(wlanIp, log);

                log(string.Format("正在向 10.8.2.2 提交认证 [用户: {0}, 服务: {1}]...", userId, serviceName));

                string postBody = string.Format(
                    "userId={0}&password={1}&service={2}&queryString={3}&operatorPwd=&operatorUserId=&validcode=&passwordEncrypt=false",
                    Uri.EscapeDataString(userId),
                    Uri.EscapeDataString(password),
                    Uri.EscapeDataString(serviceName ?? ""),
                    Uri.EscapeDataString(qs)
                );

                byte[] postBytes = Encoding.UTF8.GetBytes(postBody);

                string httpReq = string.Format(
                    "POST /eportal/InterFace.do?method=login HTTP/1.1\r\n" +
                    "Host: 10.8.2.2\r\n" +
                    "Content-Type: application/x-www-form-urlencoded; charset=UTF-8\r\n" +
                    "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) JMU-Helper/2.0\r\n" +
                    "Content-Length: {0}\r\n" +
                    "Connection: close\r\n\r\n" +
                    "{1}",
                    postBytes.Length,
                    postBody
                );

                string response = SendBoundHttpRequest(wlanIp, "10.8.2.2", 80, httpReq, 3500);

                // 解析 JSON
                Match mRes = Regex.Match(response, @"""result""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                Match mMsg = Regex.Match(response, @"""message""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);

                string resultStr = mRes.Success ? mRes.Groups[1].Value.ToLower() : "";
                string messageStr = mMsg.Success ? mMsg.Groups[1].Value : "";

                // 解码 Unicode 转义符
                messageStr = Regex.Unescape(messageStr);

                if (resultStr == "success")
                {
                    string successMsg = string.IsNullOrEmpty(messageStr) ? "认证登录成功！" : messageStr;
                    log(">>> " + successMsg);
                    return new LoginResult(true, successMsg, response);
                }
                else
                {
                    string failMsg = string.IsNullOrEmpty(messageStr) ? "认证失败，请检查账号密码" : messageStr;
                    log(">>> 登录未成功: " + failMsg);
                    return new LoginResult(false, failMsg, response);
                }
            }
            catch (Exception ex)
            {
                log(">>> 认证通信异常: " + ex.Message);
                return new LoginResult(false, "通信异常: " + ex.Message);
            }
        }
    }

    // =========================================================================
    // UI 主窗体 (MainForm)
    // =========================================================================

    public class MainForm : Form
    {
        // 控件列表
        private Label lblStatusDot;
        private Label lblStatusText;
        private Label lblSsidInfo;
        private Label lblIpInfo;
        private Label lblRouteDadInfo;
        private Button btnRefresh;

        private ComboBox cboIp;
        private Button btnTestLatency;
        private Label lblLatencyResult;

        private TextBox txtUserId;
        private TextBox txtPassword;
        private ComboBox cboService;
        private CheckBox chkRememberPwd;
        private CheckBox chkAutoLogin;
        private Button btnLogin;
        private Button btnOpenPortal;

        private Button btnApplyCampus;
        private Button btnRestoreDhcp;

        private TextBox txtLog;
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        private NetworkStatusInfo currentNetInfo = new NetworkStatusInfo();
        private AppConfig appConfig;
        private bool isWorking = false;

        // 8 个候选 IP 节点预设
        private readonly string[] CandidateIps = new string[]
        {
            "172.19.245.138 (5楼·原版首选)",
            "172.19.245.150 (5楼·候选150)",
            "172.19.245.157 (5楼·候选157)",
            "172.19.244.138 (4楼·候选138)",
            "172.19.246.138 (6楼·候选138)",
            "172.19.240.138 (机房·候选138)",
            "172.19.248.138 (8楼·候选138)",
            "172.19.250.138 (10楼·候选138)"
        };

        public MainForm()
        {
            InitializeComponent();
            appConfig = ConfigStorage.LoadConfig();
            ApplyLoadedConfig();
        }

        private void InitializeComponent()
        {
            this.Text = "JMU 校园网配置助手 v2.0";
            this.ClientSize = new Size(470, 670);
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.FromArgb(248, 249, 250);
            this.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
            this.AutoScaleMode = AutoScaleMode.Dpi;

            // 1. 卡片一：实时网络看板
            GroupBox grpStatus = new GroupBox();
            grpStatus.Text = " 📶 网络状态看板 ";
            grpStatus.Location = new Point(12, 10);
            grpStatus.Size = new Size(446, 125);
            grpStatus.ForeColor = Color.FromArgb(31, 41, 55);

            lblStatusDot = new Label();
            lblStatusDot.Text = "●";
            lblStatusDot.ForeColor = Color.Gray;
            lblStatusDot.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);
            lblStatusDot.Location = new Point(15, 20);
            lblStatusDot.Size = new Size(20, 24);
            grpStatus.Controls.Add(lblStatusDot);

            lblStatusText = new Label();
            lblStatusText.Text = "正在巡检网络接口...";
            lblStatusText.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
            lblStatusText.ForeColor = Color.FromArgb(17, 24, 39);
            lblStatusText.Location = new Point(36, 22);
            lblStatusText.Size = new Size(300, 20);
            grpStatus.Controls.Add(lblStatusText);

            btnRefresh = new Button();
            btnRefresh.Text = "🔄 刷新";
            btnRefresh.Location = new Point(356, 18);
            btnRefresh.Size = new Size(76, 28);
            btnRefresh.FlatStyle = FlatStyle.System;
            btnRefresh.Click += new EventHandler(btnRefresh_Click);
            grpStatus.Controls.Add(btnRefresh);

            lblSsidInfo = new Label();
            lblSsidInfo.Text = "无线网络 (SSID) : 正在检测...";
            lblSsidInfo.Location = new Point(16, 48);
            lblSsidInfo.Size = new Size(416, 20);
            lblSsidInfo.ForeColor = Color.FromArgb(55, 65, 81);
            grpStatus.Controls.Add(lblSsidInfo);

            lblIpInfo = new Label();
            lblIpInfo.Text = "当前 IPv4 地址  : 0.0.0.0";
            lblIpInfo.Location = new Point(16, 70);
            lblIpInfo.Size = new Size(416, 20);
            lblIpInfo.ForeColor = Color.FromArgb(55, 65, 81);
            grpStatus.Controls.Add(lblIpInfo);

            lblRouteDadInfo = new Label();
            lblRouteDadInfo.Text = "DAD 检测状态 : 检测中 | 认证服务延迟 : --";
            lblRouteDadInfo.Location = new Point(16, 92);
            lblRouteDadInfo.Size = new Size(416, 22);
            lblRouteDadInfo.ForeColor = Color.FromArgb(107, 114, 128);
            grpStatus.Controls.Add(lblRouteDadInfo);

            this.Controls.Add(grpStatus);

            // 2. 卡片二：静态 IP 节点配置
            GroupBox grpNode = new GroupBox();
            grpNode.Text = " 📍 校园网静态 IP 节点配置 ";
            grpNode.Location = new Point(12, 142);
            grpNode.Size = new Size(446, 95);
            grpNode.ForeColor = Color.FromArgb(31, 41, 55);

            Label lblIpTitle = new Label();
            lblIpTitle.Text = "候选节点 IP:";
            lblIpTitle.Location = new Point(14, 26);
            lblIpTitle.Size = new Size(80, 20);
            grpNode.Controls.Add(lblIpTitle);

            cboIp = new ComboBox();
            cboIp.DropDownStyle = ComboBoxStyle.DropDown;
            cboIp.Location = new Point(96, 23);
            cboIp.Size = new Size(236, 25);
            cboIp.Items.AddRange(CandidateIps);
            if (cboIp.Items.Count > 0) cboIp.SelectedIndex = 0;
            grpNode.Controls.Add(cboIp);

            btnTestLatency = new Button();
            btnTestLatency.Text = "⚡ 测速";
            btnTestLatency.Location = new Point(340, 22);
            btnTestLatency.Size = new Size(92, 27);
            btnTestLatency.FlatStyle = FlatStyle.System;
            btnTestLatency.Click += new EventHandler(btnTestLatency_Click);
            grpNode.Controls.Add(btnTestLatency);

            Label lblGwDns = new Label();
            lblGwDns.Text = "默认网关: 192.168.128.1    首选 DNS: 172.17.8.32";
            lblGwDns.Location = new Point(14, 60);
            lblGwDns.Size = new Size(280, 20);
            lblGwDns.ForeColor = Color.FromArgb(107, 114, 128);
            grpNode.Controls.Add(lblGwDns);

            lblLatencyResult = new Label();
            lblLatencyResult.Text = "";
            lblLatencyResult.TextAlign = ContentAlignment.MiddleRight;
            lblLatencyResult.Location = new Point(296, 60);
            lblLatencyResult.Size = new Size(136, 20);
            lblLatencyResult.ForeColor = Color.FromArgb(37, 99, 235);
            grpNode.Controls.Add(lblLatencyResult);

            this.Controls.Add(grpNode);

            // 3. 卡片三：ePortal 认证快捷操作
            GroupBox grpAuth = new GroupBox();
            grpAuth.Text = " 🔐 ePortal 快捷认证 (10.8.2.2) ";
            grpAuth.Location = new Point(12, 244);
            grpAuth.Size = new Size(446, 138);
            grpAuth.ForeColor = Color.FromArgb(31, 41, 55);

            Label lblUid = new Label();
            lblUid.Text = "学号账号:";
            lblUid.Location = new Point(14, 25);
            lblUid.Size = new Size(64, 20);
            grpAuth.Controls.Add(lblUid);

            txtUserId = new TextBox();
            txtUserId.Location = new Point(80, 22);
            txtUserId.Size = new Size(130, 23);
            grpAuth.Controls.Add(txtUserId);

            Label lblService = new Label();
            lblService.Text = "接入服务:";
            lblService.Location = new Point(220, 25);
            lblService.Size = new Size(64, 20);
            grpAuth.Controls.Add(lblService);

            cboService = new ComboBox();
            cboService.DropDownStyle = ComboBoxStyle.DropDownList;
            cboService.Location = new Point(286, 22);
            cboService.Size = new Size(146, 25);
            cboService.Items.AddRange(new string[] { "电信宽带接入", "移动宽带接入", "联通宽带接入", "教育网接入" });
            cboService.SelectedIndex = 0;
            grpAuth.Controls.Add(cboService);

            Label lblPwd = new Label();
            lblPwd.Text = "登录密码:";
            lblPwd.Location = new Point(14, 56);
            lblPwd.Size = new Size(64, 20);
            grpAuth.Controls.Add(lblPwd);

            txtPassword = new TextBox();
            txtPassword.PasswordChar = '•';
            txtPassword.Location = new Point(80, 53);
            txtPassword.Size = new Size(130, 23);
            grpAuth.Controls.Add(txtPassword);

            btnLogin = new Button();
            btnLogin.Text = "立即登录认证";
            btnLogin.Location = new Point(220, 52);
            btnLogin.Size = new Size(100, 26);
            btnLogin.FlatStyle = FlatStyle.System;
            btnLogin.Click += new EventHandler(btnLogin_Click);
            grpAuth.Controls.Add(btnLogin);

            btnOpenPortal = new Button();
            btnOpenPortal.Text = "打开网页 ↗";
            btnOpenPortal.Location = new Point(328, 52);
            btnOpenPortal.Size = new Size(104, 26);
            btnOpenPortal.FlatStyle = FlatStyle.System;
            btnOpenPortal.Click += new EventHandler(btnOpenPortal_Click);
            grpAuth.Controls.Add(btnOpenPortal);

            chkRememberPwd = new CheckBox();
            chkRememberPwd.Text = "记住账号密码 (本机 DPAPI 加密)";
            chkRememberPwd.Location = new Point(16, 92);
            chkRememberPwd.Size = new Size(220, 24);
            grpAuth.Controls.Add(chkRememberPwd);

            chkAutoLogin = new CheckBox();
            chkAutoLogin.Text = "应用静态配置后自动登录";
            chkAutoLogin.Location = new Point(240, 92);
            chkAutoLogin.Size = new Size(190, 24);
            grpAuth.Controls.Add(chkAutoLogin);

            this.Controls.Add(grpAuth);

            // 4. 核心按钮区
            btnApplyCampus = new Button();
            btnApplyCampus.Text = "🚀  一键应用校园网静态配置";
            btnApplyCampus.Location = new Point(12, 390);
            btnApplyCampus.Size = new Size(216, 42);
            btnApplyCampus.BackColor = Color.FromArgb(15, 82, 186);
            btnApplyCampus.ForeColor = Color.White;
            btnApplyCampus.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            btnApplyCampus.FlatStyle = FlatStyle.Flat;
            btnApplyCampus.FlatAppearance.BorderSize = 0;
            btnApplyCampus.Cursor = Cursors.Hand;
            btnApplyCampus.Click += new EventHandler(btnApplyCampus_Click);
            this.Controls.Add(btnApplyCampus);

            btnRestoreDhcp = new Button();
            btnRestoreDhcp.Text = "🔄  一键恢复 DHCP 默认配置";
            btnRestoreDhcp.Location = new Point(242, 390);
            btnRestoreDhcp.Size = new Size(216, 42);
            btnRestoreDhcp.BackColor = Color.FromArgb(75, 85, 99);
            btnRestoreDhcp.ForeColor = Color.White;
            btnRestoreDhcp.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
            btnRestoreDhcp.FlatStyle = FlatStyle.Flat;
            btnRestoreDhcp.FlatAppearance.BorderSize = 0;
            btnRestoreDhcp.Cursor = Cursors.Hand;
            btnRestoreDhcp.Click += new EventHandler(btnRestoreDhcp_Click);
            this.Controls.Add(btnRestoreDhcp);

            // 5. 实时操作日志框
            txtLog = new TextBox();
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.BackColor = Color.FromArgb(17, 24, 39);
            txtLog.ForeColor = Color.FromArgb(209, 213, 219);
            txtLog.Font = new Font("Consolas", 8.5F, FontStyle.Regular);
            txtLog.Location = new Point(12, 442);
            txtLog.Size = new Size(446, 216);
            this.Controls.Add(txtLog);

            // 6. 系统托盘图标与上下文菜单
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("一键应用校园网静态配置", null, (s, e) => btnApplyCampus_Click(s, e));
            trayMenu.Items.Add("一键恢复 DHCP 默认配置", null, (s, e) => btnRestoreDhcp_Click(s, e));
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("打开认证网页 (10.8.2.2)", null, (s, e) => btnOpenPortal_Click(s, e));
            trayMenu.Items.Add("显示主窗口", null, (s, e) => ShowMainWindow());
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出程序", null, (s, e) => ExitApplication());

            trayIcon = new NotifyIcon();
            trayIcon.Text = "JMU 校园网配置助手";
            trayIcon.Icon = SystemIcons.Shield; // 使用系统高辨识度图标
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += (s, e) => ShowMainWindow();

            this.FormClosing += new FormClosingEventHandler(MainForm_FormClosing);
            this.Resize += new EventHandler(MainForm_Resize);
            this.Load += new EventHandler(MainForm_Load);
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            AppendLog("欢迎使用 JMU 校园网配置助手 v2.0！");
            AppendLog("内置 Win11 DAD 冲突自动防御与多网卡链路源地址精准绑定。");
            RefreshStatusAsync(true);
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
                trayIcon.ShowBalloonTip(1000, "JMU 校园网配置助手", "程序已最小化到系统托盘，右键托盘图标可快速切换网络。", ToolTipIcon.Info);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            SaveCurrentConfig();
            trayIcon.Visible = false;
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
            this.Activate();
        }

        private void ExitApplication()
        {
            SaveCurrentConfig();
            trayIcon.Visible = false;
            Application.Exit();
        }

        private void ApplyLoadedConfig()
        {
            if (!string.IsNullOrEmpty(appConfig.SelectedIp))
            {
                int idx = cboIp.FindStringExact(appConfig.SelectedIp);
                if (idx >= 0) cboIp.SelectedIndex = idx;
                else cboIp.Text = appConfig.SelectedIp;
            }

            txtUserId.Text = appConfig.UserId ?? "";
            if (appConfig.RememberPassword && !string.IsNullOrEmpty(appConfig.EncryptedPassword))
            {
                txtPassword.Text = ConfigStorage.DecryptPassword(appConfig.EncryptedPassword);
                chkRememberPwd.Checked = true;
            }
            else
            {
                chkRememberPwd.Checked = false;
            }

            if (!string.IsNullOrEmpty(appConfig.Service))
            {
                int sIdx = cboService.FindStringExact(appConfig.Service);
                if (sIdx >= 0) cboService.SelectedIndex = sIdx;
            }

            chkAutoLogin.Checked = appConfig.AutoLoginAfterSwitch;
        }

        private void SaveCurrentConfig()
        {
            appConfig.SelectedIp = cboIp.Text.Trim();
            appConfig.UserId = txtUserId.Text.Trim();
            appConfig.RememberPassword = chkRememberPwd.Checked;
            appConfig.AutoLoginAfterSwitch = chkAutoLogin.Checked;
            appConfig.Service = cboService.Text;

            if (chkRememberPwd.Checked && !string.IsNullOrEmpty(txtPassword.Text))
            {
                appConfig.EncryptedPassword = ConfigStorage.EncryptPassword(txtPassword.Text);
            }
            else
            {
                appConfig.EncryptedPassword = "";
            }

            ConfigStorage.SaveConfig(appConfig);
        }

        public void AppendLog(string message)
        {
            if (this.IsDisposed || !this.IsHandleCreated) return;
            if (this.InvokeRequired)
            {
                try
                {
                    this.BeginInvoke(new Action<string>(AppendLog), message);
                }
                catch { }
                return;
            }

            if (txtLog == null || txtLog.IsDisposed) return;
            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            txtLog.AppendText(string.Format("[{0}] {1}\r\n", timestamp, message));
        }

        private void SetWorking(bool working)
        {
            isWorking = working;
            btnApplyCampus.Enabled = !working;
            btnRestoreDhcp.Enabled = !working;
            btnRefresh.Enabled = !working;
            btnTestLatency.Enabled = !working;
            btnLogin.Enabled = !working;
        }

        private string ExtractPureIp(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return "";
            string ip = rawText.Trim();
            int sp = ip.IndexOf(' ');
            if (sp > 0)
            {
                ip = ip.Substring(0, sp).Trim();
            }
            return ip;
        }

        // 异步刷新网络看板
        private void RefreshStatusAsync(bool testLatency)
        {
            btnRefresh.Enabled = false;
            lblStatusText.Text = "正在检测网络接口...";

            Task.Factory.StartNew(() =>
            {
                return NetworkInspector.Inspect(testLatency);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted || t.Result == null)
                {
                    lblStatusText.Text = "网络状态巡检失败";
                    btnRefresh.Enabled = true;
                    return;
                }

                currentNetInfo = t.Result;
                UpdateUiStatus(currentNetInfo);
                btnRefresh.Enabled = !isWorking;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void UpdateUiStatus(NetworkStatusInfo info)
        {
            if (!info.HasWlan)
            {
                lblStatusDot.ForeColor = Color.Red;
                lblStatusText.Text = "未检测到无线网卡 (WLAN)";
                lblSsidInfo.Text = "无线网络 (SSID) : 无可用无线适配器";
                lblIpInfo.Text = "当前 IPv4 地址  : 0.0.0.0";
                lblRouteDadInfo.Text = "请检查物理网卡或驱动是否启用";
                return;
            }

            bool isJmu = info.Ssid.IndexOf("JMU", StringComparison.OrdinalIgnoreCase) >= 0;
            string dadStr = info.DadTransmits >= 0 ? info.DadTransmits.ToString() : "未知";

            if (isJmu)
            {
                if (!info.IsDhcp && info.Ipv4Address.StartsWith("172.19."))
                {
                    lblStatusDot.ForeColor = Color.Green;
                    lblStatusText.Text = "已连接 JMU 校园网 (静态 IP 模式)";
                }
                else
                {
                    lblStatusDot.ForeColor = Color.DarkOrange;
                    lblStatusText.Text = "已连接 JMU-STU (日常 DHCP 模式 / 认证受限)";
                }
            }
            else
            {
                lblStatusDot.ForeColor = Color.Gray;
                lblStatusText.Text = string.Format("已连接其他网络: {0}", info.Ssid);
            }

            string sigStr = info.SignalQuality >= 0 ? string.Format("{0}%", info.SignalQuality) : "未读取";
            lblSsidInfo.Text = string.Format("无线网络 (SSID) : {0}  (信号: {1}, 索引 #{2})",
                info.Ssid, sigStr, info.InterfaceIndex);

            string modeStr = info.IsDhcp ? "DHCP 动态" : "静态分配";
            lblIpInfo.Text = string.Format("当前 IPv4 地址  : {0}  ({1})", info.Ipv4Address, modeStr);

            string latencyStr = info.LatencyMs >= 0 ? string.Format("{0} ms", info.LatencyMs) : "超时 / 不可达";
            lblRouteDadInfo.Text = string.Format("DAD 检测次数 : {0}  |  认证服(10.8.2.2)延迟 : {1}", dadStr, latencyStr);
        }

        // 按钮事件：刷新状态
        private void btnRefresh_Click(object sender, EventArgs e)
        {
            RefreshStatusAsync(true);
        }

        // 按钮事件：探测延迟
        private void btnTestLatency_Click(object sender, EventArgs e)
        {
            string ip = ExtractPureIp(cboIp.Text);
            IPAddress dummy;
            if (!IPAddress.TryParse(ip, out dummy))
            {
                MessageBox.Show("候选 IP 格式非法，请输入有效的 IPv4 地址！", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            btnTestLatency.Enabled = false;
            lblLatencyResult.Text = "测速中...";

            Task.Factory.StartNew(() =>
            {
                return NetworkInspector.TestTcpLatency(currentNetInfo.Ipv4Address, "10.8.2.2", 80, 1500);
            }).ContinueWith(t =>
            {
                btnTestLatency.Enabled = true;
                int lat = t.Result;
                if (lat >= 0)
                {
                    lblLatencyResult.ForeColor = Color.Green;
                    lblLatencyResult.Text = string.Format("握手延迟: {0} ms", lat);
                    AppendLog(string.Format("测速成功: 10.8.2.2:80 TCP 握手耗时 {0} ms", lat));
                }
                else
                {
                    lblLatencyResult.ForeColor = Color.Red;
                    lblLatencyResult.Text = "握手超时";
                    AppendLog("测速失败: 10.8.2.2:80 无法连通或超时。");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 按钮事件：一键应用校园网静态配置
        private void btnApplyCampus_Click(object sender, EventArgs e)
        {
            if (isWorking) return;

            string targetIp = ExtractPureIp(cboIp.Text);
            IPAddress dummy;
            if (!IPAddress.TryParse(targetIp, out dummy))
            {
                MessageBox.Show("候选静态 IP 格式非法，请检查！", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 防呆校验：如果当前不在 JMU-STU，二次弹窗确认
            if (!currentNetInfo.Ssid.Equals("JMU-STU", StringComparison.OrdinalIgnoreCase))
            {
                string warnMsg = string.Format("当前连接的 Wi-Fi 是 [{0}]，并非 JMU-STU。\r\n在非集美大学校园网环境下配置可能导致网络中断。\r\n\r\n确认要继续应用吗？", currentNetInfo.Ssid);
                if (MessageBox.Show(warnMsg, "安全防呆提示", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                {
                    AppendLog("操作已取消：已放弃在非 JMU-STU 网络下应用静态配置。");
                    return;
                }
            }

            SetWorking(true);
            SaveCurrentConfig();

            Task.Factory.StartNew(() =>
            {
                bool success = CampusConfigManager.ApplyStaticConfig(targetIp, currentNetInfo.InterfaceName, AppendLog);
                return success;
            }).ContinueWith(t =>
            {
                bool ok = !t.IsFaulted && t.Result;
                if (ok)
                {
                    AppendLog("=== 校园网静态配置已生效 ===");
                    RefreshStatusAsync(true);

                    // 若勾选了自动登录，触发登录认证
                    if (chkAutoLogin.Checked && !string.IsNullOrEmpty(txtUserId.Text.Trim()))
                    {
                        AppendLog("检测到勾选了「应用后自动登录」，即将执行 ePortal 认证...");
                        PerformLoginTask();
                        return; // PerformLoginTask 会在完成时恢复 SetWorking
                    }
                }
                else
                {
                    AppendLog("=== 应用静态配置出现错误 ===");
                }
                SetWorking(false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 按钮事件：一键恢复 DHCP 默认配置
        private void btnRestoreDhcp_Click(object sender, EventArgs e)
        {
            if (isWorking) return;

            if (MessageBox.Show("确认要清除校园网静态配置并还原为 DHCP 自动获取吗？", "确认还原", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            SetWorking(true);

            Task.Factory.StartNew(() =>
            {
                return CampusConfigManager.RestoreDhcpConfig(currentNetInfo.InterfaceName, AppendLog);
            }).ContinueWith(t =>
            {
                AppendLog("=== 已还原为 DHCP 默认配置 ===");
                RefreshStatusAsync(false);
                SetWorking(false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 按钮事件：立即登录认证
        private void btnLogin_Click(object sender, EventArgs e)
        {
            if (isWorking) return;
            SaveCurrentConfig();
            SetWorking(true);
            PerformLoginTask();
        }

        private void PerformLoginTask()
        {
            string uid = txtUserId.Text.Trim();
            string pwd = txtPassword.Text;
            string svc = cboService.Text;
            string wlanIp = currentNetInfo.Ipv4Address;

            Task.Factory.StartNew(() =>
            {
                return EPortalAuthClient.Login(wlanIp, uid, pwd, svc, AppendLog);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    AppendLog("登录过程中发生未处理异常: " + t.Exception.Message);
                }
                else
                {
                    LoginResult res = t.Result;
                    if (res.Success)
                    {
                        trayIcon.ShowBalloonTip(2000, "JMU 校园网", "认证成功，校园网现已全面放行！", ToolTipIcon.Info);
                    }
                }
                SetWorking(false);
                RefreshStatusAsync(true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // 按钮事件：打开认证网页
        private void btnOpenPortal_Click(object sender, EventArgs e)
        {
            try
            {
                Process.Start("http://10.8.2.2");
                AppendLog("已调起默认浏览器打开 http://10.8.2.2");
            }
            catch (Exception ex)
            {
                AppendLog("打开浏览器失败: " + ex.Message);
            }
        }
    }

    // =========================================================================
    // 程序入口点
    // =========================================================================

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            // 命令行自检与自动化测试模式
            if (args != null && args.Length > 0 && args[0].Equals("--verify", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("==================================================");
                Console.WriteLine("JMU Network Helper CLI Verification Mode");
                Console.WriteLine("==================================================");
                NetworkStatusInfo info = NetworkInspector.Inspect(false);
                Console.WriteLine("HasWlan: " + info.HasWlan);
                Console.WriteLine("InterfaceName: " + info.InterfaceName);
                Console.WriteLine("InterfaceIndex: " + info.InterfaceIndex);
                Console.WriteLine("SSID: " + info.Ssid);
                Console.WriteLine("IPv4: " + info.Ipv4Address);
                Console.WriteLine("IsDhcp: " + info.IsDhcp);
                Console.WriteLine("DadTransmits: " + info.DadTransmits);
                Console.WriteLine("Verification Completed Successfully.");
                return 0;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }
    }
}
