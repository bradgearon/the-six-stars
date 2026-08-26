# API keys for The Last Cat: the Six Stars

For a local developer build:

1. Copy `app/src/main/assets/standalone-services.local.json.example` to `app/src/main/assets/standalone-services.local.json`.
2. Fill in `LLM.ApiKey` with the Google AI Studio key.
3. Fill in `ASR.ApiKey` with the Groq key.
4. Fill in `TTS.ApiKey` with the Alibaba DashScope key.
5. Build/install the standalone APK.

The real `.local.json` file is git-ignored.

The future Settings screen will write a device-private override at `<app files>/config/standalone-services.local.json`; runtime settings already support that location now.

Developer warning: keys packaged into an APK can be extracted. Use restricted/limited keys for builds you distribute.
