# run-ai-services prebuilt EXE restore

`run-ai-services.cmd` is restored to services-only prebuilt engine behavior.

- `run-ai-companion.cmd` launches the full integrated prebuilt companion app path.
- `run-ai-services.cmd` launches the prebuilt engine EXEs only, using `launch.cmd --skip-install --no-ui`.
- `run-ai-engine-services.cmd` is now an alias for `run-ai-services.cmd`.
- `run-ai-source-services.cmd` is the venv/source debug flow for Python ASR/TTS.
- Advanced manual Python flow remains: activate `.venv`, then run `python scripts\start_services.py`.

Use this when the frozen/prebuilt service EXEs should be the normal one-click services-only path, while Python source service launching remains available for debugging.
