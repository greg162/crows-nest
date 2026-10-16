@echo off
rem Double-click to put Crowsnest firmware on a CrowPanel. Runs Flash-Crowsnest.ps1 without
rem changing PowerShell's execution policy. Arguments pass through (Flash-Crowsnest.cmd -Port COM4).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Flash-Crowsnest.ps1" %*
rem Keep the window open after a double-click so the result can be read.
if "%~1"=="" pause
