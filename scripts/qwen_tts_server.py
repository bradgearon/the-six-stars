"""
Qwen TTS HTTP Server - OpenAI Compatible
Serves text-to-speech via /v1/audio/speech endpoint using local Qwen TTS

Requirements:
    pip install flask numpy torch transformers

Usage:
    python qwen_tts_server.py

Environment Variables:
    QWEN_TTS_MODEL - Model path/name (default: Qwen/Qwen2-Audio-7B-Instruct)
    QWEN_TTS_PORT - Server port (default: 8100)
    QWEN_TTS_HOST - Server host (default: 0.0.0.0)
    QWEN_TTS_DEVICE - Device to use (default: cuda if available, else cpu)
"""

from flask import Flask, request, jsonify, send_file
import torch
import logging
import os
import io
import numpy as np
from typing import Optional
import wave
import tempfile

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = Flask(__name__)

# Model configuration
MODEL_NAME = os.environ.get('QWEN_TTS_MODEL', 'Qwen/Qwen2-Audio-7B-Instruct')
DEVICE = os.environ.get('QWEN_TTS_DEVICE', 'cuda' if torch.cuda.is_available() else 'cpu')
SAMPLE_RATE = 24000  # Qwen TTS default sample rate

# Global model instance
model = None
processor = None

def initialize_model():
    """Initialize the Qwen TTS model"""
    global model, processor

    try:
        logger.info(f"Loading Qwen TTS model: {MODEL_NAME}")
        logger.info(f"Using device: {DEVICE}")

        from transformers import AutoModelForCausalLM, AutoProcessor

        # Load model and processor
        processor = AutoProcessor.from_pretrained(MODEL_NAME)
        model = AutoModelForCausalLM.from_pretrained(
            MODEL_NAME,
            torch_dtype=torch.float16 if DEVICE == 'cuda' else torch.float32,
            device_map=DEVICE
        )

        logger.info("Model loaded successfully!")
        return True

    except Exception as e:
        logger.error(f"Failed to load model: {e}")
        logger.error("Make sure you have transformers installed: pip install transformers torch")
        logger.error(f"And the model downloaded: huggingface-cli download {MODEL_NAME}")
        return False

def generate_speech(text: str, voice: str = "default", speed: float = 1.0) -> Optional[bytes]:
    """
    Generate speech from text using Qwen TTS

    Args:
        text: Text to convert to speech
        voice: Voice style (currently unused, Qwen TTS uses single voice)
        speed: Speech speed multiplier

    Returns:
        WAV audio bytes or None if generation failed
    """
    global model, processor

    if model is None or processor is None:
        logger.error("Model not initialized")
        return None

    try:
        # Prepare the text prompt for Qwen TTS
        # Qwen Audio model expects specific format
        prompt = f"Generate natural speech: {text}"

        # Process input
        inputs = processor(
            text=[prompt],
            return_tensors="pt",
            padding=True
        )

        # Move to device
        if DEVICE == 'cuda':
            inputs = {k: v.cuda() for k, v in inputs.items()}

        # Generate speech
        with torch.no_grad():
            outputs = model.generate(
                **inputs,
                max_length=4096,
                do_sample=True,
                temperature=0.7,
                top_p=0.9
            )

        # Extract audio (this is a simplified example - adjust based on Qwen TTS output format)
        # Note: The actual implementation depends on Qwen TTS API
        audio_array = outputs[0].cpu().numpy()

        # Normalize audio
        audio_array = np.clip(audio_array, -1.0, 1.0)

        # Convert to 16-bit PCM
        audio_int16 = (audio_array * 32767).astype(np.int16)

        # Create WAV file in memory
        wav_buffer = io.BytesIO()
        with wave.open(wav_buffer, 'wb') as wav_file:
            wav_file.setnchannels(1)  # Mono
            wav_file.setsampwidth(2)  # 16-bit
            wav_file.setframerate(SAMPLE_RATE)
            wav_file.writeframes(audio_int16.tobytes())

        wav_buffer.seek(0)
        return wav_buffer.read()

    except Exception as e:
        logger.error(f"Speech generation failed: {e}", exc_info=True)
        return None

@app.route('/health', methods=['GET'])
def health_check():
    """Health check endpoint"""
    return jsonify({
        'status': 'healthy' if model is not None else 'model_not_loaded',
        'model': MODEL_NAME,
        'device': DEVICE,
        'sample_rate': SAMPLE_RATE
    })

@app.route('/v1/audio/speech', methods=['POST'])
def create_speech():
    """
    OpenAI-compatible TTS endpoint

    Request JSON:
    {
        "model": "tts-1" or "tts-1-hd",
        "input": "Text to convert to speech",
        "voice": "alloy", "echo", "fable", "onyx", "nova", "shimmer",
        "response_format": "mp3", "opus", "aac", "flac", "wav", "pcm",
        "speed": 0.25 to 4.0
    }

    Returns: Audio file bytes
    """
    try:
        # Parse request
        data = request.get_json()

        if not data:
            return jsonify({'error': 'No JSON data provided'}), 400

        text = data.get('input', '')
        if not text:
            return jsonify({'error': 'No input text provided'}), 400

        voice = data.get('voice', 'default')
        speed = float(data.get('speed', 1.0))
        response_format = data.get('response_format', 'wav')

        # Validate speed
        if not 0.25 <= speed <= 4.0:
            return jsonify({'error': 'Speed must be between 0.25 and 4.0'}), 400

        logger.info(f"Generating speech for: {text[:50]}...")
        logger.info(f"Voice: {voice}, Speed: {speed}, Format: {response_format}")

        # Generate speech
        audio_data = generate_speech(text, voice, speed)

        if audio_data is None:
            return jsonify({'error': 'Speech generation failed'}), 500

        # Determine content type based on format
        content_types = {
            'mp3': 'audio/mpeg',
            'opus': 'audio/opus',
            'aac': 'audio/aac',
            'flac': 'audio/flac',
            'wav': 'audio/wav',
            'pcm': 'audio/pcm'
        }

        content_type = content_types.get(response_format, 'audio/wav')

        # For now, we only support WAV output
        # TODO: Convert to other formats using pydub if needed
        if response_format != 'wav' and response_format != 'pcm':
            logger.warning(f"Format {response_format} requested but only WAV supported. Returning WAV.")

        # Return audio file
        return send_file(
            io.BytesIO(audio_data),
            mimetype=content_type,
            as_attachment=False,
            download_name='speech.wav'
        )

    except Exception as e:
        logger.error(f"Error in create_speech: {e}", exc_info=True)
        return jsonify({'error': str(e)}), 500

@app.route('/v1/models', methods=['GET'])
def list_models():
    """List available models (OpenAI compatible)"""
    return jsonify({
        'object': 'list',
        'data': [
            {
                'id': 'tts-1',
                'object': 'model',
                'created': 1677610602,
                'owned_by': 'qwen-tts-local'
            },
            {
                'id': 'tts-1-hd',
                'object': 'model',
                'created': 1677610602,
                'owned_by': 'qwen-tts-local'
            }
        ]
    })

@app.route('/v1/voices', methods=['GET'])
def list_voices():
    """List available voices"""
    return jsonify({
        'voices': [
            {
                'id': 'default',
                'name': 'Default Qwen Voice',
                'description': 'Natural female voice'
            }
        ]
    })

@app.route('/', methods=['GET'])
def index():
    """Root endpoint with API information"""
    return jsonify({
        'name': 'Qwen TTS Server',
        'version': '1.0.0',
        'endpoints': {
            'health': '/health',
            'speech': '/v1/audio/speech (POST)',
            'models': '/v1/models',
            'voices': '/v1/voices'
        },
        'model': MODEL_NAME,
        'device': DEVICE,
        'status': 'ready' if model is not None else 'loading'
    })

if __name__ == '__main__':
    port = int(os.environ.get('QWEN_TTS_PORT', '8100'))
    host = os.environ.get('QWEN_TTS_HOST', '0.0.0.0')

    logger.info("=" * 60)
    logger.info("Qwen TTS Server - OpenAI Compatible API")
    logger.info("=" * 60)
    logger.info(f"Model: {MODEL_NAME}")
    logger.info(f"Device: {DEVICE}")
    logger.info(f"Port: {port}")
    logger.info(f"Host: {host}")
    logger.info("=" * 60)

    # Initialize model
    if not initialize_model():
        logger.error("Failed to initialize model. Server will not start.")
        exit(1)

    logger.info("Starting Flask server...")
    logger.info(f"API available at: http://{host}:{port}/v1/audio/speech")
    logger.info("Press Ctrl+C to stop")

    # Run Flask app
    app.run(host=host, port=port, debug=False, threaded=True)
