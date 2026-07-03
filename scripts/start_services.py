#!/usr/bin/env python3
"""
Clean launcher for Six Stars local backend services.

Starts the local services that are shipped with this repo and keeps their noisy
server output in logs/services/*.log. The console prints a compact process table
so you can see what launched and which PID belongs to each service.
"""

from __future__ import annotations

import json
import os
import signal
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, List, Optional
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from model_paths import MODELS_ROOT, configure_model_environment, describe_model_environment, resolve_model_ref

ROOT = Path(__file__).resolve().parents[1]
LOG_DIR = ROOT / "logs" / "services"
configure_model_environment()


@dataclass
class ManagedService:
    name: str
    url: str
    command: List[str]
    process: subprocess.Popen
    log_path: Path


class ServiceManager:
    def __init__(self) -> None:
        self.services: List[ManagedService] = []
        LOG_DIR.mkdir(parents=True, exist_ok=True)

    def start_service(self, name: str, command: List[str], url: str, env: Optional[Dict[str, str]] = None) -> Optional[ManagedService]:
        log_path = LOG_DIR / (name.lower().replace(" ", "_").replace("/", "_") + ".log")
        merged_env = os.environ.copy()
        if env:
            merged_env.update(env)

        try:
            log_file = open(log_path, "a", encoding="utf-8", buffering=1)
            log_file.write("\n" + "=" * 80 + "\n")
            log_file.write(time.strftime("%Y-%m-%d %H:%M:%S") + f" launching {name}\n")
            log_file.write("command: " + " ".join(command) + "\n")
            log_file.write("=" * 80 + "\n")

            process = subprocess.Popen(
                command,
                cwd=str(ROOT),
                env=merged_env,
                stdout=log_file,
                stderr=subprocess.STDOUT,
                text=True,
                creationflags=getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0),
            )
            service = ManagedService(name=name, url=url, command=command, process=process, log_path=log_path)
            self.services.append(service)
            return service
        except Exception as exc:
            print(f"FAILED  {name}: {exc}")
            return None

    def print_table(self) -> None:
        print()
        print("Launched services")
        print("-" * 110)
        print(f"{'Service':24} {'PID':>8} {'Status':12} {'URL':34} Log")
        print("-" * 110)
        for service in self.services:
            status = "running" if service.process.poll() is None else f"exited {service.process.returncode}"
            print(f"{service.name:24} {service.process.pid:>8} {status:12} {service.url:34} {service.log_path}")
        print("-" * 110)
        print("Server output is hidden from this console and written to the log files above.")
        print("Press Ctrl+C here to stop launched services.")
        print()

    def stop_all(self) -> None:
        print("Stopping launched services...")
        for service in self.services:
            process = service.process
            if process.poll() is not None:
                continue
            try:
                process.terminate()
                process.wait(timeout=8)
            except Exception:
                try:
                    process.kill()
                except Exception:
                    pass
        print("Stopped.")

    def wait(self) -> None:
        try:
            while True:
                for service in self.services:
                    if service.process.poll() is not None:
                        print(f"WARNING {service.name} exited with code {service.process.returncode}. See {service.log_path}")
                time.sleep(3)
        except KeyboardInterrupt:
            self.stop_all()


def check_json_endpoint(url: str, timeout: int = 4, headers: Optional[Dict[str, str]] = None) -> tuple[int, object]:
    request_headers = {"Accept": "application/json"}
    if headers:
        request_headers.update(headers)
    request = Request(url, headers=request_headers)
    with urlopen(request, timeout=timeout) as response:
        payload = response.read().decode("utf-8")
        return response.status, json.loads(payload)


def load_llm_api_key() -> Optional[str]:
    for path in [ROOT / "src" / "Wds.SixStars.Ui" / "appsettings.json", ROOT / "appsettings.json"]:
        if not path.exists():
            continue
        try:
            settings = json.loads(path.read_text(encoding="utf-8"))
            api_key = settings.get("LLM", {}).get("ApiKey")
            if api_key:
                return api_key
        except Exception:
            pass
    return None



def truthy(value: Optional[str]) -> bool:
    return (value or "").strip().lower() in {"1", "true", "yes", "y", "on"}


def falsy(value: Optional[str]) -> bool:
    return (value or "").strip().lower() in {"0", "false", "no", "n", "off"}


def normalize_llm_model_id(value: Optional[str]) -> str:
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


def discover_openwakeword_models() -> List[Path]:
    """Return local custom wake-word model files that are safe to pass to openWakeWord.

    We intentionally do not start openWakeWord by default unless at least one
    custom model is present, because the package's bundled/default model can be
    missing depending on how openwakeword was installed on Windows. That caused
    the launcher to spam "OpenWakeWord exited with code 1" even though the rest
    of Six Stars was working.
    """
    model_dir = Path(os.environ.get("OWW_MODEL_DIR", str(ROOT / "models")))
    candidates: List[Path] = []
    if model_dir.exists():
        for pattern in ("*.tflite", "*.onnx"):
            candidates.extend(sorted(model_dir.glob(pattern)))
    return candidates


def print_probe(name: str, url: str, headers: Optional[Dict[str, str]] = None) -> None:
    try:
        status, data = check_json_endpoint(url, headers=headers)
        extra = ""
        if isinstance(data, dict) and isinstance(data.get("data"), list):
            extra = f", {len(data['data'])} model(s)"
        print(f"READY   {name}: HTTP {status}{extra} - {url}")
    except Exception as exc:
        print(f"CHECK   {name}: not ready yet - {url} ({exc})")


def wait_for_llm_health(port: str, headers: Optional[Dict[str, str]] = None) -> None:
    wait_seconds = int(os.environ.get("LOCAL_LLM_READY_WAIT_SECONDS", "180"))
    url = f"http://localhost:{port}/health"
    deadline = time.time() + max(1, wait_seconds)
    last_status = ""
    last_error = ""

    print(f"CHECK   LLM health: waiting up to {wait_seconds}s for {url}")
    while time.time() < deadline:
        try:
            status, data = check_json_endpoint(url, timeout=4, headers=headers)
            if isinstance(data, dict):
                state = str(data.get("status") or ("ready" if data.get("ready") else "unknown"))
                resolved = str(data.get("resolvedModel") or data.get("model") or "")
                if state == "ready":
                    print(f"READY   LLM health: HTTP {status}, model ready: {resolved}")
                    return
                if state == "error":
                    err = str(data.get("lastLoadError") or "unknown load error")
                    print(f"ERROR   LLM health: model failed to load: {err}")
                    print("        See logs/services/local_gemma_llm.log for the full stack trace.")
                    return
                message = f"{state} {resolved}".strip()
                if message != last_status:
                    print(f"WAIT    LLM health: {message}")
                    last_status = message
            else:
                print(f"WAIT    LLM health: HTTP {status}")
        except Exception as exc:
            last_error = str(exc)
            if last_error != last_status:
                print(f"WAIT    LLM health: server not accepting connections yet ({last_error})")
                last_status = last_error
        time.sleep(3)

    print(f"CHECK   LLM health: still not ready after {wait_seconds}s. The launcher will keep running.")
    if last_error:
        print(f"        Last check: {last_error}")
    print("        First model download/load can take a while. Watch logs/services/local_gemma_llm.log.")


def main() -> int:
    os.chdir(ROOT)
    print("=" * 72)
    print("Six Stars Backend Launcher")
    print("=" * 72)
    print(f"Repo:   {ROOT}")
    print(f"Python: {sys.executable}")
    paths = describe_model_environment()
    print(f"Models: {paths['modelsRoot']}")
    print(f"HF cache: {paths['hfHubCache']}")
    print()

    manager = ServiceManager()

    oww_port = os.environ.get("OWW_PORT", "9000")
    whisper_port = os.environ.get("WHISPER_PORT", "8000")
    # LM Studio is the default LLM again because it is much faster for this workflow.
    # Set SIX_STARS_START_LOCAL_LLM=true only when explicitly testing scripts/local_llm_openai_server.py.
    external_llm_endpoint = os.environ.get("SIX_STARS_LLM_ENDPOINT", "http://localhost:8080/v1/chat/completions")
    llm_port = os.environ.get("LOCAL_LLM_PORT", os.environ.get("LLM_PORT", "8200"))
    tts_port = os.environ.get("QWEN_TTS_PORT", os.environ.get("TTS_PORT", "8100"))

    openwakeword_script = ROOT / "scripts" / "openwakeword_server.py"
    openwakeword_setting = os.environ.get("SIX_STARS_START_OPENWAKEWORD", "false")
    openwakeword_models = discover_openwakeword_models()

    if falsy(openwakeword_setting):
        print("SKIP    OpenWakeWord disabled by SIX_STARS_START_OPENWAKEWORD=false")
    elif not openwakeword_script.exists():
        print(f"SKIP    OpenWakeWord script missing: {openwakeword_script}")
    elif openwakeword_setting.strip().lower() == "auto" and not openwakeword_models:
        print("SKIP    OpenWakeWord: disabled by default. The app now uses amplitude VAD + Whisper transcription. Set SIX_STARS_START_OPENWAKEWORD=true only if you are testing the old wake model path.")
    else:
        print(f"START   OpenWakeWord: {len(openwakeword_models)} custom model file(s) found" if openwakeword_models else "START   OpenWakeWord: forced by environment")
        manager.start_service(
            "OpenWakeWord",
            [sys.executable, str(openwakeword_script)],
            f"http://localhost:{oww_port}/health",
            {"OWW_PORT": oww_port, "OWW_ALLOW_DEFAULT_MODEL": "false"},
        )

    whisper_script = ROOT / "scripts" / "faster_whisper_openai_server.py"
    whisper_setting = os.environ.get("SIX_STARS_START_WHISPER", "true")
    if falsy(whisper_setting):
        print("SKIP    Whisper disabled by SIX_STARS_START_WHISPER=false")
    elif not whisper_script.exists():
        print(f"SKIP    Whisper script missing: {whisper_script}")
    else:
        print("START   faster-whisper transcription server")
        manager.start_service(
            "Whisper",
            [sys.executable, str(whisper_script)],
            f"http://localhost:{whisper_port}/health",
            {
                "WHISPER_PORT": whisper_port,
                "WHISPER_HOST": os.environ.get("WHISPER_HOST", "0.0.0.0"),
                "WHISPER_MODEL": os.environ.get("WHISPER_MODEL", "tiny.en"),
                "WHISPER_MODEL_PATH": os.environ.get("WHISPER_MODEL_PATH", ""),
                "WHISPER_DOWNLOAD_ROOT": os.environ.get("WHISPER_DOWNLOAD_ROOT", str(MODELS_ROOT / "asr")),
                "SIX_STARS_MODELS_ROOT": str(MODELS_ROOT),
                "HF_HOME": str(MODELS_ROOT / "hf"),
                "HF_HUB_CACHE": str(MODELS_ROOT / "hf" / "hub"),
                "WHISPER_DEVICE": os.environ.get("WHISPER_DEVICE", "auto"),
                "WHISPER_COMPUTE_TYPE": os.environ.get("WHISPER_COMPUTE_TYPE", "auto"),
                "WHISPER_PRELOAD": os.environ.get("WHISPER_PRELOAD", "true"),
                "WHISPER_DEFAULT_LANGUAGE": os.environ.get("WHISPER_DEFAULT_LANGUAGE", "en"),
            },
        )

    local_llm_script = ROOT / "scripts" / "local_llm_openai_server.py"
    local_llm_setting = os.environ.get("SIX_STARS_START_LOCAL_LLM", os.environ.get("LOCAL_LLM_ENABLED", "false"))
    local_llm_enabled = not falsy(local_llm_setting)
    if not local_llm_enabled:
        print("SKIP    Local Gemma LLM disabled. Using external LM Studio/OpenAI-compatible endpoint instead.")
        print(f"        LLM endpoint: {external_llm_endpoint}")
        print("        To test the Python Gemma server later, set SIX_STARS_START_LOCAL_LLM=true.")
    elif not local_llm_script.exists():
        print(f"SKIP    Local LLM script missing: {local_llm_script}")
    else:
        llm_model = normalize_llm_model_id(os.environ.get("LOCAL_LLM_MODEL") or os.environ.get("SIX_STARS_LLM_MODEL"))
        resolved_llm_model = resolve_model_ref(
            "llm",
            llm_model,
            os.environ.get("LOCAL_LLM_MODEL_PATH", ""),
            aliases=["gemma", "e2b", "gemma-e2b", "gemma-4-E2B-it", "gemma-2b", "google__gemma-4-E2B-it", "google_gemma-4-E2B-it", "e4b", "gemma-e4b", "gemma-4-E4B-it", "gemma-4b", "gemma-3-4b-it", "google__gemma-4-E4B-it", "google_gemma-4-E4B-it"],
        )
        print(f"START   Local LLM OpenAI-compatible server: {llm_model}")
        print(f"        resolved model: {resolved_llm_model}")
        manager.start_service(
            "Local Gemma LLM",
            [sys.executable, str(local_llm_script)],
            f"http://localhost:{llm_port}/v1/chat/completions",
            {
                "LOCAL_LLM_PORT": llm_port,
                "LOCAL_LLM_HOST": os.environ.get("LOCAL_LLM_HOST", "0.0.0.0"),
                "LOCAL_LLM_MODEL": llm_model,
                "LOCAL_LLM_MODEL_PATH": os.environ.get("LOCAL_LLM_MODEL_PATH", ""),
                "LOCAL_LLM_DEVICE": os.environ.get("LOCAL_LLM_DEVICE", "auto"),
                "LOCAL_LLM_DTYPE": os.environ.get("LOCAL_LLM_DTYPE", "auto"),
                "LOCAL_LLM_PRELOAD": os.environ.get("LOCAL_LLM_PRELOAD", "true"),
                # GemmaTokenizerFast can crash on some Gemma snapshots with extra_special_tokens
                # as a list. Slow tokenizer avoids that while generation still dominates latency.
                "LOCAL_LLM_TOKENIZER_USE_FAST": os.environ.get("LOCAL_LLM_TOKENIZER_USE_FAST", "false"),
                "LOCAL_LLM_MAX_NEW_TOKENS": os.environ.get("LOCAL_LLM_MAX_NEW_TOKENS", "2048"),
                "SIX_STARS_MODELS_ROOT": str(MODELS_ROOT),
                "HF_HOME": str(MODELS_ROOT / "hf"),
                "HF_HUB_CACHE": str(MODELS_ROOT / "hf" / "hub"),
            },
        )

    websocket_tts_script = ROOT / "scripts" / "websocket_tts_server.py"
    if websocket_tts_script.exists():
        qwen_model = os.environ.get("QWEN_TTS_MODEL_PATH", "Qwen/Qwen3-TTS-12Hz-0.6B-Base")
        resolved_qwen_model = resolve_model_ref("tts", qwen_model, "", aliases=["qwen3-tts-0.6b", "qwen3-tts-0.6b-base", "qwen3-tts-1.7b", "qwen3-tts-1.7b-base"])
        tts_env = {
            "QWEN_TTS_PORT": tts_port,
            "PYTHONNOUSERSITE": os.environ.get("PYTHONNOUSERSITE", "1"),
            "QWEN_TTS_DEVICE": os.environ.get("QWEN_TTS_DEVICE", "cuda"),
            "QWEN_TTS_LANGUAGE": os.environ.get("QWEN_TTS_LANGUAGE", "English"),
            # Voice Clone Studio is fast in the user's test because it uses the 0.6B Base model
            # with CUDA graph acceleration. Match that by default; override with env vars if needed.
            "QWEN_TTS_MODEL_PATH": resolved_qwen_model,
            "QWEN_TTS_USE_CUDA_GRAPHS": os.environ.get("QWEN_TTS_USE_CUDA_GRAPHS", "true"),
            "QWEN_TTS_USE_FASTER_QWEN3": os.environ.get("QWEN_TTS_USE_FASTER_QWEN3", "true"),
            "QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS": os.environ.get("QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS", "3"),
            "QWEN_TTS_CHUNK_GENERATION_MODE": os.environ.get("QWEN_TTS_CHUNK_GENERATION_MODE", "first_then_prefetch"),
            "QWEN_TTS_PARALLEL_CHUNKS": os.environ.get("QWEN_TTS_PARALLEL_CHUNKS", "false"),
            "QWEN_TTS_MAX_PARALLEL_CHUNKS": os.environ.get("QWEN_TTS_MAX_PARALLEL_CHUNKS", "1"),
            "SIX_STARS_MODELS_ROOT": str(MODELS_ROOT),
            "HF_HOME": str(MODELS_ROOT / "hf"),
            "HF_HUB_CACHE": str(MODELS_ROOT / "hf" / "hub"),
        }
        manager.start_service(
            "WebSocket Qwen TTS",
            [sys.executable, str(websocket_tts_script)],
            f"ws://localhost:{tts_port}/tts/stream",
            tts_env,
        )
    else:
        print(f"SKIP    WebSocket TTS script missing: {websocket_tts_script}")

    manager.print_table()

    print("External services expected by the WPF app")
    print("-" * 72)
    print(f"Whisper: http://localhost:{whisper_port}/v1/audio/transcriptions or your configured endpoint")
    active_llm_endpoint = f"http://localhost:{llm_port}/v1/chat/completions" if local_llm_enabled else external_llm_endpoint
    print(f"LLM:     {active_llm_endpoint}")
    print(f"Models:  {MODELS_ROOT}")
    print()

    llm_key = load_llm_api_key()
    headers = {"Authorization": f"Bearer {llm_key}"} if llm_key else None
    if local_llm_enabled:
        wait_for_llm_health(llm_port, headers=headers)
        print_probe("LLM models", f"http://localhost:{llm_port}/v1/models", headers=headers)
    else:
        models_url = external_llm_endpoint.rsplit("/v1/chat/completions", 1)[0].rstrip("/") + "/v1/models"
        print_probe("External LLM models", models_url, headers=headers)
        print("        LM Studio must have a model loaded. If this check says no connection or no models, open LM Studio and load/start the model.")
    print()

    manager.wait()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
