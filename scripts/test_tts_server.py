"""
Test script for Qwen TTS Server
Sends a test request and saves the audio output

Usage:
    python test_tts_server.py
"""

import requests
import sys
import os

SERVER_URL = "http://localhost:8100"

def test_health():
    """Test server health endpoint"""
    print("Testing health endpoint...")
    try:
        response = requests.get(f"{SERVER_URL}/health", timeout=5)
        if response.status_code == 200:
            data = response.json()
            print(f"? Server is healthy")
            print(f"  Backend: {data.get('backend', 'unknown')}")
            print(f"  Model loaded: {data.get('model_loaded', False)}")
            return True
        else:
            print(f"? Health check failed: {response.status_code}")
            return False
    except requests.exceptions.ConnectionError:
        print(f"? Could not connect to server at {SERVER_URL}")
        print("  Make sure the server is running: python qwen2_audio_tts_server.py")
        return False
    except Exception as e:
        print(f"? Health check error: {e}")
        return False

def test_speech_generation():
    """Test speech generation endpoint"""
    print("\nTesting speech generation...")

    test_cases = [
        {
            "input": "Hello, this is a test of the Qwen TTS server.",
            "voice": "alloy",
            "filename": "test_basic.wav"
        },
        {
            "input": "Elusiaa speaks with a gentle and kind voice.",
            "voice": "shimmer",
            "filename": "test_elusiaa.wav"
        },
        {
            "input": "The darkness calls to me, drawing me into the void.",
            "voice": "onyx",
            "filename": "test_kaelia.wav"
        }
    ]

    for i, test_case in enumerate(test_cases, 1):
        print(f"\nTest {i}/{len(test_cases)}: {test_case['input'][:40]}...")

        try:
            response = requests.post(
                f"{SERVER_URL}/v1/audio/speech",
                headers={"Content-Type": "application/json"},
                json={
                    "model": "tts-1",
                    "input": test_case["input"],
                    "voice": test_case["voice"],
                    "response_format": "wav",
                    "speed": 1.0
                },
                timeout=30
            )

            if response.status_code == 200:
                # Save audio file
                filename = test_case["filename"]
                with open(filename, 'wb') as f:
                    f.write(response.content)

                file_size = len(response.content)
                print(f"? Generated {file_size} bytes")
                print(f"  Saved to: {filename}")
                print(f"  Voice: {test_case['voice']}")

            else:
                print(f"? Request failed: {response.status_code}")
                print(f"  Response: {response.text}")
                return False

        except Exception as e:
            print(f"? Speech generation error: {e}")
            return False

    return True

def test_models_endpoint():
    """Test models listing endpoint"""
    print("\nTesting models endpoint...")
    try:
        response = requests.get(f"{SERVER_URL}/v1/models", timeout=5)
        if response.status_code == 200:
            data = response.json()
            models = data.get('data', [])
            print(f"? Found {len(models)} model(s)")
            for model in models:
                print(f"  - {model.get('id', 'unknown')}")
            return True
        else:
            print(f"? Models endpoint failed: {response.status_code}")
            return False
    except Exception as e:
        print(f"? Models endpoint error: {e}")
        return False

def play_audio(filename):
    """Attempt to play the generated audio"""
    print(f"\nAttempting to play {filename}...")

    if not os.path.exists(filename):
        print(f"? File not found: {filename}")
        return

    try:
        if sys.platform == 'win32':
            os.system(f'start {filename}')
            print(f"? Playing on Windows")
        elif sys.platform == 'darwin':
            os.system(f'afplay {filename}')
            print(f"? Playing on macOS")
        else:
            os.system(f'aplay {filename}')
            print(f"? Playing on Linux")
    except Exception as e:
        print(f"? Could not auto-play: {e}")
        print(f"  Please manually play: {filename}")

def main():
    print("=" * 60)
    print("Qwen TTS Server Test Suite")
    print("=" * 60)

    # Test health
    if not test_health():
        print("\n? Server health check failed. Exiting.")
        return 1

    # Test models
    test_models_endpoint()

    # Test speech generation
    if not test_speech_generation():
        print("\n? Speech generation tests failed.")
        return 1

    print("\n" + "=" * 60)
    print("? All tests passed!")
    print("=" * 60)

    # Offer to play audio
    print("\nGenerated audio files:")
    print("  - test_basic.wav")
    print("  - test_elusiaa.wav")
    print("  - test_kaelia.wav")

    try:
        play_first = input("\nPlay test_basic.wav? (y/n): ").strip().lower()
        if play_first == 'y':
            play_audio("test_basic.wav")
    except KeyboardInterrupt:
        print("\nSkipping playback.")

    print("\nTTS server is working correctly!")
    print("You can now use it with Six Stars application.")

    return 0

if __name__ == '__main__':
    sys.exit(main())
