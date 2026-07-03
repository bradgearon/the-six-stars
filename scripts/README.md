# Six Stars Server Scripts

This directory contains Python server scripts for backend services used by Six Stars.

## Available Servers

### 1. Qwen TTS Server (Port 8100)
Text-to-speech server compatible with OpenAI API format.

**Files:**
- `qwen2_audio_tts_server.py` - Main TTS server with Qwen2-Audio support
- `qwen_tts_simple_server.py` - Simplified multi-backend TTS server
- `test_tts_server.py` - Test script for TTS server

**Quick Start:**
```bash
# Install dependencies
pip install flask edge-tts

# Start server
python qwen2_audio_tts_server.py

# Test it
python test_tts_server.py
```

**Documentation:** `../docs/Qwen-TTS-Server-Setup.md`

### 2. OpenWakeWord Server (Port 9000)
Wake word detection for voice activation.

**Files:**
- `openwakeword_server.py` - Wake word detection HTTP server

**Quick Start:**
```bash
# Install dependencies
pip install openwakeword flask numpy

# Start server
python openwakeword_server.py
```

**Documentation:** `../docs/OpenWakeWord-Setup.md`

### 3. faster-whisper Server (Port 8000)
Speech-to-text server with both the current Six Stars `/transcribe` route and an OpenAI-compatible `/v1/audio/transcriptions` route.

**Files:**
- `faster_whisper_openai_server.py` - OpenAI-compatible faster-whisper HTTP server

**Quick Start:**
```bash
# Install dependencies
pip install flask faster-whisper

# Start server
python faster_whisper_openai_server.py
```

**Default endpoints:**
- `http://localhost:8000/transcribe`
- `http://localhost:8000/v1/audio/transcriptions`

### 4. Service Launcher
Helper script to start multiple services at once.

**Files:**
- `start_services.py` - Multi-service launcher

**Quick Start:**
```bash
python start_services.py
```

## Port Assignments

| Service | Default Port | Environment Variable |
|---------|-------------|---------------------|
| OpenWakeWord | 9000 | `OWW_PORT` |
| Whisper | 8000 | `WHISPER_PORT` |
| LLM | 8080 | `LLM_PORT` |
| TTS | 8100 | `QWEN_TTS_PORT` |
| Screen LLM | 8080 | `SCREEN_LLM_PORT` |

## Requirements

### Minimal (TTS only)
```bash
pip install flask edge-tts
```

### Full Setup (All Services)
```bash
pip install flask edge-tts openwakeword numpy faster-whisper
```

### Optional (Better TTS Quality)
```bash
# Bark (best quality, slower)
pip install git+https://github.com/suno-ai/bark.git

# Coqui TTS (good quality, faster)
pip install TTS

# pyttsx3 (offline, fast, lower quality)
pip install pyttsx3
```

## Common Tasks

### Start TTS Server Only
```bash
python qwen2_audio_tts_server.py
```

### Start TTS with Specific Backend
```bash
export TTS_BACKEND=bark
python qwen_tts_simple_server.py
```

### Start Wake Word Detection
```bash
python openwakeword_server.py
```

### Start Whisper Transcription
```bash
python faster_whisper_openai_server.py
```

### Start All Services
```bash
python start_services.py
```

### Test TTS Server
```bash
# Make sure server is running first
python test_tts_server.py
```

## Environment Variables

### TTS Server
- `QWEN_TTS_PORT` - Server port (default: 8100)
- `QWEN_TTS_HOST` - Server host (default: 0.0.0.0)
- `QWEN_TTS_DEVICE` - Device (cuda/cpu)
- `TTS_BACKEND` - Backend (bark/coqui/edge/pyttsx3)
- `QWEN_MODEL_PATH` - Path to Qwen model
- `QWEN_TTS_TASK_TYPE` - Default task (`VoiceClone` recommended)
- `QWEN_TTS_CLONE_AUDIO_DIR` - Folder with clone reference files (default: `src/Wds.SixStars.Api/Content/VoiceClones`)

### OpenWakeWord Server
- `OWW_PORT` - Server port (default: 9000)
- `OWW_HOST` - Server host (default: 0.0.0.0)
- `OWW_MODEL_DIR` - Model directory
- `OWW_THRESHOLD` - Detection threshold (default: 0.5)

### faster-whisper Server
- `WHISPER_PORT` - Server port (default: 8000)
- `WHISPER_HOST` - Server host (default: 0.0.0.0)
- `WHISPER_MODEL` - Model name to download/use (default: `tiny.en`)
- `WHISPER_MODEL_PATH` - Local model directory to load instead of `WHISPER_MODEL`
- `WHISPER_DEVICE` - `auto`, `cpu`, or `cuda`
- `WHISPER_COMPUTE_TYPE` - `auto`, `int8`, `float16`, etc.
- `WHISPER_DEFAULT_LANGUAGE` - Default transcription language (default: `en`)
- `WHISPER_API_KEY` - Optional bearer key for `/v1/*` routes

## Server Status Endpoints

All servers provide health check endpoints:

```bash
# TTS Server
curl http://localhost:8100/health

# OpenWakeWord Server
curl http://localhost:9000/health

# faster-whisper Server
curl http://localhost:8000/health
```

## Integration with Six Stars

### Configuration File
Update `src/Wds.SixStars.Ui/appsettings.json`:

```json
{
  "OpenWakeWord": {
    "EndpointUrl": "http://localhost:9000/detect"
  },
  "TTS": {
    "EndpointUrl": "http://localhost:8100/v1/audio/speech"
  }
}
```

### Environment Variables
Or use environment variables:

```bash
# Windows PowerShell
$env:SIX_STARS_HOTWORD_ENDPOINT="http://localhost:9000/detect"
$env:SIX_STARS_TTS_ENDPOINT="http://localhost:8100/v1/audio/speech"

# Linux/Mac
export SIX_STARS_HOTWORD_ENDPOINT="http://localhost:9000/detect"
export SIX_STARS_TTS_ENDPOINT="http://localhost:8100/v1/audio/speech"
```

## Troubleshooting

### Port Already in Use
```bash
# Windows
netstat -ano | findstr :8100
taskkill /PID <pid> /F

# Linux/Mac
lsof -i :8100
kill -9 <pid>
```

### Cannot Connect from Six Stars
Check firewall:
```bash
# Windows
netsh advfirewall firewall add rule name="TTS Server" dir=in action=allow protocol=TCP localport=8100

# Linux
sudo ufw allow 8100
```

### Module Not Found
Install missing dependencies:
```bash
pip install flask edge-tts openwakeword numpy
```

### Server Crashes on Start
Check logs and install dependencies:
```bash
pip install --upgrade flask torch transformers
```

## Development

### Adding a New Server

1. Create `your_server.py` in this directory
2. Follow the Flask pattern from existing servers
3. Add health check endpoint: `/health`
4. Document in this README
5. Add to `start_services.py` if appropriate

### Testing Changes

```bash
# Run individual server
python your_server.py

# Test with curl
curl http://localhost:<port>/health

# Integration test with Six Stars
# (start server, then run Six Stars app)
```

## Performance Optimization

### Use GPU for TTS
```bash
export QWEN_TTS_DEVICE=cuda
python qwen2_audio_tts_server.py
```

### Pre-load Models
Models are loaded on first request. For faster first response:
```bash
# Pre-warm by sending a test request
curl -X POST http://localhost:8100/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{"input": "warmup", "voice": "alloy"}'
```

### Use Faster Backend for Development
```bash
export TTS_BACKEND=edge  # Fast, online
python qwen_tts_simple_server.py
```

## Production Deployment

For production use:

1. **Use HTTPS** - Add SSL/TLS certificates
2. **Add Authentication** - Implement API key verification
3. **Rate Limiting** - Prevent abuse
4. **Load Balancing** - Multiple server instances
5. **Monitoring** - Log requests and errors
6. **Caching** - Cache frequently used audio

Example with gunicorn:
```bash
pip install gunicorn
gunicorn -w 4 -b 0.0.0.0:8100 qwen2_audio_tts_server:app
```

## Documentation

- **TTS Setup:** `../docs/Qwen-TTS-Server-Setup.md`
- **Wake Word Setup:** `../docs/OpenWakeWord-Setup.md`
- **Feature Guide:** `../docs/Enhanced-Features-Guide.md`
- **Quick Start:** `../QUICKSTART.md`

## License

These scripts are part of the Six Stars project.

## Support

For issues:
1. Check server logs (console output)
2. Test health endpoints
3. Verify firewall/port settings
4. Check documentation in `../docs/`
