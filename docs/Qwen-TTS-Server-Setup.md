# Qwen TTS Server Setup Guide

This guide helps you set up a local TTS server on port 8100 that works with your Six Stars application.

## Quick Start (Easiest Option)

The simplest way to get started is using the fallback Edge TTS server:

```bash
# Install dependencies
pip install flask edge-tts

# Run the server
cd scripts
python qwen2_audio_tts_server.py
```

This will start a server on `http://localhost:8100` with Edge TTS as the backend (requires internet).

## Option 1: Edge TTS (Recommended for Quick Testing)

**Pros:** Fast setup, good quality, free
**Cons:** Requires internet connection

```bash
pip install flask edge-tts
python qwen2_audio_tts_server.py
```

## Option 2: Bark TTS (Best Quality, Offline)

**Pros:** Excellent quality, offline, multiple voices
**Cons:** Slower generation, requires GPU for good performance

```bash
# Install Bark
pip install flask numpy scipy git+https://github.com/suno-ai/bark.git

# Set backend
export TTS_BACKEND=bark

# Run
python qwen_tts_simple_server.py
```

## Option 3: Coqui TTS (Good Quality, Offline)

**Pros:** Good quality, offline, fast
**Cons:** Moderate quality compared to Bark

```bash
# Install Coqui
pip install flask TTS

# Set backend
export TTS_BACKEND=coqui

# Run
python qwen_tts_simple_server.py
```

## Option 4: pyttsx3 (Fastest, Offline)

**Pros:** Very fast, offline, no dependencies
**Cons:** Lower quality, robotic sound

```bash
# Install pyttsx3
pip install flask pyttsx3

# Set backend
export TTS_BACKEND=pyttsx3

# Run
python qwen_tts_simple_server.py
```

## Option 5: Actual Qwen2-Audio Model (Advanced)

If you have the actual Qwen2-Audio model installed:

```bash
# Install dependencies
pip install flask torch transformers accelerate soundfile

# Download model (first time only)
huggingface-cli login
huggingface-cli download Qwen/Qwen2-Audio-7B-Instruct

# Run with Qwen model
export QWEN_MODEL_PATH="Qwen/Qwen2-Audio-7B-Instruct"
python qwen2_audio_tts_server.py
```

## Testing the Server

Once the server is running, test it:

### Using curl
```bash
curl -X POST http://localhost:8100/v1/audio/speech \
  -H "Content-Type: application/json" \
  -d '{
    "model": "tts-1",
    "input": "Hello, this is a test of the Qwen TTS server.",
    "voice": "alloy"
  }' \
  --output test.wav

# Play the file
# Windows: start test.wav
# Linux: aplay test.wav
# Mac: afplay test.wav
```

### Using Python
```python
import requests
import json

response = requests.post(
    'http://localhost:8100/v1/audio/speech',
    headers={'Content-Type': 'application/json'},
    json={
        'model': 'tts-1',
        'input': 'Hello from Python!',
        'voice': 'alloy'
    }
)

with open('output.wav', 'wb') as f:
    f.write(response.content)

print("Audio saved to output.wav")
```

### Health Check
```bash
curl http://localhost:8100/health
```

Expected response:
```json
{
  "status": "healthy",
  "backend": "edge-tts-fallback",
  "model_loaded": false,
  "device": "cpu"
}
```

## Configure Six Stars to Use the Server

Update `src/Wds.SixStars.Ui/appsettings.json`:

```json
{
  "TTS": {
    "EndpointUrl": "http://localhost:8100/v1/audio/speech",
    "Model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
    "ApiKey": null,
    "PayloadShape": "OpenAiSpeech",
    "TaskType": "VoiceClone",
    "DefaultLanguage": "Auto"
  }
}
```

## Star Clone Audio Configuration

Set each Star's clone reference file in:

- `src/Wds.SixStars.Api/Content/Stars/<star>.json`

Use these fields:

- `voiceCloneAudioFile`: filename in `src/Wds.SixStars.Api/Content/VoiceClones`
- `voiceCloneRefText`: exact transcript of that audio clip

Example:

```json
{
  "starId": "Elusiaa",
  "voiceCloneAudioFile": "elusiaa_clone.wav",
  "voiceCloneRefText": "Little one, stay close to the light. Mercy is how we remember we are not gods."
}
```

The `Content/VoiceClones` directory is copied to output automatically. You can add your generated clone voices there.

## Recommended Clone Recording Script

For each clone voice, record one clean reference clip (6-12 seconds) using a line like this:

`I am [STAR NAME]. I speak clearly, calmly, and naturally. Mercy and strength guide my words.`

Better quality tips:

- Record in a quiet room with no music or effects.
- Keep a steady pace and neutral emotion.
- Use the exact same text as `voiceCloneRefText`.
- Export as mono WAV when possible.

Or set environment variable:
```bash
# Windows (PowerShell)
$env:SIX_STARS_TTS_ENDPOINT="http://localhost:8100/v1/audio/speech"

# Linux/Mac
export SIX_STARS_TTS_ENDPOINT="http://localhost:8100/v1/audio/speech"
```

## Available Voices

The server supports OpenAI-compatible voice names:

- `alloy` - Female, balanced
- `echo` - Male, deeper
- `fable` - Male, expressive
- `onyx` - Male, authoritative
- `nova` - Female, energetic
- `shimmer` - Female, gentle

Example:
```json
{
  "input": "Elusiaa speaks with a gentle voice.",
  "voice": "elusiaa-dev",
  "task_type": "VoiceClone",
  "voice_clone_audio": "elusiaa_clone.wav",
  "voice_clone_ref_text": "Little one, stay close to the light. Mercy is how we remember we are not gods."
}
```

## Server Scripts Comparison

| Script | Best For | Quality | Speed | Offline |
|--------|----------|---------|-------|---------|
| `qwen2_audio_tts_server.py` | Production with Qwen model | Excellent | Medium | ? |
| `qwen_tts_simple_server.py` | Easy setup, multiple backends | Good | Fast | Depends |
| Edge TTS fallback | Quick testing | Good | Fast | ? |

## Environment Variables

All scripts support these variables:

- `QWEN_TTS_PORT` - Server port (default: 8100)
- `QWEN_TTS_HOST` - Server host (default: 0.0.0.0)
- `QWEN_TTS_DEVICE` - Device for models (cuda/cpu)
- `TTS_BACKEND` - Backend to use (bark/coqui/edge/pyttsx3)
- `QWEN_MODEL_PATH` - Path to Qwen model
- `QWEN_TTS_TASK_TYPE` - Default task type (`VoiceClone` recommended)
- `QWEN_TTS_CLONE_AUDIO_DIR` - Directory containing reference clone files

Examples:
```bash
# Run on different port
export QWEN_TTS_PORT=9100
python qwen2_audio_tts_server.py

# Use GPU
export QWEN_TTS_DEVICE=cuda
python qwen2_audio_tts_server.py

# Use Bark backend
export TTS_BACKEND=bark
python qwen_tts_simple_server.py
```

## API Endpoints

### POST /v1/audio/speech
Generate speech from text (OpenAI compatible)

**Request:**
```json
{
  "model": "tts-1",
  "input": "Text to convert to speech",
  "voice": "alloy",
  "response_format": "wav",
  "speed": 1.0
}
```

**Response:** WAV audio file (binary)

### GET /health
Check server health

**Response:**
```json
{
  "status": "healthy",
  "backend": "edge-tts-fallback",
  "model_loaded": true
}
```

### GET /v1/models
List available models (OpenAI compatible)

**Response:**
```json
{
  "object": "list",
  "data": [
    {
      "id": "tts-1",
      "object": "model",
      "owned_by": "qwen-local"
    }
  ]
}
```

## Troubleshooting

### Server won't start

**Error:** `ModuleNotFoundError: No module named 'flask'`
```bash
pip install flask
```

**Error:** `No TTS backend available`
```bash
# Install at least one backend
pip install edge-tts  # Easiest
# OR
pip install git+https://github.com/suno-ai/bark.git  # Best quality
```

### Audio quality is poor

Try a different backend:
```bash
export TTS_BACKEND=bark
python qwen_tts_simple_server.py
```

### Server is slow

- Use Edge TTS (fast but online): `TTS_BACKEND=edge`
- Use pyttsx3 (fastest but lower quality): `TTS_BACKEND=pyttsx3`
- Use GPU if available: `QWEN_TTS_DEVICE=cuda`

### Connection refused from Six Stars

Check firewall:
```bash
# Windows
netsh advfirewall firewall add rule name="Qwen TTS" dir=in action=allow protocol=TCP localport=8100

# Linux
sudo ufw allow 8100
```

Test connection:
```bash
curl http://localhost:8100/health
```

### Out of memory (GPU)

Use CPU instead:
```bash
export QWEN_TTS_DEVICE=cpu
python qwen2_audio_tts_server.py
```

Or use a lighter backend:
```bash
export TTS_BACKEND=edge
python qwen_tts_simple_server.py
```

## Performance Tips

1. **Use GPU if available** - Much faster generation
2. **Pre-warm the model** - First request is slow, subsequent ones faster
3. **Use Edge TTS for testing** - Fast and reliable during development
4. **Use Bark for production** - Best quality for final deployment
5. **Cache common phrases** - Pre-generate frequently used audio

## Integration with Six Stars

Once the server is running:

1. Start the TTS server: `python qwen2_audio_tts_server.py`
2. Start Six Stars application
3. Send a message to a Star
4. The Star's response will be converted to speech automatically
5. Check Debug Output for TTS status

Debug output will show:
```
[TTS] Configured endpoint: http://localhost:8100/v1/audio/speech
Voice generated: 45320 bytes from Qwen/Qwen2-Audio-7B-Instruct.
Playing Star voice. Microphone input is muted during playback.
Star voice playback completed. Listening can resume.
```

## Next Steps

- Train custom voices for each Star character
- Implement voice caching for faster responses
- Add emotion-based voice modulation
- Create voice profiles matching each Star's personality

## Support

For issues:
1. Check server logs (console output)
2. Test with curl commands above
3. Verify firewall settings
4. Check available backends with health endpoint
