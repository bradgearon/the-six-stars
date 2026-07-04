# LM Studio auth token fix

This update keeps LM Studio as the default LLM endpoint and fixes the new LM Studio authentication flow.

## Problem

LM Studio can require an API token. When that mode is enabled, Six Stars sent requests without an `Authorization: Bearer ...` header, so LM Studio returned:

```text
401 Unauthorized: An LM Studio API token is required
```

## Fixes

- The WPF app now reads LLM auth from any of these environment variables:
  - `SIX_STARS_LLM_API_KEY`
  - `LM_API_TOKEN`
  - `LM_STUDIO_API_KEY`
- Screen awareness LLM requests use the same fallback token sources.
- `scripts/start_services.py` uses the same token sources for its `/v1/models` probe.
- `launch.cmd` and `start.cmd` map `LM_API_TOKEN` / `LM_STUDIO_API_KEY` into `SIX_STARS_LLM_API_KEY` when present.
- The LLM HTTP client now detects LM Studio auth errors and stops retrying all response_format fallbacks. It reports a direct fix message instead.

## Setup

Either disable API authentication in LM Studio, or set the token before running Six Stars:

```bat
set LM_API_TOKEN=your_lm_studio_token_here
launch.cmd
```

For Visual Studio debugging, set a User environment variable in Windows, or place the token in:

```json
"LLM": {
  "ApiKey": "your_lm_studio_token_here"
}
```

