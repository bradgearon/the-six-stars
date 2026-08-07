# Windows sleep microphone resume recovery

The desktop UI now restores voice input automatically after Windows sleep/resume instead of requiring an application restart.

## Changes

- `MainWindow` listens for `WM_POWERBROADCAST` suspend/resume events.
- On resume, `MainViewModel` asks the microphone source to rebuild the NAudio capture device.
- `NaudioMicrophoneSpeechSource` now remembers whether listening was requested and fully recreates `WaveInEvent`, its channel, cancellation source, pump task, and health timer.
- Recovery waits briefly for Windows audio devices to become available and retries up to five times.
- Unexpected NAudio device stops also trigger recovery.
- A five-second heartbeat checks whether `DataAvailable` buffers are still arriving. If a stale WaveIn handle survives sleep without raising `RecordingStopped`, it is detected and reopened automatically.
- The speech pipeline discards any partial utterance and clears a stale "Star speaking" microphone mute after the audio device is rebuilt.
- Duplicate Windows resume notifications share one in-flight recovery operation.

## Expected behavior

When the computer wakes, the UI status briefly reports that microphone capture is recovering. Always-listening voice input should resume automatically within a few seconds without restarting the UI.
