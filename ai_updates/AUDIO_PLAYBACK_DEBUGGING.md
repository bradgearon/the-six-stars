# Audio Playback Debugging Guide

## Issue: Generated audio (24kHz WAV) isn't playing

### Diagnostic Steps

Run the app and check the Debug output window for these specific messages:

#### 1. Check if WebSocket TTS is being used
Look for:
```
[TTS] Using streaming WebSocket TTS: True
```

If you see `False`, streaming isn't enabled. Check:
- `appsettings.json` ? `WebSocketTTS.Enabled` should be `true`
- Environment variable `SIX_STARS_USE_STREAMING_TTS` if set

#### 2. Check if audio chunks are being received
Look for:
```
[WebSocketTtsVoiceGenerator] Audio chunk 1/3: 22050 bytes, text='...'
[StreamingStarVoiceResponsePipeline] Audio chunk 1/3 received (22050 bytes)
```

If you DON'T see these, the WebSocket server isn't sending audio.

#### 3. Check if playback is being attempted
Look for:
```
[StreamingStarVoiceResponsePipeline] Playing chunk 1/3 (22050 bytes)
[StreamingStarVoiceResponsePipeline] Calling PlayWavAsync for chunk 1...
[NaudioPlayback] Playing 22050 bytes (audio/wav) for Elusiaa/elusiaa-dev.
[NaudioPlayback] WAV Format: 24000Hz, 16bit, 1ch, Duration: 2.45s
```

If you see "Playing..." but NOT "WAV Format:", NAudio can't read the WAV header.

#### 4. Check for playback completion
Look for:
```
[NaudioPlayback] ? WASAPI playback completed successfully.
```
OR
```
[NaudioPlayback] ? WaveOutEvent playback completed successfully.
```

If you DON'T see this completion message, playback is hanging or failing silently.

### Common Issues & Fixes

#### Issue 1: WebSocket Not Connecting
**Symptoms:**
- No audio chunks received
- Connection errors in Debug output

**Fix:**
1. Make sure Python server is running: `python scripts\websocket_tts_server.py`
2. Check firewall isn't blocking port 8100
3. Verify `appsettings.json` ? `WebSocketTTS.WebSocketUrl` = `"ws://localhost:8100/tts/stream"`

#### Issue 2: Audio Generated But No Playback Attempt
**Symptoms:**
- See "Audio chunk received" but not "Playing chunk"
- Playback loop might not be processing chunks

**Fix:**
Check if playback observer is blocking. Temporarily disable microphone muting:
```csharp
// In MainViewModel constructor
_voiceCaptureSettings = new VoiceCaptureSettings
{
    ...
    MuteInputWhileStarSpeaking = false  // Change to false temporarily
};
```

#### Issue 3: NAudio Can't Read WAV Format
**Symptoms:**
- See "Playing..." but no "WAV Format:" message
- Exception about invalid WAV header

**Fix:**
The Python server might be sending invalid WAV data. Check server output for errors.

#### Issue 4: Playback Starts But No Sound
**Symptoms:**
- See "Playing..." and "WAV Format: 24000Hz..."
- But no completion message and no sound

**Possible Causes:**
1. **Volume muted** - Check Windows volume mixer
2. **Wrong audio device** - WASAPI using wrong output
3. **Playback hanging** - Task never completes

**Fix for audio device:**
Add this debug code to `NaudioWavePlaybackEndpoint.cs` line 53:
```csharp
using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
Debug.WriteLine($"[NaudioPlayback] Using audio device: {device.FriendlyName}");
```

**Fix for playback hanging:**
Check if there's a deadlock. Look for NAudio exceptions in Debug output.

#### Issue 5: Interruption Manager Canceling Playback
**Symptoms:**
- See "Playback cancelled" immediately
- Or "User interrupted Star's speech" without actually speaking

**Fix:**
Temporarily disable interruption in `MainViewModel` constructor:
```csharp
_interruptionManager = new SpeechInterruptionManager(new SpeechInterruptionOptions
{
    EnableInterruption = false,  // Disable temporarily
    ...
});
```

### Quick Test: Use HTTP TTS Instead

To bypass WebSocket complexity and test basic audio playback:

1. **Disable WebSocket TTS** in `appsettings.json`:
   ```json
   "WebSocketTTS": {
     "Enabled": false,
     ...
   }
   ```

2. **Ensure HTTP TTS endpoint is configured**:
   ```json
   "TTS": {
     "EndpointUrl": "http://localhost:8100/v1/audio/speech",
     ...
   }
   ```

3. **Run the app**
   - Debug output should show: `[TTS] Using streaming WebSocket TTS: False`
   - This will use the regular HTTP TTS endpoint and `StarVoiceResponsePipeline`

If HTTP TTS plays audio but WebSocket TTS doesn't, the issue is in the streaming pipeline.

### Advanced: Test NAudio Directly

Create a test WAV file and play it directly:

```csharp
// Add this test button in MainWindow.xaml
<Button Content="Test NAudio" Click="TestNAudio_Click" />
```

```csharp
// Add this in MainWindow.xaml.cs
private void TestNAudio_Click(object sender, RoutedEventArgs e)
{
    // Generate a 1-second 440Hz sine wave at 24kHz
    int sampleRate = 24000;
    int samples = sampleRate;
    byte[] wavData = GenerateTestWav(440, sampleRate, samples);

    var playback = new NaudioWavePlaybackEndpoint();
    var result = new VoiceSynthesisResult 
    { 
        WavAudio = wavData,
        StarId = "Test",
        VoiceId = "Test"
    };

    Task.Run(async () =>
    {
        try
        {
            await playback.PlayWavAsync(result, CancellationToken.None);
            Debug.WriteLine("[Test] Audio playback completed!");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Test] Audio playback failed: {ex}");
        }
    });
}

private byte[] GenerateTestWav(int frequency, int sampleRate, int samples)
{
    using (var ms = new MemoryStream())
    {
        using (var writer = new WaveFileWriter(ms, new WaveFormat(sampleRate, 16, 1)))
        {
            for (int i = 0; i < samples; i++)
            {
                double time = i / (double)sampleRate;
                short sample = (short)(Math.Sin(2 * Math.Pi * frequency * time) * short.MaxValue * 0.5);
                writer.WriteSample(sample / 32768f);
            }
        }
        return ms.ToArray();
    }
}
```

If this plays a beep, NAudio is working and the issue is with the WebSocket audio data.

### Check Python Server Output

The server should log:
```
[HH:MM:SS] [INFO] Client connected: 127.0.0.1:xxxxx
[HH:MM:SS] [DEBUG] Received message type: generate
[HH:MM:SS] [INFO] Split into 3 chunks
[HH:MM:SS] [DEBUG] Generating chunk 1/3: '...'
[HH:MM:SS] [INFO] Sent chunk 1/3 (22050 bytes)
```

If chunks aren't being sent, the Python TTS engine has an issue.

### Most Likely Causes (in order)

1. **WebSocket server not running** or not reachable
2. **Python server generating invalid WAV data**
3. **Playback observer muting everything**
4. **Audio device selection issue**
5. **NAudio task hanging/deadlock**

### Next Steps

1. Run the app with Debug output visible
2. Send a test message
3. Compare Debug output to the diagnostic steps above
4. Try the "Quick Test: Use HTTP TTS" workaround
5. If still failing, add the test NAudio button
6. Report which specific debug messages you see/don't see
