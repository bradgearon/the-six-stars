# PyInstaller NumPy ASR Runtime Fix

Fixes the redist launcher crash where `faster_whisper_openai_server.exe` fails before startup with:

```text
ModuleNotFoundError: No module named 'numpy._core._exceptions'
Importing the numpy C-extensions failed
```

Changes:

- Pins ASR CPU and NVIDIA requirements to `numpy==1.26.4` instead of the newest NumPy.
- Adds PyInstaller collection for NumPy.
- Adds explicit hidden imports for NumPy `_exceptions`.
- Adds ASR collection for `faster_whisper`, `ctranslate2`, and `soundfile`.

Rebuild with `-Force` so the old bad venv and redist exe are removed.
