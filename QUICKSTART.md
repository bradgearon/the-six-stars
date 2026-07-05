# Quick Setup Guide

This guide helps you get Six Stars running with all features enabled.

## Prerequisites

- .NET 8 SDK
- Python 3.8+ (for backend services)
- Visual Studio 2022 or later

## Step 1: Clone and Build

```bash
cd U:\six-stars-solution
dotnet build
```

## Step 2: Configure Services

Edit `src/Wds.SixStars.Ui/appsettings.json` with your service endpoints.

### Option A: Use Mock Services (No Setup Required)

Leave all endpoint URLs empty in `appsettings.json`. The app will use mock implementations.

```json
{
  "LLM": { "EndpointUrl": "" },
  "Whisper": { "EndpointUrl": "" },
  "OpenWakeWord": { "EndpointUrl": "" },
  "TTS": { "EndpointUrl": "" }
}
```

### Option B: Use Real LLM (Recommended First Step)

1. Install and start Ollama:
   ```bash
   # Download from https://ollama.ai
   ollama pull qwen2.5:7b-instruct
   ollama serve
   ```

2. Update `appsettings.json`:
   ```json
   {
     "LLM": {
       "EndpointUrl": "http://localhost:11434/v1/chat/completions",
       "Model": "qwen2.5:7b-instruct"
     }
   }
   ```

### Option C: Full Setup (All Services)

#### Install Python Dependencies

```bash
pip install openwakeword flask numpy faster-whisper
```

#### Start OpenWakeWord Server

```bash
cd scripts
python openwakeword_server.py
```

Or use the quick start script:

```bash
python scripts/start_services.py
```

#### Start Other Services

See `docs/OpenWakeWord-Setup.md` for detailed setup instructions.

## Step 3: Run Six Stars

### From Visual Studio

1. Open `six-stars-solution.sln`
2. Set `Wds.SixStars.Ui` as startup project
3. Press F5

### From Command Line

```bash
cd src/Wds.SixStars.Ui
dotnet run
```

## Step 4: Test Features

### Test LLM Integration

1. In the UI, type: "Elusiaa, tell me a story while I work."
2. Click "Send"
3. Watch the status bar for LLM communication messages
4. Check the Debug JSON panel for detailed request/response
5. Open Debug Output in Visual Studio to see state change totals

**Expected Output in Debug Window:**
```
=== DETERMINISTIC STATE CHANGE ===
Star: Elusiaa
Heart Delta: +2 (Suggested: +3)
Trust Delta: +1 (Suggested: +1)
Romance Delta: +2 (Suggested: +2)
Benaveil Delta: 0 (Suggested: 0)

=== COMBINED SESSION TOTALS ===
Total Interactions: 1
Combined Heart: +2
Combined Trust: +1
Combined Romance: +2
Combined Benaveil: 0

=== PER-STAR BREAKDOWN ===
Elusiaa Session Totals:
  Interactions: 1
  Heart: +2 (1+ / 0-)
  Trust: +1 (1+ / 0-)
  Romance: +2 (1+ / 0-)
  Benaveil: 0
  Last: 14:32:15
================================
```

### Test State Tracking

1. Send multiple messages to different Stars
2. Watch the Debug JSON panel ? look for `sessionTotals` and `combinedTotals`
3. Check Visual Studio Debug Output for running totals
4. Verify positive/negative counts are accurate

### Test Speech with OpenWakeWord

1. Ensure OpenWakeWord server is running (see Step 2)
2. Click "Start Listen" in the UI
3. Say "Elusiaa, what are you doing?"
4. The wake word should trigger detection
5. Your speech will be transcribed and sent to the LLM

### Test Quick Buttons

Try the preset quick test buttons:
- "Tell Me A Story"
- "Goblin / Harpy Discernment"
- "Kind Company Test"

Each should trigger an LLM request and update state totals.

## Verification Checklist

- [ ] Application builds without errors
- [ ] `appsettings.json` is copied to output directory
- [ ] Mock services work (with empty endpoints)
- [ ] LLM requests show status messages
- [ ] Debug JSON shows request and response data
- [ ] Debug Output shows state change summaries
- [ ] State totals accumulate across interactions
- [ ] Combined totals show all Stars
- [ ] Configuration is logged to Debug Output

## Debug Output Locations

### Visual Studio Debug Output
- View ? Output
- Show output from: Debug
- See state changes after each interaction

### UI Debug JSON Panel
- Right side of the main window
- Shows full request/response with state totals
- Updates after each interaction

### Status Bar
- Bottom of the main window
- Shows current operation and state deltas

## Troubleshooting

### Build Fails

```bash
# Clean and rebuild
dotnet clean
dotnet restore
dotnet build
```

### appsettings.json Not Found

Make sure the file is in `src/Wds.SixStars.Ui/appsettings.json` and the project file includes:

```xml
<ItemGroup>
  <Content Include="appsettings.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```

### LLM Not Responding

1. Check Debug Output for "[LLM] Configured endpoint: ..."
2. Verify the endpoint is accessible: `curl http://localhost:11434/v1/models`
3. Check firewall settings
4. Try with empty endpoint to use mock

### State Totals Not Showing

1. Check Debug JSON panel for `sessionTotals` field
2. Open View ? Output ? Debug to see console output
3. Verify interactions are completing successfully

### OpenWakeWord Not Working

1. Check if server is running: `curl http://localhost:9000/health`
2. Lower the threshold in appsettings.json (try 0.3)
3. Check Debug Output for "[OpenWakeWord] Configured endpoint: ..."

## Next Steps

Once basic functionality works:

1. **Train Custom Wake Words** - See `docs/OpenWakeWord-Setup.md`
2. **Set Up Whisper** - For real speech-to-text
3. **Configure TTS** - For voice output
4. **Explore State Tracking** - Watch how relationships evolve

## Configuration Reference

All services can be configured via `appsettings.json` or environment variables.

**Environment variable override examples:**

```bash
# Windows (PowerShell)
$env:SIX_STARS_LLM_ENDPOINT="http://localhost:11434/v1/chat/completions"
$env:SIX_STARS_LLM_MODEL="qwen2.5:7b-instruct"

# Linux/Mac
export SIX_STARS_LLM_ENDPOINT="http://localhost:11434/v1/chat/completions"
export SIX_STARS_LLM_MODEL="qwen2.5:7b-instruct"
```

Environment variables take precedence over `appsettings.json`.

## Documentation

- **`docs/Enhanced-Features-Guide.md`** - Complete feature documentation
- **`docs/OpenWakeWord-Setup.md`** - Wake word detection setup
- **`scripts/openwakeword_server.py`** - OpenWakeWord server implementation
- **`scripts/start_services.py`** - Service launcher helper

## Support

Check the documentation files in the `docs/` folder for detailed information on each feature.


## Services launcher note

`run-ai-companion.cmd` is the normal full companion launcher again: it uses the integrated/prebuilt engine launcher (`redist\six-stars-launcher.exe` when built, or the launcher project fallback) to start the local engine services and open the desktop app. Use `run-ai-services.cmd` when you want services-only with the prebuilt engine EXEs, then open the UI separately with `launch-ui-only.cmd`. Use `run-ai-source-services.cmd` or `python scripts\start_services.py` for the repo `.venv` Python debug flow.


### Launcher split: prebuilt app vs venv services

- `run-ai-companion.cmd` launches the normal integrated/prebuilt-engine companion app path. It calls `launch.cmd`, which prefers `redist\six-stars-launcher.exe` and falls back to the .NET launcher project for development builds.
- `run-ai-services.cmd` is services-only and keeps the desktop app closed. It uses the prebuilt engine EXEs from `config/runtime-manifest.json` through `launch.cmd --skip-install --no-ui`.
- `launch-ui-only.cmd` opens only the desktop UI after services are already running.
- `run-ai-companion-with-ui.cmd` is the dev/source flow that starts `run-ai-source-services.cmd` in a second window and then opens the UI.


## Launcher modes updated: prebuilt app vs source services

- `run-ai-companion.cmd` is the normal full prebuilt companion launcher. It starts the integrated engine services and opens the desktop app.
- `run-ai-services.cmd` is services-only for the prebuilt engine EXEs. It calls `launch.cmd --skip-install --no-ui` and does not launch the UI.
- `launch-ui-only.cmd` opens the desktop UI after services are already running.
- `run-ai-source-services.cmd` is the Python source/venv debug helper for ASR/TTS. It calls `start.cmd`, which activates `.venv` and then runs `python scripts\start_services.py`.
- Manual Python debug flow is still supported: activate `.venv` yourself and run `python scripts\start_services.py`.

### Android Star state controls

The Android app now has a Star dropdown, met/ally toggles, and Clear History for the selected Star. These go through the API dispatcher:

```text
POST /api/mobile/dispatch
GET  /api/mobile/events?after=<sequence>
GET  /api/mobile/state
```

Use the voice journal/debug panel on Android to verify queued `SelectedStarChanged`, `StarStateChanged`, and `StarHistoryCleared` events.

## CUDA-only services

`config/runtime-manifest.json` now requires CUDA and disables CPU fallback for LLM, ASR, and TTS. Setup changes should be made in the JSON config; the environment variables you see in logs are only internal values passed from the launcher to child services.

For Python source testing:

```bat
.venv\Scripts\activate
python scripts\start_services.py
```

This path also enforces CUDA for Qwen TTS and faster-whisper. If it would run on CPU, it exits with a hosted-service message instead.
