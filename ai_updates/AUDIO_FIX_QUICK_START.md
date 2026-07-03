# Quick Fix: Test if It's Using Streaming vs HTTP TTS

Since you know the audio is being generated properly (24kHz WAV with voice), but nothing is playing, we need to determine which pipeline is actually being used.

## Step 1: Check Which TTS Mode Is Active

Add this to see which mode is running. Open the **Debug** output window in Visual Studio (View ? Output, then select "Debug" from the dropdown).

When you send a message and she generates a response, look for:
```
[TTS] Using streaming WebSocket TTS: True
```
or
```
[TTS] Using streaming WebSocket TTS: False
```

## Step 2a: If Using Streaming (WebSocket) TTS

You should see these messages when she responds:
```
[WebSocketTtsVoiceGenerator] Connected successfully
[WebSocketTtsVoiceGenerator] Sending generation request ...
[WebSocketTtsVoiceGenerator] Audio chunk 1/X: NNN bytes
[StreamingStarVoiceResponsePipeline] Playing chunk 1/X (NNN bytes)
[NaudioPlayback] Playing NNN bytes
[NaudioPlayback] WAV Format: 24000Hz, 16bit, 1ch, Duration: X.XXs
```

**If you DON'T see "Playing chunk"**: The playback loop isn't starting. 

**Quick Fix:** Disable WebSocket TTS temporarily in `appsettings.json`:
```json
"WebSocketTTS": {
  "Enabled": false
}
```

This will force it to use HTTP TTS mode which is simpler.

## Step 2b: If Using HTTP TTS (Regular)

You should see:
```
[TTS] Configured endpoint: http://localhost:8100/v1/audio/speech
[NaudioPlayback] Playing NNN bytes
[NaudioPlayback] WAV Format: 24000Hz, 16bit, 1ch, Duration: X.XXs
```

**If you see "Playing..." but no sound:**

### Test 1: Check Audio Device
Windows might be routing to wrong output. Press Windows key, type "Sound settings", check which device is default for both "Output" and "Communications".

### Test 2: Check Volume
- Right-click volume icon in taskbar
- Open Volume Mixer
- Check if your app is muted or volume is at 0

### Test 3: Force WaveOutEvent (Fallback)

Edit `src\Wds.SixStars.Ui\Audio\NaudioWavePlaybackEndpoint.cs`, line 32:

**Change from:**
```csharp
try
{
    await PlayWithWasapiAsync(wav, voiceAudio.MimeType, cancellationToken).ConfigureAwait(false);
    System.Diagnostics.Debug.WriteLine("[NaudioPlayback] ? WASAPI playback completed successfully.");
    return;
}
catch (OperationCanceledException)
{
    System.Diagnostics.Debug.WriteLine("[NaudioPlayback] Playback cancelled before/while audio could be heard.");
    throw;
}
catch (Exception ex)
{
    System.Diagnostics.Debug.WriteLine("[NaudioPlayback] WASAPI playback failed, falling back to WaveOutEvent: " + ex.Message);
}

await PlayWithWaveOutEventAsync(wav, voiceAudio.MimeType, cancellationToken).ConfigureAwait(false);
```

**To:**
```csharp
// TEMPORARILY skip WASAPI and force WaveOutEvent
System.Diagnostics.Debug.WriteLine("[NaudioPlayback] FORCING WaveOutEvent for testing...");
await PlayWithWaveOutEventAsync(wav, voiceAudio.MimeType, cancellationToken).ConfigureAwait(false);
```

This forces the simpler audio API which is more reliable.

## Step 3: Emergency Fallback - Save to File

If nothing works, at least verify the WAV file is valid by saving it:

Add this temporary code to `NaudioWavePlaybackEndpoint.cs`, line 30 (right after the debug line showing WAV format):

```csharp
// TEMPORARY: Save to file to verify WAV is valid
string tempPath = Path.Combine(Path.GetTempPath(), $"star_audio_{DateTime.Now:yyyyMMddHHmmss}.wav");
File.WriteAllBytes(tempPath, wav);
System.Diagnostics.Debug.WriteLine($"[NaudioPlayback] SAVED TO: {tempPath}");
```

Then:
1. Run the app
2. Send a message
3. Look in Debug output for the file path
4. Open that WAV file in Windows Media Player or VLC
5. If it plays there, the WAV is fine and it's a playback API issue

## Most Likely Issue

Since you confirmed the audio IS generated and has voice, this is almost certainly:
1. **WebSocket streaming pipeline not calling playback** (fix: disable WebSocket TTS)
2. **WASAPI can't open the audio device** (fix: force WaveOutEvent)
3. **Volume/device routing** (fix: check Windows sound settings)

Try these in order and let me know which Debug messages you see!
