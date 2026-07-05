#!/usr/bin/env python3
"""Shared model-path helpers for Six Stars local services.

All local AI sidecars use this module so Qwen TTS, faster-whisper, and the local
LLM server agree on the same repository-local models folder. This file is used
both from source (``python scripts/...``) and from frozen runtime-pack EXEs under
``engines/...``. Therefore root discovery must not depend only on ``__file__``.
"""

from __future__ import annotations

import os
import re
import sys
from pathlib import Path
from typing import Iterable, Optional


def _looks_like_repo_root(path: Path) -> bool:
    return (
        (path / "SixStars.sln").exists()
        or (path / "config" / "runtime-manifest.json").exists()
        or ((path / "models").exists() and (path / "scripts").exists())
    )


def _candidate_roots() -> list[Path]:
    candidates: list[Path] = []
    for env_name in ("SIX_STARS_APP_ROOT", "SIX_STARS_REPO_ROOT", "SIX_STARS_ROOT"):
        value = os.environ.get(env_name, "").strip()
        if value:
            candidates.append(Path(value).expanduser())

    try:
        candidates.append(Path.cwd())
    except Exception:
        pass

    if getattr(sys, "frozen", False):
        # PyInstaller/Nuitka runtime packs are normally launched from
        # engines/<kind>/<pack>. Search upward from the actual EXE first.
        candidates.append(Path(sys.executable).resolve().parent)
        try:
            candidates.append(Path(getattr(sys, "_MEIPASS", "")).resolve())
        except Exception:
            pass

    try:
        candidates.append(Path(__file__).resolve().parent)
    except Exception:
        pass

    expanded: list[Path] = []
    seen: set[str] = set()
    for candidate in candidates:
        if not str(candidate):
            continue
        candidate = candidate.expanduser().resolve()
        for path in (candidate, *candidate.parents):
            key = str(path).lower()
            if key in seen:
                continue
            seen.add(key)
            expanded.append(path)
    return expanded


def find_repo_root() -> Path:
    for candidate in _candidate_roots():
        if _looks_like_repo_root(candidate):
            return candidate.resolve()

    # Source-tree fallback: scripts/model_paths.py -> repo root.
    try:
        return Path(__file__).resolve().parents[1]
    except Exception:
        return Path.cwd().resolve()


ROOT = find_repo_root()
MODELS_ROOT = Path(os.environ.get("SIX_STARS_MODELS_ROOT", str(ROOT / "models"))).expanduser().resolve()
HF_HOME = Path(os.environ.get("HF_HOME", str(MODELS_ROOT / "hf"))).expanduser().resolve()
HF_HUB_CACHE = Path(os.environ.get("HF_HUB_CACHE", str(HF_HOME / "hub"))).expanduser().resolve()


def configure_model_environment() -> None:
    MODELS_ROOT.mkdir(parents=True, exist_ok=True)
    for child in ("hf", "llm", "tts", "asr", "voice_clones"):
        (MODELS_ROOT / child).mkdir(parents=True, exist_ok=True)
    HF_HOME.mkdir(parents=True, exist_ok=True)
    HF_HUB_CACHE.mkdir(parents=True, exist_ok=True)

    os.environ.setdefault("SIX_STARS_APP_ROOT", str(ROOT))
    os.environ.setdefault("SIX_STARS_REPO_ROOT", str(ROOT))
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


def _resolve_relative_path(value: str) -> Optional[Path]:
    """Resolve a model value that is intended to be a filesystem path.

    Previous versions only treated paths as local when they were absolute,
    started with '.', or contained backslashes. That failed for normal repo paths
    like ``models/tts/qwen3-tts-0.6b-base`` when launched from an engine folder.
    """
    raw = (value or "").strip().strip('"')
    if not raw:
        return None

    # Hugging Face repo IDs also contain '/', so only classify as path when the
    # string clearly points at local storage.
    local_markers = (
        raw.startswith("."),
        raw.startswith("~"),
        "\\" in raw,
        raw.lower().startswith(("models/", "engines/", "runtime/", "redist/")),
        raw.endswith((".gguf", ".bin", ".safetensors", ".pt", ".pth")),
    )
    candidate = Path(raw).expanduser()
    if candidate.is_absolute():
        return candidate
    if any(local_markers):
        return (ROOT / candidate).resolve()
    return None


def resolve_model_ref(category: str, model_ref: str, explicit_path: str = "", aliases: Optional[Iterable[str]] = None) -> str:
    configure_model_environment()

    if explicit_path and explicit_path.strip():
        path = _resolve_relative_path(explicit_path) or Path(explicit_path).expanduser()
        if not path.is_absolute():
            path = ROOT / path
        # Return the resolved path even when it does not exist so the downstream
        # error says exactly which local path was attempted.
        return str(path.resolve())

    value = (model_ref or "").strip()
    if not value:
        return value

    local_path = _resolve_relative_path(value)
    if local_path is not None:
        return str(local_path.resolve())

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
