@echo off
rem Double-click to start the auto-builder (keeps running in this window).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0watch-build.ps1"
pause
