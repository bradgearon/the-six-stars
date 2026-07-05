param(
    [string]$Backend = "cuda12",
    [string]$OutDir = "engines\llm\llama-cpp-dynamic-win-x64",
    [int]$MaxReleases = 20,
    [switch]$AllowPrerelease,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

function Get-ProjectRoot {
    $cwd = (Get-Location).Path

    if (
        (Test-Path (Join-Path $cwd "build-ai-redist.cmd")) -or
        (Test-Path (Join-Path $cwd "scripts")) -or
        (Get-ChildItem $cwd -Filter "*.sln" -ErrorAction SilentlyContinue | Select-Object -First 1)
    ) {
        return $cwd
    }

    if ((Split-Path -Leaf $PSScriptRoot) -ieq "scripts") {
        return (Split-Path -Parent $PSScriptRoot)
    }

    return $PSScriptRoot
}

function Get-BackendPatterns {
    param([string]$Backend)

    switch ($Backend.ToLowerInvariant()) {
        "cuda12" {
            return @{
                BinWildcard = "llama-*-bin-win-cuda-12.4-x64.zip"
                DllWildcard = "cudart-llama-bin-win-cuda-12.4-x64.zip"
                BinRegex = "https://github\.com/ggml-org/llama\.cpp/releases/download/[^)\s]+/llama-[^)\s]+-bin-win-cuda-12\.4-x64\.zip"
                DllRegex = "https://github\.com/ggml-org/llama\.cpp/releases/download/[^)\s]+/cudart-llama-bin-win-cuda-12\.4-x64\.zip"
            }
        }
        "cuda13" {
            return @{
                BinWildcard = "llama-*-bin-win-cuda-13.3-x64.zip"
                DllWildcard = "cudart-llama-bin-win-cuda-13.3-x64.zip"
                BinRegex = "https://github\.com/ggml-org/llama\.cpp/releases/download/[^)\s]+/llama-[^)\s]+-bin-win-cuda-13\.3-x64\.zip"
                DllRegex = "https://github\.com/ggml-org/llama\.cpp/releases/download/[^)\s]+/cudart-llama-bin-win-cuda-13\.3-x64\.zip"
            }
        }
        "cpu" {
            return @{
                BinWildcard = "llama-*-bin-win-cpu-x64.zip"
                DllWildcard = $null
                BinRegex = "https://github\.com/ggml-org/llama\.cpp/releases/download/[^)\s]+/llama-[^)\s]+-bin-win-cpu-x64\.zip"
                DllRegex = $null
            }
        }
        "vulkan" {
            return @{
                BinWildcard = "llama-*-bin-win-vulkan-x64.zip"
                DllWildcard = $null
                BinRegex = "https://github\.com/ggml-org/llama\.cpp/releases/download/[^)\s]+/llama-[^)\s]+-bin-win-vulkan-x64\.zip"
                DllRegex = $null
            }
        }
        default {
            throw "Unknown backend '$Backend'. Use cuda12, cuda13, cpu, or vulkan."
        }
    }
}

function Find-DownloadUrl {
    param(
        [Parameter(Mandatory=$true)] $Release,
        [Parameter(Mandatory=$true)] [string] $Wildcard,
        [Parameter(Mandatory=$true)] [string] $RegexPattern
    )

    if ($Release.assets -and $Release.assets.Count -gt 0) {
        $asset = $Release.assets |
            Where-Object { $_.name -like $Wildcard } |
            Select-Object -First 1

        if ($asset) {
            return $asset.browser_download_url
        }
    }

    if ($Release.body) {
        $match = [regex]::Match($Release.body, $RegexPattern)
        if ($match.Success) {
            return $match.Value
        }
    }

    return $null
}

function Download-FileStrict {
    param(
        [Parameter(Mandatory=$true)][string]$Url,
        [Parameter(Mandatory=$true)][string]$OutFile,
        [Parameter(Mandatory=$true)][hashtable]$Headers
    )

    Write-Host "Downloading:"
    Write-Host "  $Url"

    Invoke-WebRequest -Uri $Url -OutFile $OutFile -Headers $Headers

    if (!(Test-Path $OutFile)) {
        throw "Download did not create file: $OutFile"
    }

    $item = Get-Item $OutFile

    if ($item.Length -lt 1MB) {
        throw "Downloaded file looks too small: $($item.Length) bytes"
    }
}

$root = Get-ProjectRoot

Write-Host "Project root:"
Write-Host "  $root"

Push-Location $root

try {
    $patterns = Get-BackendPatterns -Backend $Backend

    $outFull = Join-Path $root $OutDir
    $tmpRoot = Join-Path $root "build\llama-prebuilt-download"

    if ($Force -and (Test-Path $outFull)) {
        Remove-Item $outFull -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $outFull | Out-Null
    New-Item -ItemType Directory -Force -Path $tmpRoot | Out-Null

    $headers = @{
        "User-Agent" = "SixStars-AI-Installer"
        "Accept" = "application/vnd.github+json"
    }

    Write-Host "Fetching llama.cpp releases..."

    $releasesUrl = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=$MaxReleases"
    $releases = Invoke-RestMethod -Uri $releasesUrl -Headers $headers

    if (!$AllowPrerelease) {
        $releases = $releases | Where-Object { -not $_.prerelease }
    }

    if (!$releases -or $releases.Count -eq 0) {
        throw "No llama.cpp releases found."
    }

    $lastError = $null

    foreach ($release in $releases) {
        $tag = $release.tag_name

        Write-Host ""
        Write-Host "Trying llama.cpp release $tag..."

        try {
            $binUrl = Find-DownloadUrl `
                -Release $release `
                -Wildcard $patterns.BinWildcard `
                -RegexPattern $patterns.BinRegex

            if (!$binUrl) {
                Write-Warning "Release $tag does not contain $($patterns.BinWildcard). Trying previous release."
                continue
            }

            $dllUrl = $null

            if ($patterns.DllWildcard) {
                $dllUrl = Find-DownloadUrl `
                    -Release $release `
                    -Wildcard $patterns.DllWildcard `
                    -RegexPattern $patterns.DllRegex

                if (!$dllUrl) {
                    Write-Warning "Release $tag does not contain $($patterns.DllWildcard). Trying previous release."
                    continue
                }
            }

            $stage = Join-Path $tmpRoot $tag

            if (Test-Path $stage) {
                Remove-Item $stage -Recurse -Force
            }

            New-Item -ItemType Directory -Force -Path $stage | Out-Null

            $binName = Split-Path $binUrl -Leaf
            $binZip = Join-Path $stage $binName

            Write-Host "Found llama binary package:"
            Write-Host "  $binName"

            Download-FileStrict -Url $binUrl -OutFile $binZip -Headers $headers

            $dllZip = $null

            if ($dllUrl) {
                $dllName = Split-Path $dllUrl -Leaf
                $dllZip = Join-Path $stage $dllName

                Write-Host "Found CUDA runtime DLL package:"
                Write-Host "  $dllName"

                Download-FileStrict -Url $dllUrl -OutFile $dllZip -Headers $headers
            }

            Write-Host "Extracting llama.cpp binary package..."
            Expand-Archive -Path $binZip -DestinationPath $stage -Force

            if ($dllZip) {
                Write-Host "Extracting CUDA runtime DLL package..."
                Expand-Archive -Path $dllZip -DestinationPath $stage -Force
            }

            $server = Get-ChildItem $stage -Recurse -Filter "llama-server.exe" | Select-Object -First 1

            if (!$server) {
                throw "Release $tag extracted, but llama-server.exe was not found."
            }

            Write-Host "Found llama-server.exe:"
            Write-Host "  $($server.FullName)"

            if (Test-Path $outFull) {
                Remove-Item $outFull -Recurse -Force
            }

            New-Item -ItemType Directory -Force -Path $outFull | Out-Null

            Write-Host "Installing release $tag to:"
            Write-Host "  $outFull"

            Copy-Item -Path (Join-Path $server.Directory.FullName "*") -Destination $outFull -Recurse -Force

            $installedServer = Join-Path $outFull "llama-server.exe"

            if (!(Test-Path $installedServer)) {
                throw "Install failed. llama-server.exe was not copied to $outFull."
            }

            Write-Host ""
            Write-Host "SUCCESS: Installed llama.cpp $tag for backend '$Backend'."
            Write-Host "  $installedServer"

            if ($dllZip) {
                Write-Host ""
                Write-Host "CUDA runtime DLLs were included from:"
                Write-Host "  $(Split-Path $dllZip -Leaf)"
            }

            Write-Host ""
            Write-Host "Test it with:"
            Write-Host "  `"$installedServer`" --help"

            exit 0
        }
        catch {
            $lastError = $_
            Write-Warning "Release $tag failed: $($_.Exception.Message)"
            Write-Warning "Trying previous release..."
            continue
        }
    }

    throw "Could not install llama.cpp backend '$Backend' after trying $MaxReleases releases. Last error: $lastError"
}
finally {
    Pop-Location
}