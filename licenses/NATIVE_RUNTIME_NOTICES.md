# Native runtime redistribution checklist

This project can package third-party native runtime components, but the build
pipeline must preserve each upstream license and any model-specific license.

Runtime components currently expected by `config/runtime-manifest.json`:

- llama.cpp — preserve the upstream llama.cpp license/notices for the exact version shipped.
- whisper.cpp / ggml — MIT; preserve the upstream license.
- qwentts.cpp / ggml — MIT; preserve the upstream license. The Qwen3-TTS model
  and audio codec have their own upstream model licenses/terms and must be
  reviewed for redistribution separately from the C++ runtime.
- NVIDIA CUDA runtime libraries — redistribute only files permitted by the
  NVIDIA CUDA Toolkit EULA/redistribution terms for the version used to build
  the pack.

The model files are separate artifacts from the runtime binaries. Review the
license/terms of the exact Gemma, Whisper, and Qwen checkpoints included in a
release before distributing them.
