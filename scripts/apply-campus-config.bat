@echo off
chcp 936 >nul
setlocal
title 应用校园网配置 - JMU-STU

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
echo           应用校园网配置（集美大学 JMU-STU）
echo ============================================================
echo.
echo  将要执行的操作：
echo    [1] 清理旧的校园网路由
echo    [2] WLAN 设为静态 IP   172.19.245.138  (掩码 255.255.255.0)
echo    [3] 默认网关           192.168.128.1
echo    [4] DNS                172.17.8.32 / 172.17.8.33
echo    [5] 新增持久路由       10.0.0.0/8     -^> 192.168.128.1
echo                           172.17.0.0/16  -^> 192.168.128.1
echo.
echo  这套配置的作用：
echo    - 校园网认证页 10.8.2.2 可以直接打开
echo    - 校内资源 10.14.x.x / 172.17.x.x 直连（教务、图书馆、校内VPN）
echo    - 上网走校园网，比手机热点快得多
echo.
echo  注意：本配置只适用于集美大学校园网，在别的 WiFi 下会导致上不了网！
echo.

netsh wlan show interfaces | findstr /C:"JMU-STU" >nul
if errorlevel 1 (
    echo ***********************************************************
    echo   [警告] 当前似乎没有连接到 JMU-STU
    echo ***********************************************************
    echo   在别的网络下应用这套配置会直接断网。
    echo   如果只是想先配好、稍后再连校园网，可以继续。
    echo.
    choice /c YN /n /m "仍要继续吗？(Y=继续 / N=取消) "
    if errorlevel 2 (
        echo.
        echo 已取消，未做任何改动。
        ping -n 3 127.0.0.1 >nul
        exit /b
    )
) else (
    choice /c YN /n /m "确认执行？(Y=执行 / N=取消) "
    if errorlevel 2 (
        echo.
        echo 已取消，未做任何改动。
        ping -n 3 127.0.0.1 >nul
        exit /b
    )
)

echo.
echo ------------------------------------------------------------
echo [1/5] 清理旧的校园网路由...
route delete 10.0.0.0 >nul 2>&1
route delete 172.17.0.0 >nul 2>&1

echo [2/5] 设置静态 IP 与默认网关...
netsh interface ip set address name="WLAN" static 172.19.245.138 255.255.255.0 192.168.128.1

echo [3/5] 设置 DNS...
netsh interface ip set dns name="WLAN" static 172.17.8.32 primary
netsh interface ip add dns name="WLAN" 172.17.8.33 index=2

echo [4/5] 写入持久路由...
netsh interface ipv4 add route prefix=10.0.0.0/8 interface="WLAN" nexthop=192.168.128.1 metric=1 store=persistent
netsh interface ipv4 add route prefix=172.17.0.0/16 interface="WLAN" nexthop=192.168.128.1 metric=1 store=persistent

echo [5/5] 等待网络稳定...
ping -n 3 127.0.0.1 >nul

echo.
echo ============================================================
echo  完成！当前 WLAN 配置：
echo ============================================================
netsh interface ipv4 show config "WLAN"
echo.
echo ------------------------------------------------------------
echo  持久路由（应该能看到 10.0.0.0 和 172.17.0.0 两条）：
echo ------------------------------------------------------------
route print -4 | findstr /C:"Persistent" /C:"192.168.128.1"
echo ------------------------------------------------------------
echo.
echo  下一步：
echo    如果还上不了网，打开浏览器访问任意网站，会自动跳转到
echo    校园网认证页，用学号密码登录一次即可。
echo.
pause
endlocal
