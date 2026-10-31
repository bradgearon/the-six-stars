@echo off
cd /d "%~dp0"
dotnet run --project "tools\Resonator\Resonator.Api" -- --urls http://127.0.0.1:5000
pause
