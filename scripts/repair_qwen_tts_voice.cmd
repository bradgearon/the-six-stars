@echo off
setlocal EnableExtensions
cd /d "%~dp0\.."

echo Repairing Six Stars Qwen3-TTS voice dependencies...
echo This keeps LM Studio for the LLM and repairs only the local voice stack.
echo.

echo Step 1/2: install base requirements without torch...
if exist ".venv\Scripts\activate.cmd" (
  call ".venv\Scripts\activate.cmd"
) else if exist ".venv\Scripts\activate.bat" (
  call ".venv\Scripts\activate.bat"
) else (
  echo .venv was not found. Run setup.cmd first.
  pause
  exit /b 1
)
python -m pip install --upgrade pip setuptools wheel
python -m pip install --no-cache-dir -r requirements.txt
if errorlevel 1 exit /b 1

echo.
echo Step 2/2: install CUDA PyTorch and Qwen packages...
call scripts\install_cuda_torch.cmd /nopause
if errorlevel 1 exit /b 1

echo.
echo Done. Restart launch.cmd after this.
pause
exit /b 0
