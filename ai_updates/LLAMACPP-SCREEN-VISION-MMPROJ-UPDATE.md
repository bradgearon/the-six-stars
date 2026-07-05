# llama.cpp Screen Vision / mmproj Update

Updated the local screen-awareness path for llama-server multimodal image support.

Changes:

- Added `models.vision` to `config/models.json`.
- The launcher now checks `models.vision.enabled` and `models.vision.localPath`.
- If the matching mmproj GGUF exists, the launcher appends `--mmproj <path>` to `llama-server.exe` args.
- If the mmproj is missing, the launcher disables screen vision for that launch by setting `SIX_STARS_SCREEN_LLM_ENABLED=false` so normal Star chat still works.
- The UI now honors `SIX_STARS_SCREEN_LLM_ENABLED`, `SIX_STARS_SCREEN_LLM_MAX_TOKENS`, and `SIX_STARS_SCREEN_LLM_TIMEOUT_SECONDS`.
- The screen summarizer sends the canonical OpenAI-compatible image payload:

```json
{
  "messages": [
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "..." },
        { "type": "image_url", "image_url": { "url": "data:image/jpeg;base64,...", "detail": "low" } }
      ]
    }
  ]
}
```

- It also retries with a compatibility fallback where `image_url` is the data URL string.
- Desktop captures are downscaled to max width 1024 before JPEG/base64 so the POST body is smaller and faster for local VLMs.

To enable screen vision, put the matching projector here or edit the path:

```text
models/llm/gemma-e4b-it-mmproj.gguf
```

Then rebuild and relaunch with the redist launcher.
