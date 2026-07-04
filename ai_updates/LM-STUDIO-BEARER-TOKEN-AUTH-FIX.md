# LM Studio Bearer Token Auth Fix

LM Studio Developer Server can require an API token. When that setting is enabled, it rejects requests without:

```text
Authorization: Bearer <token>
```

## What changed

- The WPF app now reads the LLM token from any of these names:
  - `SIX_STARS_LLM_API_KEY`
  - `LM_API_TOKEN`
  - `LM_STUDIO_API_KEY`
  - `LLM:ApiKey` in `appsettings.json`
- Screen LLM uses the same LM Studio token fallback.
- `launch.cmd` and `start.cmd` copy `LM_API_TOKEN` into the Six Stars env var when present.
- `scripts/start_services.py` uses the same token when checking LM Studio `/v1/models`.
- A 401 from the LLM client now gives a direct LM Studio token message.

## Use

Either disable LM Studio API key requirement, or set the token before launch:

```bat
set LM_API_TOKEN=your_lm_studio_token_here
launch.cmd
```

If launching the WPF app from Visual Studio, set `LM_API_TOKEN` in Windows User Environment Variables or paste the token into `src/Wds.SixStars.Ui/appsettings.json` under `LLM.ApiKey` and `ScreenLLM.ApiKey`.
