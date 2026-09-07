# Android mic monitor + desktop Speak as Star

## Android

- Added a persisted **Echo microphone while recording** toggle to the cat/companion console in both Android app variants.
- While Talk capture is active, microphone PCM is also written to a low-latency streaming `AudioTrack`.
- Monitor writes are non-blocking: if playback falls behind, monitor samples are dropped instead of delaying microphone capture and creating an ever-growing echo.
- The monitor uses `USAGE_MEDIA` so device/screen-recording workflows can capture the echoed line as app/media audio where Android capture policy permits it.
- Monitoring is off by default and is released immediately when capture stops or the toggle is disabled.

## Desktop

- Added **Speak as Star (Record)** beside the existing push-to-talk controls.
  - First click starts the same microphone/Whisper capture path as normal push-to-talk.
  - The button changes to **Send / Speak as Star** while recording.
  - The dedicated Cancel button discards the recording.
  - On Send, the Whisper transcript bypasses the LLM/conversation runtime and is spoken directly with the selected Star's configured TTS/voice clone.
- Added **Speak as Star** under the main textbox.
  - It captures the current textbox text, clears the textbox, and sends the text directly to selected-Star TTS without creating an LLM conversation turn.
- Direct recorded playback snapshots the selected Star when recording starts and restores the prior speech/wake settings after send, cancel, or failure.

## Validation in this environment

- WPF XAML and Android companion-console XML parse successfully.
- C# delimiter/binding smoke checks pass.
- `javac` reports no syntax-like errors in the changed Java files (full Android symbols are unavailable without the Android/Gradle classpath).
- A full Android Gradle build could not be run because the wrapper needs to download Gradle 9.6.1 and this execution environment cannot resolve `services.gradle.org`.
- A desktop .NET build could not be run because this execution environment does not have the .NET SDK/MSBuild installed.
