# Quick Start Guide - Streaming WebSocket TTS

## Setup (5 minutes)

### 1. Install Python Dependencies
```bash
pip install websockets
```

### 2. Start the WebSocket TTS Server
```bash
cd U:\six-stars-solution
python scripts\websocket_tts_server.py
```

You should see:
```
[HH:MM:SS] [INFO] Starting WebSocket TTS server on ws://0.0.0.0:8100/tts/stream
[HH:MM:SS] [INFO] Press Ctrl+C to stop
```

### 3. Verify Configuration
Check `src\Wds.SixStars.Ui\appsettings.json` contains:
```json
{
  "WebSocketTTS": {
    "WebSocketUrl": "ws://localhost:8100/tts/stream",
    "Model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
    "DefaultLanguage": "Auto",
    "ResponseFormat": "wav",
    "MaxWordsPerChunk": 30,
    "MaxSentencesPerChunk": 2
  }
}
```

### 4. Run the Application
1. Open `Wds.SixStars.Ui` in Visual Studio
2. Press F5 to run
3. Check the Debug output window for:
   ```
   [TTS] Using streaming TTS: True
   [Streaming TTS] Configured WebSocket URL: ws://localhost:8100/tts/stream
   [WebSocketTtsVoiceGenerator] Connected successfully
   ```

## Test It

### Basic Test
1. In the app, enter: `Elusiaa, tell me a story while I work.`
2. Click **Send**
3. Watch the status bar for chunk progress:
   ```
   Voice chunk 1/3 received: 22050 bytes
   Playing chunk 1/3: 'Once upon a time, in a land...'
   Chunk 1/3 playback completed.
   Voice chunk 2/3 received: 26400 bytes
   ...
   ```

### Interruption Test
1. Click **Tell Story** (or send any message)
2. While Star is speaking, click **Start Listen**
3. Speak into your microphone for ~500ms
4. Status should show: `? Star speech interrupted by user`
5. Generation and playback should stop immediately

## Switching Between HTTP and WebSocket TTS

### Use WebSocket Streaming (Recommended)
In `appsettings.json`:
```json
{
  "WebSocketTTS": {
    "WebSocketUrl": "ws://localhost:8100/tts/stream"
  }
}
```

### Use HTTP TTS (Fallback)
In `appsettings.json`:
```json
{
  "WebSocketTTS": {
    "WebSocketUrl": ""
  },
  "TTS": {
    "EndpointUrl": "http://localhost:8100/v1/audio/speech"
  }
}
```

## Customizing Chunking

### Fast Playback (Low Latency)
For minimal delay before first audio:
```json
{
  "MaxWordsPerChunk": 15,
  "MaxSentencesPerChunk": 1
}
```

### Quality Playback (Better Coherence)
For more natural prosody:
```json
{
  "MaxWordsPerChunk": 50,
  "MaxSentencesPerChunk": 3
}
```

### Balanced (Default)
Good mix of speed and quality:
```json
{
  "MaxWordsPerChunk": 30,
  "MaxSentencesPerChunk": 2
}
```

## Interruption Settings

Configured in `MainViewModel.cs` constructor:
```csharp
new SpeechInterruptionOptions
{
    MinInterruptionDurationMs = 300,  // How long user must speak to interrupt
    InterruptionDelayMs = 200,        // Delay before interruption is allowed
    EnableInterruption = true,
    CancelGenerationOnInterruption = true,
    CancelPlaybackOnInterruption = true
}
```

To make interruption more sensitive (faster):
```csharp
MinInterruptionDurationMs = 150,  // Interrupt after 150ms
InterruptionDelayMs = 100,        // Delay 100ms
```

To make interruption less sensitive (require longer speech):
```csharp
MinInterruptionDurationMs = 500,  // Interrupt after 500ms
InterruptionDelayMs = 300,        // Delay 300ms
```

## Integrating Real TTS

The server now uses real Qwen3-TTS generation through the `qwen-tts` package. To configure it:

### Quick Option: Edge TTS (Free, No Setup)
```bash
pip install edge-tts
```

Edit `scripts/websocket_tts_server.py`, replace the `TtsEngine` class:
```python
import edge_tts
import io

class TtsEngine:
    def __init__(self):
        self.voice = "en-US-AriaNeural"
        logger.info("Edge-TTS initialized")

    async def generate_audio(self, text: str, voice_id: str, settings: Dict[str, Any]) -> bytes:
        communicate = edge_tts.Communicate(text, self.voice)
        audio_data = b''
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                audio_data += chunk["data"]
        return audio_data
```

### Advanced: Qwen3-TTS
See `scripts/README_WEBSOCKET_TTS.md` for full instructions.

## Debugging

### Python Server Not Starting
```bash
# Check if port 8100 is already in use
netstat -ano | findstr :8100

# Kill the process if needed
taskkill /PID <process_id> /F

# Restart the server
python scripts\websocket_tts_server.py
```

### App Not Connecting
1. Check the server is running (look for "Starting WebSocket TTS server...")
2. Check `appsettings.json` has correct `WebSocketUrl`
3. Check firewall isn't blocking port 8100
4. Look at Debug output for connection errors

### No Audio Playing
1. Check Python server logs for "Sent chunk X/Y" messages
2. Check C# Debug output for "Audio chunk received" messages
3. Verify NAudio is working (test with HTTP TTS mode)
4. Check volume/audio device settings

### Choppy Audio
1. Increase `MaxWordsPerChunk` to reduce chunk count
2. Ensure TTS model runs on GPU (if using real TTS)
3. Check CPU/memory usage during generation

## What's Next?

1. **Configure Qwen3-TTS** with your model path, CUDA/CPU device, and clone audio directory.
2. **Tune chunking** parameters for your preference
3. **Test interruption** behavior thoroughly
4. **Monitor performance** and adjust as needed

For detailed documentation, see:
- `scripts/README_WEBSOCKET_TTS.md` - Server setup and TTS integration
- `STREAMING_TTS_IMPLEMENTATION.md` - Full technical documentation

---

**Everything is ready!** Just start the Python server and run the app. ??
