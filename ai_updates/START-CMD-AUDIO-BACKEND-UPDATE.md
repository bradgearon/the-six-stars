# Start Command and Audio Backend Update

## What changed

- `start.cmd` now activates `.venv\Scripts\activate.bat` before launching services.
- If `.venv` is missing, `start.cmd` attempts to create it with Python 3.12 first, then generic `python -m venv`.
- `start.cmd` sets local execution environment defaults before launching:
  - `PYTHONNOUSERSITE=1`
  - `QWEN_TTS_DEVICE=cuda`
  - `QWEN_TTS_LANGUAGE=English`
  - `QWEN_TTS_MODEL_PATH=Qwen/Qwen3-TTS-12Hz-1.7B-Base`
  - clone audio and Star JSON directories
- On first run, `start.cmd` installs `requirements.txt` into `.venv` and writes `.venv\.sixstars_requirements_installed`.
- To skip requirement installation, set `SIX_STARS_SKIP_PIP_INSTALL=true` before running `start.cmd`.
- `.gitignore` now covers both .NET and Python/pip project outputs.
- WPF UI now has an Audio Backend dropdown in Voice Output.
- Audio backend originally defaulted to `directsound`; it now defaults to `wasapi` after DirectSoundOut interop failures were seen on Windows 11.

## Audio backend options

- `wasapi` — default; modern Windows shared-mode path using the current default output device.
- `waveout` — legacy fallback.
- `directsound` — available, but can fail on some Windows 11/.NET/driver combinations with an NAudio DirectSoundOut interop exception.
- `auto` — tries WASAPI, then WaveOut, then DirectSound.

## Config

```json
"VoiceOutput": {
  "SaveGeneratedWavs": false,
  "GeneratedWavDirectory": "",
  "AudioBackend": "wasapi",
  "SaveMetadataText": true
}
```

Override from CMD:

```bat
set SIX_STARS_AUDIO_BACKEND=wasapi
```

Then launch:

```bat
start.cmd
```
