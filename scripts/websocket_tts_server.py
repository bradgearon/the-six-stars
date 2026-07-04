#!/usr/bin/env python3
"""
WebSocket Qwen3-TTS streaming server for Six Stars.

This server receives JSON generation requests from the WPF app on
ws://localhost:8100/tts/stream, chunks the text into short sentence groups,
generates real Qwen3-TTS audio for each chunk, and sends each WAV back as a
binary WebSocket message.

For the Qwen3-TTS Base voice-clone model, the WPF/Unity host must send an
absolute voiceCloneAudio path in each WebSocket request. The Python side does
not search for clone files, read Star JSON voice config, or guess filenames. It
only validates the full path and reads voice/style fields supplied in the request,
then builds/caches a reusable voice_clone_prompt.
This matches the Qwen3-TTS documented pattern:

    prompt_items = model.create_voice_clone_prompt(...)
    wavs, sr = model.generate_voice_clone(..., voice_clone_prompt=prompt_items)

Environment examples, CMD:
    set PYTHONNOUSERSITE=1
    set QWEN_TTS_DEVICE=cuda
    set QWEN_TTS_LANGUAGE=English
    set QWEN_TTS_MODEL_PATH=Qwen/Qwen3-TTS-12Hz-1.7B-Base
    python scripts\websocket_tts_server.py

Environment examples, PowerShell:
    $env:PYTHONNOUSERSITE = "1"
    $env:QWEN_TTS_DEVICE = "cuda"
    $env:QWEN_TTS_LANGUAGE = "English"
    $env:QWEN_TTS_MODEL_PATH = "Qwen/Qwen3-TTS-12Hz-1.7B-Base"
    python scripts\websocket_tts_server.py
"""

from __future__ import annotations

import asyncio
import hashlib
import inspect
import io
import json
import logging
import os
import re
import struct
import threading
import time
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from model_paths import MODELS_ROOT, configure_model_environment, resolve_model_ref

configure_model_environment()

import numpy as np

try:
    import torch
except Exception:  # pragma: no cover - handled at runtime
    torch = None  # type: ignore

try:
    from websockets.server import serve, WebSocketServerProtocol
    from websockets.exceptions import ConnectionClosed
except ImportError:
    print("ERROR: websockets library not installed. Run: pip install websockets")
    raise


logging.basicConfig(
    level=getattr(logging, os.environ.get("QWEN_TTS_LOG_LEVEL", "INFO").upper(), logging.INFO),
    format="[%(asctime)s] [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
)
logger = logging.getLogger(__name__)


def patch_transformers_check_model_inputs_for_qwen() -> None:
    """Patch a Qwen3-TTS/Transformers compatibility mismatch at runtime.

    Some Qwen3-TTS builds decorate model methods as ``@check_model_inputs()``,
    while some Transformers builds expose ``check_model_inputs`` as a direct
    decorator that requires ``func``. The result is this runtime error:

        TypeError: check_model_inputs() missing 1 required positional argument: 'func'

    Voice Clone Studio works around this by using a compatible environment. The
    Six Stars launcher has to survive venv changes caused by Gemma experiments,
    so we install a small compatibility shim before importing qwen_tts or
    faster_qwen3_tts. The shim preserves the common no-op validation behavior
    needed by Qwen's generated modeling files.
    """
    if not PATCH_TRANSFORMERS_CHECK_MODEL_INPUTS:
        return
    try:
        import transformers.utils.generic as generic  # type: ignore
    except Exception as exc:
        logger.debug("Could not import transformers.utils.generic for Qwen compatibility patch: %s", exc)
        return

    current = getattr(generic, "check_model_inputs", None)
    if getattr(current, "_six_stars_qwen_compat", False):
        return

    def _six_stars_check_model_inputs(func=None, *decorator_args, **decorator_kwargs):  # type: ignore[no-untyped-def]
        if func is None:
            def decorator(inner_func):  # type: ignore[no-untyped-def]
                return inner_func
            return decorator
        if callable(func):
            return func
        def decorator(inner_func):  # type: ignore[no-untyped-def]
            return inner_func
        return decorator

    setattr(_six_stars_check_model_inputs, "_six_stars_qwen_compat", True)
    generic.check_model_inputs = _six_stars_check_model_inputs  # type: ignore[attr-defined]
    logger.info("Installed Qwen3-TTS compatibility shim for transformers.utils.generic.check_model_inputs")


HOST = os.environ.get("QWEN_TTS_HOST", "0.0.0.0")
PORT = int(os.environ.get("QWEN_TTS_PORT", "8100"))
MODEL_PATH = resolve_model_ref(
    "tts",
    os.environ.get("QWEN_TTS_MODEL_PATH")
    or os.environ.get("QWEN_MODEL_PATH")
    or "Qwen/Qwen3-TTS-12Hz-0.6B-Base",
    os.environ.get("QWEN_TTS_LOCAL_PATH", ""),
    aliases=["qwen3-tts-0.6b", "qwen3-tts-0.6b-base", "qwen3-tts-1.7b", "qwen3-tts-1.7b-base"],
)
DEVICE = os.environ.get(
    "QWEN_TTS_DEVICE",
    "cuda" if torch is not None and getattr(torch, "cuda", None) is not None and torch.cuda.is_available() else "cpu",
)
DEFAULT_LANGUAGE = os.environ.get("QWEN_TTS_LANGUAGE", "English")
DEFAULT_TASK_TYPE = os.environ.get("QWEN_TTS_TASK_TYPE", "VoiceClone")
DEFAULT_MAX_NEW_TOKENS = int(os.environ.get("QWEN_TTS_MAX_NEW_TOKENS", "2048"))
ATTN_IMPLEMENTATION = os.environ.get("QWEN_TTS_ATTN_IMPLEMENTATION", "sdpa")
DTYPE_NAME = os.environ.get("QWEN_TTS_DTYPE", "bfloat16" if DEVICE.startswith("cuda") else "float32")
USE_CUDA_GRAPHS = os.environ.get("QWEN_TTS_USE_CUDA_GRAPHS", "true").strip().lower() not in {"0", "false", "no"}
# Voice Clone Studio gets its speed from faster_qwen3_tts when CUDA graphs are enabled.
# Keep this on by default and fall back to qwen_tts only if the package is missing.
USE_FASTER_QWEN3 = os.environ.get("QWEN_TTS_USE_FASTER_QWEN3", "true").strip().lower() not in {"0", "false", "no"}
PATCH_TRANSFORMERS_CHECK_MODEL_INPUTS = os.environ.get("QWEN_TTS_PATCH_TRANSFORMERS_CHECK_MODEL_INPUTS", "true").strip().lower() not in {"0", "false", "no"}
CUDA_GRAPH_WARMUP_RUNS = int(os.environ.get("QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS", "3"))
USE_CLONE_PROMPT_CACHE = os.environ.get("QWEN_TTS_CACHE_CLONE_PROMPTS", "true").strip().lower() not in {"0", "false", "no"}
DIRECT_FALLBACK_ON_SILENCE = os.environ.get("QWEN_TTS_DIRECT_FALLBACK_ON_SILENCE", "true").strip().lower() not in {"0", "false", "no"}
SILENCE_RMS_THRESHOLD = float(os.environ.get("QWEN_TTS_SILENCE_RMS_THRESHOLD", "0.0001"))

DEBUG_OUTPUT_DIR = os.environ.get("QWEN_TTS_DEBUG_OUTPUT_DIR", "").strip()
SAVE_SERVER_WAVS = os.environ.get("QWEN_TTS_SAVE_WAVS", "false").strip().lower() in {"1", "true", "yes"}
# Chunk generation mode:
# - parallel (default): start every chunk generation task immediately and send results in order.
#   This is fastest when your local Qwen build/GPU can handle concurrent chunk generation.
# - first_then_prefetch: generate chunk 1 alone, send it immediately, then start chunk 2 while
#   the client plays chunk 1. Safer if parallel mode overloads your model/GPU.
# - first_then_parallel: generate chunk 1 alone, send it, then generate remaining chunks with
#   bounded parallelism and send them in order.
# - sequential: generate and send one chunk at a time. Functionally safe, but less explicit.
CHUNK_GENERATION_MODE = os.environ.get("QWEN_TTS_CHUNK_GENERATION_MODE", "parallel").strip().lower()
PARALLEL_CHUNK_GENERATION = os.environ.get("QWEN_TTS_PARALLEL_CHUNKS", "true").strip().lower() not in {"0", "false", "no"}
# 0 means start generation for every chunk immediately in legacy parallel mode.
MAX_PARALLEL_CHUNKS = int(os.environ.get("QWEN_TTS_MAX_PARALLEL_CHUNKS", "0"))
# For first_then_parallel mode only. Keep this at 1 for safest local Qwen behavior; set 2 if your
# GPU/model build is stable with concurrent generate_voice_clone calls.
AFTER_FIRST_MAX_PARALLEL_CHUNKS = int(os.environ.get("QWEN_TTS_AFTER_FIRST_MAX_PARALLEL_CHUNKS", "1"))


def _parse_int(value: Any, default: int) -> int:
    try:
        if value is None:
            return default
        text = str(value).strip()
        if text == "":
            return default
        return int(text)
    except (TypeError, ValueError):
        return default


def _resolve_chunk_generation_mode(settings: Dict[str, Any]) -> str:
    mode = str(
        settings.get("chunkGenerationMode")
        or settings.get("chunk_generation_mode")
        or CHUNK_GENERATION_MODE
        or "parallel"
    ).strip().lower()

    if PARALLEL_CHUNK_GENERATION and mode in {"first_then_prefetch", "pipelined", "sequential"}:
        # Backward compatibility: the boolean env flag can still force parallel mode.
        mode = "parallel"

    if mode not in {
        "parallel",
        "all_parallel",
        "legacy_parallel",
        "first_then_parallel",
        "staggered_parallel",
        "first_then_prefetch",
        "pipelined",
        "prefetch",
        "sequential",
    }:
        logger.warning("Unknown chunkGenerationMode=%s; falling back to parallel", mode)
        mode = "parallel"

    return mode


def _resolve_max_parallel_chunks(settings: Dict[str, Any], default: int) -> int:
    return _parse_int(
        settings.get("maxParallelChunks")
        if "maxParallelChunks" in settings
        else settings.get("max_parallel_chunks"),
        default,
    )


VOICE_ALIASES = {
    "shimmer": "Serena",
    "alloy": "Serena",
    "echo": "Ryan",
    "fable": "Eric",
    "onyx": "Dylan",
    "nova": "Vivian",
    "elusiaa": "Serena",
    "elusiaa-dev": "Serena",
    "kaelia": "Vivian",
    "kaelia-dev": "Vivian",
    "maera": "Sohee",
    "maera-dev": "Sohee",
    "khavia": "Ono_Anna",
    "khavia-dev": "Ono_Anna",
    "auerlis": "Serena",
    "aurelis": "Serena",
    "auerlis-dev": "Serena",
    "nyxari": "Vivian",
    "nyxari-dev": "Vivian",
}


@dataclass
class TextChunk:
    index: int
    text: str
    total_chunks: int


@dataclass
class GeneratedChunk:
    chunk: TextChunk
    audio_data: bytes
    duration_ms: float
    elapsed_ms: float




class TextChunker:
    """Splits text into short sentence chunks for faster first audio."""

    @staticmethod
    def split_into_sentences(text: str) -> List[str]:
        sentence_parts = re.split(r"([.!?]+\s+|[.!?]+$)", text)
        result: List[str] = []
        current = ""
        for part in sentence_parts:
            current += part
            if re.match(r"[.!?]+(\s+|$)", part):
                if current.strip():
                    result.append(current.strip())
                current = ""
        if current.strip():
            result.append(current.strip())
        return [s for s in result if s]

    @staticmethod
    def chunk_text(text: str, max_words_per_chunk: int = 30, max_sentences_per_chunk: int = 2) -> List[TextChunk]:
        sentences = TextChunker.split_into_sentences(text)
        if not sentences and text.strip():
            sentences = [text.strip()]
        if not sentences:
            return []

        chunks: List[str] = []
        current_chunk: List[str] = []
        current_word_count = 0
        for sentence in sentences:
            word_count = len(sentence.split())
            if current_chunk and (
                len(current_chunk) >= max_sentences_per_chunk
                or current_word_count + word_count > max_words_per_chunk
            ):
                chunks.append(" ".join(current_chunk))
                current_chunk = []
                current_word_count = 0
            current_chunk.append(sentence)
            current_word_count += word_count
        if current_chunk:
            chunks.append(" ".join(current_chunk))

        total = len(chunks)
        return [TextChunk(index=i, text=chunk, total_chunks=total) for i, chunk in enumerate(chunks)]


class QwenTtsEngine:
    """Real Qwen3-TTS engine with cached voice clone prompts per Star."""

    def __init__(self) -> None:
        self.model: Any = None
        self.model_loaded = False
        self.model_type = "unknown"
        self.using_faster_backend = False
        self.cuda_graphs_active = False
        self.supported_speakers: List[str] = []
        self.supported_languages: List[str] = []
        # The host owns Star voice selection and sends resolved voice settings per request.
        self.voice_clone_prompt_cache: Dict[str, Any] = {}
        self.cache_failures: set[str] = set()
        self.disable_faster_backend_for_session = False
        # Request-time chunk generation can run in parallel. This lock protects
        # the shared voice-clone prompt cache so the same Star prompt is built once.
        self._cache_lock = threading.Lock()
        self._model_lock = asyncio.Lock()
        logger.info("Qwen TTS engine created")

    async def ensure_loaded(self) -> None:
        if self.model_loaded:
            return
        async with self._model_lock:
            if self.model_loaded:
                return
            await asyncio.to_thread(self._load_model_sync)

    @staticmethod
    def _call_if_supported(target: Any, method_names: List[str], *args: Any, **kwargs: Any) -> bool:
        """Call the first CUDA-graph helper method that exists and accepts the supplied args.

        Different qwen_tts builds expose CUDA graph acceleration under different names.
        This keeps the Six Stars server compatible with older builds while letting newer
        builds use the same fast path seen in Voice Clone Studio.
        """
        for method_name in method_names:
            method = getattr(target, method_name, None)
            if method is None or not callable(method):
                continue
            try:
                try:
                    signature = inspect.signature(method)
                    accepted_kwargs = {key: value for key, value in kwargs.items() if key in signature.parameters}
                except (TypeError, ValueError):
                    accepted_kwargs = kwargs
                method(*args, **accepted_kwargs)
                logger.info("Called Qwen CUDA graph helper: %s.%s", type(target).__name__, method_name)
                return True
            except TypeError:
                try:
                    method()
                    logger.info("Called Qwen CUDA graph helper without args: %s.%s", type(target).__name__, method_name)
                    return True
                except Exception as exc:
                    logger.debug("Qwen CUDA graph helper %s.%s did not run: %s", type(target).__name__, method_name, exc)
            except Exception as exc:
                logger.debug("Qwen CUDA graph helper %s.%s failed: %s", type(target).__name__, method_name, exc)
        return False

    def _try_enable_cuda_graphs_sync(self) -> None:
        if self.using_faster_backend:
            logger.info("Qwen CUDA graph acceleration is active through FasterQwen3TTS.")
            return
        if not USE_CUDA_GRAPHS:
            logger.info("Qwen CUDA graphs disabled by QWEN_TTS_USE_CUDA_GRAPHS=false")
            return
        if not DEVICE.startswith("cuda"):
            logger.info("Qwen CUDA graphs skipped because device is %s", DEVICE)
            return
        if self.model is None:
            return

        raw_model = getattr(self.model, "model", None)
        targets = [self.model]
        if raw_model is not None and raw_model is not self.model:
            targets.append(raw_model)

        called = False
        for target in targets:
            called = self._call_if_supported(
                target,
                [
                    "enable_cuda_graphs",
                    "enable_cuda_graph",
                    "capture_cuda_graphs",
                    "capture_cuda_graph",
                    "prepare_cuda_graphs",
                    "prepare_cuda_graph",
                    "warmup_cuda_graphs",
                    "warmup_cuda_graph",
                ],
                warmup_runs=CUDA_GRAPH_WARMUP_RUNS,
                num_warmup_runs=CUDA_GRAPH_WARMUP_RUNS,
            ) or called

        if called:
            logger.info("Qwen CUDA graph acceleration requested and at least one graph helper ran.")
        else:
            logger.info(
                "Qwen CUDA graph acceleration requested, but this qwen_tts build did not expose a known graph helper. "
                "The server will still run normally; Voice Clone Studio may be faster if it has a newer graph-enabled backend."
            )

    @staticmethod
    def _infer_model_type_from_path(model_path: str) -> str:
        value = (model_path or "").lower().replace("_", "").replace("-", "")
        if "voicedesign" in value:
            return "voice_design"
        if "customvoice" in value:
            return "custom_voice"
        if "base" in value:
            return "base"
        return "unknown"

    def _try_load_faster_qwen3_sync(self, dtype: Any) -> bool:
        """Load FasterQwen3TTS exactly like Voice Clone Studio when available.

        Voice Clone Studio's tts_manager.py uses FasterQwen3TTS.from_pretrained(...)
        with device="cuda", dtype=bfloat16, and attn_implementation="eager". That path
        warms up and captures CUDA graphs internally, producing the log lines:
        "Warming up predictor", "CUDA graph captured", "Talker CUDA graph captured".
        """
        if self.disable_faster_backend_for_session:
            logger.info("FasterQwen3TTS skipped after a previous runtime failure; using official qwen_tts fallback for this server session")
            return False
        if not USE_CUDA_GRAPHS:
            logger.info("FasterQwen3TTS skipped: QWEN_TTS_USE_CUDA_GRAPHS=false")
            return False
        if not USE_FASTER_QWEN3:
            logger.info("FasterQwen3TTS skipped: QWEN_TTS_USE_FASTER_QWEN3=false")
            return False
        if not DEVICE.startswith("cuda"):
            logger.info("FasterQwen3TTS skipped: device is %s", DEVICE)
            return False

        patch_transformers_check_model_inputs_for_qwen()
        try:
            from faster_qwen3_tts import FasterQwen3TTS  # type: ignore
        except ImportError as exc:
            logger.warning(
                "FasterQwen3TTS is not installed, so CUDA graph parity with Voice Clone Studio is not active: %s. "
                "Install with: python -m pip install faster-qwen3-tts",
                exc,
            )
            return False

        logger.info("Loading Qwen3-TTS with FasterQwen3TTS CUDA graph backend (Voice Clone Studio parity).")
        logger.info("FasterQwen3TTS settings: device=cuda dtype=%s attn_implementation=eager warmup_runs=%s", dtype, CUDA_GRAPH_WARMUP_RUNS)
        try:
            self.model = FasterQwen3TTS.from_pretrained(
                MODEL_PATH,
                device="cuda",
                dtype=dtype,
                attn_implementation="eager",
            )
        except Exception as exc:
            logger.warning(
                "FasterQwen3TTS failed to load, so the server will fall back to official qwen_tts. "
                "This usually means the current venv has an incompatible transformers/qwen_tts mix: %s: %s",
                type(exc).__name__,
                exc,
            )
            self.model = None
            self.using_faster_backend = False
            self.cuda_graphs_active = False
            self.disable_faster_backend_for_session = True
            return False

        self.using_faster_backend = True
        self.cuda_graphs_active = True
        self.model_type = self._infer_model_type_from_path(MODEL_PATH)
        logger.info("[OK] Model loaded with CUDA graphs acceleration")
        return True

    def _load_model_sync(self) -> None:
        if torch is None:
            raise RuntimeError("PyTorch is not installed or failed to import. Install torch in this venv.")

        logger.info("=" * 70)
        logger.info("Loading Qwen3-TTS model")
        logger.info("Model: %s", MODEL_PATH)
        logger.info("Device: %s", DEVICE)
        logger.info("dtype: %s", DTYPE_NAME)
        logger.info("attn_implementation: %s", ATTN_IMPLEMENTATION)
        logger.info("Clone audio lookup and Star JSON voice lookup: disabled; requests must include an absolute voiceCloneAudio path and voice settings")
        logger.info("=" * 70)

        dtype = self._resolve_torch_dtype(DTYPE_NAME)

        # Fast path: match Voice Clone Studio. Its TTSManager loads FasterQwen3TTS,
        # which captures predictor/talker CUDA graphs internally. This is the path
        # that prints the warmup/capture messages and produces the 0.6B speed you saw.
        if self._try_load_faster_qwen3_sync(dtype):
            raw_model = getattr(self.model, "model", None)
        else:
            patch_transformers_check_model_inputs_for_qwen()
            from qwen_tts import Qwen3TTSModel

            device_map = "cuda:0" if DEVICE.startswith("cuda") else "cpu"

            # Fallback path: official qwen_tts model. This is correct but usually much slower
            # because it does not use FasterQwen3TTS static-cache CUDA graph replay.
            kwargs: Dict[str, Any] = {
                "device_map": device_map,
                "dtype": dtype,
            }
            if ATTN_IMPLEMENTATION:
                kwargs["attn_implementation"] = ATTN_IMPLEMENTATION

            self.model = Qwen3TTSModel.from_pretrained(MODEL_PATH, **kwargs)
            raw_model = getattr(self.model, "model", None)
            self.model_type = getattr(raw_model, "tts_model_type", "unknown") if raw_model is not None else self._infer_model_type_from_path(MODEL_PATH)

        try:
            self.supported_speakers = list(self.model.get_supported_speakers() or [])
        except Exception:
            self.supported_speakers = []
        try:
            self.supported_languages = list(self.model.get_supported_languages() or [])
        except Exception:
            self.supported_languages = []

        self.model_loaded = True
        self._try_enable_cuda_graphs_sync()
        logger.info("Qwen3-TTS loaded successfully. Detected model type: %s", self.model_type)
        if self.supported_speakers:
            logger.info("Supported speakers: %s", ", ".join(self.supported_speakers))
        if self.supported_languages:
            logger.info("Supported languages: %s", ", ".join(self.supported_languages))

    @staticmethod
    def _resolve_torch_dtype(value: str):
        value = (value or "").strip().lower()
        if value in {"bf16", "bfloat16"}:
            return torch.bfloat16
        if value in {"fp16", "float16", "half"}:
            return torch.float16
        if value in {"fp32", "float32"}:
            return torch.float32
        return torch.bfloat16 if DEVICE.startswith("cuda") else torch.float32

    @staticmethod
    def _resolve_dir(path: Path) -> Path:
        return path if path.is_absolute() else (Path.cwd() / path).resolve()

    @staticmethod
    def _normalize_task_type(value: Optional[str]) -> str:
        if not value:
            return DEFAULT_TASK_TYPE
        compact = "".join(ch for ch in str(value) if ch.isalnum()).lower()
        if compact == "customvoice":
            return "CustomVoice"
        if compact == "voicedesign":
            return "VoiceDesign"
        if compact == "voiceclone":
            return "VoiceClone"
        return DEFAULT_TASK_TYPE

    @staticmethod
    def _request_value(settings: Dict[str, Any], *names: str, default: Any = None) -> Any:
        for name in names:
            if name in settings and settings[name] not in (None, ""):
                return settings[name]
        return default


    def _resolve_clone_audio_path(self, value: Optional[str]) -> str:
        """Validate the request-provided clone audio path.

        The desktop app or Unity host owns voice selection and file resolution.
        This server intentionally does not search Content/VoiceClones, Star JSON,
        or guessed filenames. That keeps the Python side deterministic and makes
        missing-file errors point to the exact path the host sent.
        """
        requested = (str(value).strip().strip('"') if value is not None else "")
        if not requested:
            raise RuntimeError(
                "VoiceClone requires voiceCloneAudio to be a full absolute path supplied in the WebSocket request. "
                "The host should resolve the selected Star's voice clone file before calling the TTS server."
            )

        expanded = os.path.expandvars(os.path.expanduser(requested))
        path = Path(expanded)
        if not path.is_absolute():
            raise RuntimeError(
                f"VoiceClone requires an absolute voiceCloneAudio path. Received relative value: {requested!r}. "
                "Resolve it in the host/main view model before sending the WebSocket request."
            )

        if not path.exists() or not path.is_file():
            raise RuntimeError(f"VoiceClone reference audio file does not exist: {path}")

        return str(path.resolve())

    def _normalize_voice_name(self, voice_id: str) -> str:
        value = (voice_id or "").strip() or "elusiaa"
        mapped = VOICE_ALIASES.get(value.lower(), value)
        if self.supported_speakers:
            lookup = {speaker.lower(): speaker for speaker in self.supported_speakers}
            if mapped.lower() in lookup:
                return lookup[mapped.lower()]
        return mapped


    def _normalize_language(self, value: Optional[str]) -> str:
        """Return a Qwen-supported language, falling back to DEFAULT_LANGUAGE.

        The desktop app usually sends English, but local LLMs can accidentally put
        acting style text into voiceDirective.language. This server-side guard keeps
        Qwen from receiving values such as "Warm and poetic.".
        """
        fallback = str(DEFAULT_LANGUAGE or "English").strip() or "English"
        raw = str(value or "").strip()
        if not raw:
            raw = fallback

        def compact(text: str) -> str:
            return "".join(ch.lower() for ch in text if ch.isalpha())

        aliases = {
            "auto": "auto",
            "en": "english", "eng": "english", "english": "english", "enus": "english", "engb": "english",
            "zh": "chinese", "zho": "chinese", "chi": "chinese", "cn": "chinese", "chinese": "chinese",
            "fr": "french", "fra": "french", "fre": "french", "french": "french",
            "de": "german", "deu": "german", "ger": "german", "german": "german",
            "es": "spanish", "spa": "spanish", "spanish": "spanish",
            "it": "italian", "ita": "italian", "italian": "italian",
            "ja": "japanese", "jpn": "japanese", "japanese": "japanese",
            "ko": "korean", "kor": "korean", "korean": "korean",
            "pt": "portuguese", "por": "portuguese", "portuguese": "portuguese",
            "ru": "russian", "rus": "russian", "russian": "russian",
        }

        key = compact(raw)
        canonical_key = aliases.get(key)
        if canonical_key is None and ("-" in raw or "_" in raw):
            canonical_key = aliases.get(compact(raw.split("-", 1)[0].split("_", 1)[0]))

        # Qwen reports supported languages as lowercase names in many builds.
        supported = {str(lang).strip().lower(): str(lang).strip() for lang in self.supported_languages if str(lang).strip()}
        if canonical_key:
            if supported:
                if canonical_key in supported:
                    return supported[canonical_key]
                # Prefer English if Auto is not accepted by a particular local build.
                if canonical_key == "auto" and "english" in supported:
                    return supported["english"]
            return "English" if canonical_key == "english" else canonical_key.title()

        fallback_key = aliases.get(compact(fallback), "english")
        if supported and fallback_key in supported:
            safe = supported[fallback_key]
        else:
            safe = "English"
        logger.warning("Unsupported TTS language %r; falling back to %s", raw, safe)
        return safe

    def _build_instruct(self, settings: Dict[str, Any]) -> str:
        pieces: List[str] = []
        voice_design_prompt = self._request_value(settings, "voiceDesignPrompt", "voice_design_prompt", default="")
        delivery = self._request_value(settings, "deliveryInstructions", "delivery_instructions", default="")
        primary_emotion = self._request_value(settings, "primaryEmotion", "primary_emotion", default="")
        pace = self._request_value(settings, "pace", default="")
        volume = self._request_value(settings, "volume", default="")
        pitch = self._request_value(settings, "pitchDescription", "pitch", default="")
        prosody = self._request_value(settings, "prosody", default="")

        personality_short = self._request_value(settings, "personalityShort", "personality_short", default="")
        voice_archetype = self._request_value(settings, "voiceArchetype", "voice_archetype", default="")
        speaking_style = self._request_value(settings, "speakingStyle", "speaking_style", default="")
        voice_style_rules = self._request_value(settings, "voiceStyleRules", "voice_style_rules", default=[])
        voice_generation_rules = self._request_value(settings, "voiceGenerationRules", "voice_generation_rules", default=[])

        if personality_short:
            pieces.append(f"Personality: {personality_short}")
        if voice_archetype:
            pieces.append(f"Voice archetype: {voice_archetype}")
        if speaking_style:
            pieces.append(f"Speaking style: {speaking_style}")
        if voice_style_rules:
            if isinstance(voice_style_rules, list):
                pieces.append("Voice style rules: " + " ".join(str(x) for x in voice_style_rules if str(x).strip()))
            else:
                pieces.append("Voice style rules: " + str(voice_style_rules))
        if voice_generation_rules:
            if isinstance(voice_generation_rules, list):
                pieces.append("Voice generation rules: " + " ".join(str(x) for x in voice_generation_rules if str(x).strip()))
            else:
                pieces.append("Voice generation rules: " + str(voice_generation_rules))
        if voice_design_prompt:
            pieces.append(f"Stable voice identity: {voice_design_prompt}")
        if delivery:
            pieces.append(f"Line delivery: {delivery}")
        if primary_emotion:
            pieces.append(f"Primary emotion: {primary_emotion}")
        if pace:
            pieces.append(f"Pace: {pace}")
        if pitch:
            pieces.append(f"Pitch/register: {pitch}")
        if volume:
            pieces.append(f"Volume: {volume}")
        if prosody:
            pieces.append(f"Prosody: {prosody}")

        return "\n".join(pieces).strip()

    def _cache_key(self, star_id: str, voice_id: str, ref_audio_path: str, ref_text: str, x_vector_only: bool) -> str:
        raw = "|".join([
            MODEL_PATH,
            (star_id or "").lower(),
            (voice_id or "").lower(),
            str(Path(ref_audio_path).resolve()).lower(),
            ref_text or "",
            "xvector" if x_vector_only else "full",
        ])
        return hashlib.sha256(raw.encode("utf-8")).hexdigest()

    async def generate_audio(self, text: str, star_id: str, voice_id: str, settings: Dict[str, Any]) -> bytes:
        await self.ensure_loaded()
        return await asyncio.to_thread(self._generate_audio_sync, text, star_id, voice_id, settings)

    def _generate_audio_sync(self, text: str, star_id: str, voice_id: str, settings: Dict[str, Any]) -> bytes:
        if self.model is None:
            raise RuntimeError("Qwen3-TTS model is not loaded")

        language = self._normalize_language(self._request_value(settings, "language", default=DEFAULT_LANGUAGE))
        task_type = self._normalize_task_type(self._request_value(settings, "taskType", "task_type", default=DEFAULT_TASK_TYPE))
        qwen_model_type = (self.model_type or "").lower()
        instruct = self._build_instruct(settings)

        # Base models are voice-clone models, so force VoiceClone for them.
        if qwen_model_type == "base" or task_type == "VoiceClone":
            wav_bytes = self._generate_voice_clone_sync(text, star_id, voice_id, language, settings)
        elif qwen_model_type == "voice_design" or task_type == "VoiceDesign":
            wavs, sample_rate = self.model.generate_voice_design(
                text=text,
                language=language,
                instruct=instruct,
                max_new_tokens=DEFAULT_MAX_NEW_TOKENS,
            )
            wav_bytes = audio_to_wav(wavs[0], sample_rate)
        elif qwen_model_type == "custom_voice" or task_type == "CustomVoice":
            speaker = self._normalize_voice_name(voice_id or star_id)
            wavs, sample_rate = self.model.generate_custom_voice(
                text=text,
                language=language,
                speaker=speaker,
                instruct=instruct or None,
                max_new_tokens=DEFAULT_MAX_NEW_TOKENS,
            )
            wav_bytes = audio_to_wav(wavs[0], sample_rate)
        else:
            raise RuntimeError(f"Unsupported Qwen3-TTS model type '{self.model_type}'. Use Base, CustomVoice, or VoiceDesign.")

        stats = wav_stats_from_bytes(wav_bytes)
        logger.info(
            "Generated Qwen audio: star=%s voice=%s bytes=%s duration=%.2fs peak=%s rms=%.6f",
            star_id,
            voice_id,
            len(wav_bytes),
            stats["duration_seconds"],
            stats["peak"],
            stats["rms"],
        )
        if stats["rms"] <= SILENCE_RMS_THRESHOLD:
            logger.warning("Generated waveform looks silent or near silent: rms=%.8f", stats["rms"])
        if SAVE_SERVER_WAVS or DEBUG_OUTPUT_DIR:
            self._save_debug_wav(wav_bytes, star_id, voice_id, text)
        return wav_bytes

    def _generate_voice_clone_sync(
        self,
        text: str,
        star_id: str,
        voice_id: str,
        language: str,
        settings: Dict[str, Any],
    ) -> bytes:
        voice_clone_audio = self._request_value(settings, "voiceCloneAudio", "voice_clone_audio", "voiceCloneAudioFile", "voice_clone_audio_file", default="")
        voice_clone_ref_text = self._request_value(settings, "voiceCloneRefText", "voice_clone_ref_text", default="")

        x_vector_only = bool(self._request_value(settings, "xVectorOnly", "x_vector_only", default=False))
        ref_audio_path = self._resolve_clone_audio_path(voice_clone_audio)

        logger.info(
            "VoiceClone request: star=%s voice=%s ref_audio=%s ref_text_chars=%s x_vector_only=%s",
            star_id,
            voice_id,
            Path(ref_audio_path).name,
            len(str(voice_clone_ref_text or "")),
            x_vector_only,
        )

        if self.using_faster_backend:
            # Voice Clone Studio's fast CUDA-graph path passes ref_audio/ref_text directly
            # to FasterQwen3TTS.generate_voice_clone instead of building our qwen_tts prompt cache.
            kwargs: Dict[str, Any] = {
                "text": text.strip(),
                "language": language,
                "ref_audio": ref_audio_path,
                "ref_text": "" if x_vector_only else (voice_clone_ref_text or ""),
                "do_sample": True,
                "temperature": float(os.environ.get("QWEN_TTS_TEMPERATURE", "0.9")),
                "top_k": int(os.environ.get("QWEN_TTS_TOP_K", "50")),
                "top_p": float(os.environ.get("QWEN_TTS_TOP_P", "1.0")),
                "repetition_penalty": float(os.environ.get("QWEN_TTS_REPETITION_PENALTY", "1.05")),
                "max_new_tokens": DEFAULT_MAX_NEW_TOKENS,
            }
            try:
                signature = inspect.signature(self.model.generate_voice_clone)
                params = set(signature.parameters.keys())
                if "xvec_only" in params:
                    kwargs["xvec_only"] = x_vector_only
                elif "x_vector_only" in params:
                    kwargs["x_vector_only"] = x_vector_only
            except (TypeError, ValueError):
                kwargs["xvec_only"] = x_vector_only

            logger.info("Using FasterQwen3TTS direct ref_audio voice clone path for %s/%s", star_id, voice_id)
            try:
                wavs, sample_rate = self.model.generate_voice_clone(**kwargs)
                return audio_to_wav(wavs[0], sample_rate)
            except Exception as exc:
                logger.warning(
                    "FasterQwen3TTS generation failed for %s/%s; falling back to official qwen_tts for the rest of this server session. Error: %s: %s",
                    star_id,
                    voice_id,
                    type(exc).__name__,
                    exc,
                )
                self.disable_faster_backend_for_session = True
                self.model_loaded = False
                self.using_faster_backend = False
                self.cuda_graphs_active = False
                self.voice_clone_prompt_cache.clear()
                self.cache_failures.clear()
                self.model = None
                if torch is not None and DEVICE.startswith("cuda"):
                    try:
                        torch.cuda.empty_cache()
                    except Exception:
                        pass
                self._load_model_sync()
                return self._generate_voice_clone_sync(text, star_id, voice_id, language, settings)

        cache_key = self._cache_key(star_id, voice_id, ref_audio_path, str(voice_clone_ref_text or ""), x_vector_only)
        use_cache = USE_CLONE_PROMPT_CACHE and cache_key not in self.cache_failures

        if use_cache:
            with self._cache_lock:
                prompt_items = self.voice_clone_prompt_cache.get(cache_key)
                if prompt_items is None:
                    logger.info("Building reusable Qwen voice clone prompt for %s/%s", star_id, voice_id)
                    prompt_items = self.model.create_voice_clone_prompt(
                        ref_audio=ref_audio_path,
                        ref_text=None if x_vector_only else (voice_clone_ref_text or None),
                        x_vector_only_mode=x_vector_only,
                    )
                    self.voice_clone_prompt_cache[cache_key] = prompt_items
                    logger.info("Cached reusable Qwen voice clone prompt for %s/%s", star_id, voice_id)
                else:
                    logger.info("Using cached Qwen voice clone prompt for %s/%s", star_id, voice_id)

            wavs, sample_rate = self.model.generate_voice_clone(
                text=text,
                language=language,
                voice_clone_prompt=prompt_items,
                max_new_tokens=DEFAULT_MAX_NEW_TOKENS,
            )
            wav_bytes = audio_to_wav(wavs[0], sample_rate)

            stats = wav_stats_from_bytes(wav_bytes)
            if stats["rms"] > SILENCE_RMS_THRESHOLD or not DIRECT_FALLBACK_ON_SILENCE:
                return wav_bytes

            logger.warning(
                "Cached clone prompt produced near-silent output for %s/%s; trying direct ref_audio generation once.",
                star_id,
                voice_id,
            )
            with self._cache_lock:
                self.cache_failures.add(cache_key)

        # Direct fallback is deliberately kept because some local qwen_tts builds can
        # produce silence with cached prompt items but normal direct generation works.
        # The direct call still follows Qwen3-TTS docs: pass ref_audio + ref_text.
        wavs, sample_rate = self.model.generate_voice_clone(
            text=text,
            language=language,
            ref_audio=ref_audio_path,
            ref_text=None if x_vector_only else (voice_clone_ref_text or None),
            max_new_tokens=DEFAULT_MAX_NEW_TOKENS,
        )
        return audio_to_wav(wavs[0], sample_rate)

    def _save_debug_wav(self, wav_bytes: bytes, star_id: str, voice_id: str, text: str) -> None:
        try:
            output_dir = Path(DEBUG_OUTPUT_DIR) if DEBUG_OUTPUT_DIR else Path.cwd()
            output_dir.mkdir(parents=True, exist_ok=True)
            safe_star = re.sub(r"[^A-Za-z0-9_-]+", "_", star_id or "star")
            safe_voice = re.sub(r"[^A-Za-z0-9_-]+", "_", voice_id or "voice")
            name = f"qwen-ws-{time.strftime('%Y%m%d-%H%M%S')}-{safe_star}-{safe_voice}.wav"
            path = output_dir / name
            path.write_bytes(wav_bytes)
            (output_dir / f"{path.stem}.txt").write_text(text, encoding="utf-8")
            logger.info("Saved server debug WAV: %s", path)
        except Exception as exc:
            logger.warning("Could not save server debug WAV: %s", exc)


def audio_to_wav(wav: Any, sample_rate: int) -> bytes:
    """Convert a Qwen waveform object to 16-bit mono WAV bytes."""
    array = waveform_to_float32_numpy(wav)

    if array.ndim == 2:
        # If batched or stereo, prefer first row for batched output; average stereo.
        if array.shape[0] == 1:
            array = array[0]
        elif array.shape[1] == 1:
            array = array[:, 0]
        else:
            # Heuristic: Qwen usually returns [samples], but some tools return [channels, samples].
            if array.shape[0] <= 8 and array.shape[1] > array.shape[0]:
                array = array[0]
            else:
                array = array.mean(axis=1)

    array = np.nan_to_num(array.astype(np.float32), nan=0.0, posinf=0.0, neginf=0.0)
    if array.size == 0:
        raise RuntimeError("Qwen3-TTS returned an empty waveform")

    peak = float(np.max(np.abs(array)))
    # If output is normalized float, clip to [-1, 1]. If it is PCM-ish, normalize.
    if peak > 1.5:
        array = array / max(peak, 1.0)
    array = np.clip(array, -1.0, 1.0)
    pcm = (array * 32767.0).astype(np.int16)

    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(int(sample_rate))
        wav_file.writeframes(pcm.tobytes())
    return buffer.getvalue()


def waveform_to_float32_numpy(wav: Any) -> np.ndarray:
    if torch is not None and isinstance(wav, torch.Tensor):
        wav = wav.detach().float().cpu().numpy()
    elif isinstance(wav, np.ndarray):
        pass
    elif isinstance(wav, (list, tuple)):
        # Qwen generally returns a single ndarray/tensor. This handles nested chunks.
        if len(wav) == 0:
            return np.asarray([], dtype=np.float32)
        if len(wav) == 1:
            return waveform_to_float32_numpy(wav[0])
        pieces = [waveform_to_float32_numpy(x).reshape(-1) for x in wav]
        return np.concatenate(pieces).astype(np.float32)
    else:
        wav = np.asarray(wav)
    return np.asarray(wav, dtype=np.float32)


def wav_stats_from_bytes(wav_bytes: bytes) -> Dict[str, Any]:
    try:
        with wave.open(io.BytesIO(wav_bytes), "rb") as wav_file:
            frames = wav_file.readframes(wav_file.getnframes())
            sample_rate = wav_file.getframerate()
            sample_count = wav_file.getnframes()
        if not frames:
            return {"sample_rate": sample_rate, "samples": sample_count, "duration_seconds": 0.0, "peak": 0, "rms": 0.0}
        pcm = np.frombuffer(frames, dtype=np.int16)
        peak = int(np.max(np.abs(pcm))) if pcm.size else 0
        rms = float(np.sqrt(np.mean(pcm.astype(np.float64) ** 2)) / 32767.0) if pcm.size else 0.0
        return {
            "sample_rate": sample_rate,
            "samples": sample_count,
            "duration_seconds": sample_count / sample_rate if sample_rate else 0.0,
            "peak": peak,
            "rms": rms,
        }
    except Exception:
        return {"sample_rate": 0, "samples": 0, "duration_seconds": 0.0, "peak": 0, "rms": 0.0}


def wav_duration_ms(wav_bytes: bytes) -> float:
    stats = wav_stats_from_bytes(wav_bytes)
    return float(stats.get("duration_seconds", 0.0)) * 1000.0


class TtsSession:
    """Manages one client TTS generation session."""

    def __init__(self, websocket: WebSocketServerProtocol, tts_engine: QwenTtsEngine):
        self.websocket = websocket
        self.tts_engine = tts_engine
        self.current_task: Optional[asyncio.Task] = None
        self.cancelled = False

    async def handle_generation_request(self, request: Dict[str, Any]) -> None:
        request_id = request.get("requestId", 0)
        text = request.get("text", "")
        text_parts = request.get("textParts") or request.get("spokenText") or []
        if isinstance(text_parts, str):
            text_parts = [text_parts]
        if not isinstance(text_parts, list):
            text_parts = []
        text_parts = [str(part).strip() for part in text_parts if str(part).strip()]
        if text_parts:
            text = " ".join(text_parts)
        star_id = request.get("starId", "unknown")
        voice_id = request.get("voiceId", star_id)
        max_words = int(request.get("maxWordsPerChunk", 30) or 30)
        max_sentences = int(request.get("maxSentencesPerChunk", 2) or 2)

        logger.info(
            "Request %s: Generating speech for '%s...' (star=%s, voice=%s)",
            request_id,
            text[:70],
            star_id,
            voice_id,
        )

        await self.cancel_generation()
        self.cancelled = False
        self.current_task = asyncio.create_task(
            self._generate_and_stream(text, star_id, voice_id, request, max_words, max_sentences, text_parts)
        )

        try:
            await self.current_task
        except asyncio.CancelledError:
            logger.info("Request %s: Generation cancelled", request_id)
            await self.send_complete_message(star_id, 0, 0, True)

    async def _generate_and_stream(
        self,
        text: str,
        star_id: str,
        voice_id: str,
        settings: Dict[str, Any],
        max_words: int,
        max_sentences: int,
        explicit_text_parts: Optional[List[str]] = None,
    ) -> None:
        if explicit_text_parts:
            total = len(explicit_text_parts)
            chunks = [TextChunk(index=i, text=part, total_chunks=total) for i, part in enumerate(explicit_text_parts)]
            logger.info("Using %s AI-provided spokenText chunks; deterministic text splitting disabled", len(chunks))
        else:
            chunks = TextChunker.chunk_text(text, max_words, max_sentences)
            logger.info("Split into %s chunks", len(chunks))
        if not chunks:
            await self.send_error_message("No text to generate", star_id)
            return

        mode = _resolve_chunk_generation_mode(settings)
        max_parallel = _resolve_max_parallel_chunks(settings, MAX_PARALLEL_CHUNKS)
        after_first_max_parallel = _resolve_max_parallel_chunks(settings, AFTER_FIRST_MAX_PARALLEL_CHUNKS)
        logger.info(
            "Chunk generation mode resolved: mode=%s, max_parallel=%s, after_first_max_parallel=%s",
            mode,
            max_parallel if max_parallel > 0 else "all",
            after_first_max_parallel if after_first_max_parallel > 0 else "all",
        )

        if mode in {"parallel", "all_parallel", "legacy_parallel"} and len(chunks) > 1:
            await self._generate_and_stream_parallel(chunks, star_id, voice_id, settings, max_parallel)
        elif mode in {"first_then_parallel", "staggered_parallel"} and len(chunks) > 1:
            await self._generate_and_stream_first_then_prefetch(
                chunks, star_id, voice_id, settings, parallel_after_first=True, max_parallel_after_first=after_first_max_parallel
            )
        elif mode in {"first_then_prefetch", "pipelined", "prefetch"} and len(chunks) > 1:
            await self._generate_and_stream_first_then_prefetch(
                chunks, star_id, voice_id, settings, parallel_after_first=False, max_parallel_after_first=1
            )
        else:
            await self._generate_and_stream_sequential(chunks, star_id, voice_id, settings)

    async def _generate_single_chunk(
        self,
        chunk: TextChunk,
        star_id: str,
        voice_id: str,
        settings: Dict[str, Any],
        semaphore: Optional[asyncio.Semaphore],
    ) -> GeneratedChunk:
        if semaphore is None:
            return await self._generate_single_chunk_inner(chunk, star_id, voice_id, settings)
        async with semaphore:
            return await self._generate_single_chunk_inner(chunk, star_id, voice_id, settings)

    async def _generate_single_chunk_inner(
        self,
        chunk: TextChunk,
        star_id: str,
        voice_id: str,
        settings: Dict[str, Any],
    ) -> GeneratedChunk:
        start_time = time.monotonic()
        logger.info(
            "Generating chunk %s/%s: '%s...'",
            chunk.index + 1,
            chunk.total_chunks,
            chunk.text[:60],
        )
        audio_data = await self.tts_engine.generate_audio(chunk.text, star_id, voice_id, settings)
        duration_ms = wav_duration_ms(audio_data)
        elapsed_ms = (time.monotonic() - start_time) * 1000.0
        logger.info(
            "Generated chunk %s/%s in %.0fms (%s bytes, %.0fms audio)",
            chunk.index + 1,
            chunk.total_chunks,
            elapsed_ms,
            len(audio_data),
            duration_ms,
        )
        return GeneratedChunk(chunk=chunk, audio_data=audio_data, duration_ms=duration_ms, elapsed_ms=elapsed_ms)

    async def _generate_and_stream_first_then_prefetch(
        self,
        chunks: List[TextChunk],
        star_id: str,
        voice_id: str,
        settings: Dict[str, Any],
        parallel_after_first: bool,
        max_parallel_after_first: int = 1,
    ) -> None:
        """Generate the first chunk by itself, send it immediately, then prefetch the rest.

        This is the safest low-latency mode for local Qwen3-TTS on one GPU/model instance:
        the first audible phrase is not competing with later chunk generation, and chunk 2
        starts as soon as chunk 1 is sent so the client can play chunk 1 while the server
        prepares the next audio. Remaining chunks are always sent in order.
        """
        total_start = time.monotonic()
        total_duration_ms = 0.0
        first = chunks[0]
        remaining = chunks[1:]

        logger.info(
            "First-chunk prefetch mode: generating chunk 1/%s alone, then %s remaining chunk(s) with %s.",
            len(chunks),
            len(remaining),
            "bounded parallelism" if parallel_after_first else "single-generator prefetch",
        )

        try:
            if self.cancelled:
                return

            generated_first = await self._generate_single_chunk(first, star_id, voice_id, settings, None)
            total_duration_ms += generated_first.duration_ms
            await self.send_chunk_info(first.index, first.total_chunks, first.text, star_id)
            await self.send_audio_chunk(
                first.index,
                first.total_chunks,
                first.text,
                star_id,
                voice_id,
                generated_first.audio_data,
            )
            logger.info(
                "Sent first chunk %s/%s immediately (%s bytes). Starting next chunk generation now.",
                first.index + 1,
                first.total_chunks,
                len(generated_first.audio_data),
            )

            if not remaining:
                if not self.cancelled:
                    await self.send_complete_message(star_id, len(chunks), total_duration_ms, False)
                return

            if parallel_after_first:
                max_parallel = max_parallel_after_first if max_parallel_after_first > 0 else len(remaining)
            else:
                max_parallel = 1
            semaphore = asyncio.Semaphore(max_parallel)

            # Create all remaining tasks now. With max_parallel=1, chunk 2 starts immediately while
            # the client plays chunk 1, and later chunks wait their turn without competing for GPU.
            tasks: Dict[int, asyncio.Task[GeneratedChunk]] = {
                chunk.index: asyncio.create_task(
                    self._generate_single_chunk(chunk, star_id, voice_id, settings, semaphore)
                )
                for chunk in remaining
            }

            for chunk in remaining:
                if self.cancelled:
                    logger.info(
                        "First-chunk prefetch generation cancelled before sending chunk %s/%s",
                        chunk.index + 1,
                        chunk.total_chunks,
                    )
                    break

                generated = await tasks[chunk.index]
                total_duration_ms += generated.duration_ms
                await self.send_chunk_info(chunk.index, chunk.total_chunks, chunk.text, star_id)
                await self.send_audio_chunk(
                    chunk.index,
                    chunk.total_chunks,
                    chunk.text,
                    star_id,
                    voice_id,
                    generated.audio_data,
                )
                logger.info(
                    "Sent prefetched chunk %s/%s in order (%s bytes). Ready tasks: %s/%s",
                    chunk.index + 1,
                    chunk.total_chunks,
                    len(generated.audio_data),
                    sum(1 for task in tasks.values() if task.done()),
                    len(tasks),
                )

            if not self.cancelled:
                total_elapsed_ms = (time.monotonic() - total_start) * 1000.0
                await self.send_complete_message(star_id, len(chunks), total_duration_ms, False)
                logger.info(
                    "First-chunk prefetch complete: %s chunks, %.0fms audio, %.0fms wall time",
                    len(chunks),
                    total_duration_ms,
                    total_elapsed_ms,
                )
        except Exception as exc:
            logger.error("Error during first-chunk prefetch generation: %s", exc, exc_info=True)
            await self.send_error_message(f"Generation error: {type(exc).__name__}: {exc}", star_id)
            return
        finally:
            if 'tasks' in locals() and self.cancelled:
                for task in tasks.values():
                    if not task.done():
                        task.cancel()
                await asyncio.gather(*tasks.values(), return_exceptions=True)

    async def _generate_and_stream_parallel(
        self,
        chunks: List[TextChunk],
        star_id: str,
        voice_id: str,
        settings: Dict[str, Any],
        max_parallel_override: int = 0,
    ) -> None:
        total_start = time.monotonic()
        total_duration_ms = 0.0
        max_parallel = max_parallel_override if max_parallel_override > 0 else len(chunks)
        semaphore = asyncio.Semaphore(max_parallel) if max_parallel > 0 else None

        logger.info(
            "Parallel chunk generation enabled: starting %s chunk tasks, max_parallel=%s. "
            "Chunks will still be sent to the client in order.",
            len(chunks),
            max_parallel,
        )

        tasks: Dict[int, asyncio.Task[GeneratedChunk]] = {
            chunk.index: asyncio.create_task(
                self._generate_single_chunk(chunk, star_id, voice_id, settings, semaphore)
            )
            for chunk in chunks
        }

        try:
            for chunk in chunks:
                if self.cancelled:
                    logger.info("Parallel generation cancelled before sending chunk %s/%s", chunk.index + 1, chunk.total_chunks)
                    break

                # Await the next chunk in order. Later chunks may already be generated;
                # they stay buffered in their task result until their turn arrives.
                generated = await tasks[chunk.index]
                total_duration_ms += generated.duration_ms
                await self.send_chunk_info(chunk.index, chunk.total_chunks, chunk.text, star_id)
                await self.send_audio_chunk(
                    chunk.index,
                    chunk.total_chunks,
                    chunk.text,
                    star_id,
                    voice_id,
                    generated.audio_data,
                )
                logger.info(
                    "Sent chunk %s/%s in order (%s bytes). Buffered ready chunks: %s/%s",
                    chunk.index + 1,
                    chunk.total_chunks,
                    len(generated.audio_data),
                    sum(1 for task in tasks.values() if task.done()),
                    len(tasks),
                )

            if not self.cancelled:
                total_elapsed_ms = (time.monotonic() - total_start) * 1000.0
                await self.send_complete_message(star_id, len(chunks), total_duration_ms, False)
                logger.info(
                    "Parallel generation complete: %s chunks, %.0fms audio, %.0fms wall time",
                    len(chunks),
                    total_duration_ms,
                    total_elapsed_ms,
                )
        except Exception as exc:
            logger.error("Error during parallel chunk generation: %s", exc, exc_info=True)
            for task in tasks.values():
                if not task.done():
                    task.cancel()
            await asyncio.gather(*tasks.values(), return_exceptions=True)
            await self.send_error_message(f"Generation error: {type(exc).__name__}: {exc}", star_id)
            return
        finally:
            if self.cancelled:
                for task in tasks.values():
                    if not task.done():
                        task.cancel()
                await asyncio.gather(*tasks.values(), return_exceptions=True)

    async def _generate_and_stream_sequential(
        self,
        chunks: List[TextChunk],
        star_id: str,
        voice_id: str,
        settings: Dict[str, Any],
    ) -> None:
        total_duration_ms = 0.0
        for chunk in chunks:
            if self.cancelled:
                logger.info("Chunk %s/%s cancelled", chunk.index + 1, chunk.total_chunks)
                break

            try:
                generated = await self._generate_single_chunk(chunk, star_id, voice_id, settings, None)
                total_duration_ms += generated.duration_ms
                await self.send_chunk_info(chunk.index, chunk.total_chunks, chunk.text, star_id)
                await self.send_audio_chunk(chunk.index, chunk.total_chunks, chunk.text, star_id, voice_id, generated.audio_data)
                logger.info("Sent chunk %s/%s (%s bytes)", chunk.index + 1, chunk.total_chunks, len(generated.audio_data))
            except Exception as exc:
                logger.error("Error generating chunk %s: %s", chunk.index + 1, exc, exc_info=True)
                await self.send_error_message(f"Generation error: {type(exc).__name__}: {exc}", star_id)
                return

        if not self.cancelled:
            await self.send_complete_message(star_id, len(chunks), total_duration_ms, False)
            logger.info("Generation complete: %s chunks, %.0fms", len(chunks), total_duration_ms)

    async def send_chunk_info(self, chunk_index: int, total_chunks: int, text: str, star_id: str) -> None:
        message = {
            "type": "chunk_info",
            "chunkIndex": chunk_index,
            "totalChunks": total_chunks,
            "text": text[:80] + "..." if len(text) > 80 else text,
            "starId": star_id,
        }
        await self.websocket.send(json.dumps(message))

    async def send_audio_chunk(
        self,
        chunk_index: int,
        total_chunks: int,
        text: str,
        star_id: str,
        voice_id: str,
        audio_data: bytes,
    ) -> None:
        text_bytes = text.encode("utf-8")
        star_id_bytes = star_id.encode("utf-8")
        voice_id_bytes = voice_id.encode("utf-8")

        message = b""
        message += struct.pack("<i", chunk_index)
        message += struct.pack("<i", total_chunks)
        message += struct.pack("<i", len(text_bytes))
        message += text_bytes
        message += struct.pack("<i", len(star_id_bytes))
        message += star_id_bytes
        message += struct.pack("<i", len(voice_id_bytes))
        message += voice_id_bytes
        message += audio_data
        await self.websocket.send(message)

    async def send_complete_message(self, star_id: str, total_chunks: int, total_duration_ms: float, cancelled: bool) -> None:
        message = {
            "type": "complete",
            "starId": star_id,
            "totalChunks": total_chunks,
            "totalDurationMs": total_duration_ms,
            "cancelled": cancelled,
        }
        await self.websocket.send(json.dumps(message))

    async def send_error_message(self, error_message: str, star_id: str = "") -> None:
        message = {"type": "error", "message": error_message, "starId": star_id}
        await self.websocket.send(json.dumps(message))

    async def cancel_generation(self) -> None:
        if self.current_task and not self.current_task.done():
            self.cancelled = True
            self.current_task.cancel()
            try:
                await self.current_task
            except asyncio.CancelledError:
                pass


ENGINE = QwenTtsEngine()


async def handle_client(websocket: WebSocketServerProtocol, path: str) -> None:
    client_id = f"{websocket.remote_address[0]}:{websocket.remote_address[1]}"
    logger.info("Client connected: %s", client_id)
    session = TtsSession(websocket, ENGINE)

    try:
        await websocket.send(json.dumps({
            "type": "status",
            "message": "Connected to Qwen3-TTS WebSocket server",
            "backend": "qwen3-tts",
            "model": MODEL_PATH,
            "device": DEVICE,
            "cudaGraphsRequested": USE_CUDA_GRAPHS,
            "cudaGraphsActive": ENGINE.cuda_graphs_active,
            "fasterBackend": ENGINE.using_faster_backend,
            "torchCudaAvailable": bool(torch is not None and getattr(torch, "cuda", None) is not None and torch.cuda.is_available()),
        }))

        async for message in websocket:
            try:
                if isinstance(message, str):
                    data = json.loads(message)
                    msg_type = data.get("type", "")
                    logger.debug("Received message type: %s", msg_type)
                    if msg_type == "generate":
                        await session.handle_generation_request(data)
                    elif msg_type == "cancel":
                        logger.info("Cancel request received")
                        await session.cancel_generation()
                    else:
                        logger.warning("Unknown message type: %s", msg_type)
                else:
                    logger.warning("Received unexpected binary message from client")
            except json.JSONDecodeError as exc:
                logger.error("JSON decode error: %s", exc)
                await session.send_error_message(f"Invalid JSON: {exc}")
            except Exception as exc:
                logger.error("Error processing message: %s", exc, exc_info=True)
                await session.send_error_message(f"Processing error: {type(exc).__name__}: {exc}")
    except ConnectionClosed:
        logger.info("Client disconnected: %s", client_id)
    except Exception as exc:
        logger.error("Client error: %s", exc, exc_info=True)
    finally:
        await session.cancel_generation()
        logger.info("Client session ended: %s", client_id)


async def main() -> None:
    logger.info("Starting Qwen3-TTS WebSocket server on ws://%s:%s/tts/stream", HOST, PORT)
    logger.info(
        "Chunk generation mode: %s; legacy_parallel_flag=%s; max_parallel=%s; after_first_max_parallel=%s; cuda_graphs=%s; faster_qwen3=%s; model=%s",
        CHUNK_GENERATION_MODE,
        PARALLEL_CHUNK_GENERATION,
        MAX_PARALLEL_CHUNKS if MAX_PARALLEL_CHUNKS > 0 else "all",
        AFTER_FIRST_MAX_PARALLEL_CHUNKS,
        USE_CUDA_GRAPHS,
        USE_FASTER_QWEN3,
        MODEL_PATH,
    )
    logger.info("Model will lazy-load on first request. Press Ctrl+C to stop.")
    async with serve(handle_client, HOST, PORT, subprotocols=None, max_size=32 * 1024 * 1024):
        await asyncio.Future()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("Server stopped by user")
