@echo off
setlocal
set GRADLE_VERSION=9.6.1
set SCRIPT_DIR=%~dp0
set BOOTSTRAP_DIR=%SCRIPT_DIR%.gradle\bootstrap
set GRADLE_HOME=%BOOTSTRAP_DIR%\gradle-%GRADLE_VERSION%
set GRADLE_BAT=%GRADLE_HOME%\bin\gradle.bat
if not exist "%GRADLE_BAT%" (
  echo Downloading Gradle %GRADLE_VERSION% for Six Stars Mobile...
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $d='%BOOTSTRAP_DIR%'; New-Item -ItemType Directory -Force -Path $d | Out-Null; $zip=Join-Path $d 'gradle-%GRADLE_VERSION%-bin.zip'; if (!(Test-Path $zip)) { Invoke-WebRequest -Uri 'https://services.gradle.org/distributions/gradle-%GRADLE_VERSION%-bin.zip' -OutFile $zip }; Expand-Archive -Force $zip $d"
  if errorlevel 1 exit /b 1
)
"%GRADLE_BAT%" %*
