@echo off
chcp 65001 > nul
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe

echo [CoolNotifier] C# 컴파일을 시작합니다...
"%CSC%" /target:winexe /optimize+ /platform:anycpu /win32icon:"%~dp0CoolNotifier.ico" /out:"%~dp0CoolNotifier.exe" "%~dp0CoolNotifier.cs"

if %ERRORLEVEL% equ 0 (
    echo.
    echo =======================================================
    echo  [성공] CoolNotifier.exe 빌드가 성공적으로 완료되었습니다!
    echo  위치: %~dp0CoolNotifier.exe
    echo =======================================================
) else (
    echo.
    echo [실패] 컴파일 중 오류가 발생했습니다.
)

pause
