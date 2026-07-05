#!/usr/bin/env python3
"""Six Stars services-only launcher.

Default behavior is now intentionally source/venv-first for the Python services:

    .venv\\Scripts\\python.exe scripts/faster_whisper_openai_server.py
    .venv\\Scripts\\python.exe scripts/websocket_tts_server.py

The local LLM is still started from the llama.cpp engine pack by default because
that service is not a Python process. Set SIX_STARS_USE_ENGINE_PACKS=true to use
frozen ASR/TTS EXE packs too, or SIX_STARS_USE_ENGINE_PACKS=false to avoid all
engine packs and use only Python source fallbacks.
"""

from __future__ import annotations

import json
import os
import shlex
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.error import HTTPError
from urllib.request import Request, urlopen

from model_paths import ROOT, MODELS_ROOT, configure_model_environment, describe_model_environment, resolve_model_ref

configure_model_environment()
LOG_DIR = ROOT / "logs" / "services"
CONFIG_DIR = ROOT / "config"


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

    def start_service(
        self,
        name: str,
        command: List[str],
        url: str,
        env: Optional[Dict[str, str]] = None,
        cwd: Optional[Path] = None,
    ) -> Optional[ManagedService]:
        log_path = LOG_DIR / (name.lower().replace(" ", "_").replace("/", "_") + ".log")
        merged_env = os.environ.copy()
        merged_env.update(shared_environment())
        if env:
            merged_env.update({k: str(v) for k, v in env.items() if v is not None})

        try:
            log_file = open(log_path, "a", encoding="utf-8", buffering=1)
            log_file.write("\n" + "=" * 80 + "\n")
            log_file.write(time.strftime("%Y-%m-%d %H:%M:%S") + f" launching {name}\n")
            log_file.write("cwd: " + str(cwd or ROOT) + "\n")
            log_file.write("command: " + " ".join(command) + "\n")
            for key in ("SIX_STARS_APP_ROOT", "SIX_STARS_MODELS_ROOT", "QWEN_TTS_MODEL_PATH", "LOCAL_LLM_MODEL", "WHISPER_MODEL", "WHISPER_MODEL_PATH"):
                if key in merged_env:
                    log_file.write(f"env {key}={merged_env[key]}\n")
            log_file.write("=" * 80 + "\n")

            creationflags = getattr(subprocess, "CREATE_NEW_PROCESS_GROUP", 0)
            process = subprocess.Popen(
                command,
                cwd=str(cwd or ROOT),
                env=merged_env,
                stdout=log_file,
                stderr=subprocess.STDOUT,
                text=True,
                creationflags=creationflags,
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
        print("-" * 118)
        print(f"{'Service':28} {'PID':>8} {'Status':12} {'URL':36} Log")
        print("-" * 118)
        for service in self.services:
            status = "running" if service.process.poll() is None else f"exited {service.process.returncode}"
            print(f"{service.name:28} {service.process.pid:>8} {status:12} {service.url:36} {service.log_path}")
        print("-" * 118)
        print("Server output is written to logs/services/*.log. Press Ctrl+C here to stop services.")
        print()

    def stop_all(self) -> None:
        print("Stopping launched services...")
        for service in reversed(self.services):
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
        warned: set[int] = set()
        try:
            while True:
                for service in self.services:
                    if service.process.poll() is not None and service.process.pid not in warned:
                        warned.add(service.process.pid)
                        print(f"WARNING {service.name} exited with code {service.process.returncode}. See {service.log_path}")
                time.sleep(3)
        except KeyboardInterrupt:
            self.stop_all()


def truthy(value: Optional[str]) -> bool:
    return (value or "").strip().lower() in {"1", "true", "yes", "y", "on"}


def falsy(value: Optional[str]) -> bool:
    return (value or "").strip().lower() in {"0", "false", "no", "n", "off"}


def full_path(value: str | Path) -> Path:
    path = Path(value).expanduser()
    if not path.is_absolute():
        path = ROOT / path
    return path.resolve()


def load_json(path: Path) -> Dict[str, Any]:
    if not path.exists():
        return {}
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except Exception as exc:
        print(f"WARNING Could not read {path}: {exc}")
        return {}


def model_section(kind: str) -> Dict[str, Any]:
    return (load_json(CONFIG_DIR / "models.json").get("models") or {}).get(kind) or {}


def runtime_manifest() -> Dict[str, Any]:
    return load_json(CONFIG_DIR / "runtime-manifest.json")


def gpu_policy() -> Dict[str, Any]:
    manifest = runtime_manifest()
    policy = manifest.get("gpuPolicy") or {}
    return policy if isinstance(policy, dict) else {}


def require_cuda_policy() -> bool:
    value = gpu_policy().get("requireCuda", True)
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() not in {"0", "false", "no", "off"}


def allow_cpu_fallback() -> bool:
    value = gpu_policy().get("allowCpuFallback", False)
    if isinstance(value, bool):
        return value
    return str(value).strip().lower() in {"1", "true", "yes", "on"}


def hosted_service_message() -> str:
    return str(gpu_policy().get("hostedServiceMessage") or "CUDA/GPU is required for local Six Stars ASR/TTS/LLM. Install/configure NVIDIA CUDA engines or use a hosted service option.")


def model_local_path_if_exists(kind: str) -> Optional[str]:
    section = model_section(kind)
    local = str(section.get("localPath") or "").strip()
    if local:
        candidate = full_path(local)
        if candidate.exists():
            return str(candidate)
    return None


def model_name_or_path(kind: str, default: str) -> str:
    section = model_section(kind)
    existing = model_local_path_if_exists(kind)
    if existing:
        return existing
    return str(section.get("modelName") or section.get("hfSpecifier") or default).strip()


def shared_environment() -> Dict[str, str]:
    env: Dict[str, str] = {
        "SIX_STARS_APP_ROOT": str(ROOT),
        "SIX_STARS_REPO_ROOT": str(ROOT),
        "SIX_STARS_MODELS_ROOT": str(MODELS_ROOT),
        "HF_HOME": str(MODELS_ROOT / "hf"),
        "HF_HUB_CACHE": str(MODELS_ROOT / "hf" / "hub"),
        "PYTHONNOUSERSITE": os.environ.get("PYTHONNOUSERSITE", "1"),
        "WHISPER_PORT": os.environ.get("WHISPER_PORT", "8000"),
        "WHISPER_HOST": os.environ.get("WHISPER_HOST", "0.0.0.0"),
        "WHISPER_MODEL": os.environ.get("WHISPER_MODEL", str(model_section("asr").get("modelName") or "large-v3-turbo")),
        "WHISPER_DEVICE": os.environ.get("WHISPER_DEVICE", "cuda"),
        "WHISPER_COMPUTE_TYPE": os.environ.get("WHISPER_COMPUTE_TYPE", "float16"),
        "WHISPER_DOWNLOAD_ROOT": os.environ.get("WHISPER_DOWNLOAD_ROOT", str(MODELS_ROOT / "asr")),
        "QWEN_TTS_PORT": os.environ.get("QWEN_TTS_PORT", os.environ.get("TTS_PORT", "8100")),
        "QWEN_TTS_MODEL_PATH": os.environ.get("QWEN_TTS_MODEL_PATH", model_name_or_path("tts", "Qwen/Qwen3-TTS-12Hz-0.6B-Base")),
        "QWEN_TTS_MODEL_NAME": str(model_section("tts").get("modelName") or "Qwen/Qwen3-TTS-12Hz-0.6B-Base"),
        "QWEN_TTS_LANGUAGE": os.environ.get("QWEN_TTS_LANGUAGE", "English"),
        "QWEN_TTS_CLONE_AUDIO_DIR": os.environ.get("QWEN_TTS_CLONE_AUDIO_DIR", str(full_path("src/Wds.SixStars.Api/Content/VoiceClones"))),
        "QWEN_TTS_STARS_DIR": os.environ.get("QWEN_TTS_STARS_DIR", str(full_path("src/Wds.SixStars.Api/Content/Stars"))),
        "QWEN_TTS_DEVICE": os.environ.get("QWEN_TTS_DEVICE", "cuda"),
        "SIX_STARS_REQUIRE_CUDA": os.environ.get("SIX_STARS_REQUIRE_CUDA", "true"),
        "QWEN_TTS_CHUNK_GENERATION_MODE": os.environ.get("QWEN_TTS_CHUNK_GENERATION_MODE", "parallel"),
        "QWEN_TTS_PARALLEL_CHUNKS": os.environ.get("QWEN_TTS_PARALLEL_CHUNKS", "true"),
        "QWEN_TTS_MAX_PARALLEL_CHUNKS": os.environ.get("QWEN_TTS_MAX_PARALLEL_CHUNKS", "0"),
        "QWEN_TTS_OFFICIAL_DEVICE_MAP": os.environ.get("QWEN_TTS_OFFICIAL_DEVICE_MAP", "cuda:0"),
        "QWEN_TTS_USE_CUDA_GRAPHS": os.environ.get("QWEN_TTS_USE_CUDA_GRAPHS", "true"),
        "QWEN_TTS_USE_FASTER_QWEN3": os.environ.get("QWEN_TTS_USE_FASTER_QWEN3", "true"),
        "QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS": os.environ.get("QWEN_TTS_CUDA_GRAPH_WARMUP_RUNS", "3"),
    }
    asr_local = model_local_path_if_exists("asr")
    if asr_local:
        env["WHISPER_MODEL_PATH"] = os.environ.get("WHISPER_MODEL_PATH", asr_local)
    if "LM_API_TOKEN" in os.environ and "SIX_STARS_LLM_API_KEY" not in os.environ:
        env["SIX_STARS_LLM_API_KEY"] = os.environ["LM_API_TOKEN"]
    return env


def parse_args(args: str) -> List[str]:
    if not args.strip():
        return []
    return shlex.split(args, posix=(os.name != "nt"))


def command_works(file_name: str, args: List[str]) -> bool:
    try:
        p = subprocess.run([file_name, *args], capture_output=True, text=True, timeout=3)
        return p.returncode == 0
    except Exception:
        return False


def has_nvidia() -> bool:
    return command_works("nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"])


def pack_compatible(pack: Dict[str, Any], nvidia: bool) -> bool:
    vendor = str(pack.get("vendor") or "").lower()
    if require_cuda_policy():
        return vendor == "nvidia" and nvidia
    if vendor == "cpu" and not allow_cpu_fallback():
        return False
    if vendor == "nvidia":
        return nvidia
    return True


def should_use_engine_pack(kind: str) -> bool:
    """Return whether start_services should use frozen engine packs for a kind.

    Values for SIX_STARS_USE_ENGINE_PACKS:
    - true/1/yes/on/auto/all: use packs for LLM, ASR, and TTS.
    - false/0/no/off/none: use no packs; use source fallbacks only.
    - llm/llm-only/python/venv/source/default: use the llama.cpp pack for the
      LLM only, but run ASR/TTS from the local Python venv.

    The default is llm-only because the user-installed Python environment is the
    easiest place to debug Qwen/faster-whisper, while llama.cpp remains the
    correct local LLM server.
    """
    value = (os.environ.get("SIX_STARS_USE_ENGINE_PACKS") or "llm").strip().lower()
    if value in {"1", "true", "yes", "y", "on", "auto", "all", "engines"}:
        return True
    if value in {"0", "false", "no", "n", "off", "none"}:
        return False
    if value in {"llm", "llm-only", "python", "venv", "source", "default"}:
        return kind.lower() == "llm"
    # Unknown value: choose the safer debug-friendly behavior.
    print(f"WARNING Unknown SIX_STARS_USE_ENGINE_PACKS={value!r}; using engine pack for LLM only.")
    return kind.lower() == "llm"


def python_executable() -> str:
    """Return the Python interpreter that should run source services.

    When start.cmd activates .venv, sys.executable is already .venv\\Scripts\\python.exe.
    If someone runs this script through a global Python by mistake but a repo-local
    venv exists, prefer the repo-local interpreter so Qwen/faster-whisper use the
    packages that were installed there.
    """
    for rel in (".venv/Scripts/python.exe", ".venv/bin/python"):
        candidate = (ROOT / rel).resolve()
        if candidate.exists():
            current = Path(sys.executable).resolve()
            if current != candidate:
                print(f"INFO    Source Python services will use repo venv: {candidate}")
            return str(candidate)
    return sys.executable


PY_EXE = python_executable()


def select_pack(kind: str, manifest: Dict[str, Any], nvidia: bool) -> Optional[Dict[str, Any]]:
    packs = manifest.get("packs") or []
    order = (manifest.get("selectionOrder") or {}).get(kind) or [p.get("id") for p in packs if p.get("kind") == kind]
    by_id = {str(p.get("id")): p for p in packs}
    for pack_id in order:
        pack = by_id.get(str(pack_id))
        if not pack or str(pack.get("kind") or "").lower() != kind:
            continue
        if not pack_compatible(pack, nvidia):
            continue
        exe = full_path(str(pack.get("path") or ""))
        if exe.exists():
            return pack
    return None


def build_pack_command(pack: Dict[str, Any]) -> tuple[List[str], Path, Dict[str, str], str]:
    kind = str(pack.get("kind") or "").lower()
    exe = full_path(str(pack.get("path") or ""))
    cwd = full_path(str(pack.get("workingDirectory") or "."))
    args = parse_args(str(pack.get("args") or ""))
    env: Dict[str, str] = {}

    pack_id = str(pack.get("id") or "")
    if "nvidia" in pack_id.lower():
        env.update({
            "WHISPER_DEVICE": "cuda",
            "WHISPER_COMPUTE_TYPE": "float16",
            "QWEN_TTS_DEVICE": "cuda",
            "QWEN_TTS_OFFICIAL_DEVICE_MAP": os.environ.get("QWEN_TTS_OFFICIAL_DEVICE_MAP", "cuda:0"),
        })
    elif "cpu" in pack_id.lower():
        raise RuntimeError(hosted_service_message())

    if kind == "llm":
        llm = model_section("llm")
        local = str(llm.get("localPath") or "").strip()
        local_path = full_path(local) if local else None
        if local_path is not None and local_path.exists():
            args = ["-m", str(local_path), *args]
        elif llm.get("hfSpecifier"):
            args = ["-hf", str(llm["hfSpecifier"]), *args]

        vision = model_section("vision")
        mmproj = str(vision.get("localPath") or "").strip()
        if vision.get("enabled", False) and mmproj and "--mmproj" not in args:
            mmproj_path = full_path(mmproj)
            if mmproj_path.exists():
                args.extend(["--mmproj", str(mmproj_path)])
        env["GGML_BACKEND_PATH"] = str(cwd)

    url = str(pack.get("openAiChatUrl") or pack.get("healthUrl") or "")
    return [str(exe), *args], cwd, env, url


def check_json_endpoint(url: str, timeout: int = 4, headers: Optional[Dict[str, str]] = None) -> tuple[int, object]:
    request_headers = {"Accept": "application/json"}
    if headers:
        request_headers.update(headers)
    request = Request(url, headers=request_headers)
    with urlopen(request, timeout=timeout) as response:
        payload = response.read().decode("utf-8")
        return response.status, json.loads(payload or "{}")


def wait_for_http(url: str, seconds: int, name: str) -> bool:
    deadline = time.time() + max(1, seconds)
    last = ""
    while time.time() < deadline:
        try:
            status, _ = check_json_endpoint(url, timeout=3)
            print(f"READY   {name}: HTTP {status} - {url}")
            return True
        except HTTPError as exc:
            message = f"HTTP {exc.code}"
        except Exception as exc:
            message = str(exc)
        if message != last:
            print(f"WAIT    {name}: {message}")
            last = message
        time.sleep(2)
    print(f"CHECK   {name}: not ready after {seconds}s - {url}")
    return False


def start_engine_service(manager: ServiceManager, kind: str, manifest: Dict[str, Any], nvidia: bool) -> Optional[str]:
    if not should_use_engine_pack(kind):
        if kind.lower() in {"asr", "tts"}:
            print(f"SOURCE  {kind.upper()}: using local Python venv service, not frozen engine EXE")
        else:
            print(f"SOURCE  {kind.upper()}: engine pack disabled; using source fallback")
        return None

    pack = select_pack(kind, manifest, nvidia)
    if not pack:
        print(f"WARNING No installed engine pack found for {kind}; falling back to source script.")
        return None

    command, cwd, env, url = build_pack_command(pack)
    service_name = f"Engine {kind.upper()} {pack.get('id')}"
    service = manager.start_service(service_name, command, url or str(pack.get("healthUrl") or ""), env=env, cwd=cwd)
    if not service:
        return None

    # WebSocket TTS has no HTTP health endpoint. Give it a moment and trust the socket connect path.
    health_url = str(pack.get("healthUrl") or "")
    if health_url.startswith("http"):
        wait_for_http(health_url, int(os.environ.get("LOCAL_LLM_READY_WAIT_SECONDS", "180")) if kind == "llm" else 45, service_name)
    else:
        time.sleep(1.5)
        if service.process.poll() is not None:
            print(f"WARNING {service_name} exited immediately with code {service.process.returncode}. See {service.log_path}")
            return None
        print(f"READY   {service_name}: process is running; client will verify socket {health_url}")

    return url or health_url


def print_probe(name: str, url: str, headers: Optional[Dict[str, str]] = None) -> None:
    try:
        status, data = check_json_endpoint(url, headers=headers)
        extra = ""
        if isinstance(data, dict) and isinstance(data.get("data"), list):
            extra = f", {len(data['data'])} model(s)"
        print(f"READY   {name}: HTTP {status}{extra} - {url}")
    except HTTPError as exc:
        if exc.code == 401:
            print(f"AUTH    {name}: HTTP 401 from {url}")
        else:
            print(f"CHECK   {name}: not ready yet - {url} (HTTP {exc.code})")
    except Exception as exc:
        print(f"CHECK   {name}: not ready yet - {url} ({exc})")


def load_llm_api_key() -> Optional[str]:
    for key in ("SIX_STARS_LLM_API_KEY", "LM_API_TOKEN", "LM_STUDIO_API_KEY"):
        value = os.environ.get(key, "").strip()
        if value:
            return value
    for rel in ("src/Wds.SixStars.Ui/appsettings.Local.json", "src/Wds.SixStars.Ui/appsettings.json", "appsettings.Local.json", "appsettings.json"):
        path = full_path(rel)
        if path.exists():
            try:
                settings = json.loads(path.read_text(encoding="utf-8-sig"))
                for section in ("LLM", "ScreenLLM"):
                    value = str((settings.get(section) or {}).get("ApiKey") or "").strip()
                    if value:
                        return value
            except Exception:
                pass
    return None


def main() -> int:
    os.chdir(ROOT)
    print("=" * 72)
    print("Six Stars Backend Launcher")
    print("=" * 72)
    print(f"Repo:   {ROOT}")
    print(f"Python launcher: {sys.executable}")
    print(f"Python services: {PY_EXE}")
    paths = describe_model_environment()
    print(f"Models: {paths['modelsRoot']}")
    print(f"HF cache: {paths['hfHubCache']}")
    print()

    manager = ServiceManager()
    manifest = load_json(CONFIG_DIR / "runtime-manifest.json")
    nvidia = has_nvidia()
    print(f"Hardware: NVIDIA={nvidia}")
    if require_cuda_policy() and not nvidia:
        print("ERROR   " + hosted_service_message())
        return 2

    oww_port = os.environ.get("OWW_PORT", "9000")
    whisper_port = os.environ.get("WHISPER_PORT", "8000")
    llm_port = os.environ.get("LOCAL_LLM_PORT", os.environ.get("LLM_PORT", "8080"))
    tts_port = os.environ.get("QWEN_TTS_PORT", os.environ.get("TTS_PORT", "8100"))

    # Local LLM is now part of the engine runtime. Default to starting it unless explicitly disabled.
    llm_endpoint: Optional[str] = None
    if not falsy(os.environ.get("SIX_STARS_START_LOCAL_LLM", "true")):
        llm_endpoint = start_engine_service(manager, "llm", manifest, nvidia)
    else:
        print("SKIP    Local LLM disabled by SIX_STARS_START_LOCAL_LLM=false")

    # Source fallback for LLM only exists for transformer test mode; engine llama.cpp is preferred.
    if llm_endpoint is None and not falsy(os.environ.get("SIX_STARS_START_LOCAL_LLM", "true")):
        local_llm_script = ROOT / "scripts" / "local_llm_openai_server.py"
        if local_llm_script.exists():
            model_name = os.environ.get("LOCAL_LLM_MODEL") or str(model_section("llm").get("modelName") or "google/gemma-4-E2B-it")
            model_path = os.environ.get("LOCAL_LLM_MODEL_PATH") or (model_local_path_if_exists("llm") or "")
            print(f"START   Source Local LLM OpenAI-compatible server: {model_name}")
            manager.start_service(
                "Source Local LLM",
                [PY_EXE, str(local_llm_script)],
                f"http://localhost:{llm_port}/v1/chat/completions",
                {
                    "LOCAL_LLM_PORT": llm_port,
                    "LOCAL_LLM_HOST": os.environ.get("LOCAL_LLM_HOST", "0.0.0.0"),
                    "LOCAL_LLM_MODEL": model_name,
                    "LOCAL_LLM_MODEL_PATH": model_path,
                    "LOCAL_LLM_DEVICE": os.environ.get("LOCAL_LLM_DEVICE", "cuda:0"),
                    "LOCAL_LLM_DTYPE": os.environ.get("LOCAL_LLM_DTYPE", "bfloat16"),
                    "SIX_STARS_REQUIRE_CUDA": os.environ.get("SIX_STARS_REQUIRE_CUDA", "true"),
                    "LOCAL_LLM_PRELOAD": os.environ.get("LOCAL_LLM_PRELOAD", "true"),
                    "LOCAL_LLM_TOKENIZER_USE_FAST": os.environ.get("LOCAL_LLM_TOKENIZER_USE_FAST", "false"),
                    "LOCAL_LLM_MAX_NEW_TOKENS": os.environ.get("LOCAL_LLM_MAX_NEW_TOKENS", "2048"),
                },
            )
            llm_endpoint = f"http://localhost:{llm_port}/v1/chat/completions"

    # OpenWakeWord remains optional/legacy.
    if truthy(os.environ.get("SIX_STARS_START_OPENWAKEWORD", "false")):
        openwakeword_script = ROOT / "scripts" / "openwakeword_server.py"
        if openwakeword_script.exists():
            manager.start_service("OpenWakeWord", [PY_EXE, str(openwakeword_script)], f"http://localhost:{oww_port}/health", {"OWW_PORT": oww_port})
    else:
        print("SKIP    OpenWakeWord disabled by SIX_STARS_START_OPENWAKEWORD=false")

    if not falsy(os.environ.get("SIX_STARS_START_WHISPER", "true")):
        asr_url = start_engine_service(manager, "asr", manifest, nvidia)
        if asr_url is None:
            whisper_script = ROOT / "scripts" / "faster_whisper_openai_server.py"
            if whisper_script.exists():
                print("START   Source faster-whisper transcription server")
                manager.start_service(
                    "Source Whisper",
                    [PY_EXE, str(whisper_script)],
                    f"http://localhost:{whisper_port}/health",
                    {
                        "WHISPER_DEVICE": "cuda",
                        "WHISPER_COMPUTE_TYPE": "float16",
                        "SIX_STARS_REQUIRE_CUDA": "true",
                    },
                )
    else:
        print("SKIP    Whisper disabled by SIX_STARS_START_WHISPER=false")

    if not falsy(os.environ.get("SIX_STARS_START_TTS", "true")):
        tts_url = start_engine_service(manager, "tts", manifest, nvidia)
        if tts_url is None:
            websocket_tts_script = ROOT / "scripts" / "websocket_tts_server.py"
            if websocket_tts_script.exists():
                qwen_model = os.environ.get("QWEN_TTS_MODEL_PATH", model_name_or_path("tts", "Qwen/Qwen3-TTS-12Hz-0.6B-Base"))
                resolved_qwen_model = resolve_model_ref("tts", qwen_model, os.environ.get("QWEN_TTS_LOCAL_PATH", ""), aliases=["qwen3-tts-0.6b", "qwen3-tts-0.6b-base", "qwen3-tts-1.7b", "qwen3-tts-1.7b-base"])
                print(f"START   Source WebSocket Qwen TTS: {resolved_qwen_model}")
                manager.start_service(
                    "Source WebSocket Qwen TTS",
                    [PY_EXE, str(websocket_tts_script)],
                    f"ws://localhost:{tts_port}/tts/stream",
                    {
                        "QWEN_TTS_MODEL_PATH": resolved_qwen_model,
                        "QWEN_TTS_DEVICE": "cuda",
                        "SIX_STARS_REQUIRE_CUDA": "true",
                        "QWEN_TTS_CHUNK_GENERATION_MODE": "parallel",
                        "QWEN_TTS_PARALLEL_CHUNKS": "true",
                        "QWEN_TTS_MAX_PARALLEL_CHUNKS": "0",
                    },
                )
    else:
        print("SKIP    Qwen TTS disabled by SIX_STARS_START_TTS=false")

    manager.print_table()

    print("Service endpoints")
    print("-" * 72)
    print(f"Whisper: http://localhost:{whisper_port}/v1/audio/transcriptions")
    active_llm_endpoint = llm_endpoint or os.environ.get("SIX_STARS_LLM_ENDPOINT", "http://localhost:8080/v1/chat/completions")
    print(f"LLM:     {active_llm_endpoint}")
    print(f"TTS:     ws://localhost:{tts_port}/tts/stream")
    print(f"Models:  {MODELS_ROOT}")
    print()

    llm_key = load_llm_api_key()
    headers = {"Authorization": f"Bearer {llm_key}"} if llm_key else None
    models_url = active_llm_endpoint.rsplit("/v1/chat/completions", 1)[0].rstrip("/") + "/v1/models"
    print_probe("LLM models", models_url, headers=headers)
    print()

    manager.wait()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
