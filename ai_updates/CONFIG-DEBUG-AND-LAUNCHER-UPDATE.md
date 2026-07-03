# Config, Debug, and Launcher Update

- `VoiceOutput.SaveGeneratedWavs` now defaults to `false` in `appsettings.json`.
- You can enable generated WAV archiving with `SIX_STARS_SAVE_GENERATED_WAVS=true` or the config flag.
- Legacy `SIX_STARS_SAVE_VOICE_WAVS` still works.
- The OpenAI-compatible LLM client now logs the formatted request, raw HTTP response, and extracted model content to the WPF debug window before Star JSON parsing.
- `start.cmd` now calls `scripts/start_services.py` correctly.
- `scripts/start_services.py` now starts managed local Python services quietly, writes noisy server output to `logs/services/*.log`, and prints a clean process/PID table.
