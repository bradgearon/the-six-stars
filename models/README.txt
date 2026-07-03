Six Stars local model folder
============================

All local sidecar services now share this root folder. You can either let the
services auto-download from Hugging Face into models/hf, or manually place model
folders here.

Recommended layout:

  models/
    hf/
      hub/                         Hugging Face cache used by Transformers
    llm/
      gemma-4-E2B-it/                 Optional local Gemma chat model folder
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

LLM:
  LOCAL_LLM_MODEL                  Default: google/gemma-4-E2B-it
  LOCAL_LLM_MODEL_PATH             Explicit local folder/file path. Wins over aliases.
  LOCAL_LLM_PORT                   Default: 8200

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
