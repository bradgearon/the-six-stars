# llama.cpp / Gemma 4 reasoning-content fix

Problem observed in the dev harness:

- llama-server returned HTTP 200.
- `message.content` was empty.
- `message.reasoning_content` contained the whole internal plan.
- `finish_reason` was `length` at 512 completion tokens.
- The client failed with `Could not find model content in local chat response`.

This update fixes that path for real-time Star chat.

## Changes

### Launcher

All llama-server runtime packs now run with bounded generation and reasoning disabled by default:

```text
--predict -1 --reasoning off --reasoning-budget 0 --reasoning-format none
```

`RuntimeSupervisor.ResolveArgs` also appends those reasoning flags if a custom manifest omits them.

### UI environment

When the launcher starts the UI, it sets:

```text
SIX_STARS_LLM_DISABLE_REASONING=true
SIX_STARS_LLM_REASONING_BUDGET=0
SIX_STARS_LLM_REASONING_FORMAT=none
SIX_STARS_LLM_SEND_REASONING_CONTROL_FIELDS=true
SIX_STARS_LLM_SPEECH_MAX_TOKENS=0
SIX_STARS_LLM_REVIEW_MAX_TOKENS=1024
```

The same reasoning controls are set for screen vision.

### OpenAI-compatible chat client

`OpenAiCompatibleChatModelClient` now sends local request-level reasoning controls to localhost/127.0.0.1 endpoints:

```json
{
  "reasoning": "off",
  "reasoning_budget": 0,
  "reasoning_format": "none"
}
```

If a local endpoint rejects those fields, the client retries the same request without them. The launcher CLI args are still the main protection when the app starts llama-server itself.

If the model still returns `reasoning_content` with empty `message.content`, the exception now clearly says to disable reasoning or fix reasoning/server stop behavior, and includes a short reasoning preview.

### Screen vision

`OpenAiCompatibleVisionScreenSummarizer` also sends the same local reasoning controls so screenshot summaries do not get trapped in `reasoning_content`.

### Model defaults

`config/models.json` now defaults to the user's Gemma 4 E4B local model:

```text
models/llm/gemma-4-E4B-it-Q6_K.gguf
models/llm/mmproj-gemma-4-E4B-it-Q8_0.gguf
```

Update those paths if your local filenames differ.

## Rebuild

```cmd
cd /d U:\six-stars-solution
build-ai-redist.cmd -Profile nvidia-cu12 -SkipRuntimeInstall
```

Then relaunch from redist.
