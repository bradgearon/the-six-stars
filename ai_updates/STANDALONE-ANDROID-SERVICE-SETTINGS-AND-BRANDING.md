# Standalone Android hosted-service settings and branding

## App identity

The standalone relationship-builder APK is branded **The Last Cat: the Six Stars** and remains a separate Android application (`com.wds.sixstarsstandalone`) so it can be installed alongside the bridge-based game companion app.

The launcher icon is sourced from the supplied Last Cat pixel-art icon and is packaged into Android mipmap density resources.

## Where API keys go

The committed defaults file is:

`android/six-stars-standalone/app/src/main/assets/standalone-services.json`

Do **not** put long-lived secrets in that committed defaults file. For a developer/test build, copy:

`android/six-stars-standalone/app/src/main/assets/standalone-services.local.json.example`

to:

`android/six-stars-standalone/app/src/main/assets/standalone-services.local.json`

and set:

```json
{
  "LLM": { "ApiKey": "YOUR_GOOGLE_AI_STUDIO_KEY" },
  "ASR": { "ApiKey": "YOUR_GROQ_KEY" },
  "TTS": { "ApiKey": "YOUR_DASHSCOPE_KEY" }
}
```

The repository already ignores `*.local.json`, so this local secrets file should not be committed accidentally.

At runtime the app merges configuration in this order, with later sources winning:

1. bundled `standalone-services.json` defaults;
2. optional bundled `standalone-services.local.json` developer override;
3. optional device-private `<app files>/config/standalone-services.local.json` runtime override.

The third location is the intended storage target for the future Settings screen. It also makes the service configuration independent of the canonical Star state file.

## Important APK-key security note

A key bundled in `standalone-services.local.json` is convenient for local development, but anything shipped inside an APK can be extracted by a sufficiently motivated user. Production distribution should therefore use provider restrictions/quotas and, if stronger secret protection is required, an ephemeral-token/proxy design. A future Settings UI can keep user-supplied keys in private app storage, but a client-held provider key is still ultimately present on the device while in use.

## State/import contract remains unchanged

This branding/settings update does not alter the canonical Star-state or memory semantics. The rules in `STANDALONE-ANDROID-STATE-IMPORT-COMPATIBILITY-CONTRACT.md` remain authoritative: deterministic relationship updates, state fields, profile semantics, memory behavior, background review contracts, and import materialization must stay aligned with the desktop/game implementation.
