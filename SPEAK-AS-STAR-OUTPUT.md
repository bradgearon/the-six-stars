# Speak as Star output

Both typed Speak as Star and Speak as Star (Record) use these controls:

- **Enable microphone output** mirrors generated WAV chunks to a virtual audio cable while normal desktop playback continues.
- **Authoring mode (save WAV)** saves the entire completed line as a single PCM WAV at 24 kHz, 16-bit mono. Output is beside the running executable: `Authoring/StarName/speech_words.wav`. For example, `Authoring/Elusiaa/Welcome_home_my_Cat.wav`. Repeated takes receive `_2`, `_3`, etc. Names are sanitized and limited to 100 characters. Cancelled/failed lines discard their temporary export.

Both switches default to false and are immediately persisted in the running app's `appsettings.json`, under `VoiceOutput`. The UI-owned switches take precedence over copies in `appsettings.Local.json`. Save errors appear in the status text. Other configuration values are retained; saving rewrites JSON formatting and removes comments.

## Microphone setup

Windows capture endpoints cannot be written to. This does not inject audio into a physical microphone or install a virtual audio driver.

With an already installed VB-CABLE, set its **CABLE Output** recording endpoint as the Windows default recording device. The app resolves the corresponding **CABLE Input** playback endpoint automatically (including the named A–D cable pairs). The app's recorder now explicitly uses the Windows default capture mapper instead of device index zero. Keep speakers/headphones as your normal default playback device. Other applications should select the cable's recording endpoint to hear the Star.

For another virtual mixer, set `VoiceOutput.MicrophoneOutputDeviceId` to its Windows **render/playback endpoint ID**, not its capture endpoint ID. This is an explicit routing override; the mixer must feed that output into the desired microphone stream. A physical microphone requires an external mixer route if you want to combine your live voice with generated Star speech. With the cable itself selected as default recording input, recording hears the cable rather than your physical microphone.

Unsupported automatic routing fails with a visible message before speech generation. No arbitrary speakers are selected as a substitute microphone. Both audio outputs use the same generated chunks, but separate device buffers mean they are not sample-synchronized; the virtual route plays chunks sequentially and can have additional inter-chunk latency. Cancellation stops the virtual route as well. These options apply only to direct Speak as Star, not normal conversations/mobile playback.

## Validation

The changes were inspected for typed/recorded convergence, streaming scope propagation, settings persistence, path sanitization, chunk ordering, cancellation cleanup and single-header WAV export. JSON and XAML were parsed, and the resulting ZIP was checked. This editing environment has no .NET SDK or Windows audio hardware: compilation and live device playback remain unverified.

On Windows, build `src/Wds.SixStars.Ui/Wds.SixStars.Ui.csproj`, test a multi-chunk typed line and a recorded line, restart to verify both switches, export a repeated line to verify take numbering, and cancel a longer line. Verify cable input using another recorder; also verify that normal conversations do not export or route to the cable.
