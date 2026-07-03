#!/usr/bin/env python3
"""Shared model-path helpers for Six Stars local services.

All local AI sidecars use this module so Qwen TTS, faster-whisper, and the local
LLM server agree on the same repository-local models folder:

    ./models
      hf/hub     Hugging Face download cache
      llm/       Local chat/instruct model folders
      tts/       Local Qwen TTS folders
      asr/       Local faster-whisper folders

You can still pass any absolute model path or Hugging Face repo id. If a matching
local folder exists, it wins; otherwise the model id is returned and Hugging Face
will auto-download into ./models/hf.
"""

from __future__ import annotations

import os
import re
from pathlib import Path
from typing import Iterable, Optional

ROOT = Path(__file__).resolve().parents[1]
MODELS_ROOT = Path(os.environ.get("SIX_STARS_MODELS_ROOT", str(ROOT / "models"))).expanduser().resolve()
HF_HOME = Path(os.environ.get("HF_HOME", str(MODELS_ROOT / "hf"))).expanduser().resolve()
HF_HUB_CACHE = Path(os.environ.get("HF_HUB_CACHE", str(HF_HOME / "hub"))).expanduser().resolve()


def configure_model_environment() -> None:
    MODELS_ROOT.mkdir(parents=True, exist_ok=True)
    for child in ("hf", "llm", "tts", "asr", "voice_clones"):
        (MODELS_ROOT / child).mkdir(parents=True, exist_ok=True)
    HF_HOME.mkdir(parents=True, exist_ok=True)
    HF_HUB_CACHE.mkdir(parents=True, exist_ok=True)

    os.environ.setdefault("SIX_STARS_MODELS_ROOT", str(MODELS_ROOT))
    os.environ.setdefault("HF_HOME", str(HF_HOME))
    os.environ.setdefault("HF_HUB_CACHE", str(HF_HUB_CACHE))
    # Do not set TRANSFORMERS_CACHE; Transformers v5 removes it and warns.
    # HF_HOME / HF_HUB_CACHE are the supported cache controls.


def sanitize_model_ref(value: str) -> str:
    cleaned = value.strip().replace("\\", "/")
    cleaned = cleaned.rstrip("/")
    cleaned = cleaned.split("/")[-1] if "/" in cleaned else cleaned
    cleaned = re.sub(r"[^A-Za-z0-9._-]+", "-", cleaned)
    return cleaned.strip("-._") or "model"


def candidate_names(model_ref: str, extra_aliases: Optional[Iterable[str]] = None) -> list[str]:
    names: list[str] = []
    if extra_aliases:
        names.extend([x for x in extra_aliases if x])
    sanitized = sanitize_model_ref(model_ref)
    names.append(sanitized)
    names.append(sanitized.lower())
    names.append(model_ref.replace("/", "__").replace("\\", "__"))
    names.append(model_ref.replace("/", "_").replace("\\", "_"))
    # Preserve order while removing duplicates.
    seen: set[str] = set()
    result: list[str] = []
    for name in names:
        if not name:
            continue
        key = name.lower()
        if key in seen:
            continue
        seen.add(key)
        result.append(name)
    return result


def resolve_model_ref(category: str, model_ref: str, explicit_path: str = "", aliases: Optional[Iterable[str]] = None) -> str:
    configure_model_environment()

    if explicit_path and explicit_path.strip():
        path = Path(explicit_path).expanduser()
        if not path.is_absolute():
            path = ROOT / path
        if path.exists():
            return str(path.resolve())
        return str(path)

    value = (model_ref or "").strip()
    if not value:
        return value

    possible_path = Path(value).expanduser()
    if possible_path.is_absolute() or value.startswith(".") or "\\" in value:
        if not possible_path.is_absolute():
            possible_path = ROOT / possible_path
        if possible_path.exists():
            return str(possible_path.resolve())

    category_root = MODELS_ROOT / category
    for name in candidate_names(value, aliases):
        path = category_root / name
        if path.exists():
            return str(path.resolve())

    return value


def describe_model_environment() -> dict[str, str]:
    configure_model_environment()
    return {
        "repoRoot": str(ROOT),
        "modelsRoot": str(MODELS_ROOT),
        "hfHome": str(HF_HOME),
        "hfHubCache": str(HF_HUB_CACHE),
        "llmRoot": str(MODELS_ROOT / "llm"),
        "ttsRoot": str(MODELS_ROOT / "tts"),
        "asrRoot": str(MODELS_ROOT / "asr"),
    }
