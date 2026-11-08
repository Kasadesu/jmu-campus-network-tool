@echo off
chcp 936 >nul
setlocal enabledelayedexpansion
title 集美大学候选 IP 连通性测试工具 (Win11 DadTransmits 彻底免冲突版)

:: ===== 自动请求管理员权限 =====
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo 正在请求管理员权限，请在弹出的 UAC 窗口点“是”...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cls
echo ============================================================
echo      集美大学校园网候选 IP 自动化诊断测试 (Win11 DadTransmits 优化)
echo ============================================================
echo.

set "TEST_IP=%~1"
if "%TEST_IP%"=="" (
    set "TEST_IP=172.19.245.157"
)

echo 当前测试目标候选 IP: !TEST_IP!
echo 网关目标:            192.168.128.1
echo.
echo [核心突破]：Win11 专门参数 dadtransmits=0，
echo 彻底关闭网卡 DAD 检测，防止 Windows 被网关 Proxy-ARP 误伤降级！
echo.
choice /c YN /n /m "按 Y 开始测试，按 N 退出: "
if errorlevel 2 (
    echo 已取消。
    exit /b
)

echo.
echo ------------------------------------------------------------
echo [1/5] 关闭 WLAN 接口 DAD 冲突检测 (dadtransmits=0)...
netsh interface ipv4 set interface "WLAN" dadtransmits=0

echo [2/5] 正在配置 WLAN 静态 IP: !TEST_IP! ...
netsh interface ip set address name="WLAN" static !TEST_IP! 255.255.255.0 192.168.128.1
netsh interface ip set dns name="WLAN" static 172.17.8.32 primary
netsh interface ip add dns name="WLAN" 172.17.8.33 index=2

echo [3/5] 写入定向测试路由...
route delete 10.0.0.0 >nul 2>&1
route delete 172.17.0.0 >nul 2>&1
netsh interface ipv4 add route prefix=10.0.0.0/8 interface="WLAN" nexthop=192.168.128.1 metric=1 store=active
netsh interface ipv4 add route prefix=172.17.0.0/16 interface="WLAN" nexthop=192.168.128.1 metric=1 store=active

echo [4/5] 等待网卡生效 (约 2 秒)...
ping -n 3 127.0.0.1 >nul

echo ------------------------------------------------------------
echo 正在检查网卡真实分配状态：
powershell -NoProfile -Command "Get-NetIPAddress -InterfaceAlias WLAN -AddressFamily IPv4 | Select-Object IPAddress, AddressState | Format-Table -AutoSize"

echo ------------------------------------------------------------
echo [5/5] 正在向认证服务器 http://10.8.2.2/ 发起 HTTP 握手测试...
curl.exe -s -i --max-time 6 http://10.8.2.2/ > "%TEMP%\jmu_test_curl.txt" 2>&1
findstr /C:"10.8.2.2" "%TEMP%\jmu_test_curl.txt" >nul
if %errorlevel% equ 0 (
    echo.
    echo ============================================================
    echo [测试成功！] 恭喜！!TEST_IP! 完美可用！
    echo 成功接收到学校 ePortal 认证服务响应！认证页面可以秒开！
    echo ============================================================
) else (
    echo.
    echo ============================================================
    echo [测试反馈] 响应结果如下：
    type "%TEMP%\jmu_test_curl.txt"
    echo ============================================================
)

echo.
choice /c YN /n /m "测试完成，是否立即恢复 WLAN 为 DHCP 自动获取？(Y=恢复 / N=保留当前静态配置) "
if errorlevel 2 (
    echo 保留当前静态配置。
) else (
    echo 正在恢复 WLAN 为 DHCP 并复原 DAD 设置...
    netsh interface ipv4 set interface "WLAN" dadtransmits=3
    netsh interface ip set address name="WLAN" dhcp
    netsh interface ip set dns name="WLAN" dhcp
    route delete 10.0.0.0 >nul 2>&1
    route delete 172.17.0.0 >nul 2>&1
    echo 已恢复 DHCP 自动获取。
)

echo.
pause
endlocal
