# One-shot installer for the NacreCraft development toolchain.
# Installs the .NET 10 SDK, the dotnet new project template and the ncm CLI.
# Idempotent: existing pieces are skipped or updated in place, so reruns are safe.
# Usage: ./tools/install.ps1

$ErrorActionPreference = "Stop"

$Channel = "10.0"
$TemplateId = "NacreCraft.ModsProjectType"
$ToolId = "NacreCraft.ModBuild.Tools"
$InstallRoot = Join-Path $env:USERPROFILE ".dotnet"
$ToolsDir = Join-Path $InstallRoot "tools"

#Invoke-Dotnet runs a dotnet command and aborts on a non-zero exit code, because a native failure does not trip $ErrorActionPreference
function Invoke-Dotnet([string[]]$Command) {
    & dotnet @Command
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Command -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Test-Command([string]$Name) {
    return [bool](Get-Command $Name -ErrorAction SilentlyContinue)
}

#Test-Sdk checks whether the machine has an SDK for the given major version
function Test-Sdk([string]$Major) {
    if (-not (Test-Command "dotnet")) { return $false }
    $sdks = & dotnet --list-sdks 2>$null
    return [bool]($sdks | Where-Object { $_ -match "^$Major\." })
}

#Install-Sdk uses the official script to install the SDK into the user directory without touching the system-wide copy
function Install-Sdk {
    $script = Join-Path $env:TEMP "dotnet-install.ps1"
    Write-Host "downloading dotnet-install.ps1"
    Invoke-WebRequest "https://dot.net/v1/dotnet-install.ps1" -OutFile $script -UseBasicParsing
    Write-Host "installing .NET SDK $Channel into $InstallRoot"
    & $script -Channel $Channel -InstallDir $InstallRoot -NoPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed with exit code $LASTEXITCODE" }
    Remove-Item $script -Force
}

Write-Host "==> checking .NET SDK"

if (Test-Sdk "10") {
    Write-Host "found SDK $(& dotnet --version)"
}
else {
    Install-Sdk
    $env:PATH = "$InstallRoot;$env:PATH"
    if (-not (Test-Sdk "10")) { throw ".NET 10 SDK still missing after install" }
    Write-Host "installed SDK $(& dotnet --version)"
}

#Tools directory must also be on PATH for ncm to be callable later in this session
$env:PATH = "$ToolsDir;$env:PATH"

Write-Host "==> installing project template"

#Install from a temp directory, otherwise a same-named folder in the current directory is treated as the template source and local sources get installed instead of the NuGet package
Push-Location $env:TEMP
try {
    Invoke-Dotnet @("new", "install", $TemplateId, "--force")
}
finally {
    Pop-Location
}

Write-Host "==> installing ncm"

$globalTools = Invoke-Dotnet @("tool", "list", "-g")
if ($globalTools -match $ToolId) {
    Invoke-Dotnet @("tool", "update", "-g", $ToolId)
}
else {
    Invoke-Dotnet @("tool", "install", "-g", $ToolId)
}

Write-Host "==> verifying"

if (Test-Command "ncm") {
    Write-Host "ncm ok -> $((Get-Command ncm).Source)"
}
else {
    Write-Host "ncm not on PATH in this session, open a new shell"
}

Write-Host ""
Write-Host "done"
Write-Host ""
Write-Host "if dotnet or ncm is not found in a new shell, add these to PATH:"
Write-Host "  $InstallRoot"
Write-Host "  $ToolsDir"
