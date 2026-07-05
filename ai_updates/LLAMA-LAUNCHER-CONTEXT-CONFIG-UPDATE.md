# Llama.cpp launcher context and output budget update

This update makes the redist launcher safer for local llama.cpp/Gemma use.

## Runtime manifest

`config/runtime-manifest.json` is the launcher config for local engines. The LLM packs now start llama-server with:

```text
--ctx-size 16384 --predict 512
```

Change `--ctx-size` there if you want to test 32768 later. Do not use huge prompt sizes as a memory store; keep large state in JSON and send selected context slices.

## UI LLM defaults

The launcher also supplies these environment defaults to the WPF UI:

```text
SIX_STARS_LLM_USE_JSON_SCHEMA=false
SIX_STARS_LLM_MAX_TOKENS=768
SIX_STARS_LLM_SPEECH_MAX_TOKENS=512
SIX_STARS_LLM_REVIEW_MAX_TOKENS=768
SIX_STARS_LLM_STORY_PLAN_MAX_TOKENS=3072
```

This prevents llama-server from generating until the whole context window is consumed. It also avoids strict llama.cpp json_schema grammar issues with Gemma special tokens.

## Redist launch command

The generated `redist/Launch Six Stars AI Companion.cmd` now runs:

```cmd
six-stars-launcher.exe --skip-install
```

Use install/build scripts for setup; use the launcher command to start already-built engines and UI.
