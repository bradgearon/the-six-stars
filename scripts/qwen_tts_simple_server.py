"""
Qwen TTS Server - Simplified Implementation
Uses Qwen2-Audio or compatible TTS models via transformers

This is a simplified version that works with various TTS backends.
Adjust the generate_speech function based on your specific Qwen TTS installation.

Requirements:
    pip install flask torch transformers soundfile numpy scipy

Usage:
    python qwen_tts_simple_server.py
"""

from flask import Flask, request, jsonify, send_file
import logging
import os
import io
import numpy as np
import wave

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = Flask(__name__)

# Configuration
PORT = int(os.environ.get('QWEN_TTS_PORT', '8100'))
HOST = os.environ.get('QWEN_TTS_HOST', '0.0.0.0')
SAMPLE_RATE = 24000

# TTS Backend selection
TTS_BACKEND = os.environ.get('TTS_BACKEND', 'bark')  # Options: bark, coqui, edge, pyttsx3

def generate_speech_bark(text: str, voice: str = "default") -> bytes:
    """Generate speech using Bark TTS (recommended for quality)"""
    try:
        from bark import SAMPLE_RATE as BARK_RATE, generate_audio, preload_models

        # Preload models on first use
        if not hasattr(generate_speech_bark, 'models_loaded'):
            logger.info("Loading Bark models...")
            preload_models()
            generate_speech_bark.models_loaded = True

        # Generate audio
        audio_array = generate_audio(text)

        # Convert to 16-bit PCM
        audio_int16 = (audio_array * 32767).astype(np.int16)

        # Create WAV
        wav_buffer = io.BytesIO()
        with wave.open(wav_buffer, 'wb') as wav_file:
            wav_file.setnchannels(1)
            wav_file.setsampwidth(2)
            wav_file.setframerate(BARK_RATE)
            wav_file.writeframes(audio_int16.tobytes())

        wav_buffer.seek(0)
        return wav_buffer.read()

    except ImportError:
        logger.error("Bark not installed. Install with: pip install git+https://github.com/suno-ai/bark.git")
        raise

def generate_speech_coqui(text: str, voice: str = "default") -> bytes:
    """Generate speech using Coqui TTS"""
    try:
        from TTS.api import TTS

        # Initialize TTS on first use
        if not hasattr(generate_speech_coqui, 'tts'):
            logger.info("Loading Coqui TTS model...")
            generate_speech_coqui.tts = TTS("tts_models/en/ljspeech/tacotron2-DDC")

        # Generate to temporary file
        import tempfile
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            tmp_path = tmp.name

        generate_speech_coqui.tts.tts_to_file(text=text, file_path=tmp_path)

        # Read file
        with open(tmp_path, 'rb') as f:
            audio_data = f.read()

        # Clean up
        os.remove(tmp_path)

        return audio_data

    except ImportError:
        logger.error("Coqui TTS not installed. Install with: pip install TTS")
        raise

def generate_speech_edge(text: str, voice: str = "en-US-AriaNeural") -> bytes:
    """Generate speech using Edge TTS (online, free)"""
    try:
        import edge_tts
        import asyncio

        async def generate():
            communicate = edge_tts.Communicate(text, voice)
            audio_data = b''
            async for chunk in communicate.stream():
                if chunk["type"] == "audio":
                    audio_data += chunk["data"]
            return audio_data

        # Run async function
        loop = asyncio.new_event_loop()
        asyncio.set_event_loop(loop)
        audio_data = loop.run_until_complete(generate())
        loop.close()

        return audio_data

    except ImportError:
        logger.error("Edge TTS not installed. Install with: pip install edge-tts")
        raise

def generate_speech_pyttsx3(text: str, voice: str = "default") -> bytes:
    """Generate speech using pyttsx3 (offline, fast, lower quality)"""
    try:
        import pyttsx3
        import tempfile

        # Initialize engine
        engine = pyttsx3.init()

        # Configure voice
        voices = engine.getProperty('voices')
        if len(voices) > 1:
            engine.setProperty('voice', voices[1].id)  # Female voice

        # Generate to temporary file
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            tmp_path = tmp.name

        engine.save_to_file(text, tmp_path)
        engine.runAndWait()

        # Read file
        with open(tmp_path, 'rb') as f:
            audio_data = f.read()

        # Clean up
        os.remove(tmp_path)

        return audio_data

    except ImportError:
        logger.error("pyttsx3 not installed. Install with: pip install pyttsx3")
        raise

# Select TTS function based on backend
TTS_FUNCTIONS = {
    'bark': generate_speech_bark,
    'coqui': generate_speech_coqui,
    'edge': generate_speech_edge,
    'pyttsx3': generate_speech_pyttsx3
}

def generate_speech(text: str, voice: str = "default", speed: float = 1.0) -> bytes:
    """Generate speech using configured backend"""
    tts_func = TTS_FUNCTIONS.get(TTS_BACKEND)

    if tts_func is None:
        raise ValueError(f"Unknown TTS backend: {TTS_BACKEND}")

    return tts_func(text, voice)

@app.route('/health', methods=['GET'])
def health_check():
    """Health check endpoint"""
    return jsonify({
        'status': 'healthy',
        'backend': TTS_BACKEND,
        'port': PORT
    })

@app.route('/v1/audio/speech', methods=['POST'])
def create_speech():
    """
    OpenAI-compatible TTS endpoint

    Request JSON:
    {
        "input": "Text to convert to speech",
        "voice": "default",
        "speed": 1.0,
        "response_format": "wav"
    }
    """
    try:
        data = request.get_json()

        if not data:
            return jsonify({'error': 'No JSON data provided'}), 400

        text = data.get('input', '')
        if not text:
            return jsonify({'error': 'No input text provided'}), 400

        voice = data.get('voice', 'default')
        speed = float(data.get('speed', 1.0))
        response_format = data.get('response_format', 'wav')

        logger.info(f"Generating speech: {text[:50]}... (backend: {TTS_BACKEND})")

        # Generate speech
        audio_data = generate_speech(text, voice, speed)

        # Return audio
        return send_file(
            io.BytesIO(audio_data),
            mimetype='audio/wav',
            as_attachment=False,
            download_name='speech.wav'
        )

    except Exception as e:
        logger.error(f"Error: {e}", exc_info=True)
        return jsonify({'error': str(e)}), 500

@app.route('/v1/models', methods=['GET'])
def list_models():
    """List available models"""
    return jsonify({
        'object': 'list',
        'data': [
            {
                'id': 'tts-1',
                'object': 'model',
                'owned_by': f'{TTS_BACKEND}-local'
            }
        ]
    })

@app.route('/', methods=['GET'])
def index():
    """Root endpoint"""
    return jsonify({
        'name': 'Qwen TTS Server (Simplified)',
        'backend': TTS_BACKEND,
        'port': PORT,
        'endpoints': {
            'speech': '/v1/audio/speech (POST)',
            'health': '/health',
            'models': '/v1/models'
        }
    })

if __name__ == '__main__':
    logger.info("=" * 60)
    logger.info("Qwen TTS Server - OpenAI Compatible")
    logger.info("=" * 60)
    logger.info(f"Backend: {TTS_BACKEND}")
    logger.info(f"Port: {PORT}")
    logger.info(f"Host: {HOST}")
    logger.info("=" * 60)

    # Test backend
    try:
        logger.info("Testing TTS backend...")
        test_audio = generate_speech("Hello, this is a test.", "default", 1.0)
        logger.info(f"? TTS backend working! Generated {len(test_audio)} bytes")
    except Exception as e:
        logger.error(f"? TTS backend test failed: {e}")
        logger.error("Install a TTS backend:")
        logger.error("  Bark (best quality):  pip install git+https://github.com/suno-ai/bark.git")
        logger.error("  Coqui TTS:           pip install TTS")
        logger.error("  Edge TTS (online):   pip install edge-tts")
        logger.error("  pyttsx3 (fast):      pip install pyttsx3")
        exit(1)

    logger.info("Starting server...")
    logger.info(f"API: http://{HOST}:{PORT}/v1/audio/speech")

    app.run(host=HOST, port=PORT, debug=False, threaded=True)
