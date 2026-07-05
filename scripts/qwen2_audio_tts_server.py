"""
Qwen3-TTS Server
OpenAI-compatible speech endpoint backed by the qwen-tts package.

This server prefers a local Qwen3-TTS model and falls back to lighter TTS
backends when the Qwen runtime is unavailable.
"""

from flask import Flask, request, jsonify, send_file
import torch
import logging
import os
import io
import importlib.util
import numpy as np
import wave
from pathlib import Path
from typing import Optional, Tuple


logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger(__name__)

app = Flask(__name__)


MODEL_PATH = os.environ.get('QWEN_MODEL_PATH', 'Qwen/Qwen3-TTS-12Hz-1.7B-Base')
PORT = int(os.environ.get('QWEN_TTS_PORT', '8100'))
HOST = os.environ.get('QWEN_TTS_HOST', '0.0.0.0')
DEVICE = os.environ.get('QWEN_TTS_DEVICE', 'cuda' if torch.cuda.is_available() else 'cpu')
DEFAULT_LANGUAGE = os.environ.get('QWEN_TTS_LANGUAGE', 'Auto')
DEFAULT_TASK_TYPE = os.environ.get('QWEN_TTS_TASK_TYPE', 'VoiceClone')
CLONE_AUDIO_DIR = Path(os.environ.get('QWEN_TTS_CLONE_AUDIO_DIR', 'src/Wds.SixStars.Api/Content/VoiceClones'))
SAMPLE_RATE = 24000
SERVER_VERSION = '2026-07-01-qwen3-v1'


model = None
processor = None
model_loaded = False
model_tts_supported = False
model_tts_support_reason = 'Qwen3-TTS model not initialized'
model_tts_mode = 'unavailable'
supported_speakers = []
supported_languages = []

VOICE_ALIASES = {
    'shimmer': 'serena',
    'alloy': 'serena',
    'echo': 'ryan',
    'fable': 'eric',
    'onyx': 'dylan',
    'nova': 'vivian',
    'elusiaa': 'serena',
    'elusiaa-dev': 'serena',
    'kaelia': 'vivian',
    'kaelia-dev': 'vivian',
    'maera': 'sohee',
    'maera-dev': 'sohee',
    'khavia': 'ono_anna',
    'khavia-dev': 'ono_anna',
    'auerlis': 'serena',
    'auerlis-dev': 'serena',
    'nyxari': 'vivian',
    'nyxari-dev': 'vivian'
}


def module_available(module_name: str) -> bool:
    return importlib.util.find_spec(module_name) is not None


def current_backend_name() -> str:
    if model_tts_supported:
        return 'qwen3-tts'
    if module_available('edge_tts'):
        return 'edge-tts-fallback'
    if module_available('pyttsx3'):
        return 'pyttsx3-fallback'
    return 'unavailable'


def normalize_task_type(value: str | None) -> str:
    if not value:
        return 'Auto'
    compact = ''.join(ch for ch in value if ch.isalnum()).lower()
    if compact == 'customvoice':
        return 'CustomVoice'
    if compact == 'voicedesign':
        return 'VoiceDesign'
    if compact == 'voiceclone':
        return 'VoiceClone'
    return 'Auto'


def normalized_language(value: str | None) -> str:
    if value is None or not str(value).strip():
        return DEFAULT_LANGUAGE
    return str(value).strip()


def normalize_voice_name(voice: str) -> str:
    value = (voice or '').strip()
    if not value:
        value = 'elusiaa'

    lowered = value.lower()
    mapped = VOICE_ALIASES.get(lowered, value)

    if supported_speakers:
        supported_lookup = {speaker.lower(): speaker for speaker in supported_speakers}
        if mapped.lower() in supported_lookup:
            resolved = supported_lookup[mapped.lower()]
            if resolved.lower() != lowered:
                logger.info("Mapped requested voice '%s' to Qwen3 speaker '%s'", value, resolved)
            return resolved

    return mapped


def resolve_clone_audio_path(value: str | None) -> Optional[str]:
    if value is None:
        return None

    candidate = str(value).strip()
    if not candidate:
        return None

    direct = Path(candidate)
    if direct.exists() and direct.is_file():
        return str(direct.resolve())

    clone_dir = CLONE_AUDIO_DIR
    if not clone_dir.is_absolute():
        clone_dir = (Path.cwd() / clone_dir).resolve()

    candidate_path = clone_dir / candidate
    if candidate_path.exists() and candidate_path.is_file():
        return str(candidate_path)

    if not Path(candidate).suffix:
        for ext in ('.wav', '.mp3', '.flac'):
            ext_path = clone_dir / f'{candidate}{ext}'
            if ext_path.exists() and ext_path.is_file():
                return str(ext_path)

    return None


def load_qwen_model() -> bool:
    global model, processor, model_loaded, model_tts_supported, model_tts_support_reason, model_tts_mode, supported_speakers, supported_languages

    try:
        logger.info(f"Loading Qwen3-TTS model from: {MODEL_PATH}")
        logger.info(f"Device: {DEVICE}")

        from qwen_tts import Qwen3TTSModel

        model = Qwen3TTSModel.from_pretrained(
            MODEL_PATH,
            dtype=torch.float16 if DEVICE == 'cuda' else torch.float32,
            device_map='auto' if DEVICE == 'cuda' else None,
            trust_remote_code=True
        )
        processor = getattr(model, 'processor', None)

        model_loaded = True
        raw_model = getattr(model, 'model', None)
        model_tts_mode = getattr(raw_model, 'tts_model_type', 'unknown') if raw_model is not None else 'unknown'
        supported_speakers = model.get_supported_speakers() or []
        supported_languages = model.get_supported_languages() or []
        if model_tts_mode in {'custom_voice', 'voice_design', 'base'}:
            model_tts_supported = True
            model_tts_support_reason = f'Loaded Qwen3-TTS model type: {model_tts_mode}'
            logger.info(f"Qwen3-TTS model loaded successfully ({model_tts_mode})")
            if supported_speakers:
                logger.info("Supported speakers: %s", ', '.join(supported_speakers))
            return True

        model_tts_supported = False
        model_tts_support_reason = f'Unsupported Qwen3-TTS model type: {model_tts_mode}'
        logger.warning(model_tts_support_reason)
        return True

    except Exception as e:
        logger.error(f"Failed to load Qwen3-TTS model: {e}")
        logger.warning('Falling back to alternative TTS...')
        model_loaded = False
        model_tts_supported = False
        model_tts_mode = 'unavailable'
        supported_speakers = []
        supported_languages = []
        model_tts_support_reason = f"{type(e).__name__}: {e}"
        return False


def generate_with_qwen(
    text: str,
    voice: str = 'default',
    instructions: str = '',
    language: str = 'Auto',
    task_type: str = 'VoiceClone',
    voice_clone_audio: str | None = None,
    voice_clone_ref_text: str | None = None,
    x_vector_only: bool = False
) -> Tuple[Optional[bytes], Optional[str]]:
    global model

    if not model_loaded:
        return None, 'Qwen3-TTS model is not loaded'

    if not model_tts_supported:
        return None, model_tts_support_reason

    try:
        resolved_task_type = normalize_task_type(task_type)
        resolved_language = normalized_language(language)
        qwen_model_type = model_tts_mode
        resolved_voice = normalize_voice_name(voice)

        request_voice_clone = resolved_task_type == 'VoiceClone' or qwen_model_type == 'base'

        if request_voice_clone:
            clone_audio_path = resolve_clone_audio_path(voice_clone_audio)
            if clone_audio_path is None:
                return None, (
                    'VoiceClone requires a reference audio file. '
                    'Set voice_clone_audio in the request, for example "elusiaa_clone.wav". '
                    f'Current clone dir: {str(CLONE_AUDIO_DIR)}'
                )

            if not hasattr(model, 'create_voice_clone_prompt') or not hasattr(model, 'generate_voice_clone'):
                return None, 'Loaded qwen_tts package does not support voice clone APIs (create_voice_clone_prompt/generate_voice_clone)'

            clone_prompt = model.create_voice_clone_prompt(
                ref_audio=clone_audio_path,
                ref_text=None if x_vector_only else (voice_clone_ref_text or None),
                x_vector_only_mode=x_vector_only,
            )

            wavs, sample_rate = model.generate_voice_clone(
                text=text,
                language=resolved_language,
                voice_clone_prompt=clone_prompt,
            )

            if not wavs:
                return None, 'Qwen3-TTS returned no waveform data for voice clone generation'

            return audio_to_wav(wavs[0], sample_rate), None

        if qwen_model_type == 'voice_design':
            wavs, sample_rate = model.generate_voice_design(
                text=text,
                instruct=instructions or '',
                language=resolved_language,
            )
        elif qwen_model_type == 'custom_voice':
            wavs, sample_rate = model.generate_custom_voice(
                text=text,
                speaker=resolved_voice,
                language=resolved_language,
                instruct=instructions or None,
            )
        elif qwen_model_type == 'base':
            return None, 'Loaded Qwen3-TTS base model only supports VoiceClone. Set task_type to VoiceClone and provide voice_clone_audio.'
        else:
            return None, f'Unsupported loaded Qwen3-TTS model type: {qwen_model_type}'

        if resolved_task_type == 'VoiceDesign' and qwen_model_type != 'voice_design':
            logger.warning('Request asked for VoiceDesign, but loaded model type is %s; using the loaded model type instead', qwen_model_type)
        if resolved_task_type == 'CustomVoice' and qwen_model_type != 'custom_voice':
            logger.warning('Request asked for CustomVoice, but loaded model type is %s; using the loaded model type instead', qwen_model_type)

        if not wavs:
            return None, 'Qwen3-TTS returned no waveform data'

        return audio_to_wav(wavs[0], sample_rate), None

    except Exception as e:
        logger.error(f"Qwen3-TTS generation failed: {e}", exc_info=True)
        return None, f"{type(e).__name__}: {e}"


def fallback_tts(text: str, voice: str = 'default') -> Tuple[Optional[bytes], Optional[str]]:
    fallback_errors = []

    if module_available('edge_tts'):
        try:
            import edge_tts
            import asyncio

            voice_map = {
                'default': 'en-US-AriaNeural',
                'female': 'en-US-AriaNeural',
                'male': 'en-US-GuyNeural',
                'alloy': 'en-US-AriaNeural',
                'echo': 'en-US-GuyNeural',
                'fable': 'en-US-DavisNeural',
                'onyx': 'en-US-ChristopherNeural',
                'nova': 'en-US-JennyNeural',
                'shimmer': 'en-US-MonicaNeural',
                'elusiaa': 'en-US-MonicaNeural'
            }

            selected_voice = voice_map.get(voice.lower(), 'en-US-AriaNeural')

            async def generate():
                communicate = edge_tts.Communicate(text, selected_voice)
                audio_data = b''
                async for chunk in communicate.stream():
                    if chunk['type'] == 'audio':
                        audio_data += chunk['data']
                return audio_data

            loop = asyncio.new_event_loop()
            asyncio.set_event_loop(loop)
            try:
                audio_data = loop.run_until_complete(generate())
            finally:
                loop.close()

            return audio_data, None

        except Exception as e:
            logger.error(f"Edge TTS fallback failed: {e}", exc_info=True)
            fallback_errors.append(f"edge_tts {type(e).__name__}: {e}")
    else:
        fallback_errors.append('edge_tts ModuleNotFoundError: No module named edge_tts')

    if module_available('pyttsx3'):
        try:
            import pyttsx3
            import tempfile

            engine = pyttsx3.init()
            voices = engine.getProperty('voices')
            if len(voices) > 1:
                engine.setProperty('voice', voices[1].id)

            with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
                tmp_path = tmp.name

            try:
                engine.save_to_file(text, tmp_path)
                engine.runAndWait()
                with open(tmp_path, 'rb') as audio_file:
                    return audio_file.read(), None
            finally:
                if os.path.exists(tmp_path):
                    os.remove(tmp_path)

        except Exception as e:
            logger.error(f"pyttsx3 fallback failed: {e}", exc_info=True)
            fallback_errors.append(f"pyttsx3 {type(e).__name__}: {e}")
    else:
        fallback_errors.append('pyttsx3 ModuleNotFoundError: No module named pyttsx3')

    return None, '; '.join(fallback_errors)


def audio_to_wav(audio_data, sample_rate: int = SAMPLE_RATE) -> bytes:
    """Convert a Qwen waveform to a standard mono PCM16 WAV.

    Qwen3-TTS may return a NumPy array, a torch.Tensor, or a list-like
    waveform depending on package version. This helper is deliberately
    defensive because a malformed conversion can create a WAV that has bytes
    but contains silence or unusable samples.
    """
    if audio_data is None:
        raise ValueError('audio_to_wav received None audio_data')

    if isinstance(audio_data, torch.Tensor):
        if getattr(audio_data, "is_meta", False):
            raise RuntimeError("Qwen TTS returned a meta tensor with no audio data; check CUDA model loading/device-map configuration.")
        if getattr(audio_data, "device", None) is not None and audio_data.device.type == "cuda":
            try:
                torch.cuda.synchronize(audio_data.device)
            except Exception:
                pass
        audio_np = audio_data.detach().to(device="cpu", dtype=torch.float32, copy=True).contiguous().numpy().copy()
    else:
        audio_np = np.asarray(audio_data)

    if audio_np.dtype == object:
        # Some streaming/batched APIs return a list of chunks. Flatten them.
        chunks = [np.asarray(chunk).reshape(-1) for chunk in audio_data if chunk is not None]
        if not chunks:
            raise ValueError('audio_to_wav received an empty object/chunk list')
        audio_np = np.concatenate(chunks)

    audio_np = np.asarray(audio_np)
    audio_np = np.squeeze(audio_np)

    if audio_np.ndim == 0:
        raise ValueError(f'audio_to_wav received scalar audio with shape {audio_np.shape}')

    if audio_np.ndim > 1:
        # Convert multi-channel or batched audio to mono. If it looks like
        # [channels, samples], average channels. If it looks like
        # [samples, channels], average last axis. Otherwise flatten safely.
        if audio_np.shape[0] in (1, 2):
            audio_np = np.mean(audio_np, axis=0)
        elif audio_np.shape[-1] in (1, 2):
            audio_np = np.mean(audio_np, axis=-1)
        else:
            audio_np = audio_np.reshape(-1)

    if audio_np.size == 0:
        raise ValueError('Qwen3-TTS returned an empty waveform array')

    audio_np = audio_np.astype(np.float32, copy=False)
    audio_np = np.nan_to_num(audio_np, nan=0.0, posinf=0.0, neginf=0.0)

    peak = float(np.max(np.abs(audio_np))) if audio_np.size else 0.0
    rms = float(np.sqrt(np.mean(np.square(audio_np)))) if audio_np.size else 0.0
    duration = float(audio_np.size) / float(sample_rate or SAMPLE_RATE)

    # If a backend returns integer-scale or otherwise over-range float audio,
    # normalize it instead of clipping almost everything.
    if peak > 1.2:
        logger.warning('Qwen waveform peak %.4f is outside [-1,1]; normalizing before WAV write', peak)
        audio_np = audio_np / peak
        peak = float(np.max(np.abs(audio_np)))
        rms = float(np.sqrt(np.mean(np.square(audio_np))))

    logger.info(
        'Qwen waveform stats: samples=%s, sample_rate=%s, duration=%.2fs, peak=%.6f, rms=%.6f',
        audio_np.size,
        sample_rate,
        duration,
        peak,
        rms,
    )

    if rms < 0.00001:
        logger.warning('Generated waveform is nearly silent. Check reference audio, ref_text, language, and x_vector_only settings.')

    audio_np = np.clip(audio_np, -1.0, 1.0)
    audio_int16 = (audio_np * 32767.0).astype(np.int16)

    wav_buffer = io.BytesIO()
    with wave.open(wav_buffer, 'wb') as wav_file:
        wav_file.setnchannels(1)
        wav_file.setsampwidth(2)
        wav_file.setframerate(int(sample_rate or SAMPLE_RATE))
        wav_file.writeframes(audio_int16.tobytes())

    wav_bytes = wav_buffer.getvalue()

    debug_dir = os.environ.get('QWEN_TTS_DEBUG_OUTPUT_DIR')
    if debug_dir:
        try:
            import datetime
            out_dir = Path(debug_dir)
            out_dir.mkdir(parents=True, exist_ok=True)
            stamp = datetime.datetime.now().strftime('%Y%m%d_%H%M%S_%f')
            out_path = out_dir / f'qwen_tts_{stamp}.wav'
            out_path.write_bytes(wav_bytes)
            logger.info('Saved debug WAV: %s', out_path)
        except Exception as debug_error:
            logger.warning('Failed to save debug WAV: %s', debug_error)

    return wav_bytes


@app.route('/health', methods=['GET'])
def health_check():
    return jsonify({
        'status': 'healthy',
        'model': MODEL_PATH,
        'device': DEVICE,
        'model_loaded': model_loaded,
        'backend': current_backend_name(),
        'server_version': SERVER_VERSION,
        'error_detail_level': 'enhanced',
        'qwen_tts_supported': model_tts_supported,
        'qwen_tts_reason': model_tts_support_reason,
        'qwen_tts_model_type': model_tts_mode,
        'supported_speakers': supported_speakers,
        'supported_languages': supported_languages,
        'default_task_type': DEFAULT_TASK_TYPE,
        'default_language': DEFAULT_LANGUAGE,
        'clone_audio_dir': str(CLONE_AUDIO_DIR)
    })


@app.route('/v1/audio/speech', methods=['POST'])
def create_speech():
    try:
        data = request.get_json()

        if not data:
            return jsonify({'error': 'No JSON data provided'}), 400

        text = data.get('input', '')
        if not text:
            return jsonify({'error': 'No input text provided'}), 400

        voice = data.get('voice', 'elusiaa')
        speed = float(data.get('speed', 1.0))
        response_format = data.get('response_format', 'wav')
        model_name = data.get('model', MODEL_PATH)
        instructions = data.get('instructions', '')
        language = data.get('language', DEFAULT_LANGUAGE)
        task_type = data.get('task_type', DEFAULT_TASK_TYPE)
        voice_clone_audio = (
            data.get('voice_clone_audio')
            or data.get('voice_clone_audio_file')
            or data.get('clone_audio')
            or data.get('reference_audio')
            or data.get('ref_audio')
        )
        voice_clone_ref_text = (
            data.get('voice_clone_ref_text')
            or data.get('clone_ref_text')
            or data.get('reference_text')
            or data.get('ref_text')
            or ''
        )
        x_vector_only = bool(data.get('x_vector_only', False))

        if not 0.25 <= speed <= 4.0:
            return jsonify({'error': 'Speed must be between 0.25 and 4.0'}), 400

        logger.info(
            "TTS Request: '%s...' | Voice: %s | Speed: %s | TaskType: %s | CloneAudio: %s",
            text[:60],
            voice,
            speed,
            task_type,
            voice_clone_audio or '(none)'
        )

        failure_details = []
        backend_used = current_backend_name()
        audio_bytes = None

        if model_tts_supported:
            logger.info('Using Qwen3-TTS model...')
            audio_bytes, qwen_error = generate_with_qwen(
                text,
                voice,
                instructions,
                language,
                task_type,
                voice_clone_audio,
                voice_clone_ref_text,
                x_vector_only,
            )

            if audio_bytes is not None:
                backend_used = 'qwen3-tts'
            else:
                logger.warning('Qwen generation failed, using fallback...')
                if qwen_error:
                    failure_details.append({
                        'backend': 'qwen3-tts',
                        'error': qwen_error
                    })
                audio_bytes, fallback_error = fallback_tts(text, voice)
                if audio_bytes is not None:
                    backend_used = current_backend_name()
                elif fallback_error:
                    failure_details.append({
                        'backend': current_backend_name(),
                        'error': fallback_error
                    })
        else:
            logger.info('Using fallback TTS backend...')
            if model_loaded and not model_tts_supported:
                failure_details.append({
                    'backend': 'qwen3-tts',
                    'error': model_tts_support_reason
                })
            audio_bytes, fallback_error = fallback_tts(text, voice)
            if fallback_error:
                failure_details.append({
                    'backend': current_backend_name(),
                    'error': fallback_error
                })

        if audio_bytes is None:
            response_payload = {
                'error': 'Speech generation failed',
                'backend': backend_used,
                'requestedVoice': voice,
                'requestedModel': model_name,
                'responseFormat': response_format,
                'modelLoaded': model_loaded,
                'qwenTtsSupported': model_tts_supported,
                'qwenTtsModelType': model_tts_mode,
            }
            if failure_details:
                response_payload['details'] = failure_details
            return jsonify(response_payload), 500

        logger.info(f"Generated {len(audio_bytes)} bytes of audio")

        return send_file(
            io.BytesIO(audio_bytes),
            mimetype='audio/wav',
            as_attachment=False,
            download_name='speech.wav'
        )

    except Exception as e:
        logger.error(f"Error in create_speech: {e}", exc_info=True)
        return jsonify({'error': str(e)}), 500


@app.route('/v1/models', methods=['GET'])
def list_models():
    return jsonify({
        'object': 'list',
        'data': [
            {
                'id': MODEL_PATH,
                'object': 'model',
                'created': 1699000000,
                'owned_by': 'qwen-tts-local'
            }
        ]
    })


@app.route('/', methods=['GET'])
def index():
    return jsonify({
        'name': 'Qwen3-TTS Server',
        'version': '1.0.0',
        'server_version': SERVER_VERSION,
        'model': MODEL_PATH,
        'device': DEVICE,
        'model_loaded': model_loaded,
        'backend': current_backend_name(),
        'qwen_tts_model_type': model_tts_mode,
        'endpoints': {
            'speech': '/v1/audio/speech (POST)',
            'health': '/health',
            'models': '/v1/models'
        }
    })


if __name__ == '__main__':
    logger.info('=' * 70)
    logger.info('Qwen3-TTS Server - OpenAI Compatible API')
    logger.info('=' * 70)
    logger.info(f'Model: {MODEL_PATH}')
    logger.info(f'Device: {DEVICE}')
    logger.info(f'Host: {HOST}')
    logger.info(f'Port: {PORT}')
    logger.info('=' * 70)

    logger.info('Initializing TTS engine...')
    if load_qwen_model():
        if model_tts_supported:
            logger.info(f'Using Qwen3-TTS model for speech generation ({model_tts_mode})')
        else:
            logger.warning('Qwen3-TTS loaded, but TTS generation is unsupported in this install')
            logger.info(f'Requests will use fallback backend: {current_backend_name()}')
    else:
        logger.warning('Qwen3-TTS not available')
        logger.info(f'Checking fallback backend availability: {current_backend_name()}')
        if current_backend_name() == 'unavailable':
            logger.error('No TTS backend available!')
            logger.error('Install edge-tts: pip install edge-tts')
            logger.error('Or install pyttsx3: pip install pyttsx3')
            raise SystemExit(1)

    logger.info('=' * 70)
    logger.info('Server Starting...')
    logger.info(f'API Endpoint: http://{HOST}:{PORT}/v1/audio/speech')
    logger.info('Press Ctrl+C to stop')
    logger.info('=' * 70)

    app.run(host=HOST, port=PORT, debug=False, threaded=True)
