# Generated Voice WAV Save Update

The WPF desktop harness now saves every generated Star voice payload before playback.

## Default behavior

- Enabled by default.
- Saves directly into the process working directory.
- When you run from the solution folder with `dotnet run --project ...`, this is usually `U:\six-stars-solution`.
- WebSocket streaming saves each streamed audio chunk as its own WAV file.
- HTTP `/v1/audio/speech` fallback saves the full generated WAV.
- A matching `.txt` sidecar is saved with StarId, VoiceId, source, bytes, and spoken/chunk text.

## File names

Files use this format:

```text
sixstars-YYYYMMDD-HHMMSS-fff-###-StarId-VoiceId-Source.wav
sixstars-YYYYMMDD-HHMMSS-fff-###-StarId-VoiceId-Source.txt
```

Example:

```text
sixstars-20260702-132455-021-001-Elusiaa-elusiaa-streaming-websocket.wav
sixstars-20260702-132455-021-001-Elusiaa-elusiaa-streaming-websocket.txt
```

## Config

`src/Wds.SixStars.Ui/appsettings.json`:

```json
"VoiceOutput": {
  "SaveGeneratedWavs": true,
  "GeneratedWavDirectory": "",
  "SaveMetadataText": true
}
```

`GeneratedWavDirectory` blank means use the current working directory.

## Environment overrides

CMD:

```bat
set SIX_STARS_SAVE_VOICE_WAVS=true
set SIX_STARS_GENERATED_WAV_DIR=U:\six-stars-solution
set SIX_STARS_SAVE_VOICE_METADATA=true
```

PowerShell:

```powershell
$env:SIX_STARS_SAVE_VOICE_WAVS = "true"
$env:SIX_STARS_GENERATED_WAV_DIR = "U:\six-stars-solution"
$env:SIX_STARS_SAVE_VOICE_METADATA = "true"
```

To disable saving:

```bat
set SIX_STARS_SAVE_VOICE_WAVS=false
```
