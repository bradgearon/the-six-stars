@echo off
cd /d "%~dp0"
call gradlew.bat :app:assembleDebug
if errorlevel 1 exit /b 1
echo.
echo Debug APK:
echo %CD%\app\build\outputs\apk\debug\app-debug.apk
