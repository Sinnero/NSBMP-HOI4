@echo off
title Launch NSBMP Pacer
cd /d "%~dp0"

:: Automatically unblock downloaded binary from Windows SmartScreen (strips Mark of the Web)
powershell -NoProfile -Command "Unblock-File -Path '%~dp0NSBMP_Pacer.exe' -ErrorAction SilentlyContinue"

:: Launch NSBMP Pacer
start "" "%~dp0NSBMP_Pacer.exe"
