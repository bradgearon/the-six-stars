Six Stars local model folder
============================

All local sidecar services share this root folder. Whisper and Qwen TTS can
auto-download into models/hf, or you can manually place model folders here.

The default LLM path is back to LM Studio at:

  http://localhost:8080/v1/chat/completions

That is intentionally external because LM Studio's GGUF/accelerated runtime is
much faster for the current Six Stars dialogue/story workflow than the Python
Transformers Gemma server. Start LM Studio and load a model before using the WPF
app. The app sends model="local-model" by default, which LM Studio accepts when
one model is loaded.

Recommended layout:

  models/
    hf/
      hub/                         Hugging Face cache used by sidecar services
    llm/
      gemma-4-E2B-it/              Optional local Gemma folder if testing Python LLM
      gemma-3-4b-it/               Optional alternate Gemma folder
    tts/
      qwen3-tts-0.6b/              Optional Qwen/Qwen3-TTS-12Hz-0.6B-Base files
      qwen3-tts-1.7b/              Optional larger Qwen TTS files
    asr/
      faster-whisper-tiny.en/      Optional faster-whisper model folder
      faster-whisper-small/
      faster-whisper-medium/
    voice_clones/                  Optional future voice clone references

Environment variables:

  SIX_STARS_MODELS_ROOT            Default: ./models
  HF_HOME                          Default: ./models/hf
  HF_HUB_CACHE                     Default: ./models/hf/hub

LLM defaults:
  SIX_STARS_START_LOCAL_LLM        Default: false
  SIX_STARS_LLM_ENDPOINT           Default: http://localhost:8080/v1/chat/completions
  SIX_STARS_LLM_MODEL              Default: local-model

Optional Python Gemma server test mode:
  set SIX_STARS_START_LOCAL_LLM=true
  set LOCAL_LLM_MODEL=google/gemma-4-E2B-it
  set LOCAL_LLM_PORT=8200

Whisper:
  WHISPER_MODEL                    Default: tiny.en
  WHISPER_MODEL_PATH               Explicit local folder path. Wins over aliases.
  WHISPER_DOWNLOAD_ROOT            Default: ./models/asr

Qwen TTS:
  QWEN_TTS_MODEL_PATH              Default: Qwen/Qwen3-TTS-12Hz-0.6B-Base
  QWEN_TTS_LOCAL_PATH              Explicit local folder path. Wins over aliases.

OpenWakeWord custom model files are no longer part of the normal desktop voice
path, but if you still test that old path, place .tflite/.onnx files here or set
OWW_MODEL_DIR.
