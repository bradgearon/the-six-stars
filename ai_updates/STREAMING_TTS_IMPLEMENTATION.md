# Streaming WebSocket TTS Implementation - Summary

## What Was Implemented

A complete WebSocket-based streaming TTS system that generates and plays Star speech in real-time chunks, with full cancellation support.

### Key Components Created

1. **IStreamingVoiceGenerator Interface** (`src/Wds.SixStars.Api/Voice/IStreamingVoiceGenerator.cs`)
   - Event-based streaming interface for chunked audio delivery
   - Events: `AudioChunkReady`, `GenerationComplete`, `GenerationError`
   - Methods: `GenerateStreamingAsync`, `CancelGeneration`, `ConnectAsync`, `DisconnectAsync`

2. **WebSocketTtsVoiceGenerator** (`src/Wds.SixStars.Api/Voice/WebSocketTtsVoiceGenerator.cs`)
   - Maintains persistent WebSocket connection to TTS server
   - Sends generation requests as JSON
   - Receives audio chunks as binary messages
   - Parses binary protocol (chunk index, text, audio data)
   - Full cancellation support

3. **WebSocketTtsOptions** (`src/Wds.SixStars.Api/Voice/WebSocketTtsOptions.cs`)
   - Configuration for WebSocket URL, model, chunking parameters
   - `MaxWordsPerChunk` (default: 30)
   - `MaxSentencesPerChunk` (default: 2)

4. **StreamingStarVoiceResponsePipeline** (`src/Wds.SixStars.Api/Voice/StreamingStarVoiceResponsePipeline.cs`)
   - Replaces StarVoiceResponsePipeline for streaming mode
   - Queues audio chunks as they arrive
   - Plays chunks sequentially via playback loop
   - Integrates with SpeechInterruptionManager
   - Events: `ChunkReceived`, `ChunkPlaybackStarted`, `ChunkPlaybackCompleted`, `AllChunksComplete`

5. **Python WebSocket TTS Server** (`scripts/websocket_tts_server.py`)
   - WebSocket server on port 8100
   - Text chunking based on sentences, words, and punctuation
   - Generates real Qwen3-TTS audio for each chunk
   - Binary protocol for efficient audio transmission
   - Full debug logging
   - Cancellation support

6. **Configuration Updates**
   - Added `WebSocketTtsConfig` to `AppConfiguration.cs`
   - Updated `appsettings.json` with WebSocket TTS section
   - Environment variable support

7. **MainViewModel Integration**
   - Auto-detects streaming vs. regular TTS based on config
   - Creates appropriate pipeline (`StreamingStarVoiceResponsePipeline` or `StarVoiceResponsePipeline`)
   - Wires chunk events to UI status updates
   - `SpeakStarResponseAsync` uses streaming pipeline when enabled

## How It Works

### End-to-End Flow

1. **User sends message** ? LLM generates response
2. **SpeakStarResponseAsync** is called with response text
3. **StreamingStarVoiceResponsePipeline** sends WebSocket JSON request to Python server
4. **Python server**:
   - Splits text into sentence chunks (e.g., "Hello, this is Elusiaa." + "I'm here to help.")
   - Generates audio for chunk 1
   - Sends binary chunk 1 to client
   - Generates audio for chunk 2 (in parallel)
   - Sends binary chunk 2 to client
   - Sends completion message
5. **WebSocketTtsVoiceGenerator** receives binary chunks, fires `AudioChunkReady` events
6. **StreamingStarVoiceResponsePipeline** queues chunks
7. **Playback loop** plays chunks sequentially via NAudio
8. **UI updates** show chunk progress in real-time

### Cancellation Flow

1. **User starts speaking** ? `SpeechInterruptionManager` detects speech
2. **After configurable delay** (300ms default), if user still speaking:
   - `SpeechInterruptionManager.CancelCurrentSpeech()` is called
   - `StreamingStarVoiceResponsePipeline.CancelCurrentSpeech()` cancels playback task
   - `WebSocketTtsVoiceGenerator.CancelGeneration()` sends cancel message to server
   - Python server stops generating remaining chunks
   - Playback loop exits
   - UI shows interruption status

## Protocol

### JSON Generation Request (Client ? Server)
```json
{
  "type": "generate",
  "requestId": 1,
  "text": "Full text to synthesize",
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

### Binary Audio Chunk (Server ? Client)
```
[4 bytes: chunkIndex int32]
[4 bytes: totalChunks int32]
[4 bytes: textLength int32]
[textLength bytes: UTF-8 text]
[4 bytes: starIdLength int32]
[starIdLength bytes: UTF-8 starId]
[4 bytes: voiceIdLength int32]
[voiceIdLength bytes: UTF-8 voiceId]
[remaining bytes: WAV audio data]
```

### JSON Complete Message (Server ? Client)
```json
{
  "type": "complete",
  "starId": "Elusiaa",
  "totalChunks": 3,
  "totalDurationMs": 4500.0,
  "cancelled": false
}
```

## Configuration

### Enable Streaming TTS

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

If `WebSocketUrl` is set, the app uses streaming TTS. Otherwise, it falls back to regular HTTP TTS.

### Interruption Settings

Already configured in `MainViewModel.cs`:
```csharp
new SpeechInterruptionOptions
{
    MinInterruptionDurationMs = 300,  // User must speak for 300ms to interrupt
    InterruptionDelayMs = 200,        // Wait 200ms after user starts speaking
    EnableInterruption = true,
    CancelGenerationOnInterruption = true,
    CancelPlaybackOnInterruption = true
}
```

## Benefits

### 1. **Faster Time-to-First-Audio**
   - First chunk plays while later chunks are still generating
   - No need to wait for entire response to be synthesized

### 2. **Better Responsiveness**
   - User can interrupt mid-sentence
   - Cancellation stops both generation and playback

### 3. **No Timeout Issues**
   - Long responses don't timeout HTTP requests
   - WebSocket maintains persistent connection

### 4. **Efficient Resource Use**
   - Chunks are generated/played/discarded incrementally
   - Lower memory footprint for long speeches

### 5. **Debuggable**
   - Detailed logging in Python server
   - UI shows chunk-by-chunk progress
   - Clear Debug output in C# application

## Testing

### 1. Start the Python WebSocket Server
```bash
python scripts/websocket_tts_server.py
```

Expected output:
```
[12:34:56] [INFO] Starting WebSocket TTS server on ws://0.0.0.0:8100/tts/stream
[12:34:56] [INFO] Press Ctrl+C to stop
```

### 2. Run the WPF App
1. Launch `Wds.SixStars.Ui`
2. Check Debug output for:
   ```
   [TTS] Using streaming TTS: True
   [Streaming TTS] Configured WebSocket URL: ws://localhost:8100/tts/stream
   ```

### 3. Send a Test Message
1. Enter: "Elusiaa, tell me a long story while I work."
2. Click Send
3. Watch UI status:
   ```
   Voice chunk 1/3 received: 22050 bytes
   Playing chunk 1/3: 'Once upon a time...'
   Chunk 1/3 playback completed.
   Voice chunk 2/3 received: 26400 bytes
   Playing chunk 2/3: 'In a faraway land...'
   ...
   ```

### 4. Test Interruption
1. While Star is speaking, click "Start Listen"
2. Speak for 300ms+
3. UI should show:
   ```
   ? Star speech interrupted by user
   ```

## Next Steps

### 1. Integrate Real TTS
`websocket_tts_server.py` already contains the real Qwen3-TTS engine. The core pattern is:
- Qwen3-TTS (see `README_WEBSOCKET_TTS.md`)
- Coqui XTTS
- Edge TTS
- Your custom TTS

### 2. Tune Chunking
Adjust for your use case:
- **Fast playback**: `MaxWordsPerChunk: 10-20`, `MaxSentencesPerChunk: 1`
- **Quality**: `MaxWordsPerChunk: 40-60`, `MaxSentencesPerChunk: 3`

### 3. Production Deployment
- Add reconnection logic to `WebSocketTtsVoiceGenerator`
- Implement connection health checks
- Add retry/fallback to HTTP TTS if WebSocket fails
- Consider load balancing for multiple TTS workers

## Files Changed/Created

### Created
- `src/Wds.SixStars.Api/Voice/IStreamingVoiceGenerator.cs`
- `src/Wds.SixStars.Api/Voice/WebSocketTtsVoiceGenerator.cs`
- `src/Wds.SixStars.Api/Voice/WebSocketTtsOptions.cs`
- `src/Wds.SixStars.Api/Voice/StreamingStarVoiceResponsePipeline.cs`
- `scripts/websocket_tts_server.py`
- `scripts/README_WEBSOCKET_TTS.md`

### Modified
- `src/Wds.SixStars.Ui/Configuration/AppConfiguration.cs` - Added `WebSocketTtsConfig`
- `src/Wds.SixStars.Ui/appsettings.json` - Added WebSocket TTS config section
- `src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`:
  - Added `_streamingVoiceResponsePipeline` and `_useStreamingTts` fields
  - Added `CreateStreamingVoiceGenerator()` method
  - Updated constructor to conditionally create streaming or regular pipeline
  - Updated `WireVoiceEvents()` to handle chunk events
  - Updated `SpeakStarResponseAsync()` to use streaming pipeline

## Debug Output Examples

### C# Application
```
[TTS] Using streaming TTS: True
[Streaming TTS] Configured WebSocket URL: ws://localhost:8100/tts/stream
[WebSocketTtsVoiceGenerator] Connecting to ws://localhost:8100/tts/stream...
[WebSocketTtsVoiceGenerator] Connected successfully
[Streaming TTS] Starting streaming speech for: 'The little one listens so well. It reminds me...'
[WebSocketTtsVoiceGenerator] Sending generation request 1: The little one listens so well. It remin...
[WebSocketTtsVoiceGenerator] Received text message: chunk_info
[WebSocketTtsVoiceGenerator] Chunk info received
[WebSocketTtsVoiceGenerator] Audio chunk 1/3: 22050 bytes, text='The little one listens so well....'
[Streaming TTS] Chunk 1/3: 'The little one listens so well. It...'
[StreamingStarVoiceResponsePipeline] Playing chunk 1/3
[StreamingStarVoiceResponsePipeline] Chunk 1/3 playback complete
[WebSocketTtsVoiceGenerator] Audio chunk 2/3: 26400 bytes, text='It reminds me of the quiet patie...'
[Streaming TTS] Chunk 2/3: 'It reminds me of the quiet patie...'
[StreamingStarVoiceResponsePipeline] Playing chunk 2/3
```

### Python Server
```
[12:35:01] [INFO] Client connected: 127.0.0.1:52340
[12:35:02] [DEBUG] Received message type: generate
[12:35:02] [INFO] Request 1: Generating speech for 'The little one listens so well...' (star=Elusiaa, voice=elusiaa-dev)
[12:35:02] [INFO] Split into 3 chunks
[12:35:02] [DEBUG] Generating chunk 1/3: 'The little one listens so well. It reminds me of the quiet patience...'
[12:35:03] [INFO] Sent chunk 1/3 (22050 bytes)
[12:35:03] [DEBUG] Generating chunk 2/3: 'It reminds me of the quiet patience needed to hear a truth beneath all that noise.'
[12:35:04] [INFO] Sent chunk 2/3 (26400 bytes)
[12:35:04] [DEBUG] Generating chunk 3/3: 'May your work be blessed, little one.'
[12:35:05] [INFO] Sent chunk 3/3 (15876 bytes)
[12:35:05] [INFO] Generation complete: 3 chunks, 4500.0ms
```

---

**All features requested have been implemented:**
? Sentence-by-sentence speech generation  
? Ordered playback of chunks  
? Cancelable generation and playback  
? WebSocket-based communication  
? Configurable interruption threshold  
? Full debug output  
? State tracking integration  

The system is ready for production TTS integration!
