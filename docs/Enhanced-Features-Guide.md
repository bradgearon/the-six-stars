# Six Stars - Enhanced Features Update

This document describes the recent enhancements to the Six Stars application, including LLM integration improvements, state tracking, and openWakeWord configuration.

## New Features

### 1. Real LLM Request/Response Integration

The application now properly sends requests to the configured AI LLM and displays response data.

**Features:**
- Status updates show "Sending request to AI LLM..." and "Waiting for LLM response..."
- Full request payload is sent to the configured endpoint
- Response data is properly parsed and displayed
- Error handling for LLM communication failures
- Configurable via `appsettings.json` or environment variables

**Status Bar Updates:**
- Before: "Processing..."
- Now: "Sending request to AI LLM..." ? "Waiting for LLM response..." ? "LLM Response Received. Heart ? +2, Trust ? +1..."

### 2. State Change Tracking with Running Totals

A new `StateChangeTracker` class monitors all relationship state changes across the session.

**Tracked Metrics:**
- Heart changes (total and per interaction)
- Trust changes (total and per interaction)
- Romance changes (total and per interaction)
- Benaveil Influence changes (total and per interaction)
- Positive/negative change counts for each metric
- Total interaction count
- Last interaction timestamp

**Per-Star Tracking:**
Each Star's relationship changes are tracked independently with detailed breakdowns.

**Combined Totals:**
All state changes across all Stars are aggregated for session-wide statistics.

### 3. Debug Output Enhancements

#### Visual Studio Debug Output Window

Every interaction now logs deterministic state changes to the Debug window:

```
=== DETERMINISTIC STATE CHANGE ===
Star: Elusiaa
Heart Delta: +2 (Suggested: +3)
Trust Delta: +1 (Suggested: +1)
Romance Delta: +2 (Suggested: +2)
Benaveil Delta: 0 (Suggested: 0)

=== COMBINED SESSION TOTALS ===
Total Interactions: 15
Combined Heart: +28
Combined Trust: +15
Combined Romance: +22
Combined Benaveil: -3

=== PER-STAR BREAKDOWN ===
Elusiaa Session Totals:
  Interactions: 10
  Heart: +20 (8+ / 2-)
  Trust: +12 (9+ / 1-)
  Romance: +18 (10+ / 0-)
  Benaveil: -2
  Last: 14:32:15

Kaelia Session Totals:
  Interactions: 5
  Heart: +8 (4+ / 1-)
  Trust: +3 (3+ / 0-)
  Romance: +4 (4+ / 0-)
  Benaveil: -1
  Last: 14:28:42
================================
```

#### UI Debug JSON Panel

The debug JSON panel now includes enhanced data:

```json
{
  "timestamp": "2024-01-15T14:32:15.123-08:00",
  "request": {
    "sessionId": "desktop-local-dev",
    "starId": "Elusiaa",
    "speaker": "LastCat",
    "requestType": "Chat",
    "playerText": "Elusiaa, tell me a story while I work."
  },
  "response": {
    "starId": "Elusiaa",
    "spokenText": "...",
    "emotionalPurpose": "...",
    "emotionScores": { ... },
    "suggestedStateDeltas": {
      "heart": 3,
      "trust": 1,
      "romance": 2
    },
    "actualStateDeltas": {
      "heart": 2,
      "trust": 1,
      "romance": 2
    },
    "conversationSummary": "...",
    "debug": { ... }
  },
  "sessionTotals": {
    "starId": "Elusiaa",
    "interactionCount": 10,
    "totalHeartChanges": 20,
    "totalTrustChanges": 12,
    "totalRomanceChanges": 18,
    "positiveHeartCount": 8,
    "negativeHeartCount": 2
  },
  "combinedTotals": {
    "Elusiaa": { ... },
    "Kaelia": { ... }
  }
}
```

### 4. OpenWakeWord Configuration

Speech is now properly routed to the wake word detector with full configuration support.

**Configuration File:** `appsettings.json`

```json
{
  "OpenWakeWord": {
    "Provider": "openWakeWord",
    "EndpointUrl": "http://localhost:9000/detect",
    "ApiKey": null,
    "TimeoutSeconds": 10,
    "PayloadShape": "MultipartWav",
    "Models": [
      "hey_mycroft_v0.1.0.tflite",
      "alexa_v0.1.0.tflite"
    ],
    "Threshold": 0.5,
    "CustomWakeWords": {
      "Elusiaa": "elusiaa_custom.tflite",
      "Kaelia": "kaelia_custom.tflite",
      "Maera": "maera_custom.tflite",
      "Khavia": "khavia_custom.tflite",
      "Auerlis": "auerlis_custom.tflite",
      "Nyxari": "nyxari_custom.tflite"
    }
  },
  "LLM": {
    "EndpointUrl": "http://localhost:8080/v1/chat/completions",
    "Model": "local-model",
    "UseJsonSchemaResponseFormat": true
  },
  "Whisper": {
    "Provider": "faster-whisper",
    "EndpointUrl": "http://localhost:8000/transcribe"
  },
  "TTS": {
    "EndpointUrl": "http://localhost:8100/v1/audio/speech",
    "Model": "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice"
  }
}
```

**Speech Flow with Wake Word Detection:**
1. Microphone captures audio ? NAudio
2. Audio chunks sent to VAD (Voice Activity Detection)
3. Active speech sent to openWakeWord endpoint
4. Wake word detected ? triggers Whisper transcription
5. Transcription sent to LLM
6. Response generated and spoken via TTS

### 5. Configuration System

New configuration loader supports JSON-based configuration with environment variable overrides.

**Priority Order:**
1. Environment variables (highest priority)
2. `appsettings.json` configuration
3. Default values (lowest priority)

**Environment Variables:**
- `SIX_STARS_LLM_ENDPOINT` - LLM endpoint URL
- `SIX_STARS_LLM_MODEL` - LLM model name
- `SIX_STARS_WHISPER_ENDPOINT` - Whisper endpoint URL
- `SIX_STARS_HOTWORD_ENDPOINT` - OpenWakeWord endpoint URL
- `SIX_STARS_TTS_ENDPOINT` - TTS endpoint URL
- And more...

**Debug Logging:**
Configuration loading is logged to Debug output:
```
[Config] Loaded configuration from U:\six-stars-solution\bin\Debug\appsettings.json
[LLM] Configured endpoint: http://localhost:8080/v1/chat/completions
[LLM] Model: local-model
[OpenWakeWord] Configured endpoint: http://localhost:9000/detect
[OpenWakeWord] Provider: openWakeWord
[OpenWakeWord] Threshold: 0.5
[OpenWakeWord] Loaded 6 models
```

## File Changes

### New Files

1. **`src/Wds.SixStars.Ui/appsettings.json`**
   - Main configuration file for all services
   - Copied to output directory on build

2. **`src/Wds.SixStars.Ui/Configuration/AppConfiguration.cs`**
   - Configuration model classes
   - Strongly-typed configuration structure

3. **`src/Wds.SixStars.Ui/ViewModels/StateChangeTracker.cs`**
   - Tracks relationship state changes
   - Provides running totals and summaries

4. **`scripts/openwakeword_server.py`**
   - Reference implementation of openWakeWord HTTP server
   - Flask-based REST API for wake word detection

5. **`docs/OpenWakeWord-Setup.md`**
   - Comprehensive setup guide for openWakeWord
   - Configuration reference
   - Troubleshooting tips

### Modified Files

1. **`src/Wds.SixStars.Ui/ViewModels/MainViewModel.cs`**
   - Added state tracking integration
   - Enhanced debug output generation
   - Configuration loading from JSON
   - Debug logging for all services
   - Improved status messages for LLM communication

2. **`src/Wds.SixStars.Ui/Wds.SixStars.Ui.csproj`**
   - Added `appsettings.json` to build output

3. **`src/Wds.SixStars.Api/Screen/ScreenAwarenessLoop.cs`**
   - Fixed PeriodicTimer compatibility issue (.NET Standard 2.1)

## Usage Guide

### Running with LLM

1. Start your LLM server (e.g., Ollama, llama.cpp, etc.)
2. Update `appsettings.json` with your endpoint:
   ```json
   "LLM": {
     "EndpointUrl": "http://localhost:8080/v1/chat/completions",
     "Model": "qwen2.5:7b-instruct"
   }
   ```
3. Run Six Stars application
4. Type in the Player Text field and click "Send"
5. Watch the status bar for "Sending request to AI LLM..."
6. See the response in Star Spoken Output
7. Check Debug JSON panel for detailed request/response data
8. Check Visual Studio Debug Output for state change totals

### Running with OpenWakeWord

1. Install dependencies:
   ```bash
   pip install openwakeword flask numpy
   ```

2. Start the openWakeWord server:
   ```bash
   python scripts/openwakeword_server.py
   ```

3. Update `appsettings.json`:
   ```json
   "OpenWakeWord": {
     "EndpointUrl": "http://localhost:9000/detect"
   }
   ```

4. Click "Start Listen" in Six Stars
5. Speak a Star's name (e.g., "Elusiaa")
6. The wake word detector will trigger
7. Continue speaking your message
8. Whisper transcribes your speech
9. LLM processes and responds

### Viewing State Totals

**In UI:**
- Debug JSON panel shows `sessionTotals` and `combinedTotals`

**In Visual Studio:**
- View ? Output ? Show output from: Debug
- State changes logged after each interaction
- Combined totals display all Stars' changes

## Testing

### Test LLM Integration

1. Ensure LLM server is running
2. Type: "Elusiaa, tell me a story while I work."
3. Click "Send"
4. Verify:
   - Status shows LLM communication messages
   - Star response appears
   - Debug JSON shows request and response
   - Debug Output shows state changes

### Test State Tracking

1. Send 3-5 messages to Elusiaa
2. Send 2-3 messages to Kaelia
3. Check Debug JSON ? `combinedTotals`
4. Verify:
   - Each Star has separate totals
   - Combined totals aggregate all changes
   - Positive/negative counts are accurate

### Test OpenWakeWord

1. Start openWakeWord server
2. Click "Start Listen"
3. Say "Elusiaa, what are you working on?"
4. Verify:
   - Wake word detected
   - Transcription appears
   - Response generated

## Architecture

### Configuration Loading Flow
```
Application Start
    ?
Load appsettings.json
    ?
Check Environment Variables (override)
    ?
Create Service Instances
    ?
Log Configuration to Debug
```

### Request Processing Flow
```
User Input
    ?
Create StarInteractionRequest
    ?
Send to LLM (via SixStarsRuntime)
    ?
Receive StarInteractionResponse
    ?
Track State Changes (StateChangeTracker)
    ?
Generate Enhanced Debug Output
    ?
Log to Debug Window
    ?
Update UI
    ?
Generate TTS Voice
```

### Speech Processing Flow
```
Microphone Audio
    ?
NAudio Capture (PCM chunks)
    ?
VAD (Voice Activity Detection)
    ?
Send to OpenWakeWord
    ?
Wake Word Detected?
    ? Yes
Send to Whisper
    ?
Transcription Ready
    ?
Send to LLM (same as text flow)
```

## Performance Considerations

- **State Tracking**: Minimal overhead, in-memory dictionary
- **Debug Output**: Only generated when response received
- **Configuration**: Loaded once at startup
- **Logging**: Uses `System.Diagnostics.Debug` (removed in Release builds)

## Future Enhancements

Potential areas for expansion:
- Persist state totals to disk for session continuity
- Export state statistics to CSV/JSON
- Real-time graphs of relationship progression
- Wake word confidence visualization
- Multiple wake word model support per Star
- Custom wake word training UI

## Troubleshooting

### LLM Not Responding

1. Check Debug Output for endpoint configuration
2. Verify LLM server is running: `curl http://localhost:8080/v1/models`
3. Check firewall settings
4. Try MockStarAgent by removing LLM endpoint

### State Totals Not Updating

1. Verify interactions are completing successfully
2. Check Debug JSON for `sessionTotals` field
3. Ensure no exceptions in Debug Output

### OpenWakeWord Not Detecting

1. Check server is running: `curl http://localhost:9000/health`
2. Lower threshold in `appsettings.json` (try 0.3)
3. Check Debug Output for detection logs
4. Verify audio format is correct (16kHz mono PCM)

## Support

For issues or questions:
- Check Debug Output window for detailed logs
- Review `docs/OpenWakeWord-Setup.md` for wake word setup
- Examine Debug JSON panel for request/response details
- Verify configuration in `appsettings.json`
