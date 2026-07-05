#!/usr/bin/env python3
"""OpenAI-compatible local LLM server for Six Stars.

This is intentionally small and generic: it loads a Hugging Face Transformers
chat/instruct model once and exposes:

    GET  /health
    GET  /v1/models
    POST /v1/chat/completions

The desktop app and Unity can both call this instead of talking directly to LM
Studio. Put local model folders under ./models/llm, or set LOCAL_LLM_MODEL_PATH.
If the folder is missing, Transformers downloads the repo into ./models/hf.
"""

from __future__ import annotations

import json
import logging
import os
import time
import uuid
from dataclasses import dataclass
from pathlib import Path
from threading import Lock, Thread
from typing import Any, Dict, List, Optional

from flask import Flask, Response, jsonify, request

from model_paths import configure_model_environment, describe_model_environment, resolve_model_ref

configure_model_environment()

logging.basicConfig(
    level=getattr(logging, os.environ.get("LOCAL_LLM_LOG_LEVEL", "INFO").upper(), logging.INFO),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger("six-stars-local-llm")

app = Flask(__name__)


def parse_bool(value: str | None, default: bool) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def parse_int(value: str | None, default: int) -> int:
    if value is None or not value.strip():
        return default
    return int(value)


def normalize_llm_model_id(value: str | None) -> str:
    """Accept short friendly names and return the canonical Hugging Face id.

    The launcher and app default to the small Gemma E2B model now, but this lets
    you type LOCAL_LLM_MODEL=gemma-e2b or LOCAL_LLM_MODEL=e2b in launch.cmd
    without accidentally asking Transformers for a non-existent repo id.
    """
    raw = (value or "").strip()
    if not raw:
        return "google/gemma-4-E2B-it"

    key = raw.lower().replace("_", "-").replace("/", "-")
    aliases = {
        "e2b": "google/gemma-4-E2B-it",
        "gemma-e2b": "google/gemma-4-E2B-it",
        "gemma-2b": "google/gemma-4-E2B-it",
        "gemma4-e2b": "google/gemma-4-E2B-it",
        "gemma-4-e2b": "google/gemma-4-E2B-it",
        "gemma-4-e2b-it": "google/gemma-4-E2B-it",
        "google-gemma-4-e2b-it": "google/gemma-4-E2B-it",
        "e4b": "google/gemma-4-E4B-it",
        "gemma-e4b": "google/gemma-4-E4B-it",
        "gemma-4b": "google/gemma-4-E4B-it",
        "gemma4-e4b": "google/gemma-4-E4B-it",
        "gemma-4-e4b": "google/gemma-4-E4B-it",
        "gemma-4-e4b-it": "google/gemma-4-E4B-it",
        "google-gemma-4-e4b-it": "google/gemma-4-E4B-it",
    }
    return aliases.get(key, raw)


HOST = os.environ.get("LOCAL_LLM_HOST", "0.0.0.0")
PORT = parse_int(os.environ.get("LOCAL_LLM_PORT") or os.environ.get("LLM_PORT"), 8200)
MODEL_ID = normalize_llm_model_id(os.environ.get("LOCAL_LLM_MODEL") or os.environ.get("SIX_STARS_LLM_MODEL"))
MODEL_PATH = os.environ.get("LOCAL_LLM_MODEL_PATH", "").strip()
REQUIRE_CUDA = os.environ.get("SIX_STARS_REQUIRE_CUDA", "true").strip().lower() not in {"0", "false", "no", "off"}
DEVICE = os.environ.get("LOCAL_LLM_DEVICE", "cuda:0").strip() or "cuda:0"
DTYPE = os.environ.get("LOCAL_LLM_DTYPE", "auto")
ATTN_IMPLEMENTATION = os.environ.get("LOCAL_LLM_ATTN_IMPLEMENTATION", "auto")
PRELOAD = parse_bool(os.environ.get("LOCAL_LLM_PRELOAD"), True)
MAX_NEW_TOKENS_DEFAULT = parse_int(os.environ.get("LOCAL_LLM_MAX_NEW_TOKENS"), 2048)
API_KEY = os.environ.get("LOCAL_LLM_API_KEY") or os.environ.get("SIX_STARS_LLM_API_KEY") or ""
TRUST_REMOTE_CODE = parse_bool(os.environ.get("LOCAL_LLM_TRUST_REMOTE_CODE"), False)
LOAD_IN_4BIT = parse_bool(os.environ.get("LOCAL_LLM_LOAD_IN_4BIT"), False)
STRICT_GEMMA4_VERSION_CHECK = parse_bool(os.environ.get("LOCAL_LLM_STRICT_GEMMA4_VERSION_CHECK"), False)
TOKENIZER_USE_FAST = os.environ.get("LOCAL_LLM_TOKENIZER_USE_FAST", "auto").strip().lower()

_model = None
_tokenizer = None
_model_lock = Lock()
_loaded_model_ref: str | None = None
_last_model_load_error: str = ""
_model_loading: bool = False
_model_load_started_at: float = 0.0
_model_load_finished_at: float = 0.0


def require_api_key() -> Response | None:
    if not API_KEY:
        return None
    auth_header = request.headers.get("Authorization", "")
    token = auth_header.removeprefix("Bearer ").strip() if auth_header else ""
    provided = token or request.headers.get("X-API-Key", "").strip()
    if provided == API_KEY:
        return None
    return jsonify({"error": {"message": "Invalid or missing API key.", "type": "invalid_request_error", "param": None, "code": "invalid_api_key"}}), 401


def resolved_model_ref() -> str:
    return resolve_model_ref(
        "llm",
        MODEL_ID,
        MODEL_PATH,
        aliases=[
            "gemma",
            "gemma-4-E2B-it",
            "gemma-2b",
            "gemma-3-4b-it",
            "google__gemma-4-E2B-it",
            "google_gemma-4-E2B-it",
        ],
    )


def require_cuda_runtime(torch_module) -> None:
    if not REQUIRE_CUDA:
        return
    if DEVICE == "auto" or DEVICE.startswith("cpu"):
        raise RuntimeError(
            "Six Stars source LLM is configured for CPU/auto. This build requires CUDA/GPU. "
            "Use the llama.cpp NVIDIA engine pack in config/runtime-manifest.json or use a hosted service option."
        )
    if not getattr(torch_module, "cuda", None) or not torch_module.cuda.is_available():
        raise RuntimeError(
            "Six Stars source LLM requires CUDA/GPU, but torch.cuda.is_available() is false. "
            "Use the llama.cpp NVIDIA engine pack or use a hosted service option."
        )


def torch_dtype_from_name(torch_module):
    value = DTYPE.strip().lower()
    if value in {"auto", ""}:
        return "auto"
    if value in {"float16", "fp16", "half"}:
        return torch_module.float16
    if value in {"bfloat16", "bf16"}:
        return torch_module.bfloat16
    if value in {"float32", "fp32"}:
        return torch_module.float32
    return "auto"


def tokenizer_fast_candidates(model_ref: str) -> list[bool]:
    """Return tokenizer fast/slow preference order.

    The Gemma tokenizer files on some recent Hugging Face snapshots can contain
    extra_special_tokens as a list. Older/newer Transformers combinations may
    make GemmaTokenizerFast crash with: 'list' object has no attribute 'keys'.
    Loading the slow tokenizer first avoids that crash and is fine for this local
    desktop service because model generation dominates latency.
    """
    if TOKENIZER_USE_FAST in {"true", "1", "yes", "on"}:
        return [True, False]
    if TOKENIZER_USE_FAST in {"false", "0", "no", "off"}:
        return [False, True]
    return [False, True] if "gemma" in model_ref.lower() else [True, False]




def _version_tuple(version_text: str) -> tuple[int, int, int]:
    """Parse a package version into a simple comparable tuple."""
    import re

    parts = re.findall(r"\d+", version_text or "")[:3]
    values = [int(part) for part in parts]
    while len(values) < 3:
        values.append(0)
    return tuple(values[:3])


def _looks_like_gemma4(model_ref: str) -> bool:
    lowered = (model_ref or "").lower()
    if "gemma-4" in lowered or "gemma4" in lowered:
        return True

    path = Path(model_ref)
    if path.exists():
        config_path = path / "config.json"
        if config_path.exists():
            try:
                data = json.loads(config_path.read_text(encoding="utf-8"))
                model_type = str(data.get("model_type", "")).lower()
                architectures = " ".join(str(x).lower() for x in data.get("architectures", []) if x)
                return model_type == "gemma4" or "gemma4" in architectures
            except Exception:
                return False
    return False


def ensure_transformers_supports_model(model_ref: str) -> None:
    import transformers

    version = getattr(transformers, "__version__", "0.0.0")
    if _looks_like_gemma4(model_ref) and _version_tuple(version) < (5, 5, 0):
        message = (
            "Gemma 4 support depends on a recent Transformers install. "
            f"Your environment has transformers {version}. "
            "If loading fails, run: python -m pip install --upgrade transformers huggingface-hub accelerate safetensors sentencepiece protobuf. "
            "This server will still try AutoProcessor and slow-tokenizer fallback first because those often avoid the GemmaTokenizerFast extra_special_tokens crash."
        )
        if STRICT_GEMMA4_VERSION_CHECK:
            raise RuntimeError(message)
        logger.warning(message)


def _set_pad_token_if_possible(tokenizer_like: Any) -> None:
    tokenizer = getattr(tokenizer_like, "tokenizer", tokenizer_like)
    try:
        if getattr(tokenizer, "pad_token_id", None) is None and getattr(tokenizer, "eos_token", None):
            tokenizer.pad_token = tokenizer.eos_token
    except Exception:
        pass


def load_tokenizer(model_ref: str):
    """Load the tokenizer/processor object used for prompt rendering and decoding.

    Gemma 4 is documented primarily through AutoProcessor in Transformers, while
    many text-only models work through AutoTokenizer. We try AutoProcessor first
    for Gemma 4, then fall back to AutoTokenizer with fast/slow tokenizer choices.
    """
    errors: list[str] = []

    if _looks_like_gemma4(model_ref):
        try:
            from transformers import AutoProcessor

            processor = AutoProcessor.from_pretrained(
                model_ref,
                trust_remote_code=TRUST_REMOTE_CODE,
                padding_side="left",
            )
            _set_pad_token_if_possible(processor)
            logger.info("Processor loaded for Gemma 4 model %s with AutoProcessor", model_ref)
            return processor
        except Exception as exc:
            detail = f"AutoProcessor: {type(exc).__name__}: {exc}"
            errors.append(detail)
            logger.warning("AutoProcessor load attempt failed: %s", detail)

    from transformers import AutoTokenizer

    for use_fast in tokenizer_fast_candidates(model_ref):
        for kwargs in (
            {"use_fast": use_fast},
            {"use_fast": use_fast, "extra_special_tokens": {}},
        ):
            try:
                tokenizer = AutoTokenizer.from_pretrained(
                    model_ref,
                    trust_remote_code=TRUST_REMOTE_CODE,
                    **kwargs,
                )
                _set_pad_token_if_possible(tokenizer)
                logger.info("Tokenizer loaded for %s with use_fast=%s", model_ref, use_fast)
                return tokenizer
            except Exception as exc:
                detail = f"use_fast={use_fast}, kwargs={kwargs}: {type(exc).__name__}: {exc}"
                errors.append(detail)
                logger.warning("Tokenizer load attempt failed: %s", detail)

    raise RuntimeError("Could not load tokenizer/processor for " + model_ref + ". Attempts: " + " | ".join(errors))


def encode_prompt(tokenizer_like: Any, prompt: str):
    try:
        return tokenizer_like(text=prompt, return_tensors="pt")
    except TypeError:
        return tokenizer_like(prompt, return_tensors="pt")


def decode_generated_text(tokenizer_like: Any, token_ids: Any, skip_special_tokens: bool = True) -> str:
    if hasattr(tokenizer_like, "decode"):
        return tokenizer_like.decode(token_ids, skip_special_tokens=skip_special_tokens).strip()
    inner = getattr(tokenizer_like, "tokenizer", None)
    if inner is not None and hasattr(inner, "decode"):
        return inner.decode(token_ids, skip_special_tokens=skip_special_tokens).strip()
    raise RuntimeError("Loaded tokenizer/processor does not expose decode().")


def eos_token_id(tokenizer_like: Any) -> int | None:
    for obj in (tokenizer_like, getattr(tokenizer_like, "tokenizer", None)):
        if obj is None:
            continue
        value = getattr(obj, "eos_token_id", None)
        if value is not None:
            return int(value)
    return None

def load_causal_model(model_ref: str, kwargs: dict[str, Any]):
    from transformers import AutoModelForCausalLM

    try:
        return AutoModelForCausalLM.from_pretrained(model_ref, **kwargs)
    except Exception as causal_exc:
        # Some newer Gemma-family checkpoints expose a conditional generation class
        # rather than a plain causal LM class. Try the newer AutoModel alias when it
        # exists, then surface both errors if it still cannot load.
        try:
            from transformers import AutoModelForImageTextToText
            logger.warning("AutoModelForCausalLM failed for %s; trying AutoModelForImageTextToText. Error: %s", model_ref, causal_exc)
            return AutoModelForImageTextToText.from_pretrained(model_ref, **kwargs)
        except Exception as alt_exc:
            raise RuntimeError(
                "Could not load model with AutoModelForCausalLM or AutoModelForImageTextToText. "
                + "CausalLM error: " + str(causal_exc) + " | ImageTextToText error: " + str(alt_exc)
            ) from alt_exc

def get_model():
    global _model, _tokenizer, _loaded_model_ref, _model_loading, _model_load_started_at, _model_load_finished_at, _last_model_load_error
    if _model is not None and _tokenizer is not None:
        return _model, _tokenizer

    with _model_lock:
        if _model is not None and _tokenizer is not None:
            return _model, _tokenizer

        import torch
        require_cuda_runtime(torch)

        model_ref = resolved_model_ref()
        _model_loading = True
        _model_load_started_at = time.time()
        _last_model_load_error = ""
        logger.info("Loading local LLM model %s", model_ref)
        logger.info("Model paths: %s", json.dumps(describe_model_environment(), indent=2))

        try:
            ensure_transformers_supports_model(model_ref)
            tokenizer = load_tokenizer(model_ref)
        except Exception as exc:
            _last_model_load_error = str(exc)
            _model_loading = False
            _model_load_finished_at = time.time()
            raise
        kwargs: dict[str, Any] = {
            "trust_remote_code": TRUST_REMOTE_CODE,
            "low_cpu_mem_usage": True,
        }

        dtype = torch_dtype_from_name(torch)
        if dtype != "auto":
            kwargs["torch_dtype"] = dtype
        else:
            kwargs["torch_dtype"] = "auto"

        kwargs["device_map"] = DEVICE

        if ATTN_IMPLEMENTATION and ATTN_IMPLEMENTATION.lower() != "auto":
            kwargs["attn_implementation"] = ATTN_IMPLEMENTATION

        if LOAD_IN_4BIT:
            kwargs["load_in_4bit"] = True

        try:
            model = load_causal_model(model_ref, kwargs)
            model.eval()
        except Exception as exc:
            _last_model_load_error = str(exc)
            _model_loading = False
            _model_load_finished_at = time.time()
            raise

        _model = model
        _tokenizer = tokenizer
        _loaded_model_ref = model_ref
        _last_model_load_error = ""
        _model_loading = False
        _model_load_finished_at = time.time()
        logger.info("Local LLM loaded: %s", model_ref)
        return _model, _tokenizer


def render_prompt(tokenizer, messages: List[Dict[str, str]], wants_json: bool) -> str:
    safe_messages = []
    for message in messages:
        role = str(message.get("role", "user") or "user")
        content = str(message.get("content", "") or "")
        safe_messages.append({"role": role, "content": content})

    if wants_json:
        # Keep it outside the schema because many local models ignore response_format.
        safe_messages.append({
            "role": "user",
            "content": "Return only one valid JSON object that matches the requested fields. Do not use markdown fences, commentary, or extra text.",
        })

    if hasattr(tokenizer, "apply_chat_template"):
        try:
            return tokenizer.apply_chat_template(safe_messages, tokenize=False, add_generation_prompt=True)
        except Exception as exc:
            logger.warning("apply_chat_template failed, falling back to simple prompt: %s", exc)

    chunks = []
    for message in safe_messages:
        chunks.append(f"{message['role'].upper()}: {message['content']}")
    chunks.append("ASSISTANT:")
    return "\n\n".join(chunks)


def extract_generated_text(tokenizer, outputs, prompt_input_ids):
    generated_ids = outputs[0][prompt_input_ids.shape[-1]:]
    return decode_generated_text(tokenizer, generated_ids, skip_special_tokens=True)


def coerce_messages(payload: Dict[str, Any]) -> List[Dict[str, str]]:
    messages = payload.get("messages")
    if not isinstance(messages, list) or not messages:
        raise ValueError("messages must be a non-empty array")
    result = []
    for item in messages:
        if not isinstance(item, dict):
            continue
        result.append({"role": str(item.get("role", "user")), "content": str(item.get("content", ""))})
    if not result:
        raise ValueError("messages did not contain any usable chat messages")
    return result


def model_status() -> str:
    if _model is not None and not _last_model_load_error:
        return "ready"
    if _model_loading:
        return "loading"
    if _last_model_load_error:
        return "error"
    return "not_loaded"


@app.get("/health")
def health():
    return jsonify({
        "ok": _model is not None and not _last_model_load_error,
        "ready": _model is not None and not _last_model_load_error,
        "status": model_status(),
        "service": "six-stars-local-llm",
        "model": MODEL_ID,
        "resolvedModel": resolved_model_ref(),
        "loaded": _model is not None,
        "loading": _model_loading,
        "lastLoadError": _last_model_load_error,
        "loadStartedAt": _model_load_started_at,
        "loadFinishedAt": _model_load_finished_at,
        "tokenizerUseFast": TOKENIZER_USE_FAST,
        "strictGemma4VersionCheck": STRICT_GEMMA4_VERSION_CHECK,
        "paths": describe_model_environment(),
    })


@app.get("/v1/models")
def models():
    auth_error = require_api_key()
    if auth_error is not None:
        return auth_error
    return jsonify({
        "object": "list",
        "data": [
            {
                "id": MODEL_ID,
                "object": "model",
                "created": int(time.time()),
                "owned_by": "six-stars-local",
                "resolved_path": resolved_model_ref(),
                "loaded": _model is not None,
                "loading": _model_loading,
                "status": model_status(),
                "lastLoadError": _last_model_load_error,
            }
        ],
    })


@app.post("/v1/chat/completions")
def chat_completions():
    auth_error = require_api_key()
    if auth_error is not None:
        return auth_error

    try:
        payload = request.get_json(force=True) or {}
        messages = coerce_messages(payload)
        response_format = payload.get("response_format")
        wants_json = isinstance(response_format, dict) and str(response_format.get("type", "")).lower() in {"json_object", "json_schema"}
        temperature = float(payload.get("temperature", 0.65) or 0.65)
        max_new_tokens = int(payload.get("max_tokens") or MAX_NEW_TOKENS_DEFAULT)
        top_p = float(payload.get("top_p", 0.95) or 0.95)

        model, tokenizer = get_model()
        prompt = render_prompt(tokenizer, messages, wants_json)
        inputs = encode_prompt(tokenizer, prompt)
        try:
            device = next(model.parameters()).device
            inputs = {key: value.to(device) for key, value in inputs.items()}
        except Exception:
            pass

        generation_kwargs: dict[str, Any] = {
            "max_new_tokens": max_new_tokens,
            "do_sample": temperature > 0,
            "temperature": max(0.001, temperature),
            "top_p": top_p,
        }
        eos_id = eos_token_id(tokenizer)
        if eos_id is not None:
            generation_kwargs["pad_token_id"] = eos_id

        start = time.time()
        with _model_lock:
            outputs = model.generate(**inputs, **generation_kwargs)
        elapsed_ms = int((time.time() - start) * 1000)
        text = extract_generated_text(tokenizer, outputs, inputs["input_ids"])

        return jsonify({
            "id": "chatcmpl-" + uuid.uuid4().hex,
            "object": "chat.completion",
            "created": int(time.time()),
            "model": payload.get("model") or MODEL_ID,
            "choices": [
                {
                    "index": 0,
                    "message": {"role": "assistant", "content": text},
                    "finish_reason": "stop",
                }
            ],
            "usage": {
                "prompt_tokens": int(inputs["input_ids"].shape[-1]),
                "completion_tokens": int(outputs[0].shape[-1] - inputs["input_ids"].shape[-1]),
                "total_tokens": int(outputs[0].shape[-1]),
            },
            "stats": {"elapsed_ms": elapsed_ms, "loaded_model_ref": _loaded_model_ref or resolved_model_ref()},
        })
    except Exception as exc:
        global _last_model_load_error
        _last_model_load_error = str(exc)
        logger.exception("chat completion failed")
        return jsonify({"error": {"message": str(exc), "type": "server_error", "param": None, "code": None}}), 500


def preload_model_background() -> None:
    try:
        get_model()
    except Exception as exc:
        global _last_model_load_error
        _last_model_load_error = str(exc)
        logger.exception("Preload failed. /health will report status=error; first chat request will retry.")


if __name__ == "__main__":
    logger.info("Starting Six Stars local LLM server on %s:%s", HOST, PORT)
    logger.info("Configured model id: %s", MODEL_ID)
    logger.info("Resolved model ref: %s", resolved_model_ref())
    if PRELOAD:
        logger.info("Starting local LLM preload in a background thread so /health is available while the model downloads or loads.")
        Thread(target=preload_model_background, name="six-stars-local-llm-preload", daemon=True).start()
    app.run(host=HOST, port=PORT, threaded=True)
