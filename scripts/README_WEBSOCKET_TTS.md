# WebSocket TTS Server - Setup Guide

This WebSocket-based TTS server provides streaming audio generation with automatic text chunking.

## Features

- **Streaming Audio**: Generates and sends audio chunks as they're created
- **Automatic Chunking**: Splits long text into sentence-based chunks (configurable)
- **Real-time Playback**: UI can start playing audio before generation completes
- **Cancelable Generation**: User can interrupt ongoing speech generation
- **WebSocket Connection**: Maintains persistent connection for low latency

## Quick Start

### 1. Install Dependencies

```bash
pip install websockets
```

### 2. Run the Server

```bash
python scripts/websocket_tts_server.py
```

The server will start on `ws://0.0.0.0:8100/tts/stream`

### 3. Configure the Client

In `appsettings.json`:

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

## Integrating Your TTS Engine

The implementation now uses the real Qwen3-TTS engine. To configure it:

### Option 1: Qwen3-TTS (Recommended)

```python
from transformers import AutoProcessor, Qwen2AudioForConditionalGeneration
import torch

class TtsEngine:
    def __init__(self):
        self.processor = AutoProcessor.from_pretrained("Qwen/Qwen3-TTS-12Hz-1.7B-Base")
        self.model = Qwen2AudioForConditionalGeneration.from_pretrained(
            "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
            torch_dtype=torch.float16,
            device_map="auto"
        )
        logger.info("Qwen3-TTS loaded successfully")

    async def generate_audio(self, text: str, voice_id: str, settings: Dict[str, Any]) -> bytes:
        # Prepare input
        inputs = self.processor(text=text, return_tensors="pt")
        inputs = {k: v.to(self.model.device) for k, v in inputs.items()}

        # Generate audio
        with torch.no_grad():
            audio_output = self.model.generate(**inputs)

        # Convert to WAV bytes
        audio_numpy = audio_output[0].cpu().numpy()
        return self._numpy_to_wav_bytes(audio_numpy, sample_rate=16000)

    def _numpy_to_wav_bytes(self, audio_data, sample_rate=16000):
        import io
        import wave
        import numpy as np

        buffer = io.BytesIO()
        with wave.open(buffer, 'wb') as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)  # 16-bit
            wav.setframerate(sample_rate)

            # Convert float to int16
            audio_int16 = (audio_data * 32767).astype(np.int16)
            wav.writeframes(audio_int16.tobytes())

        return buffer.getvalue()
```

### Option 2: Coqui TTS / XTTS

```python
from TTS.api import TTS

class TtsEngine:
    def __init__(self):
        self.model = TTS("tts_models/multilingual/multi-dataset/xtts_v2").to("cuda")
        logger.info("XTTS v2 loaded successfully")

    async def generate_audio(self, text: str, voice_id: str, settings: Dict[str, Any]) -> bytes:
        # Generate with XTTS
        wav_data = self.model.tts(
            text=text,
            speaker_wav=settings.get('voiceCloneAudio'),  # Optional voice cloning
            language=settings.get('language', 'en'),
            split_sentences=False  # We handle chunking ourselves
        )

        return self._numpy_to_wav_bytes(wav_data, sample_rate=self.model.synthesizer.output_sample_rate)
```

### Option 3: Edge TTS (Free, Cloud-based)

```bash
pip install edge-tts
```

```python
import edge_tts
import io

class TtsEngine:
    def __init__(self):
        self.voice = "en-US-AriaNeural"  # Change as needed
        logger.info("Edge-TTS initialized")

    async def generate_audio(self, text: str, voice_id: str, settings: Dict[str, Any]) -> bytes:
        communicate = edge_tts.Communicate(text, self.voice)

        # Generate audio
        audio_data = b''
        async for chunk in communicate.stream():
            if chunk["type"] == "audio":
                audio_data += chunk["data"]

        return audio_data
```

## Protocol

### Client ? Server (JSON)

**Generation Request:**
```json
{
  "type": "generate",
  "requestId": 1,
  "text": "Hello, this is Elusiaa speaking to you.",
  "starId": "Elusiaa",
  "voiceId": "elusiaa-dev",
  "model": "Qwen/Qwen3-TTS-12Hz-1.7B-Base",
  "language": "Auto",
  "responseFormat": "wav",
  "speed": 1.0,
  "maxWordsPerChunk": 30,
  "maxSentencesPerChunk": 2
}
```

**Cancel Request:**
```json
{
  "type": "cancel"
}
```

### Server ? Client

**Chunk Info (JSON):**
```json
{
  "type": "chunk_info",
  "chunkIndex": 0,
  "totalChunks": 3,
  "text": "Hello, this is Elusiaa...",
  "starId": "Elusiaa"
}
```

**Audio Chunk (Binary):**
- 4 bytes: chunk index (int32)
- 4 bytes: total chunks (int32)
- 4 bytes: text length (int32)
- N bytes: text (UTF-8)
- 4 bytes: starId length (int32)
- N bytes: starId (UTF-8)
- 4 bytes: voiceId length (int32)
- N bytes: voiceId (UTF-8)
- Remaining: WAV audio data

**Complete (JSON):**
```json
{
  "type": "complete",
  "starId": "Elusiaa",
  "totalChunks": 3,
  "totalDurationMs": 4500.0,
  "cancelled": false
}
```

**Error (JSON):**
```json
{
  "type": "error",
  "message": "Generation failed: ...",
  "starId": "Elusiaa"
}
```

## Configuration

### Chunking Settings

- `maxWordsPerChunk`: Maximum words per chunk (default: 30)
- `maxSentencesPerChunk`: Maximum sentences per chunk (default: 2)

**Chunking Rules:**
1. Text is first split into sentences
2. Sentences are grouped until:
   - Word count exceeds `maxWordsPerChunk`, OR
   - Sentence count reaches `maxSentencesPerChunk`
3. Each chunk is generated and sent independently

### Performance Tuning

For faster first-audio playback:
- Decrease `maxWordsPerChunk` (10-20 words)
- Set `maxSentencesPerChunk` to 1

For better quality/coherence:
- Increase `maxWordsPerChunk` (40-60 words)
- Set `maxSentencesPerChunk` to 3-4

## Debugging

The server logs all activity to console with timestamps:

```
[12:34:56] [INFO] Starting WebSocket TTS server on ws://0.0.0.0:8100/tts/stream
[12:35:01] [INFO] Client connected: 127.0.0.1:52340
[12:35:02] [INFO] Request 1: Generating speech for 'Hello, this is...' (star=Elusiaa, voice=elusiaa-dev)
[12:35:02] [INFO] Split into 3 chunks
[12:35:02] [DEBUG] Generating chunk 1/3: 'Hello, this is Elusiaa speaking...'
[12:35:03] [INFO] Sent chunk 1/3 (22050 bytes)
[12:35:03] [DEBUG] Generating chunk 2/3: 'I'm here to guide you through...'
[12:35:04] [INFO] Sent chunk 2/3 (26400 bytes)
[12:35:04] [DEBUG] Generating chunk 3/3: 'May your path be bright.'
[12:35:05] [INFO] Sent chunk 3/3 (15876 bytes)
[12:35:05] [INFO] Generation complete: 3 chunks, 4500.0ms
```

To increase verbosity, change the logging level:

```python
logging.basicConfig(level=logging.DEBUG)  # More detailed output
```

## Troubleshooting

**Problem: No audio plays**
- Check that the WebSocket server is running on the configured port
- Verify the client is connecting (check server logs for "Client connected")
- Ensure audio chunks are being sent (look for "Sent chunk X/Y" messages)

**Problem: Audio quality is poor**
- Configure the Qwen3-TTS engine and Star clone files
- Increase sample rate in WAV generation (22050 or 24000 Hz)
- Ensure proper audio format conversion (16-bit PCM)

**Problem: Playback is choppy**
- Increase chunk size (`maxWordsPerChunk`)
- Ensure TTS model is running on GPU
- Check network latency if using remote TTS

**Problem: Generation is slow**
- Use smaller TTS model
- Enable GPU acceleration
- Consider batching chunks (advanced)

## Next Steps

1. Start `websocket_tts_server.py` with the Qwen3-TTS environment variables described above
2. Tune chunking parameters for your use case
3. Test interruption behavior (speak while Star is talking)
4. Monitor performance and adjust accordingly

For questions or issues, check the debug output in both the server logs and the C# application's Debug window.

## Actual Qwen3-TTS generation

`websocket_tts_server.py` now uses the real `qwen-tts` package. It no longer returns placeholder silent WAVs. For Base voice clone models, it builds and caches Qwen `voice_clone_prompt` objects the first time each Star/voice/reference-audio combination is requested, then reuses them for later chunks and later Star replies.

See `WEBSOCKET-QWEN-ACTUAL-GENERATION-UPDATE.md` in the repository root for setup commands.
