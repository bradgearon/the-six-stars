@echo off
setlocal
cd /d "%~dp0"
call build-android-debug.cmd
if errorlevel 1 exit /b %errorlevel%
adb install -r app\build\outputs\apk\debug\app-debug.apk
