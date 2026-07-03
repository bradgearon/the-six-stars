param(
    [string]$OutputZip = "",
    [string]$SourceDir = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceDir)) {
    $SourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
$SourceDir = (Resolve-Path $SourceDir).Path

if ([string]::IsNullOrWhiteSpace($OutputZip)) {
    $parent = Split-Path -Parent $SourceDir
    $name = Split-Path -Leaf $SourceDir
    $OutputZip = Join-Path $parent ($name + "-source.zip")
}

$sevenZip = $null
foreach ($candidate in @(
    "$env:ProgramFiles\7-Zip\7z.exe",
    "$env:ProgramFiles(x86)\7-Zip\7z.exe",
    "7z.exe"
)) {
    try {
        $cmd = Get-Command $candidate -ErrorAction Stop
        $sevenZip = $cmd.Source
        break
    } catch { }
}

if (-not $sevenZip) {
    throw "7-Zip was not found. Install 7-Zip or add 7z.exe to PATH."
}

$stagingParent = Join-Path ([System.IO.Path]::GetTempPath()) ("six-stars-pack-" + [Guid]::NewGuid().ToString("N"))
$stagingRoot = Join-Path $stagingParent (Split-Path -Leaf $SourceDir)
New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null

$excludedDirectoryNames = @(
    "bin", "obj", ".git", ".vs", ".vscode", ".idea", ".venv", "venv", "env", "__pycache__",
    ".pytest_cache", ".mypy_cache", "tts-debug", "logs", "_package_staging"
)
$excludedExtensions = @(".wav", ".mp3", ".flac", ".ogg", ".zip", ".7z", ".tar", ".gz", ".log")

try {
    Get-ChildItem -LiteralPath $SourceDir -Force | ForEach-Object {
        $relative = $_.FullName.Substring($SourceDir.Length).TrimStart('\', '/')
        if ($_.PSIsContainer -and $excludedDirectoryNames -contains $_.Name) { return }
        if ($_.Name.StartsWith('.') -and $_.PSIsContainer -and $_.Name -ne '.') { return }
        if (-not $_.PSIsContainer -and $excludedExtensions -contains $_.Extension.ToLowerInvariant()) { return }

        $destination = Join-Path $stagingRoot $relative
        if ($_.PSIsContainer) {
            robocopy $_.FullName $destination /E /NFL /NDL /NJH /NJS /NC /NS `
                /XD bin obj .git .vs .vscode .idea .venv venv env __pycache__ .pytest_cache .mypy_cache tts-debug logs _package_staging `
                /XF *.wav *.mp3 *.flac *.ogg *.zip *.7z *.tar *.gz *.log | Out-Null
            if ($LASTEXITCODE -gt 7) { throw "robocopy failed with exit code $LASTEXITCODE" }
        } else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
        }
    }

    if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }
    Push-Location $stagingParent
    & $sevenZip a -tzip $OutputZip (Split-Path -Leaf $stagingRoot) -mx=9 | Out-Host
    Pop-Location
    Write-Host "Created: $OutputZip"
} finally {
    if (Test-Path $stagingParent) { Remove-Item $stagingParent -Recurse -Force }
}
