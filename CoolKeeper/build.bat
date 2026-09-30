@echo off
chcp 65001 > nul
echo [CoolKeeper] C# 컴파일을 시작합니다...

set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
    set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
)

if not exist "%CSC%" (
    echo [오류] .NET Framework csc.exe 컴파일러를 찾을 수 없습니다.
    pause
    exit /b 1
)

"%CSC%" /target:winexe /optimize+ /platform:anycpu /win32icon:"%~dp0CoolKeeper.ico" /out:"%~dp0CoolKeeper.exe" "%~dp0CoolKeeper.cs"

if %ERRORLEVEL% equ 0 (
    echo.
    echo =======================================================
    echo  [성공] CoolKeeper.exe 빌드가 성공적으로 완료되었습니다!
    echo  위치: %~dp0CoolKeeper.exe
    echo =======================================================
) else (
    echo.
    echo [실패] 컴파일 중 오류가 발생했습니다.
)

pause
