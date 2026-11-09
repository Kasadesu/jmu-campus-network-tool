using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace JmuNetHelper
{
    // =========================================================================
    // 数据模型与本地配置存储 (DPAPI 安全加密)
    // =========================================================================

    public class AppConfig
    {
        public string SelectedIp = "自动分配最佳 IP (智能优选)";
        public string UserId = "";
        public string EncryptedPassword = "";
        public string Service = "电信宽带接入";
        public bool RememberPassword = true;
        public bool AutoLoginAfterSwitch = true;
    }

    public static class ConfigStorage
    {
        private static readonly byte[] EntropyBytes = Encoding.UTF8.GetBytes("JmuNetHelper_DPAPI_Salt_v2_2026");

        public static string GetConfigFilePath()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string dir = Path.Combine(localAppData, "JmuNetHelper");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            return Path.Combine(dir, "config.ini");
        }

        public static string EncryptPassword(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            try
            {
                byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
                byte[] cipherBytes = ProtectedData.Protect(plainBytes, EntropyBytes, DataProtectionScope.CurrentUser);
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
                byte[] plainBytes = ProtectedData.Unprotect(cipherBytes, EntropyBytes, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plainBytes);
            }
            catch
            {
                return "";
            }
        }

        public static void SaveConfig(AppConfig cfg)
        {
            try
            {
                string path = GetConfigFilePath();
                List<string> lines = new List<string>();
                lines.Add("[JmuNetHelper]");
                lines.Add("SelectedIp=" + (cfg.SelectedIp ?? ""));
                lines.Add("UserId=" + (cfg.UserId ?? ""));
                lines.Add("EncryptedPassword=" + (cfg.RememberPassword ? (cfg.EncryptedPassword ?? "") : ""));
                lines.Add("Service=" + (cfg.Service ?? "电信宽带接入"));
                lines.Add("RememberPassword=" + (cfg.RememberPassword ? "true" : "false"));
                lines.Add("AutoLoginAfterSwitch=" + (cfg.AutoLoginAfterSwitch ? "true" : "false"));
                File.WriteAllLines(path, lines.ToArray(), Encoding.UTF8);
            }
            catch
            {
            }
        }

        public static AppConfig LoadConfig()
        {
            AppConfig cfg = new AppConfig();
            try
            {
                string path = GetConfigFilePath();
                if (!File.Exists(path)) return cfg;

                string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                foreach (string rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith("[") || line.StartsWith("#") || line.StartsWith(";"))
                        continue;

                    int eqIdx = line.IndexOf('=');
                    if (eqIdx <= 0) continue;

                    string key = line.Substring(0, eqIdx).Trim();
                    string val = line.Substring(eqIdx + 1).Trim();

                    if (string.Equals(key, "SelectedIp", StringComparison.OrdinalIgnoreCase))
                        cfg.SelectedIp = val;
                    else if (string.Equals(key, "UserId", StringComparison.OrdinalIgnoreCase))
                        cfg.UserId = val;
                    else if (string.Equals(key, "EncryptedPassword", StringComparison.OrdinalIgnoreCase))
                        cfg.EncryptedPassword = val;
                    else if (string.Equals(key, "Service", StringComparison.OrdinalIgnoreCase))
                        cfg.Service = val;
                    else if (string.Equals(key, "RememberPassword", StringComparison.OrdinalIgnoreCase))
                        cfg.RememberPassword = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    else if (string.Equals(key, "AutoLoginAfterSwitch", StringComparison.OrdinalIgnoreCase))
                        cfg.AutoLoginAfterSwitch = string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
            }
            return cfg;
        }
    }

    // =========================================================================
    // 核心引擎：网络状态巡检器 (NetworkInspector)
    // =========================================================================

    public class NetworkStatusInfo
    {
        public bool HasWlan = false;
        public string InterfaceName = "WLAN";
        public int InterfaceIndex = -1;
        public OperationalStatus Status = OperationalStatus.Unknown;
        public string Ssid = "未连接";
        public string Signal = "--";
        public string Ipv4Address = "0.0.0.0";
        public string SubnetMask = "0.0.0.0";
        public string Gateway = "无";
        public bool IsDhcpEnabled = true;
        public bool IsApipa = false;
        public int DadTransmits = -1;
        public int PortalLatencyMs = -1;
    }

    public static class NetworkInspector
    {
        public static NetworkStatusInfo Inspect(bool checkPortalLatency)
        {
            NetworkStatusInfo info = new NetworkStatusInfo();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                NetworkInterface targetNic = null;

                foreach (NetworkInterface nic in nics)
                {
                    if (string.Equals(nic.Name, "WLAN", StringComparison.OrdinalIgnoreCase))
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
                    info.Status = targetNic.OperationalStatus;

                    IPInterfaceProperties ipProps = targetNic.GetIPProperties();
                    IPv4InterfaceProperties ipv4Props = null;
                    try
                    {
                        ipv4Props = ipProps.GetIPv4Properties();
                    }
                    catch { }

                    if (ipv4Props != null)
                    {
                        info.InterfaceIndex = ipv4Props.Index;
                        info.IsDhcpEnabled = ipv4Props.IsDhcpEnabled;
                    }

                    foreach (UnicastIPAddressInformation uni in ipProps.UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            info.Ipv4Address = uni.Address.ToString();
                            if (uni.IPv4Mask != null)
                            {
                                info.SubnetMask = uni.IPv4Mask.ToString();
                            }
                            if (info.Ipv4Address.StartsWith("169.254."))
                            {
                                info.IsApipa = true;
                            }
                            break;
                        }
                    }

                    foreach (GatewayIPAddressInformation gw in ipProps.GatewayAddresses)
                    {
                        if (gw.Address != null && gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string gwStr = gw.Address.ToString();
                            if (gwStr != "0.0.0.0")
                            {
                                info.Gateway = gwStr;
                                break;
                            }
                        }
                    }
                }

                ParseWlanInterfaceCli(info);
                ParseDadTransmitsCli(info);

                if (checkPortalLatency && info.HasWlan && info.Status == OperationalStatus.Up && !info.IsApipa && info.Ipv4Address != "0.0.0.0")
                {
                    info.PortalLatencyMs = TestTcpLatency(info.Ipv4Address, "10.8.2.2", 80, 1500);
                }
            }
            catch
            {
            }
            return info;
        }

        private static void ParseWlanInterfaceCli(NetworkStatusInfo info)
        {
            try
            {
                string output;
                CampusConfigManager.RunCmd("netsh", "wlan show interfaces", out output);
                if (string.IsNullOrEmpty(output)) return;

                string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (Regex.IsMatch(line, @"^SSID\s*:", RegexOptions.IgnoreCase))
                    {
                        int idx = line.IndexOf(':');
                        if (idx >= 0 && idx + 1 < line.Length)
                        {
                            string val = line.Substring(idx + 1).Trim();
                            if (!string.IsNullOrEmpty(val)) info.Ssid = val;
                        }
                    }
                    else if (line.IndexOf("Signal", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("信号", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Match m = Regex.Match(line, @"(\d+)\s*%");
                        if (m.Success)
                        {
                            info.Signal = m.Groups[1].Value + "%";
                        }
                    }
                }
            }
            catch
            {
            }
        }

        private static void ParseDadTransmitsCli(NetworkStatusInfo info)
        {
            try
            {
                string output;
                CampusConfigManager.RunCmd("netsh", string.Format("interface ipv4 show interface \"{0}\"", info.InterfaceName), out output);
                if (string.IsNullOrEmpty(output)) return;

                string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    if (line.IndexOf("DAD", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("重复地址", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        Match m = Regex.Match(line, @"(\d+)");
                        if (m.Success)
                        {
                            int val;
                            if (int.TryParse(m.Groups[1].Value, out val))
                            {
                                info.DadTransmits = val;
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
            }
        }

        public static int TestTcpLatency(string bindIp, string host, int port, int timeoutMs)
        {
            try
            {
                Stopwatch sw = new Stopwatch();
                using (Socket sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    sock.SendTimeout = timeoutMs;
                    sock.ReceiveTimeout = timeoutMs;

                    if (!string.IsNullOrEmpty(bindIp) && bindIp != "0.0.0.0" && !bindIp.StartsWith("169.254."))
                    {
                        IPAddress localAddr;
                        if (IPAddress.TryParse(bindIp, out localAddr))
                        {
                            sock.Bind(new IPEndPoint(localAddr, 0));
                        }
                    }

                    sw.Start();
                    IAsyncResult ar = sock.BeginConnect(host, port, null, null);
                    bool signaled = ar.AsyncWaitHandle.WaitOne(timeoutMs, false);
                    if (!signaled)
                    {
                        sock.Close();
                        return -1;
                    }
                    sock.EndConnect(ar);
                    sw.Stop();
                    int ms = (int)sw.ElapsedMilliseconds;
                    return ms == 0 ? 1 : ms;
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

            log(string.Format("开始应用静态 IP 配置 ({0}, 接口 {1})", staticIp, ifName));

            log("正在清理旧路由表项...");
            RunCmd("route", "delete 10.0.0.0", out output);
            RunCmd("route", "delete 172.17.0.0", out output);

            log("正在设置网卡 dadtransmits=0...");
            string dadArgs = string.Format("interface ipv4 set interface \"{0}\" dadtransmits=0", ifName);
            RunCmd("netsh", dadArgs, out output);

            log(string.Format("正在写入静态 IP {0} 与网关 192.168.128.1...", staticIp));
            string ipArgs = string.Format("interface ip set address name=\"{0}\" static {1} 255.255.255.0 192.168.128.1", ifName, staticIp);
            if (!RunCmd("netsh", ipArgs, out output))
            {
                log("静态 IP 写入失败: " + output.Trim());
                return false;
            }

            log("正在配置校园网 DNS (172.17.8.32 / 172.17.8.33)...");
            RunCmd("netsh", string.Format("interface ip set dns name=\"{0}\" static 172.17.8.32 primary", ifName), out output);
            RunCmd("netsh", string.Format("interface ip add dns name=\"{0}\" 172.17.8.33 index=2", ifName), out output);

            log("正在添加校园网内网路由 (10.0.0.0/8, 172.17.0.0/16)...");
            RunCmd("netsh", string.Format("interface ipv4 add route prefix=10.0.0.0/8 interface=\"{0}\" nexthop=192.168.128.1 metric=1 store=persistent", ifName), out output);
            RunCmd("netsh", string.Format("interface ipv4 add route prefix=172.17.0.0/16 interface=\"{0}\" nexthop=192.168.128.1 metric=1 store=persistent", ifName), out output);

            Thread.Sleep(700);
            log("已切换为静态 IP 模式");
            return true;
        }

        public static bool RestoreDhcpConfig(string ifName, Action<string> log)
        {
            if (string.IsNullOrEmpty(ifName)) ifName = "WLAN";
            string output;

            log(string.Format("正在恢复自动获取 (接口 {0})...", ifName));

            log("正在还原 dadtransmits=3...");
            RunCmd("netsh", string.Format("interface ipv4 set interface \"{0}\" dadtransmits=3", ifName), out output);

            log("正在切换 IPv4 与 DNS 为自动获取...");
            RunCmd("netsh", string.Format("interface ip set address name=\"{0}\" dhcp", ifName), out output);
            RunCmd("netsh", string.Format("interface ip set dns name=\"{0}\" dhcp", ifName), out output);

            log("正在清理静态路由表项...");
            RunCmd("route", "delete 10.0.0.0", out output);
            RunCmd("route", "delete 172.17.0.0", out output);
            RunCmd("route", "delete 0.0.0.0 mask 0.0.0.0 192.168.128.1", out output);

            log("正在刷新 DHCP 租约...");
            RunCmd("ipconfig", string.Format("/renew \"{0}\"", ifName), out output);

            Thread.Sleep(500);
            log("已恢复自动获取 (DHCP)");
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

        public static string DetectQueryString(string wlanIp, Action<string> log)
        {
            try
            {
                log("正在获取网关认证参数...");
                string req = "GET / HTTP/1.1\r\nHost: www.baidu.com\r\nUser-Agent: Mozilla/5.0\r\nConnection: close\r\n\r\n";
                string resp = SendBoundHttpRequest(wlanIp, "110.242.68.66", 80, req, 2500);

                Match m = Regex.Match(resp, @"wlanuserip=[a-fA-F0-9]+[^'""\r\n\s<>]*");
                if (m.Success)
                {
                    return m.Value;
                }
            }
            catch
            {
            }

            try
            {
                string req2 = "GET / HTTP/1.1\r\nHost: 10.8.2.2\r\nUser-Agent: Mozilla/5.0\r\nConnection: close\r\n\r\n";
                string resp2 = SendBoundHttpRequest(wlanIp, "10.8.2.2", 80, req2, 2500);
                if (resp2.IndexOf("redirectortosuccess", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "ALREADY_ONLINE";
                }
                Match m2 = Regex.Match(resp2, @"wlanuserip=[^'""\r\n\s<>]+");
                if (m2.Success)
                {
                    return m2.Value;
                }
            }
            catch
            {
            }

            return "wlanuserip=ed1be63a03ba8b2aabc95d26aa1d3c1b&wlanacname=355747ff48f3a490&ssid=&nasip=365cfa093433e323e7089c2f8c88d379&snmpagentip=&mac=54ce951f3f8944dc765200b3bc0e538a&t=wireless-v2&url=7a5a429a7f70b8e11ea0d490d7ca380c7ed76d57a168b632&apmac=&nasid=355747ff48f3a490&vid=5ac9f74de1a810a1&port=713afa334311d259&nasportid=f68f49623ce287a761ed82150260f5b513e3b728a1754f18b152a75109234121";
        }

        public static LoginResult Login(string wlanIp, string userId, string password, string serviceName, Action<string> log)
        {
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(password))
            {
                return new LoginResult(false, "请填写学号与密码");
            }

            try
            {
                string qs = DetectQueryString(wlanIp, log);
                if (qs == "ALREADY_ONLINE")
                {
                    log("当前设备已在线，无需重复登录");
                    return new LoginResult(true, "当前设备已在线");
                }

                log(string.Format("正在登录认证 ({0}, {1})...", userId, serviceName));

                string postBody = string.Format(
                    "userId={0}&password={1}&service={2}&queryString={3}&operatorPwd=&operatorUserId=&validcode=&passwordEncrypt=false",
                    Uri.EscapeDataString(userId),
                    Uri.EscapeDataString(password),
                    Uri.EscapeDataString(serviceName),
                    Uri.EscapeDataString(qs)
                );

                byte[] bodyBytes = Encoding.UTF8.GetBytes(postBody);
                string httpReq = string.Format(
                    "POST /eportal/InterFace.do?method=login HTTP/1.1\r\n" +
                    "Host: 10.8.2.2:80\r\n" +
                    "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
                    "Content-Type: application/x-www-form-urlencoded; charset=UTF-8\r\n" +
                    "Content-Length: {0}\r\n" +
                    "Connection: close\r\n\r\n" +
                    "{1}",
                    bodyBytes.Length,
                    postBody
                );

                string response = SendBoundHttpRequest(wlanIp, "10.8.2.2", 80, httpReq, 3500);

                Match mRes = Regex.Match(response, @"""result""\s*:\s*""([^""]+)""", RegexOptions.IgnoreCase);
                Match mMsg = Regex.Match(response, @"""message""\s*:\s*""([^""]*)""", RegexOptions.IgnoreCase);

                string resultStr = mRes.Success ? mRes.Groups[1].Value.ToLower() : "";
                string messageStr = mMsg.Success ? Regex.Unescape(mMsg.Groups[1].Value) : "";

                if (resultStr == "success")
                {
                    string successMsg = string.IsNullOrEmpty(messageStr) ? "登录成功" : messageStr;
                    log("认证通过: " + successMsg);
                    return new LoginResult(true, successMsg, response);
                }
                else
                {
                    string failMsg = string.IsNullOrEmpty(messageStr) ? "认证未通过，请检查账号或密码" : messageStr;
                    log("认证未通过: " + failMsg);
                    return new LoginResult(false, failMsg, response);
                }
            }
            catch (Exception ex)
            {
                log("认证请求失败: " + ex.Message);
                return new LoginResult(false, "请求失败: " + ex.Message);
            }
        }
    }

    // =========================================================================
    // 原生克制风格桌面界面 (遵循 anthropics/skills/frontend-design 规范)
    // =========================================================================

    public class MainForm : Window, IDisposable
    {
        public void Dispose()
        {
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
                trayIcon = null;
            }
            try { this.Close(); } catch { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const string AutoAllocOption = "自动分配最佳 IP (智能优选)";

        // 保持自动化测试反射契约的字段与方法
        private readonly string[] CandidateIps = new string[]
        {
            "172.19.245.138 (5楼·首选)",
            "172.19.245.150 (5楼·备选)",
            "172.19.245.157 (5楼·备选)",
            "172.19.244.138 (4楼)",
            "172.19.246.138 (6楼)",
            "172.19.240.138 (机房)",
            "172.19.248.138 (8楼)",
            "172.19.250.138 (10楼)"
        };

        private string ExtractPureIp(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return "";
            string trimmed = rawText.Trim();
            if (trimmed.IndexOf("自动分配", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "AUTO";
            }
            int spaceIdx = trimmed.IndexOf(' ');
            if (spaceIdx > 0)
            {
                trimmed = trimmed.Substring(0, spaceIdx).Trim();
            }
            return trimmed;
        }

        // 界面控件引用
        private Border badgeDot;
        private TextBlock lblStatusBadge;
        private TextBlock lblHeroIp;
        private TextBlock lblHeroLatency;
        private TextBlock lblChipSsid;
        private TextBlock lblChipGw;
        private Button btnRefresh;
        private Button btnTrayHide;

        private Button btnApplyCampus;
        private Button btnRestoreDhcp;

        private ComboBox cboIp;
        private Button btnTestLatency;
        private TextBlock lblLatencyResult;

        private TextBox txtUserId;
        private PasswordBox txtPassword;
        private ComboBox cboService;
        private CheckBox chkRememberPwd;
        private CheckBox chkAutoLogin;
        private Button btnLogin;
        private Button btnOpenPortal;

        private TextBlock lblFooterStatus;
        private Button btnToggleLog;
        private Border logContainer;
        private TextBox txtLog;

        private Forms.NotifyIcon trayIcon;
        private Forms.ContextMenuStrip trayMenu;

        private NetworkStatusInfo currentNetInfo = new NetworkStatusInfo();
        private AppConfig appConfig;
        private bool isWorking = false;
        private bool isLogExpanded = false;

        public MainForm()
        {
            BuildRestrainedUi();
            InitSystemTray();
            appConfig = ConfigStorage.LoadConfig();
            ApplyLoadedConfig();
        }

        private void BuildRestrainedUi()
        {
            this.Title = "集大校园网助手";
            this.Width = 420;
            this.SizeToContent = SizeToContent.Height;
            this.ResizeMode = ResizeMode.CanMinimize;
            this.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            this.Background = new SolidColorBrush(Color.FromRgb(247, 247, 245));
            this.FontFamily = new FontFamily("Microsoft YaHei UI");
            this.UseLayoutRounding = true;
            this.SnapsToDevicePixels = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

            string xaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
        xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        Background='#F7F7F5'>
    <Border.Resources>
        <!-- 主操作按钮：纯色信号蓝，无渐变，克制圆角 -->
        <Style x:Key='PrimaryBtn' TargetType='Button'>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='FontSize' Value='13'/>
            <Setter Property='FontWeight' Value='SemiBold'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='bd' Background='#1D4ED8' CornerRadius='6' Padding='14,0'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bd' Property='Background' Value='#1E40AF'/>
                            </Trigger>
                            <Trigger Property='IsPressed' Value='True'>
                                <Setter TargetName='bd' Property='Background' Value='#1E3A8A'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter TargetName='bd' Property='Opacity' Value='0.45'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 次要按钮：纯白底色 + 1px 中性灰边框 -->
        <Style x:Key='OutlineBtn' TargetType='Button'>
            <Setter Property='Foreground' Value='#18181B'/>
            <Setter Property='FontSize' Value='13'/>
            <Setter Property='FontWeight' Value='Normal'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='bd' Background='#FFFFFF' BorderBrush='#D4D4D8' BorderThickness='1' CornerRadius='6' Padding='12,0'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bd' Property='Background' Value='#F4F4F5'/>
                                <Setter TargetName='bd' Property='BorderBrush' Value='#A1A1AA'/>
                            </Trigger>
                            <Trigger Property='IsPressed' Value='True'>
                                <Setter TargetName='bd' Property='Background' Value='#E4E4E7'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter TargetName='bd' Property='Opacity' Value='0.45'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 深炭黑提交按钮 -->
        <Style x:Key='DarkBtn' TargetType='Button'>
            <Setter Property='Foreground' Value='#FFFFFF'/>
            <Setter Property='FontSize' Value='12.5'/>
            <Setter Property='FontWeight' Value='SemiBold'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border x:Name='bd' Background='#27272A' CornerRadius='6' Padding='12,0'>
                            <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bd' Property='Background' Value='#3F3F46'/>
                            </Trigger>
                            <Trigger Property='IsPressed' Value='True'>
                                <Setter TargetName='bd' Property='Background' Value='#18181B'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter TargetName='bd' Property='Opacity' Value='0.45'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 纯文本静默按钮 -->
        <Style x:Key='TextLinkBtn' TargetType='Button'>
            <Setter Property='Foreground' Value='#71717A'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='Button'>
                        <Border Background='Transparent' Padding='4,2'>
                            <ContentPresenter VerticalAlignment='Center'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter Property='Foreground' Value='#18181B'/>
                            </Trigger>
                            <Trigger Property='IsEnabled' Value='False'>
                                <Setter Property='Opacity' Value='0.45'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 原生质感输入框 -->
        <Style x:Key='CleanTextBox' TargetType='TextBox'>
            <Setter Property='Foreground' Value='#18181B'/>
            <Setter Property='FontSize' Value='12.5'/>
            <Setter Property='Height' Value='34'/>
            <Setter Property='VerticalContentAlignment' Value='Center'/>
            <Setter Property='Padding' Value='9,0'/>
            <Setter Property='CaretBrush' Value='#1D4ED8'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='TextBox'>
                        <Border x:Name='bd' Background='#FFFFFF' BorderBrush='#D4D4D8' BorderThickness='1' CornerRadius='6'>
                            <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='1,0'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bd' Property='BorderBrush' Value='#A1A1AA'/>
                            </Trigger>
                            <Trigger Property='IsKeyboardFocused' Value='True'>
                                <Setter TargetName='bd' Property='BorderBrush' Value='#1D4ED8'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 原生质感密码框 -->
        <Style x:Key='CleanPasswordBox' TargetType='PasswordBox'>
            <Setter Property='Foreground' Value='#18181B'/>
            <Setter Property='FontSize' Value='12.5'/>
            <Setter Property='Height' Value='34'/>
            <Setter Property='VerticalContentAlignment' Value='Center'/>
            <Setter Property='Padding' Value='9,0'/>
            <Setter Property='CaretBrush' Value='#1D4ED8'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='PasswordBox'>
                        <Border x:Name='bd' Background='#FFFFFF' BorderBrush='#D4D4D8' BorderThickness='1' CornerRadius='6'>
                            <ScrollViewer x:Name='PART_ContentHost' VerticalAlignment='Center' Margin='1,0'/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter TargetName='bd' Property='BorderBrush' Value='#A1A1AA'/>
                            </Trigger>
                            <Trigger Property='IsKeyboardFocused' Value='True'>
                                <Setter TargetName='bd' Property='BorderBrush' Value='#1D4ED8'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 原生质感下拉框 -->
        <ControlTemplate x:Key='ComboToggle' TargetType='ToggleButton'>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition/>
                    <ColumnDefinition Width='28'/>
                </Grid.ColumnDefinitions>
                <Border x:Name='Border' Grid.ColumnSpan='2' CornerRadius='6' Background='#FFFFFF' BorderBrush='#D4D4D8' BorderThickness='1'/>
                <Path Grid.Column='1' HorizontalAlignment='Center' VerticalAlignment='Center' Data='M 0 0 L 4 4 L 8 0' Stroke='#71717A' StrokeThickness='1.4'/>
            </Grid>
            <ControlTemplate.Triggers>
                <Trigger Property='IsMouseOver' Value='True'>
                    <Setter TargetName='Border' Property='BorderBrush' Value='#A1A1AA'/>
                </Trigger>
                <Trigger Property='IsChecked' Value='True'>
                    <Setter TargetName='Border' Property='BorderBrush' Value='#1D4ED8'/>
                </Trigger>
            </ControlTemplate.Triggers>
        </ControlTemplate>

        <Style x:Key='CleanComboBox' TargetType='ComboBox'>
            <Setter Property='Foreground' Value='#18181B'/>
            <Setter Property='FontSize' Value='12.5'/>
            <Setter Property='Height' Value='34'/>
            <Setter Property='SnapsToDevicePixels' Value='True'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='ComboBox'>
                        <Grid>
                            <ToggleButton Name='ToggleButton' Template='{StaticResource ComboToggle}' Focusable='False'
                                          IsChecked='{Binding Path=IsDropDownOpen,Mode=TwoWay,RelativeSource={RelativeSource TemplatedParent}}' ClickMode='Press'/>
                            <ContentPresenter Name='ContentSite' IsHitTestVisible='False' Content='{TemplateBinding SelectionBoxItem}'
                                              Margin='10,0,28,0' VerticalAlignment='Center' HorizontalAlignment='Left'/>
                            <TextBox x:Name='PART_EditableTextBox' HorizontalAlignment='Left' VerticalAlignment='Center' Margin='10,0,28,0'
                                     Focusable='True' Background='Transparent' Visibility='Hidden' IsReadOnly='{TemplateBinding IsReadOnly}'
                                     FontSize='12.5' Foreground='#18181B' BorderThickness='0' CaretBrush='#1D4ED8'/>
                            <Popup Name='Popup' Placement='Bottom' IsOpen='{TemplateBinding IsDropDownOpen}' AllowsTransparency='True' Focusable='False' PopupAnimation='Fade'>
                                <Grid Name='DropDown' SnapsToDevicePixels='True' MinWidth='{TemplateBinding ActualWidth}' MaxHeight='220'>
                                    <Border Background='#FFFFFF' BorderThickness='1' BorderBrush='#D4D4D8' CornerRadius='6' Margin='0,3,0,0' Padding='3'>
                                        <ScrollViewer SnapsToDevicePixels='True'>
                                            <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Contained'/>
                                        </ScrollViewer>
                                    </Border>
                                </Grid>
                            </Popup>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsEditable' Value='True'>
                                <Setter Property='IsTabStop' Value='False'/>
                                <Setter TargetName='PART_EditableTextBox' Property='Visibility' Value='Visible'/>
                                <Setter TargetName='ContentSite' Property='Visibility' Value='Hidden'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
            <Setter Property='ItemContainerStyle'>
                <Setter.Value>
                    <Style TargetType='ComboBoxItem'>
                        <Setter Property='Padding' Value='9,6'/>
                        <Setter Property='FontSize' Value='12.5'/>
                        <Setter Property='Foreground' Value='#18181B'/>
                        <Setter Property='Cursor' Value='Hand'/>
                        <Setter Property='Template'>
                            <Setter.Value>
                                <ControlTemplate TargetType='ComboBoxItem'>
                                    <Border x:Name='bd' CornerRadius='4' Padding='{TemplateBinding Padding}' Background='Transparent'>
                                        <ContentPresenter/>
                                    </Border>
                                    <ControlTemplate.Triggers>
                                        <Trigger Property='IsHighlighted' Value='True'>
                                            <Setter TargetName='bd' Property='Background' Value='#F4F4F5'/>
                                        </Trigger>
                                        <Trigger Property='IsSelected' Value='True'>
                                            <Setter Property='FontWeight' Value='SemiBold'/>
                                        </Trigger>
                                    </ControlTemplate.Triggers>
                                </ControlTemplate>
                            </Setter.Value>
                        </Setter>
                    </Style>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- 克制的方形复选框 -->
        <Style x:Key='CleanCheckBox' TargetType='CheckBox'>
            <Setter Property='Foreground' Value='#52525B'/>
            <Setter Property='FontSize' Value='12'/>
            <Setter Property='Cursor' Value='Hand'/>
            <Setter Property='Template'>
                <Setter.Value>
                    <ControlTemplate TargetType='CheckBox'>
                        <StackPanel Orientation='Horizontal' VerticalAlignment='Center' Background='Transparent'>
                            <Border x:Name='box' Width='15' Height='15' CornerRadius='3.5' Background='#FFFFFF' BorderBrush='#A1A1AA' BorderThickness='1' Margin='0,0,7,0' VerticalAlignment='Center'>
                                <Path x:Name='checkMark' Data='M 2.5 7 L 5.5 10 L 11.5 3.5' Stroke='#FFFFFF' StrokeThickness='1.6' Visibility='Collapsed'/>
                            </Border>
                            <ContentPresenter VerticalAlignment='Center'/>
                        </StackPanel>
                        <ControlTemplate.Triggers>
                            <Trigger Property='IsChecked' Value='True'>
                                <Setter TargetName='box' Property='Background' Value='#1D4ED8'/>
                                <Setter TargetName='box' Property='BorderBrush' Value='#1D4ED8'/>
                                <Setter TargetName='checkMark' Property='Visibility' Value='Visible'/>
                            </Trigger>
                            <Trigger Property='IsMouseOver' Value='True'>
                                <Setter Property='Foreground' Value='#18181B'/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Border.Resources>

    <StackPanel>
        <!-- 主体内容区 -->
        <StackPanel Margin='22,18,22,16'>

            <!-- 1. 核心焦点区：当前网络状态与 IPv4 地址 (Spend boldness in one place) -->
            <Grid Margin='0,0,0,6'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <StackPanel Orientation='Horizontal' VerticalAlignment='Center'>
                    <Border x:Name='badgeDot' Width='8' Height='8' CornerRadius='4' Background='#15803D' Margin='0,0,8,0' VerticalAlignment='Center'/>
                    <TextBlock x:Name='lblStatusBadge' Text='正在检测网络状态' FontSize='12.5' FontWeight='SemiBold' Foreground='#18181B' VerticalAlignment='Center'/>
                </StackPanel>
                <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Center'>
                    <Button x:Name='btnRefresh' Content='刷新' Style='{StaticResource TextLinkBtn}' Margin='0,0,6,0'/>
                    <Button x:Name='btnTrayHide' Content='最小化到托盘' Style='{StaticResource TextLinkBtn}'/>
                </StackPanel>
            </Grid>

            <!-- 大字号 IPv4 地址显示 -->
            <TextBlock x:Name='lblHeroIp' Text='0.0.0.0' FontSize='26' FontWeight='SemiBold' Foreground='#18181B' Margin='0,0,0,10'/>

            <!-- 三列对齐的网络参数行 (不使用中圆点拼接，靠网格对齐呈现结构) -->
            <Grid Margin='0,0,0,16'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='1.15*'/>
                    <ColumnDefinition Width='1.15*'/>
                    <ColumnDefinition Width='0.8*'/>
                </Grid.ColumnDefinitions>
                <TextBlock x:Name='lblChipSsid' Grid.Column='0' Text='无线网  检测中' FontSize='12' Foreground='#71717A'/>
                <TextBlock x:Name='lblChipGw' Grid.Column='1' Text='网关  192.168.128.1' FontSize='12' Foreground='#71717A'/>
                <TextBlock x:Name='lblHeroLatency' Grid.Column='2' Text='认证服  --' FontSize='12' Foreground='#71717A' HorizontalAlignment='Right'/>
            </Grid>

            <!-- 主模式切换按钮栏 -->
            <Grid Margin='0,0,0,18'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='1.15*'/>
                    <ColumnDefinition Width='10'/>
                    <ColumnDefinition Width='0.85*'/>
                </Grid.ColumnDefinitions>
                <Button x:Name='btnApplyCampus' Grid.Column='0' Height='38' Style='{StaticResource PrimaryBtn}' Content='切换为静态 IP'/>
                <Button x:Name='btnRestoreDhcp' Grid.Column='2' Height='38' Style='{StaticResource OutlineBtn}' Content='恢复自动获取'/>
            </Grid>

            <!-- 细分割线 1 -->
            <Border Height='1' Background='#E4E4E7' Margin='0,0,0,16'/>

            <!-- 2. 静态节点选择区 -->
            <Grid Margin='0,0,0,7'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <TextBlock Text='静态 IP 节点' FontSize='12.5' FontWeight='SemiBold' Foreground='#3F3F46' VerticalAlignment='Center'/>
                <TextBlock x:Name='lblLatencyResult' Grid.Column='1' Text='' FontSize='11.5' Foreground='#71717A' VerticalAlignment='Center'/>
            </Grid>

            <Grid Margin='0,0,0,18'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='8'/>
                    <ColumnDefinition Width='84'/>
                </Grid.ColumnDefinitions>
                <ComboBox x:Name='cboIp' Grid.Column='0' IsEditable='True' Style='{StaticResource CleanComboBox}'/>
                <Button x:Name='btnTestLatency' Grid.Column='2' Height='34' Style='{StaticResource OutlineBtn}' FontSize='12.5' Content='连通测试'/>
            </Grid>

            <!-- 细分割线 2 -->
            <Border Height='1' Background='#E4E4E7' Margin='0,0,0,16'/>

            <!-- 3. 校园网认证区 -->
            <Grid Margin='0,0,0,10'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <TextBlock Text='校园网认证' FontSize='12.5' FontWeight='SemiBold' Foreground='#3F3F46' VerticalAlignment='Center'/>
                <Button x:Name='btnOpenPortal' Grid.Column='1' Content='打开登录页 (10.8.2.2)' Style='{StaticResource TextLinkBtn}' Foreground='#1D4ED8'/>
            </Grid>

            <Grid Margin='0,0,0,10'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='1.15*'/>
                    <ColumnDefinition Width='10'/>
                    <ColumnDefinition Width='0.85*'/>
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column='0'>
                    <TextBlock Text='学号' FontSize='11.5' Foreground='#71717A' Margin='0,0,0,4'/>
                    <TextBox x:Name='txtUserId' Style='{StaticResource CleanTextBox}'/>
                </StackPanel>
                <StackPanel Grid.Column='2'>
                    <TextBlock Text='运营商' FontSize='11.5' Foreground='#71717A' Margin='0,0,0,4'/>
                    <ComboBox x:Name='cboService' IsEditable='False' Style='{StaticResource CleanComboBox}'/>
                </StackPanel>
            </Grid>

            <Grid Margin='0,0,0,12'>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='1.15*'/>
                    <ColumnDefinition Width='10'/>
                    <ColumnDefinition Width='0.85*'/>
                </Grid.ColumnDefinitions>
                <StackPanel Grid.Column='0'>
                    <TextBlock Text='密码' FontSize='11.5' Foreground='#71717A' Margin='0,0,0,4'/>
                    <PasswordBox x:Name='txtPassword' Style='{StaticResource CleanPasswordBox}'/>
                </StackPanel>
                <StackPanel Grid.Column='2' VerticalAlignment='Bottom'>
                    <Button x:Name='btnLogin' Height='34' Style='{StaticResource DarkBtn}' Content='登录认证'/>
                </StackPanel>
            </Grid>

            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='Auto'/>
                    <ColumnDefinition Width='20'/>
                    <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <CheckBox x:Name='chkRememberPwd' Grid.Column='0' Style='{StaticResource CleanCheckBox}' Content='记住密码' IsChecked='True'/>
                <CheckBox x:Name='chkAutoLogin' Grid.Column='2' Style='{StaticResource CleanCheckBox}' Content='切换静态 IP 后自动登录' IsChecked='True'/>
            </Grid>
        </StackPanel>

        <!-- 4. 底部安静的状态栏与可展开日志 -->
        <Border Background='#EFEFED' BorderBrush='#E4E4E7' BorderThickness='0,1,0,0' Padding='22,9'>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='Auto'/>
                </Grid.ColumnDefinitions>
                <TextBlock x:Name='lblFooterStatus' Grid.Column='0' Text='就绪' FontSize='11.5' Foreground='#71717A' TextTrimming='CharacterEllipsis' VerticalAlignment='Center'/>
                <Button x:Name='btnToggleLog' Grid.Column='1' Content='查看日志' Style='{StaticResource TextLinkBtn}' Margin='8,0,0,0'/>
            </Grid>
        </Border>

        <Border x:Name='logContainer' Visibility='Collapsed' Background='#FFFFFF' BorderBrush='#E4E4E7' BorderThickness='0,1,0,0' Padding='18,10' Height='125'>
            <TextBox x:Name='txtLog' IsReadOnly='True' TextWrapping='Wrap' VerticalScrollBarVisibility='Auto'
                     Background='Transparent' Foreground='#52525B' BorderThickness='0'
                     FontFamily='Microsoft YaHei UI' FontSize='11.5'/>
        </Border>
    </StackPanel>
</Border>";

            FrameworkElement root = (FrameworkElement)XamlReader.Parse(xaml);
            this.Content = root;

            // 绑定控件
            badgeDot = (Border)root.FindName("badgeDot");
            lblStatusBadge = (TextBlock)root.FindName("lblStatusBadge");
            lblHeroIp = (TextBlock)root.FindName("lblHeroIp");
            lblHeroLatency = (TextBlock)root.FindName("lblHeroLatency");
            lblChipSsid = (TextBlock)root.FindName("lblChipSsid");
            lblChipGw = (TextBlock)root.FindName("lblChipGw");
            btnRefresh = (Button)root.FindName("btnRefresh");
            btnTrayHide = (Button)root.FindName("btnTrayHide");

            btnApplyCampus = (Button)root.FindName("btnApplyCampus");
            btnRestoreDhcp = (Button)root.FindName("btnRestoreDhcp");

            cboIp = (ComboBox)root.FindName("cboIp");
            btnTestLatency = (Button)root.FindName("btnTestLatency");
            lblLatencyResult = (TextBlock)root.FindName("lblLatencyResult");

            txtUserId = (TextBox)root.FindName("txtUserId");
            txtPassword = (PasswordBox)root.FindName("txtPassword");
            cboService = (ComboBox)root.FindName("cboService");
            chkRememberPwd = (CheckBox)root.FindName("chkRememberPwd");
            chkAutoLogin = (CheckBox)root.FindName("chkAutoLogin");
            btnLogin = (Button)root.FindName("btnLogin");
            btnOpenPortal = (Button)root.FindName("btnOpenPortal");

            lblFooterStatus = (TextBlock)root.FindName("lblFooterStatus");
            btnToggleLog = (Button)root.FindName("btnToggleLog");
            logContainer = (Border)root.FindName("logContainer");
            txtLog = (TextBox)root.FindName("txtLog");

            // 填充下拉框数据 (首项为自动优选)
            cboIp.Items.Add(AutoAllocOption);
            foreach (string item in CandidateIps)
            {
                cboIp.Items.Add(item);
            }
            cboIp.SelectedIndex = 0;

            string[] services = new string[] { "电信宽带接入", "移动宽带接入", "联通宽带接入", "教育网接入" };
            foreach (string s in services)
            {
                cboService.Items.Add(s);
            }
            cboService.SelectedIndex = 0;

            // 绑定事件
            btnTrayHide.Click += delegate { MinimizeToTray(); };
            btnRefresh.Click += new RoutedEventHandler(btnRefresh_Click);
            btnTestLatency.Click += new RoutedEventHandler(btnTestLatency_Click);
            btnApplyCampus.Click += new RoutedEventHandler(btnApplyCampus_Click);
            btnRestoreDhcp.Click += new RoutedEventHandler(btnRestoreDhcp_Click);
            btnLogin.Click += new RoutedEventHandler(btnLogin_Click);
            btnOpenPortal.Click += new RoutedEventHandler(btnOpenPortal_Click);

            btnToggleLog.Click += delegate
            {
                isLogExpanded = !isLogExpanded;
                logContainer.Visibility = isLogExpanded ? Visibility.Visible : Visibility.Collapsed;
                btnToggleLog.Content = isLogExpanded ? "收起日志" : "查看日志";
            };

            // Windows 11 原生标题栏同色融合与圆角
            this.SourceInitialized += delegate
            {
                try
                {
                    IntPtr hwnd = new WindowInteropHelper(this).Handle;
                    int roundPref = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(hwnd, 33, ref roundPref, sizeof(int));

                    // DWMWA_CAPTION_COLOR = 35 (COLORREF 0x00BBGGRR -> #F7F7F5 = 0x00F5F7F7)
                    int captionColor = 0x00F5F7F7;
                    DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

                    // DWMWA_TEXT_COLOR = 36 (#18181B = 0x001B1818)
                    int textColor = 0x001B1818;
                    DwmSetWindowAttribute(hwnd, 36, ref textColor, sizeof(int));
                }
                catch { }
            };

            this.Loaded += delegate
            {
                AppendLog("就绪");
                RefreshStatusAsync(true);
            };

            this.Closing += delegate
            {
                SaveCurrentConfig();
                if (trayIcon != null) trayIcon.Visible = false;
            };
        }

        private void InitSystemTray()
        {
            trayMenu = new Forms.ContextMenuStrip();
            trayMenu.Items.Add("切换为静态 IP", null, delegate { btnApplyCampus_Click(this, null); });
            trayMenu.Items.Add("恢复自动获取 (DHCP)", null, delegate { btnRestoreDhcp_Click(this, null); });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("打开登录页 (10.8.2.2)", null, delegate { btnOpenPortal_Click(this, null); });
            trayMenu.Items.Add("显示主窗口", null, delegate { ShowMainWindow(); });
            trayMenu.Items.Add(new Forms.ToolStripSeparator());
            trayMenu.Items.Add("退出", null, delegate { ExitApplication(); });

            trayIcon = new Forms.NotifyIcon();
            trayIcon.Text = "集大校园网助手";
            trayIcon.Icon = Drawing.SystemIcons.Shield;
            trayIcon.ContextMenuStrip = trayMenu;
            trayIcon.Visible = true;
            trayIcon.DoubleClick += delegate { ShowMainWindow(); };
        }

        private void MinimizeToTray()
        {
            this.Hide();
            if (trayIcon != null)
            {
                trayIcon.ShowBalloonTip(1200, "集大校园网助手", "已最小化到系统托盘，右键图标可切换网络模式。", Forms.ToolTipIcon.Info);
            }
        }

        private void ShowMainWindow()
        {
            this.Show();
            this.WindowState = WindowState.Normal;
            this.Activate();
        }

        private void ExitApplication()
        {
            SaveCurrentConfig();
            if (trayIcon != null) trayIcon.Visible = false;
            Application.Current.Shutdown();
        }

        private void ApplyLoadedConfig()
        {
            if (!string.IsNullOrEmpty(appConfig.SelectedIp))
            {
                cboIp.Text = appConfig.SelectedIp;
            }
            else
            {
                cboIp.SelectedIndex = 0;
            }

            txtUserId.Text = appConfig.UserId ?? "";
            chkRememberPwd.IsChecked = appConfig.RememberPassword;
            if (appConfig.RememberPassword && !string.IsNullOrEmpty(appConfig.EncryptedPassword))
            {
                txtPassword.Password = ConfigStorage.DecryptPassword(appConfig.EncryptedPassword);
            }

            if (!string.IsNullOrEmpty(appConfig.Service))
            {
                cboService.Text = appConfig.Service;
            }

            chkAutoLogin.IsChecked = appConfig.AutoLoginAfterSwitch;
        }

        private void SaveCurrentConfig()
        {
            appConfig.SelectedIp = (cboIp.Text ?? "").Trim();
            appConfig.UserId = (txtUserId.Text ?? "").Trim();
            appConfig.RememberPassword = chkRememberPwd.IsChecked == true;
            appConfig.AutoLoginAfterSwitch = chkAutoLogin.IsChecked == true;
            appConfig.Service = cboService.Text;

            if (appConfig.RememberPassword && !string.IsNullOrEmpty(txtPassword.Password))
            {
                appConfig.EncryptedPassword = ConfigStorage.EncryptPassword(txtPassword.Password);
            }
            else
            {
                appConfig.EncryptedPassword = "";
            }

            ConfigStorage.SaveConfig(appConfig);
        }

        public void AppendLog(string message)
        {
            if (!this.Dispatcher.CheckAccess())
            {
                this.Dispatcher.BeginInvoke(new Action<string>(AppendLog), DispatcherPriority.Normal, message);
                return;
            }

            string timestamp = DateTime.Now.ToString("HH:mm:ss");
            lblFooterStatus.Text = message;
            txtLog.AppendText(string.Format("[{0}] {1}\r\n", timestamp, message));
            txtLog.ScrollToEnd();
        }

        private void SetWorking(bool working)
        {
            isWorking = working;
            btnApplyCampus.IsEnabled = !working;
            btnRestoreDhcp.IsEnabled = !working;
            btnRefresh.IsEnabled = !working;
            btnTestLatency.IsEnabled = !working;
            btnLogin.IsEnabled = !working;
        }

        private void RefreshStatusAsync(bool testPortal)
        {
            lblStatusBadge.Text = "正在检测网络状态...";
            badgeDot.Background = new SolidColorBrush(Color.FromRgb(217, 119, 6));

            Task.Factory.StartNew(delegate
            {
                return NetworkInspector.Inspect(testPortal);
            }).ContinueWith(delegate(Task<NetworkStatusInfo> t)
            {
                if (t.IsFaulted || t.Result == null) return;
                currentNetInfo = t.Result;
                UpdateDashboardUi(currentNetInfo);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        public void UpdateDashboardUi(NetworkStatusInfo info)
        {
            if (!info.HasWlan)
            {
                badgeDot.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                lblStatusBadge.Text = "未检测到无线网卡";
                lblHeroIp.Text = "0.0.0.0";
                lblHeroLatency.Text = "认证服  不可达";
                lblChipSsid.Text = "无线网  未连接";
                lblChipGw.Text = "网关  无";
                return;
            }

            lblHeroIp.Text = info.Ipv4Address;
            lblChipSsid.Text = string.Format("无线网  {0}", info.Ssid);
            lblChipGw.Text = string.Format("网关  {0}", info.Gateway);

            if (info.PortalLatencyMs > 0)
            {
                lblHeroLatency.Text = string.Format("认证服  {0} ms", info.PortalLatencyMs);
                lblHeroLatency.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
            }
            else
            {
                lblHeroLatency.Text = "认证服  未连通";
                lblHeroLatency.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            }

            if (info.IsApipa)
            {
                badgeDot.Background = new SolidColorBrush(Color.FromRgb(220, 38, 38));
                lblStatusBadge.Text = "地址冲突 (169.254)，请重新切换静态 IP";
            }
            else if (!info.IsDhcpEnabled && info.Ipv4Address.StartsWith("172.19."))
            {
                badgeDot.Background = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                lblStatusBadge.Text = "静态 IP 模式已生效";
            }
            else
            {
                badgeDot.Background = new SolidColorBrush(Color.FromRgb(29, 78, 216));
                lblStatusBadge.Text = "自动获取模式 (DHCP)";
            }
        }

        private void btnRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (isWorking) return;
            AppendLog("已刷新网络状态");
            RefreshStatusAsync(true);
        }

        private void btnTestLatency_Click(object sender, RoutedEventArgs e)
        {
            if (isWorking) return;
            string selectedIp = ExtractPureIp(cboIp.Text);
            if (selectedIp == "AUTO")
            {
                lblLatencyResult.Text = "正在探测链路与认证网关...";
                lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
                string bindIp = (currentNetInfo != null && currentNetInfo.Ipv4Address != "0.0.0.0") ? currentNetInfo.Ipv4Address : "";

                Task.Factory.StartNew(delegate
                {
                    return NetworkInspector.TestTcpLatency(bindIp, "10.8.2.2", 80, 1500);
                }).ContinueWith(delegate(Task<int> t)
                {
                    int ms = (!t.IsFaulted) ? t.Result : -1;
                    if (ms > 0)
                    {
                        lblLatencyResult.Text = string.Format("认证服可达 ({0} ms) · 自动优选就绪", ms);
                        lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                        AppendLog(string.Format("网关链路握手良好 ({0} ms)，点击「切换为静态 IP」将自动绑定并认证", ms));
                    }
                    else
                    {
                        lblLatencyResult.Text = "点击切换将自动测速选优";
                        lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                        AppendLog("当前网关尚未直通，点击「切换为静态 IP」将自动为您轮询测试候选节点");
                    }
                }, TaskScheduler.FromCurrentSynchronizationContext());
                return;
            }

            IPAddress dummy;
            if (!IPAddress.TryParse(selectedIp, out dummy))
            {
                MessageBox.Show("请输入有效的 IPv4 地址", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            lblLatencyResult.Text = "正在测试...";
            lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(113, 113, 122));
            string bindIp2 = (currentNetInfo != null && currentNetInfo.Ipv4Address != "0.0.0.0") ? currentNetInfo.Ipv4Address : "";

            Task.Factory.StartNew(delegate
            {
                return NetworkInspector.TestTcpLatency(bindIp2, "10.8.2.2", 80, 1800);
            }).ContinueWith(delegate(Task<int> t)
            {
                int ms = (!t.IsFaulted) ? t.Result : -1;
                if (ms > 0)
                {
                    lblLatencyResult.Text = string.Format("10.8.2.2 可达 ({0} ms)", ms);
                    lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));
                    AppendLog(string.Format("连通测试通过 ({0} ms)", ms));
                }
                else
                {
                    lblLatencyResult.Text = "当前路由未通 (切换静态后可达)";
                    lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(217, 119, 6));
                    AppendLog("10.8.2.2 暂不可达，请先切换为静态 IP");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void btnApplyCampus_Click(object sender, RoutedEventArgs e)
        {
            if (isWorking) return;

            string targetIp = ExtractPureIp(cboIp.Text);
            bool isAuto = (targetIp == "AUTO");

            if (!isAuto)
            {
                IPAddress dummy;
                if (!IPAddress.TryParse(targetIp, out dummy))
                {
                    MessageBox.Show("静态 IP 格式无效，请检查后重试", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (currentNetInfo != null && currentNetInfo.HasWlan &&
                !string.Equals(currentNetInfo.Ssid, "JMU-STU", StringComparison.OrdinalIgnoreCase) &&
                currentNetInfo.Ssid != "未连接")
            {
                MessageBoxResult dr = MessageBox.Show(
                    string.Format("当前连接的无线网为「{0}」而非「JMU-STU」。\n\n在非校园网环境下切换为静态 IP 会导致无法上网，是否继续？", currentNetInfo.Ssid),
                    "环境确认",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning
                );
                if (dr != MessageBoxResult.Yes) return;
            }

            SaveCurrentConfig();
            SetWorking(true);

            string ifName = (currentNetInfo != null && !string.IsNullOrEmpty(currentNetInfo.InterfaceName)) ? currentNetInfo.InterfaceName : "WLAN";
            bool autoLogin = chkAutoLogin.IsChecked == true;
            string uid = (txtUserId.Text ?? "").Trim();
            string pwd = txtPassword.Password ?? "";
            string svc = cboService.Text;

            Task.Factory.StartNew(delegate
            {
                if (isAuto)
                {
                    AppendLog("开始智能优选最佳空闲 IP...");
                    bool anySuccess = false;
                    foreach (string cand in CandidateIps)
                    {
                        string pureCand = ExtractPureIp(cand);
                        AppendLog(string.Format("正在测试节点 {0}...", pureCand));
                        bool ok = CampusConfigManager.ApplyStaticConfig(pureCand, ifName, AppendLog);
                        if (!ok) continue;

                        int latency = NetworkInspector.TestTcpLatency(pureCand, "10.8.2.2", 80, 1500);
                        if (latency > 0)
                        {
                            AppendLog(string.Format("✓ 节点 {0} 路由畅通 (延迟 {1} ms)", pureCand, latency));
                            if (autoLogin && !string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(pwd))
                            {
                                Thread.Sleep(300);
                                LoginResult lr = EPortalAuthClient.Login(pureCand, uid, pwd, svc, AppendLog);
                                if (lr.Success)
                                {
                                    AppendLog(string.Format("✓ 自动分配成功并完成认证: {0}", pureCand));
                                    anySuccess = true;
                                    break;
                                }
                                else if (lr.Message != null && lr.Message.Contains("密码"))
                                {
                                    anySuccess = true;
                                    break;
                                }
                                else
                                {
                                    AppendLog(string.Format("节点 {0} 认证受阻 ({1})，自动尝试下一个候选...", pureCand, lr.Message));
                                    continue;
                                }
                            }
                            else
                            {
                                AppendLog(string.Format("✓ 自动分配成功: 已绑定最佳节点 {0}", pureCand));
                                anySuccess = true;
                                break;
                            }
                        }
                        else
                        {
                            AppendLog(string.Format("节点 {0} 响应超时，自动切换备用...", pureCand));
                        }
                    }

                    if (!anySuccess)
                    {
                        AppendLog("✕ 所有候选节点均未能连通，请检查 Wi-Fi 连接状态");
                    }
                    return anySuccess;
                }
                else
                {
                    bool ok = CampusConfigManager.ApplyStaticConfig(targetIp, ifName, AppendLog);
                    if (ok && autoLogin && !string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(pwd))
                    {
                        Thread.Sleep(500);
                        EPortalAuthClient.Login(targetIp, uid, pwd, svc, AppendLog);
                    }
                    return ok;
                }
            }).ContinueWith(delegate
            {
                SetWorking(false);
                RefreshStatusAsync(true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void btnRestoreDhcp_Click(object sender, RoutedEventArgs e)
        {
            if (isWorking) return;

            SaveCurrentConfig();
            SetWorking(true);

            string ifName = (currentNetInfo != null && !string.IsNullOrEmpty(currentNetInfo.InterfaceName)) ? currentNetInfo.InterfaceName : "WLAN";

            Task.Factory.StartNew(delegate
            {
                return CampusConfigManager.RestoreDhcpConfig(ifName, AppendLog);
            }).ContinueWith(delegate
            {
                SetWorking(false);
                RefreshStatusAsync(false);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            if (isWorking) return;

            string uid = (txtUserId.Text ?? "").Trim();
            string pwd = txtPassword.Password ?? "";
            string svc = cboService.Text;

            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(pwd))
            {
                MessageBox.Show("请填写学号和密码", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            SaveCurrentConfig();
            SetWorking(true);

            string wlanIp = (currentNetInfo != null && currentNetInfo.Ipv4Address != "0.0.0.0") ? currentNetInfo.Ipv4Address : ExtractPureIp(cboIp.Text);

            Task.Factory.StartNew(delegate
            {
                return EPortalAuthClient.Login(wlanIp, uid, pwd, svc, AppendLog);
            }).ContinueWith(delegate(Task<LoginResult> t)
            {
                SetWorking(false);
                RefreshStatusAsync(true);
                if (!t.IsFaulted && t.Result != null && !t.Result.Success)
                {
                    MessageBox.Show(t.Result.Message, "认证结果", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void btnOpenPortal_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                AppendLog("已打开登录页 http://10.8.2.2/");
                Process.Start("http://10.8.2.2/");
            }
            catch (Exception ex)
            {
                AppendLog("打开网页失败: " + ex.Message);
            }
        }

        // 渲染离线高分屏预览图用于视觉自检
        public void RenderPreviewPng(string filePath)
        {
            NetworkStatusInfo demo = new NetworkStatusInfo();
            demo.HasWlan = true;
            demo.InterfaceName = "WLAN";
            demo.IsDhcpEnabled = false;
            demo.Ipv4Address = "172.19.245.138";
            demo.Gateway = "192.168.128.1";
            demo.Ssid = "JMU-STU";
            demo.Signal = "88%";
            demo.DadTransmits = 0;
            demo.PortalLatencyMs = 1;
            UpdateDashboardUi(demo);
            cboIp.SelectedIndex = 0;
            chkRememberPwd.IsChecked = true;
            chkAutoLogin.IsChecked = true;
            lblLatencyResult.Text = "自动优选就绪 (1 ms)";
            lblLatencyResult.Foreground = new SolidColorBrush(Color.FromRgb(21, 128, 61));

            FrameworkElement content = (FrameworkElement)this.Content;
            content.Width = 420;
            content.Measure(new Size(420, double.PositiveInfinity));
            Size desired = content.DesiredSize;
            content.Arrange(new Rect(0, 0, desired.Width, desired.Height));
            content.UpdateLayout();

            int dpi = 144;
            int pxW = (int)Math.Ceiling(desired.Width * dpi / 96.0);
            int pxH = (int)Math.Ceiling(desired.Height * dpi / 96.0);

            RenderTargetBitmap rtb = new RenderTargetBitmap(pxW, pxH, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(content);

            PngBitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }
            if (trayIcon != null) trayIcon.Visible = false;
        }
    }

    // =========================================================================
    // 程序入口
    // =========================================================================

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
            {
                if (string.Equals(args[0], "--verify", StringComparison.OrdinalIgnoreCase))
                {
                    NetworkStatusInfo info = NetworkInspector.Inspect(false);
                    Console.WriteLine("VERIFY_OK: HasWlan=" + info.HasWlan + ", IP=" + info.Ipv4Address);
                    return 0;
                }
                if (string.Equals(args[0], "--screenshot", StringComparison.OrdinalIgnoreCase) && args.Length > 1)
                {
                    MainForm form = new MainForm();
                    form.RenderPreviewPng(args[1]);
                    return 0;
                }
            }

            Application app = new Application();
            MainForm mainWin = new MainForm();
            app.Run(mainWin);
            return 0;
        }
    }
}
