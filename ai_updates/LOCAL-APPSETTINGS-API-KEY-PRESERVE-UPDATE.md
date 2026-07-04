# Local appsettings/API key preservation update

This update prevents LM Studio API tokens from being overwritten by future patch zips.

## Changes

- Added `appsettings.Local.json` overlay support in the WPF configuration loader.
- Added `src/Wds.SixStars.Ui/appsettings.Local.example.json` as a safe template.
- Added `scripts/set_lm_studio_api_key.cmd` to write the local override file.
- Updated `scripts/start_services.py` so the launcher also reads the local override token for `/v1/models` checks.
- Added root `/.history/` and `/models/` ignores to `.gitignore`.

## Usage

Run:

```bat
scripts\set_lm_studio_api_key.cmd YOUR_LM_STUDIO_TOKEN
```

The generated `appsettings.Local.json` is gitignored and overrides `appsettings.json` for both `LLM.ApiKey` and `ScreenLLM.ApiKey`.
