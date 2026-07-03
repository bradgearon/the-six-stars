# WebSocket TTS Default Update

WebSocket streaming TTS is now the default voice path for the WPF desktop app.

## Defaults

```json
"WebSocketTTS": {
  "WebSocketUrl": "ws://localhost:8100/tts/stream",
  "Model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
  "DefaultLanguage": "English",
  "ResponseFormat": "wav",
  "MaxWordsPerChunk": 30,
  "MaxSentencesPerChunk": 2,
  "Enabled": true
}
```

## Override

To force fallback HTTP WAV playback:

### CMD / Cmder

```bat
set SIX_STARS_USE_STREAMING_TTS=false
```

### PowerShell

```powershell
$env:SIX_STARS_USE_STREAMING_TTS = "false"
```

To change the WebSocket URL:

```bat
set SIX_STARS_WEBSOCKET_TTS_URL=ws://localhost:8100/tts/stream
```
