@echo off
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
set UIA_CLIENT=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationClient.dll
set UIA_TYPES=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF\UIAutomationTypes.dll

echo Compiling CoolDownloader.exe...
"%CSC%" /target:winexe /optimize+ /platform:anycpu /r:"%UIA_CLIENT%" /r:"%UIA_TYPES%" /win32icon:"%~dp0CoolDownloader.ico" /out:"%~dp0CoolDownloader.exe" "%~dp0CoolDownloader.cs"

if %ERRORLEVEL% equ 0 (
    echo [SUCCESS] CoolDownloader.exe built successfully!
) else (
    echo [FAILED] Compilation error occurred.
)
