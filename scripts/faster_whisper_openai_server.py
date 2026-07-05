#!/usr/bin/env python3
"""
OpenAI-compatible faster-whisper server for Six Stars.

This sidecar supports both:
1. The current Six Stars adapter contract: POST /transcribe -> plain text body
2. OpenAI-compatible transcription: POST /v1/audio/transcriptions

Requirements:
    pip install flask faster-whisper

Optional environment variables:
    WHISPER_HOST=0.0.0.0
    WHISPER_PORT=8000
    WHISPER_MODEL=tiny.en
    WHISPER_MODEL_PATH=C:/models/faster-whisper-medium
    WHISPER_DEVICE=auto
    WHISPER_COMPUTE_TYPE=auto
    WHISPER_CPU_THREADS=0
    WHISPER_NUM_WORKERS=1
    WHISPER_BEAM_SIZE=5
    WHISPER_VAD_FILTER=true
    WHISPER_DEFAULT_LANGUAGE=en
    WHISPER_PRELOAD=false
    WHISPER_API_KEY=
"""

from __future__ import annotations

import json
import logging
import os
import sys
import tempfile
from pathlib import Path
from typing import Any

from flask import Flask, Response, jsonify, request
from faster_whisper import WhisperModel

from model_paths import MODELS_ROOT, configure_model_environment, resolve_model_ref

configure_model_environment()


if getattr(sys, "frozen", False):
    exe_dir = os.path.dirname(sys.executable)
    internal_dir = getattr(sys, "_MEIPASS", "")

    dll_dirs = [
        exe_dir,
        internal_dir,
        os.path.join(exe_dir, "_internal"),
    ]

    for dll_dir in dll_dirs:
        if dll_dir and os.path.isdir(dll_dir):
            try:
                os.add_dll_directory(dll_dir)
            except Exception:
                pass

            os.environ["PATH"] = dll_dir + os.pathsep + os.environ.get("PATH", "")


logging.basicConfig(
    level=getattr(logging, os.environ.get("WHISPER_LOG_LEVEL", "INFO").upper(), logging.INFO),
    format="%(asctime)s %(levelname)s %(name)s %(message)s",
)
logger = logging.getLogger("faster-whisper-server")

app = Flask(__name__)


def parse_bool(value: str | None, default: bool) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def parse_int(value: str | None, default: int) -> int:
    if value is None or not value.strip():
        return default
    return int(value)


HOST = os.environ.get("WHISPER_HOST", "0.0.0.0")
PORT = parse_int(os.environ.get("WHISPER_PORT"), 8000)
MODEL_ID = os.environ.get("WHISPER_MODEL", "tiny.en")
MODEL_PATH = os.environ.get("WHISPER_MODEL_PATH", "").strip()
DEVICE = os.environ.get("WHISPER_DEVICE", "auto")
COMPUTE_TYPE = os.environ.get("WHISPER_COMPUTE_TYPE", "auto")
CPU_THREADS = parse_int(os.environ.get("WHISPER_CPU_THREADS"), 0)
NUM_WORKERS = parse_int(os.environ.get("WHISPER_NUM_WORKERS"), 1)
DEFAULT_LANGUAGE = os.environ.get("WHISPER_DEFAULT_LANGUAGE", "en").strip() or "en"
DEFAULT_BEAM_SIZE = parse_int(os.environ.get("WHISPER_BEAM_SIZE"), 5)
DEFAULT_VAD_FILTER = parse_bool(os.environ.get("WHISPER_VAD_FILTER"), True)
PRELOAD_MODEL = parse_bool(os.environ.get("WHISPER_PRELOAD"), False)
DOWNLOAD_ROOT = os.environ.get("WHISPER_DOWNLOAD_ROOT", str(MODELS_ROOT / "asr")).strip()
API_KEY = os.environ.get("WHISPER_API_KEY", "").strip()

_model: WhisperModel | None = None


def configured_model_name() -> str:
    return resolve_model_ref(
        "asr",
        MODEL_ID,
        MODEL_PATH,
        aliases=["faster-whisper-" + MODEL_ID, MODEL_ID.replace("/", "__"), MODEL_ID.replace("/", "_")],
    )


def require_api_key() -> Response | None:
    if not API_KEY:
        return None

    auth_header = request.headers.get("Authorization", "")
    token = auth_header.removeprefix("Bearer ").strip() if auth_header else ""
    provided = token or request.headers.get("X-API-Key", "").strip()
    if provided == API_KEY:
        return None

    return jsonify({
        "error": {
            "message": "Invalid or missing API key.",
            "type": "invalid_request_error",
            "param": None,
            "code": "invalid_api_key",
        }
    }), 401


def get_model() -> WhisperModel:
    global _model
    if _model is not None:
        return _model

    model_name = configured_model_name()
    logger.info(
        "Loading faster-whisper model '%s' on device=%s compute_type=%s",
        model_name,
        DEVICE,
        COMPUTE_TYPE,
    )

    kwargs: dict[str, Any] = {
        "device": DEVICE,
        "compute_type": COMPUTE_TYPE,
        "num_workers": NUM_WORKERS,
        "download_root": DOWNLOAD_ROOT,
    }
    if CPU_THREADS > 0:
        kwargs["cpu_threads"] = CPU_THREADS

    _model = WhisperModel(model_name, **kwargs)
    logger.info("Model loaded successfully")
    return _model


def coerce_float(value: Any, default: float) -> float:
    if value in (None, ""):
        return default
    return float(value)


def coerce_int(value: Any, default: int) -> int:
    if value in (None, ""):
        return default
    return int(value)


def first_uploaded_file():
    if "file" in request.files:
        return request.files["file"]
    if "audio" in request.files:
        return request.files["audio"]
    for uploaded in request.files.values():
        return uploaded
    return None


def write_temp_upload(uploaded_file) -> tuple[str, str]:
    suffix = Path(uploaded_file.filename or "audio.wav").suffix or ".wav"
    temp_file = tempfile.NamedTemporaryFile(delete=False, suffix=suffix)
    try:
        uploaded_file.save(temp_file)
        temp_file.flush()
        return temp_file.name, suffix
    finally:
        temp_file.close()


def normalize_language(language: str | None) -> str | None:
    if language is None:
        return DEFAULT_LANGUAGE or None
    value = language.strip()
    if not value or value.lower() in {"auto", "null", "none"}:
        return None
    return value


def transcribe_upload(uploaded_file) -> dict[str, Any]:
    temp_path, _ = write_temp_upload(uploaded_file)
    try:
        language = normalize_language(request.form.get("language") or request.values.get("language"))
        task = (request.form.get("task") or request.values.get("task") or "transcribe").strip().lower()
        prompt = request.form.get("prompt") or request.values.get("prompt")
        temperature = coerce_float(request.form.get("temperature") or request.values.get("temperature"), 0.0)
        beam_size = coerce_int(request.form.get("beam_size") or request.values.get("beam_size"), DEFAULT_BEAM_SIZE)
        vad_filter = parse_bool(request.form.get("vad_filter") or request.values.get("vad_filter"), DEFAULT_VAD_FILTER)
        word_timestamps = parse_bool(
            request.form.get("word_timestamps") or request.values.get("word_timestamps"),
            False,
        )

        if task != "transcribe":
            raise ValueError("Only the 'transcribe' task is supported by this server.")

        model = get_model()
        segments_iter, info = model.transcribe(
            temp_path,
            language=language,
            initial_prompt=prompt,
            temperature=temperature,
            beam_size=beam_size,
            vad_filter=vad_filter,
            word_timestamps=word_timestamps,
        )

        segments = list(segments_iter)
        text = " ".join(segment.text.strip() for segment in segments if segment.text).strip()
        duration = float(getattr(info, "duration", 0.0) or 0.0)
        detected_language = getattr(info, "language", None) or language or DEFAULT_LANGUAGE
        language_probability = float(getattr(info, "language_probability", 0.0) or 0.0)

        response_segments = []
        for segment in segments:
            segment_payload = {
                "id": segment.id,
                "seek": segment.seek,
                "start": float(segment.start),
                "end": float(segment.end),
                "text": segment.text.strip(),
                "tokens": list(segment.tokens),
                "temperature": float(segment.temperature),
                "avg_logprob": float(segment.avg_logprob),
                "compression_ratio": float(segment.compression_ratio),
                "no_speech_prob": float(segment.no_speech_prob),
            }
            if word_timestamps and getattr(segment, "words", None):
                segment_payload["words"] = [
                    {
                        "start": float(word.start),
                        "end": float(word.end),
                        "word": word.word,
                        "probability": float(word.probability),
                    }
                    for word in segment.words
                ]
            response_segments.append(segment_payload)

        return {
            "text": text,
            "language": detected_language,
            "duration": duration,
            "language_probability": language_probability,
            "segments": response_segments,
            "model": configured_model_name(),
        }
    finally:
        try:
            os.unlink(temp_path)
        except OSError:
            logger.warning("Could not delete temp audio file: %s", temp_path)


def format_timestamp(seconds: float, separator: str = ",") -> str:
    millis_total = max(0, int(round(seconds * 1000)))
    hours, remainder = divmod(millis_total, 3600 * 1000)
    minutes, remainder = divmod(remainder, 60 * 1000)
    secs, millis = divmod(remainder, 1000)
    return f"{hours:02d}:{minutes:02d}:{secs:02d}{separator}{millis:03d}"


def render_srt(segments: list[dict[str, Any]]) -> str:
    lines: list[str] = []
    for index, segment in enumerate(segments, start=1):
        lines.append(str(index))
        lines.append(f"{format_timestamp(segment['start'])} --> {format_timestamp(segment['end'])}")
        lines.append(segment["text"])
        lines.append("")
    return "\n".join(lines).strip() + "\n"


def render_vtt(segments: list[dict[str, Any]]) -> str:
    lines = ["WEBVTT", ""]
    for segment in segments:
        lines.append(f"{format_timestamp(segment['start'], '.')} --> {format_timestamp(segment['end'], '.')}")
        lines.append(segment["text"])
        lines.append("")
    return "\n".join(lines).strip() + "\n"


def openai_transcription_response(result: dict[str, Any]):
    response_format = (request.form.get("response_format") or request.values.get("response_format") or "json").strip().lower()

    if response_format == "text":
        return Response(result["text"], mimetype="text/plain; charset=utf-8")

    if response_format == "verbose_json":
        return jsonify({
            "task": "transcribe",
            "language": result["language"],
            "duration": result["duration"],
            "text": result["text"],
            "segments": result["segments"],
        })

    if response_format == "srt":
        return Response(render_srt(result["segments"]), mimetype="text/plain; charset=utf-8")

    if response_format == "vtt":
        return Response(render_vtt(result["segments"]), mimetype="text/vtt; charset=utf-8")

    return jsonify({"text": result["text"]})


@app.errorhandler(ValueError)
def handle_value_error(error: ValueError):
    logger.warning("Bad request: %s", error)
    return jsonify({
        "error": {
            "message": str(error),
            "type": "invalid_request_error",
            "param": None,
            "code": None,
        }
    }), 400


@app.errorhandler(Exception)
def handle_unexpected_error(error: Exception):
    logger.exception("Unexpected server error")
    return jsonify({
        "error": {
            "message": str(error),
            "type": "server_error",
            "param": None,
            "code": None,
        }
    }), 500


@app.route("/health", methods=["GET"])
def health_check():
    return jsonify({
        "status": "healthy",
        "backend": "faster-whisper",
        "model": configured_model_name(),
        "model_loaded": _model is not None,
        "device": DEVICE,
        "compute_type": COMPUTE_TYPE,
        "default_language": DEFAULT_LANGUAGE,
        "requires_api_key": bool(API_KEY),
    })


@app.route("/v1/models", methods=["GET"])
def list_models():
    auth_error = require_api_key()
    if auth_error is not None:
        return auth_error

    model_name = Path(MODEL_PATH).name if MODEL_PATH else MODEL_ID
    return jsonify({
        "object": "list",
        "data": [
            {
                "id": model_name,
                "object": "model",
                "owned_by": "local",
            }
        ],
    })


@app.route("/transcribe", methods=["POST"])
def legacy_transcribe():
    uploaded_file = first_uploaded_file()
    if uploaded_file is None:
        raise ValueError("No audio file provided. Expected multipart/form-data with a 'file' field.")

    result = transcribe_upload(uploaded_file)
    return Response(result["text"], mimetype="text/plain; charset=utf-8")


@app.route("/v1/audio/transcriptions", methods=["POST"])
def openai_transcriptions():
    auth_error = require_api_key()
    if auth_error is not None:
        return auth_error

    uploaded_file = first_uploaded_file()
    if uploaded_file is None:
        raise ValueError("No audio file provided. Expected multipart/form-data with a 'file' field.")

    return openai_transcription_response(transcribe_upload(uploaded_file))


@app.route("/v1/audio/translations", methods=["POST"])
def openai_translations():
    auth_error = require_api_key()
    if auth_error is not None:
        return auth_error

    return jsonify({
        "error": {
            "message": "This server only supports transcription. Translation is not implemented.",
            "type": "invalid_request_error",
            "param": "task",
            "code": "unsupported_operation",
        }
    }), 400


if PRELOAD_MODEL:
    get_model()


if __name__ == "__main__":
    logger.info("Starting faster-whisper server on %s:%s", HOST, PORT)
    logger.info("Configured model: %s", configured_model_name())
    logger.info("OpenAI-compatible endpoint: http://%s:%s/v1/audio/transcriptions", HOST, PORT)
    logger.info("Legacy endpoint: http://%s:%s/transcribe", HOST, PORT)
    app.run(host=HOST, port=PORT, debug=False)