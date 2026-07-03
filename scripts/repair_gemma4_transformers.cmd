@echo off
setlocal
cd /d "%~dp0\.."
call .venv\scripts\activate.cmd
python -m pip install --upgrade transformers "huggingface-hub>=0.24.0" accelerate safetensors sentencepiece protobuf packaging
python scripts\local_llm_openai_server.py
