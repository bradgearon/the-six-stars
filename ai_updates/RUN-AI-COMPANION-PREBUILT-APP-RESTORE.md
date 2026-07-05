# run-ai-companion Prebuilt App Restore

`run-ai-companion.cmd` is restored to the normal full companion behavior. It now calls `launch.cmd`, which prefers the redistributable/prebuilt engine launcher at `redist\six-stars-launcher.exe` and falls back to the .NET launcher project in development zips.

Use `run-ai-services.cmd` for the services-only/debug flow. That script keeps using the repo `.venv` for Python ASR/TTS and the llama.cpp engine for the local LLM.

Scripts after this update:

- `run-ai-companion.cmd` — full prebuilt/integrated app launcher.
- `run-ai-services.cmd` — services-only venv/source Python launcher.
- `launch-ui-only.cmd` — UI-only launcher after services are already running.
- `run-ai-companion-with-ui.cmd` — dev/source flow: services window plus UI.
- `run-ai-engine-services.cmd` — services-only frozen engine EXE testing.
