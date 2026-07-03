# OpenWakeWord Setup for Six Stars

This document explains how to set up and configure openWakeWord for wake word detection in the Six Stars application.

## Overview

The Six Stars application now supports openWakeWord for detecting custom wake words for each Star character. Speech audio is continuously sent to the wake word detector, which determines if a Star's name was spoken before processing the full transcription.

## Configuration

Configuration is managed through `appsettings.json` in the UI project. The file is automatically copied to the output directory during build.

### Configuration Structure

```json
{
  "OpenWakeWord": {
    "Provider": "openWakeWord",
    "EndpointUrl": "http://localhost:9000/detect",
    "ApiKey": null,
    "TimeoutSeconds": 10,
    "PayloadShape": "MultipartWav",
    "Models": [
      "hey_mycroft_v0.1.0.tflite",
      "alexa_v0.1.0.tflite"
    ],
    "Threshold": 0.5,
    "CustomWakeWords": {
      "Elusiaa": "elusiaa_custom.tflite",
      "Kaelia": "kaelia_custom.tflite",
      "Maera": "maera_custom.tflite",
      "Khavia": "khavia_custom.tflite",
      "Auerlis": "auerlis_custom.tflite",
      "Nyxari": "nyxari_custom.tflite"
    }
  }
}
```

### Configuration Parameters

- **Provider**: Should be "openWakeWord" for openWakeWord backend
- **EndpointUrl**: The HTTP endpoint where openWakeWord server is running
- **ApiKey**: Optional API key for authentication
- **TimeoutSeconds**: Request timeout in seconds
- **PayloadShape**: Format for audio payload (typically "MultipartWav")
- **Models**: Pre-trained TFLite models to load
- **Threshold**: Confidence threshold (0.0 to 1.0) for wake word detection
- **CustomWakeWords**: Mapping of Star names to custom wake word models

## Setting Up OpenWakeWord Server

### 1. Install OpenWakeWord

```bash
pip install openwakeword
```

### 2. Download or Train Models

You can use pre-trained models or train custom models for each Star:

```bash
# Download pre-trained models
python -m openwakeword.download_models

# Or train custom models (requires audio samples)
# See: https://github.com/dscripka/openWakeWord#custom-models
```

### 3. Create HTTP Server

Create a simple Flask server to expose openWakeWord via HTTP:

```python
from flask import Flask, request, jsonify
from openwakeword import Model
import numpy as np
import io
import wave

app = Flask(__name__)

# Load models
oww_model = Model(
    wakeword_models=['models/elusiaa_custom.tflite', 
                    'models/kaelia_custom.tflite',
                    # ... other models
                    ],
    inference_framework='tflite'
)

@app.route('/detect', methods=['POST'])
def detect_hotword():
    try:
        # Get audio from multipart form
        audio_file = request.files['audio']

        # Read WAV data
        with wave.open(io.BytesIO(audio_file.read()), 'rb') as wav:
            audio_data = np.frombuffer(wav.readframes(wav.getnframes()), dtype=np.int16)

        # Run prediction
        prediction = oww_model.predict(audio_data)

        # Find highest scoring wake word
        detected_words = []
        for word, score in prediction.items():
            if score > 0.5:  # Threshold
                detected_words.append({
                    'word': word,
                    'score': float(score)
                })

        return jsonify({
            'detected': len(detected_words) > 0,
            'words': detected_words
        })
    except Exception as e:
        return jsonify({'error': str(e)}), 500

if __name__ == '__main__':
    app.run(host='0.0.0.0', port=9000)
```

### 4. Run the Server

```bash
python openwakeword_server.py
```

## Speech Flow with OpenWakeWord

1. **Audio Capture**: Microphone captures PCM audio chunks (~100ms)
2. **VAD (Voice Activity Detection)**: Checks if speech is present
3. **Wake Word Detection**: Audio sent to openWakeWord endpoint
4. **Wake Word Matched**: If Star name detected with confidence > threshold
5. **Whisper Transcription**: Full audio sent to Whisper for transcription
6. **LLM Processing**: Transcribed text sent to LLM for Star response
7. **TTS Generation**: Response text converted to voice
8. **Playback**: Star's voice played back to user

## Environment Variables Override

You can override configuration settings with environment variables:

- `SIX_STARS_HOTWORD_ENDPOINT` - Override endpoint URL
- `SIX_STARS_HOTWORD_PROVIDER` - Override provider name
- `SIX_STARS_HOTWORD_API_KEY` - Set API key
- `SIX_STARS_HOTWORD_PAYLOAD_SHAPE` - Override payload format

## Debug Logging

The application logs wake word detection events to the Debug output window:

```
[OpenWakeWord] Configured endpoint: http://localhost:9000/detect
[OpenWakeWord] Provider: openWakeWord
[OpenWakeWord] Threshold: 0.5
[OpenWakeWord] Loaded 6 models
```

## State Change Tracking

The application now tracks running totals of all state changes:

### Per-Star Tracking
- Heart changes (positive/negative count and total)
- Trust changes (positive/negative count and total)
- Romance changes (positive/negative count and total)
- Benaveil Influence changes
- Interaction count
- Last interaction timestamp

### Combined Totals
All state changes across all Stars are aggregated and displayed in:
1. Debug JSON output window
2. Visual Studio Debug Output window

### Debug Output Format

```
=== DETERMINISTIC STATE CHANGE ===
Star: Elusiaa
Heart Delta: +2 (Suggested: +3)
Trust Delta: +1 (Suggested: +1)
Romance Delta: +2 (Suggested: +2)
Benaveil Delta: 0 (Suggested: 0)

=== COMBINED SESSION TOTALS ===
Total Interactions: 15
Combined Heart: +28
Combined Trust: +15
Combined Romance: +22
Combined Benaveil: -3

=== PER-STAR BREAKDOWN ===
Elusiaa Session Totals:
  Interactions: 10
  Heart: +20 (8+ / 2-)
  Trust: +12 (9+ / 1-)
  Romance: +18 (10+ / 0-)
  Benaveil: -2
  Last: 14:32:15
================================
```

## Troubleshooting

### Wake Word Not Detected
- Check that openWakeWord server is running
- Verify endpoint URL in configuration
- Lower the threshold value (try 0.3 or 0.4)
- Check Debug output for connection errors

### Models Not Loading
- Ensure model files exist in the specified paths
- Check model file format (should be .tflite)
- Verify server has read permissions

### High False Positives
- Increase threshold value (try 0.6 or 0.7)
- Retrain models with more negative examples
- Use longer wake words (2-3 syllables minimum)

## Additional Resources

- [OpenWakeWord GitHub](https://github.com/dscripka/openWakeWord)
- [Custom Model Training Guide](https://github.com/dscripka/openWakeWord/blob/main/docs/custom_models.md)
- [TFLite Model Documentation](https://www.tensorflow.org/lite/guide)
