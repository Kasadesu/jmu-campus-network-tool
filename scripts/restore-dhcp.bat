@echo off
chcp 936 >nul
setlocal
title 恢复网络配置 - 清除校园网静态设置

:: ===== 自动请求管理员权限 =====
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo.
    echo 正在请求管理员权限，请在弹出的窗口点"是"...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cls
echo ============================================================
echo              恢复网络配置（还原为自动获取）
echo ============================================================
echo.
echo  将要执行的操作：
echo    [1] WLAN 网卡改为 DHCP 自动获取 IP 地址
echo    [2] WLAN 的 DNS 改为自动获取
echo    [3] 删除校园网专用路由  10.0.0.0/8 和 172.17.0.0/16
echo    [4] 删除持久默认路由    0.0.0.0 -^> 192.168.128.1
echo.
echo  适用场景：
echo    - 要离开校园网、去连别的 WiFi 了
echo    - 网络被改乱，想一键还原成系统默认状态
echo.
echo  注意：恢复后校园网认证会失效，
echo        重新连上 JMU-STU 后打开浏览器访问任意网站即可重新登录。
echo.

choice /c YN /n /m "确认执行？(Y=执行 / N=取消) "
if errorlevel 2 (
    echo.
    echo 已取消，未做任何改动。
    ping -n 4 127.0.0.1 >nul
    exit /b
)

echo.
echo ------------------------------------------------------------
echo [1/4] WLAN 改为 DHCP 自动获取...
netsh interface ip set address name="WLAN" dhcp
netsh interface ip set dns name="WLAN" dhcp

echo [2/4] 刷新 DHCP 租约，请稍候...
ipconfig /renew "WLAN" >nul 2>&1
ping -n 5 127.0.0.1 >nul

echo [3/4] 删除校园网专用路由...
route delete 10.0.0.0 >nul 2>&1
route delete 172.17.0.0 >nul 2>&1

echo [4/4] 删除持久默认路由...
route delete 0.0.0.0 mask 0.0.0.0 192.168.128.1 >nul 2>&1

echo.
echo ============================================================
echo  完成！当前 WLAN 配置如下：
echo ============================================================
netsh interface ipv4 show config "WLAN"
echo.
echo ------------------------------------------------------------
echo  剩余持久路由（正常情况下应该只剩系统默认的，或者为空）：
echo ------------------------------------------------------------
route print -4 | findstr /C:"Persistent" /C:"192.168.128.1"
echo ------------------------------------------------------------
echo.
echo  如果 WLAN 显示为"媒体已断开"，先连上 WiFi 即可自动获取地址。
echo.
pause
endlocal
