"""
OpenWakeWord HTTP Server for Six Stars
Exposes wake word detection via HTTP endpoint

Requirements:
pip install openwakeword flask numpy

Usage:
python openwakeword_server.py
"""

from flask import Flask, request, jsonify
from openwakeword import Model
import numpy as np
import io
import wave
import logging
import os

# Configure logging
logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

app = Flask(__name__)

# Model configuration - customize this based on your models
MODEL_DIR = os.environ.get('OWW_MODEL_DIR', 'models')
THRESHOLD = float(os.environ.get('OWW_THRESHOLD', '0.5'))
ALLOW_DEFAULT_MODEL = os.environ.get('OWW_ALLOW_DEFAULT_MODEL', 'false').strip().lower() in ('1', 'true', 'yes', 'on')

# Star name to model mapping
STAR_MODELS = {
    'elusiaa': os.path.join(MODEL_DIR, 'elusiaa_custom.tflite'),
    'kaelia': os.path.join(MODEL_DIR, 'kaelia_custom.tflite'),
    'maera': os.path.join(MODEL_DIR, 'maera_custom.tflite'),
    'khavia': os.path.join(MODEL_DIR, 'khavia_custom.tflite'),
    'auerlis': os.path.join(MODEL_DIR, 'auerlis_custom.tflite'),
    'nyxari': os.path.join(MODEL_DIR, 'nyxari_custom.tflite'),
}

# Load available models
available_models = []
for star, model_path in STAR_MODELS.items():
    if os.path.exists(model_path):
        available_models.append(model_path)
        logger.info(f"Found model for {star}: {model_path}")
    else:
        logger.warning(f"Model not found for {star}: {model_path}")

# Fallback to pre-trained models only when explicitly requested. On many Windows
# installs the package will not have a bundled hey_mycroft tflite file available,
# which causes immediate process exit. The managed launcher now skips this service
# in auto mode unless custom models exist.
if not available_models:
    if ALLOW_DEFAULT_MODEL:
        logger.warning("No custom models found. Using default 'hey_mycroft' model because OWW_ALLOW_DEFAULT_MODEL=true.")
        available_models = ['hey_mycroft_v0.1.0.tflite']
    else:
        logger.error("No OpenWakeWord custom models found. Put .tflite/.onnx files in OWW_MODEL_DIR/models or set OWW_ALLOW_DEFAULT_MODEL=true.")
        raise SystemExit(2)

# Initialize OpenWakeWord model
logger.info(f"Loading {len(available_models)} wake word models...")
oww_model = Model(
    wakeword_models=available_models,
    inference_framework='tflite'
)
logger.info("Models loaded successfully!")

@app.route('/health', methods=['GET'])
def health_check():
    """Health check endpoint"""
    return jsonify({
        'status': 'healthy',
        'models_loaded': len(available_models),
        'threshold': THRESHOLD
    })

@app.route('/detect', methods=['POST'])
def detect_hotword():
    """
    Detect wake words in audio

    Expects multipart/form-data with 'audio' field containing WAV file
    Returns JSON with detected wake words and confidence scores
    """
    try:
        # Check if audio file is present
        if 'audio' not in request.files:
            return jsonify({'error': 'No audio file provided'}), 400

        audio_file = request.files['audio']

        # Read WAV data
        try:
            with wave.open(io.BytesIO(audio_file.read()), 'rb') as wav:
                # Verify format
                if wav.getnchannels() != 1:
                    return jsonify({'error': 'Audio must be mono'}), 400
                if wav.getsampwidth() != 2:
                    return jsonify({'error': 'Audio must be 16-bit PCM'}), 400
                if wav.getframerate() != 16000:
                    logger.warning(f"Sample rate is {wav.getframerate()} Hz, expected 16000 Hz")

                # Read audio data
                audio_data = np.frombuffer(
                    wav.readframes(wav.getnframes()), 
                    dtype=np.int16
                )
        except Exception as e:
            return jsonify({'error': f'Invalid WAV format: {str(e)}'}), 400

        # Run prediction
        prediction = oww_model.predict(audio_data)

        # Find detected wake words above threshold
        detected_words = []
        max_score = 0.0
        best_match = None

        for word, score in prediction.items():
            score_val = float(score)
            if score_val > THRESHOLD:
                detected_words.append({
                    'word': word,
                    'score': score_val
                })
                if score_val > max_score:
                    max_score = score_val
                    best_match = word

        # Map model name back to Star ID
        star_id = None
        if best_match:
            for star, model_path in STAR_MODELS.items():
                if star in best_match.lower():
                    star_id = star.capitalize()
                    break

        logger.info(f"Detection: {len(detected_words)} word(s) detected, best: {best_match} ({max_score:.3f})")

        return jsonify({
            'detected': len(detected_words) > 0,
            'words': detected_words,
            'bestMatch': best_match,
            'bestScore': max_score,
            'starId': star_id,
            'threshold': THRESHOLD
        })

    except Exception as e:
        logger.error(f"Detection error: {str(e)}", exc_info=True)
        return jsonify({'error': str(e)}), 500

@app.route('/models', methods=['GET'])
def list_models():
    """List loaded models and their configuration"""
    return jsonify({
        'models': available_models,
        'threshold': THRESHOLD,
        'model_dir': MODEL_DIR,
        'star_models': STAR_MODELS
    })

if __name__ == '__main__':
    port = int(os.environ.get('OWW_PORT', '9000'))
    host = os.environ.get('OWW_HOST', '0.0.0.0')

    logger.info(f"Starting OpenWakeWord server on {host}:{port}")
    logger.info(f"Threshold: {THRESHOLD}")
    logger.info(f"Model directory: {MODEL_DIR}")

    app.run(host=host, port=port, debug=False)
