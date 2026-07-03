# WebSocket Qwen3-TTS Actual Generation Update

The previous `scripts/websocket_tts_server.py` generated placeholder silent WAVs. It now uses the real `qwen-tts` Python package and the Qwen3-TTS model directly.

## What changed

- Loads `Qwen3TTSModel.from_pretrained(...)` lazily on first request.
- Supports Qwen3-TTS Base voice cloning, CustomVoice, and VoiceDesign model types.
- For Base voice clone models, it builds reusable Star-specific `voice_clone_prompt` objects on first use and stores them in memory for later chunks/requests.
- Reads Star fallback voice clone filenames/ref text from `src/Wds.SixStars.Api/Content/Stars/*.json` if the WPF request does not include them.
- Reads Star voice/personality notes from JSON and includes them in the instruction text for CustomVoice/VoiceDesign models.
- Sends real WAV bytes over the existing WebSocket binary chunk protocol.
- Logs generated WAV duration, peak, and RMS so silence is easy to diagnose.
- Does not silently fall back to fake silent audio anymore; generation errors are sent back as WebSocket `error` messages.

## Start the server

CMD:

```bat
cd /d U:\six-stars-solution
.venv\Scripts\activate

set PYTHONNOUSERSITE=1
set QWEN_TTS_DEVICE=cuda
set QWEN_TTS_LANGUAGE=English
set QWEN_TTS_MODEL_PATH=Qwen/Qwen3-TTS-12Hz-1.7B-Base
set QWEN_TTS_CLONE_AUDIO_DIR=src/Wds.SixStars.Api/Content/VoiceClones
set QWEN_TTS_STARS_DIR=src/Wds.SixStars.Api/Content/Stars

python scripts\websocket_tts_server.py
```

PowerShell:

```powershell
cd U:\six-stars-solution
.\.venv\Scripts\Activate.ps1

$env:PYTHONNOUSERSITE = "1"
$env:QWEN_TTS_DEVICE = "cuda"
$env:QWEN_TTS_LANGUAGE = "English"
$env:QWEN_TTS_MODEL_PATH = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
$env:QWEN_TTS_CLONE_AUDIO_DIR = "src/Wds.SixStars.Api/Content/VoiceClones"
$env:QWEN_TTS_STARS_DIR = "src/Wds.SixStars.Api/Content/Stars"

python scripts\websocket_tts_server.py
```

## Useful diagnostics

Save server-generated WAVs while debugging:

```bat
set QWEN_TTS_DEBUG_OUTPUT_DIR=U:\six-stars-solution\tts-debug
```

Disable cached clone prompts if your local Qwen build has trouble with them:

```bat
set QWEN_TTS_CACHE_CLONE_PROMPTS=false
```

If cached prompts produce silence, the server automatically tries direct `generate_voice_clone(ref_audio=..., ref_text=...)` once and marks that cached prompt as bad for future requests.

## Requirements

`requirements.txt` now includes:

```txt
numpy==1.26.4
websockets>=12.0
qwen-tts
```

Install with:

```bat
python -m pip install -r requirements.txt
```

If you want CUDA PyTorch, install the CUDA torch wheel before installing the rest of the requirements.
