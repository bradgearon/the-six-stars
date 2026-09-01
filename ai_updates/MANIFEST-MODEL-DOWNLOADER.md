# Manifest-driven model downloader

The native Six Stars launcher now downloads missing model weights itself instead of relying on PowerShell `Invoke-WebRequest`.

- Download implementation: `bezzad/Downloader` NuGet package, pinned to 5.9.6.
- Model URLs live in `config/models.json` beside each local model path.
- `downloadUrls` and `codecDownloadUrls` are arrays so mirrors/fallback sources can be added later without code changes.
- Downloads use multipart transfer, retries, disk-space checking, and Downloader's `.download` automatic-resume support.
- Existing completed model files are left untouched.
- `launch.cmd --download-models-only` downloads models and exits.
- `--skip-model-download` is available for an intentionally offline/manual-model launch.
- `scripts/Download-AiModels.ps1` is now a compatibility wrapper that invokes the launcher downloader.
- The native runtime installer is passed `-SkipModels`; model installation is owned by the launcher so there is only one download implementation.

Current configured files:

- `models/llm/gemma-4-E4B-it-Q6_K.gguf`
- `models/llm/mmproj-gemma-4-E4B-it-Q8_0.gguf`
- `models/asr/ggml-large-v3-turbo.bin`
- `models/tts/qwen-talker-0.6b-base-Q8_0.gguf`
- `models/tts/qwen-tokenizer-12hz-Q8_0.gguf`
- `Build-AiRuntimeRedist.ps1` now leaves the giant weights out by default; use `-BundleModels` only for an offline bundle. Small voice-clone/reference assets can still be included.
