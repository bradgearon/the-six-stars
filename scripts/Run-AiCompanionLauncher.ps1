param(
  [switch]$SkipInstall,
  [switch]$NoUi,
  [ValidateSet("auto", "cpu", "nvidia-cu12")][string]$Profile = "auto"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
Set-Location $Root

$launcherProject = "src/Wds.SixStars.Launcher/Wds.SixStars.Launcher.csproj"
$args = @("run", "--project", $launcherProject, "--")
if ($SkipInstall) { $args += "--skip-install" }
if ($NoUi) { $args += "--no-ui" }
$args += @("--profile", $Profile)
& dotnet @args
