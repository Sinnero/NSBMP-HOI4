@echo off
setlocal
cd /d "%~dp0"

echo ===================================================================
echo     NSBMP Pacer - 1-Click Build Tool (Zero Dependency)
echo     Uses built-in Microsoft C# Compiler (csc.exe)
echo ===================================================================
echo.

set "CSC_EXE=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC_EXE%" (
    set "CSC_EXE=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if not exist "%CSC_EXE%" (
    echo [ERROR] Microsoft .NET Framework C# compiler was not found on this system.
    exit /b 1
)

echo [BUILD] Compiling NSBMP_Pacer.exe with %CSC_EXE%...
"%CSC_EXE%" /nologo /target:winexe /platform:x64 /optimize+ /win32manifest:src\app.manifest /win32icon:src\app.ico /r:System.Windows.Forms.dll,System.Drawing.dll,System.dll,System.Core.dll /out:NSBMP_Pacer.exe src\Program.cs src\PacerEngine.cs src\Win32.cs src\SteamConfigManager.cs src\Properties\AssemblyInfo.cs

if errorlevel 1 (
    echo.
    echo [ERROR] Compilation failed.
    exit /b 1
)

echo.
echo [SUCCESS] Built: NSBMP_Pacer.exe (Standalone Windows 64-bit Executable)
for %%F in (NSBMP_Pacer.exe) do echo           File Size: %%~zF bytes
echo.
echo The executable is completely standalone.
echo No Python, runtime, or prerequisites needed on any Windows 10/11 PC.
