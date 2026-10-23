@echo off
rem Launches the AllowList Firewall GUI (asks for admin rights).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0AllowListFirewall.ps1"
