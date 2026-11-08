@echo off
chcp 936 >nul
setlocal
title 编译 JMU 校园网配置助手

echo ============================================================
echo        正在使用系统内置 .NET 原生编译器构建 JmuNetHelper.exe
echo ============================================================
echo.

set CSC_EXE=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC_EXE%" (
    set CSC_EXE=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
)

if not exist "%CSC_EXE%" (
    echo [错误] 未在系统中检测到 .NET Framework 4.x csc.exe 编译器！
    pause
    exit /b 1
)

echo 编译器路径: %CSC_EXE%
echo 正在编译 src\JmuNetHelper.cs -> JmuNetHelper.exe ...
echo.

"%CSC_EXE%" /nologo /target:winexe /optimize+ /win32manifest:src\app.manifest /out:JmuNetHelper.exe /r:System.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Security.dll src\JmuNetHelper.cs

if %errorlevel% neq 0 (
    echo.
    echo ************************************************************
    echo   [错误] 编译失败！请检查上方报错信息。
    echo ************************************************************
    pause
    exit /b %errorlevel%
)

echo.
echo ============================================================
echo  [成功] JmuNetHelper.exe 构建完成！
echo ============================================================
for %%F in (JmuNetHelper.exe) do echo  生成产物体积: %%~zF 字节 (约 80 KB 单文件绿色免安装)
echo.
endlocal
