# API/provider access for The Last Cat: the Six Stars

The standalone app supports two service modes.

## Recommended production mode: Cloudflare proxy

The phone does **not** need the Google/Groq/DashScope provider keys.

Configure:

```json
{
  "Proxy": {
    "Enabled": true,
    "BaseUrl": "https://api.your-domain.example",
    "AccessToken": "YOUR_APP_PROXY_TOKEN"
  }
}
```

The Worker project is at `cloudflare/six-stars-ai-proxy`. Provider billing keys are stored as Cloudflare Worker secrets there.

The `AccessToken` is only an app-to-proxy gate. Because the APK must possess it, treat it as a temporary/casual-abuse control; later production hardening should issue short-lived app sessions after Play Integrity or another attestation mechanism. The provider billing keys never need to be shipped in the APK.

## Direct developer mode

For a private/local developer build:

1. Copy `app/src/main/assets/standalone-services.local.json.example` to `app/src/main/assets/standalone-services.local.json`.
2. Keep `Proxy.Enabled=false`.
3. Fill in `LLM.ApiKey` with the Google AI Studio key.
4. Fill in `ASR.ApiKey` with the Groq key.
5. Fill in `TTS.ApiKey` with the Alibaba DashScope key.
6. Build/install the standalone APK.

The real `.local.json` file is git-ignored.

The future Settings screen will write a device-private override at `<app files>/config/standalone-services.local.json`; runtime settings already support that location now.

Developer warning: direct provider keys packaged into an APK can be extracted. Direct mode is for development/private installs; use Proxy mode for distributed builds.
