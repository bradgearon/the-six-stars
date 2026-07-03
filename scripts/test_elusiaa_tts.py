#!/usr/bin/env python3
"""
Generate a single Elusiaa voice sample through the WebSocket TTS server.

Usage:
    python scripts/test_elusiaa_tts.py

Environment:
    TTS_SERVER_URL=ws://localhost:8100/tts/stream
    # http://localhost:8100 is also accepted and converted to ws://localhost:8100/tts/stream
    ELUSIAA_TTS_OUTPUT=test_elusiaa.wav
    ELUSIAA_TTS_TEXT=Little one, stay close to the light.
"""

from __future__ import annotations

import asyncio
import io
import json
import os
import struct
import sys
import wave
from pathlib import Path
from typing import Dict, List, Tuple

try:
    import websockets
except ImportError:
    print("websockets is not installed. Run: python -m pip install websockets")
    raise


def normalize_ws_url(value: str) -> str:
    raw = (value or "ws://localhost:8100/tts/stream").strip().rstrip("/")
    if raw.startswith("http://"):
        raw = "ws://" + raw[len("http://"):]
    elif raw.startswith("https://"):
        raw = "wss://" + raw[len("https://"):]
    if raw.startswith("ws://") or raw.startswith("wss://"):
        if not raw.endswith("/tts/stream"):
            raw += "/tts/stream"
        return raw
    return "ws://" + raw + "/tts/stream"


SERVER_URL = normalize_ws_url(os.environ.get("TTS_SERVER_URL", "ws://localhost:8100/tts/stream"))
OUTPUT_PATH = Path(os.environ.get("ELUSIAA_TTS_OUTPUT", "test_elusiaa.wav"))
INPUT_TEXT = os.environ.get(
    "ELUSIAA_TTS_TEXT",
    "Little one, stay close to the light. Mercy is how we remember we are not gods.",
)
VOICE_ID = os.environ.get("ELUSIAA_TTS_VOICE", "elusiaa-dev")
STAR_ID = os.environ.get("ELUSIAA_TTS_STAR", "Elusiaa")
TASK_TYPE = os.environ.get("ELUSIAA_TTS_TASK_TYPE", "VoiceClone")
MODEL_ID = os.environ.get("ELUSIAA_TTS_MODEL", "Qwen/Qwen3-TTS-12Hz-0.6B-Base")
CLONE_AUDIO = os.environ.get("ELUSIAA_TTS_CLONE_AUDIO", "elusiaa_clone.wav")
CLONE_REF_TEXT = os.environ.get(
    "ELUSIAA_TTS_CLONE_REF_TEXT",
    "Little one, stay close to the light. Mercy is how we remember we are not gods. If the cave grows loud, I will keep my voice beside your work like a lantern, soft when you need peace and radiant when you need courage.",
)


def parse_audio_message(message: bytes) -> Tuple[int, int, str, str, str, bytes]:
    offset = 0

    def read_i32() -> int:
        nonlocal offset
        value = struct.unpack_from("<i", message, offset)[0]
        offset += 4
        return value

    chunk_index = read_i32()
    total_chunks = read_i32()
    text_len = read_i32()
    text = message[offset:offset + text_len].decode("utf-8")
    offset += text_len
    star_len = read_i32()
    star_id = message[offset:offset + star_len].decode("utf-8")
    offset += star_len
    voice_len = read_i32()
    voice_id = message[offset:offset + voice_len].decode("utf-8")
    offset += voice_len
    wav_bytes = message[offset:]
    return chunk_index, total_chunks, text, star_id, voice_id, wav_bytes


def concatenate_wavs(chunks: List[bytes]) -> bytes:
    if not chunks:
        raise RuntimeError("No WAV chunks received")
    if len(chunks) == 1:
        return chunks[0]

    sample_rate = None
    sample_width = None
    channels = None
    frames: List[bytes] = []

    for wav_bytes in chunks:
        with wave.open(io.BytesIO(wav_bytes), "rb") as reader:
            fmt = (reader.getframerate(), reader.getsampwidth(), reader.getnchannels())
            if sample_rate is None:
                sample_rate, sample_width, channels = fmt
            elif fmt != (sample_rate, sample_width, channels):
                raise RuntimeError(f"Cannot concatenate WAV chunks with different formats: {fmt} != {(sample_rate, sample_width, channels)}")
            frames.append(reader.readframes(reader.getnframes()))

    out = io.BytesIO()
    with wave.open(out, "wb") as writer:
        writer.setnchannels(int(channels or 1))
        writer.setsampwidth(int(sample_width or 2))
        writer.setframerate(int(sample_rate or 24000))
        for frame_block in frames:
            writer.writeframes(frame_block)
    return out.getvalue()


async def generate_elusiaa_sample() -> bool:
    request: Dict[str, object] = {
        "type": "generate",
        "requestId": 1,
        "text": INPUT_TEXT,
        "textParts": [INPUT_TEXT],
        "starId": STAR_ID,
        "voiceId": VOICE_ID,
        "model": MODEL_ID,
        "language": "English",
        "responseFormat": "wav",
        "taskType": TASK_TYPE,
        "voiceCloneAudio": CLONE_AUDIO,
        "voiceCloneRefText": CLONE_REF_TEXT,
        "chunkGenerationMode": "first_then_prefetch",
        "maxParallelChunks": 1,
    }

    print(f"Connecting to {SERVER_URL}")
    wav_chunks_by_index: Dict[int, bytes] = {}
    expected_total = None

    async with websockets.connect(SERVER_URL, max_size=64 * 1024 * 1024) as websocket:
        first_message = await websocket.recv()
        if isinstance(first_message, str):
            status = json.loads(first_message)
            print("Server status:")
            print(json.dumps(status, indent=2))
        else:
            print("Warning: expected status text message, got binary")

        await websocket.send(json.dumps(request))
        print("Generation request sent through WebSocket.")

        while True:
            message = await websocket.recv()
            if isinstance(message, str):
                payload = json.loads(message)
                msg_type = payload.get("type")
                if msg_type == "chunk_info":
                    print(f"Chunk info: {payload.get('chunkIndex', 0) + 1}/{payload.get('totalChunks')} {payload.get('text')!r}")
                elif msg_type == "complete":
                    print("Complete:")
                    print(json.dumps(payload, indent=2))
                    expected_total = int(payload.get("totalChunks") or expected_total or len(wav_chunks_by_index))
                    break
                elif msg_type == "error":
                    print("Server returned error:")
                    print(json.dumps(payload, indent=2))
                    return False
                elif msg_type == "status":
                    print("Status:", payload.get("message"))
                else:
                    print("Message:", json.dumps(payload, indent=2))
            else:
                chunk_index, total_chunks, text, star_id, voice_id, wav_bytes = parse_audio_message(message)
                expected_total = total_chunks
                wav_chunks_by_index[chunk_index] = wav_bytes
                print(f"Received audio chunk {chunk_index + 1}/{total_chunks}: {len(wav_bytes)} bytes, text={text!r}, star={star_id}, voice={voice_id}")

    if expected_total is None:
        expected_total = len(wav_chunks_by_index)
    ordered = [wav_chunks_by_index[i] for i in sorted(wav_chunks_by_index)]
    if len(ordered) != expected_total:
        print(f"Warning: expected {expected_total} chunks, received {len(ordered)}")
    wav_data = concatenate_wavs(ordered)
    OUTPUT_PATH.write_bytes(wav_data)
    print(f"Saved Elusiaa sample to: {OUTPUT_PATH.resolve()}")
    print(f"Bytes written: {len(wav_data)}")
    return True


def main() -> int:
    print("=" * 60)
    print("Elusiaa WebSocket TTS Test")
    print("=" * 60)
    ok = asyncio.run(generate_elusiaa_sample())
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
