@echo off
chcp 936 >nul
setlocal
title 编译 JMU 校园网配置助手 (WPF 现代硬件加速版)

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "WPF=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\WPF"

if not exist "%CSC%" (
    echo [错误] 未找到系统内置的 C# 编译器: %CSC%
    pause
    exit /b 1
)

echo 正在编译 JmuNetHelper.exe ...
"%CSC%" /nologo /target:winexe /optimize+ /win32manifest:"%~dp0src\app.manifest" /out:"%~dp0JmuNetHelper.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Security.dll /r:System.Xaml.dll /r:"%WPF%\WindowsBase.dll" /r:"%WPF%\PresentationCore.dll" /r:"%WPF%\PresentationFramework.dll" "%~dp0src\JmuNetHelper.cs"

if %errorlevel% neq 0 (
    echo [失败] 编译出错，错误码: %errorlevel%
    pause
    exit /b %errorlevel%
)

echo [成功] 已生成单文件可执行程序: %~dp0JmuNetHelper.exe
endlocal
