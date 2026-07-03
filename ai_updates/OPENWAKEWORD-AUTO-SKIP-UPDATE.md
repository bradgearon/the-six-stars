# OpenWakeWord Auto-Skip Update

OpenWakeWord was exiting with code 1 when the launcher started it without any custom wake-word model files installed. The backend launcher now defaults `SIX_STARS_START_OPENWAKEWORD=auto`, which starts OpenWakeWord only when `.tflite` or `.onnx` models are found in `models/` or `OWW_MODEL_DIR`.

## Default behavior

```cmd
start.cmd
```

If no models exist, the launcher prints:

```text
SKIP    OpenWakeWord: no custom .tflite/.onnx models found in models/. Set SIX_STARS_START_OPENWAKEWORD=true to force it.
```

The rest of the services still launch.

## Force OpenWakeWord on

```cmd
set SIX_STARS_START_OPENWAKEWORD=true
set OWW_MODEL_DIR=U:\six-stars-solution\models
start.cmd
```

## Disable OpenWakeWord explicitly

```cmd
set SIX_STARS_START_OPENWAKEWORD=false
start.cmd
```

## Add custom wake word models

Place generated files such as these under `models/`:

```text
models\elusiaa_custom.tflite
models\kaelia_custom.tflite
models\maera_custom.tflite
models\khavia_custom.tflite
models\auerlis_custom.tflite
models\nyxari_custom.tflite
```

Then `start.cmd` will start the OpenWakeWord server in auto mode.
