# Benaveil VocalSynth voice processing

The desktop now targets **.NET 9 Windows x64** and references **NAudio 3.1.1-preview.1** and **NAudio.Vst3 3.1.1-preview.1** directly. Build with a .NET 9-capable SDK/Visual Studio. Framework-dependent desktop installations need the .NET 9 Windows Desktop Runtime (x64). The launcher stays on .NET 8 and now discovers the desktop's `net9.0-windows` output folder.

## Configuration and control

The checkbox **Disable Benaveil voice processing** is at the bottom of the right-hand Quick Actions pane. Its value is immediately saved as `BenaveilVoiceProcessing.Disabled` in the running app's `appsettings.json`, and survives restart even if a local override contains an older copy of that flag.

```json
"BenaveilVoiceProcessing": {
  "Disabled": false,
  "PluginPath": "C:\\Program Files\\Common Files\\VST3\\iZotope\\VocalSynth 2.vst3",
  "PresetPath": "Content\\VocalSynth\\Benaveil.vstpreset",
  "SampleRate": 48000,
  "BlockSize": 1024,
  "MaxTailSeconds": 5
}
```

The supplied preset is copied to build and publish output. VocalSynth itself must already be installed and activated on the Windows machine; it is not redistributed in this source archive. Both bare `.vst3` DLLs and folder bundles are supported by NAudio. Relative paths resolve beside the executable.

When enabled, a dedicated STA background thread loads the module, selects the exact plugin class recorded in the preset, and applies the preset. A status message shows loading, ready, or the load error. The native thread has its own Windows message pump. When disabled, the plugin is unloaded after any active native processing call finishes. An active processed line is cancelled rather than changing its PCM format mid-line. Subsequent Benaveil lines use the original clone voice.

## Audio flow

Only a case-insensitive `Benaveil` StarId activates the effect. Other Stars bypass it without loading additional plugin instances. Enabled processing errors are surfaced; there is no silent dry-voice fallback.

The playback decorator processes desktop WAVs and streaming chunks before the existing Speak as Star microphone router, authoring writer, generic archive, and speaker endpoint. Typed and recorded Speak as Star, regular desktop voice, and cached audio played through that endpoint use this path. Remote/mobile generation events and raw TTS caches remain unchanged.

Chunks are converted to the plugin's sample rate and negotiated mono/stereo input, passed through **Vst3EffectSampleProvider**, and returned as real PCM WAV bytes. The effect instance and source provider survive between chunks. The source is read only for the frames actually available, so a pause in chunk delivery does not become a false end-of-stream. At line completion, the effect tail is rendered with a configurable time cap. Plugin latency is removed once across the whole line, not once per chunk. Each subsequent line starts with a fresh preset-loaded instance to prevent carryover from previous performances.

Processed authoring exports retain VocalSynth's sample rate and output channel count at 16-bit PCM; stereo is not downmixed to mono. Existing dry-voice authoring remains at 24 kHz mono. Export filenames and take numbering remain unchanged.

This upgrade retains the existing DirectSound streaming backend and compatible legacy WASAPI APIs. It does not establish a fix for the previously reported DirectSound device error.

## Verification

- Full solution compiled using .NET SDK 9.0.317 with Windows targeting enabled.
- Routing checks passed for non-Benaveil bypass, disabled processing, single-file body/tail concatenation, immutable cached source audio, streaming order and shared sessions, error propagation, cancellation, and session cleanup.
- XAML and appsettings JSON parsed successfully; the supplied VST preset header/chunk table is valid and the preset was copied intact to build output.
- VocalSynth's native DLL, activation, live sound, and Windows device routing cannot be exercised in the Linux editing environment. Verify the preset's actual sound on the target machine.

Run the portable routing checks with:

```powershell
dotnet run --project tests/BenaveilVoiceChecks/BenaveilVoiceChecks.csproj
```

On Windows: build the solution, wait for the ready message, render a multi-chunk Benaveil line with Authoring mode enabled, listen to the saved WAV including its tail, then check Disable Benaveil voice processing and compare the dry voice. Restart to verify the persisted checkbox. Test a different Star to confirm its voice is unchanged.
