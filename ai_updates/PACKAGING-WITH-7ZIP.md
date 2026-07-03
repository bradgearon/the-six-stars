# Packaging with 7-Zip

Use this when you want to send a clean source ZIP without build output, generated WAV files, Python virtualenvs, cache folders, or hidden/dot directories.

From CMD or Cmder:

```bat
cd /d U:\six-stars-solution
pack-solution-7zip.cmd
```

From PowerShell:

```powershell
cd U:\six-stars-solution
.\pack-solution-7zip.ps1
```

By default, the ZIP is written next to the solution folder:

```text
U:\six-stars-solution-source.zip
```

Custom output path:

```bat
pack-solution-7zip.cmd -OutputZip U:\six-stars-clean.zip
```

The script requires 7-Zip. It checks the normal install locations and then `PATH` for `7z.exe`.

Excluded by default:

- `bin/`, `obj/`
- `.venv/`, `venv/`, `env/`
- hidden/dot directories such as `.git/`, `.vs/`, `.vscode/`, `.idea/`, `.cache/`
- `__pycache__/` and Python bytecode
- `*.wav`
- `*.zip`, `*.7z`, logs, temp files, NuGet packages

The root `.gitignore` is included in the package.
