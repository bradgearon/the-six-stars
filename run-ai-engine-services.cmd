@echo off
setlocal EnableExtensions
cd /d "%~dp0"

rem Alias for run-ai-services.cmd. Both start the prebuilt engine EXEs only.
call run-ai-services.cmd %*
