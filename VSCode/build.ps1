# Builds the NacreCraft VS Code extension
# Publishes the language server into server/, compiles the TypeScript, then packs the vsix
[CmdletBinding()]
param(
    # also install the packed vsix into the running editor
    [switch]$Install,

    # remove out/, server/ and the vsix before building
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$devtools = Split-Path -Parent $here
$project = Join-Path $devtools "NacreCraft.NcMod.LangServer\NacreCraft.NcMod.LangServer.csproj"
$server = Join-Path $here "server"
$vsix = Join-Path $here "nacrecraft-ncmod-0.1.0.vsix"

if ($Clean) {
    Remove-Item -Recurse -Force (Join-Path $here "out") -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $server -ErrorAction SilentlyContinue
    Remove-Item -Force $vsix -ErrorAction SilentlyContinue
    Write-Host "cleaned"
    exit 0
}

#dotnet may sit outside PATH when the shell was started from the desktop
function Find-Dotnet {
    if ($env:DOTNET_ROOT) {
        $name = if ($env:OS -eq "Windows_NT") { "dotnet.exe" } else { "dotnet" }
        $candidate = Join-Path $env:DOTNET_ROOT $name
        if (Test-Path $candidate) { return $candidate }
    }

    return "dotnet"
}

Push-Location $here
try {
    Write-Host "publishing the language server"
    & (Find-Dotnet) publish $project -c Release -o $server --nologo -v q
    if ($LASTEXITCODE -ne 0) { exit 1 }

    #the server ships once, while each platform keeps a tiny apphost plus the shim that loads it
    $shim = Join-Path $devtools "NacreCraft.NcMod.LangServer.Shim\NacreCraft.NcMod.LangServer.Shim.csproj"
    $apphosts = Join-Path $server "apphost"
    Remove-Item -Recurse -Force $apphosts -ErrorAction SilentlyContinue

    foreach ($rid in @("win-x64", "linux-x64", "osx-x64", "osx-arm64")) {
        Write-Host "building the $rid apphost"
        $temp = Join-Path $env:TEMP ("ncm-shim-" + $rid)
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue

        & (Find-Dotnet) publish $shim -c Release -r $rid --self-contained false -o $temp --nologo -v q
        if ($LASTEXITCODE -ne 0) { exit 1 }

        $target = Join-Path $apphosts $rid
        New-Item -ItemType Directory -Force -Path $target | Out-Null
        Copy-Item (Join-Path $temp "NacreCraft.NcMod.LangServer.dll") $target -Force
        Copy-Item (Join-Path $temp "NacreCraft.NcMod.LangServer.deps.json") $target -Force
        Copy-Item (Join-Path $temp "NacreCraft.NcMod.LangServer.runtimeconfig.json") $target -Force

        $launcher = if ($rid.StartsWith("win-")) {
            "NacreCraft.NcMod.LangServer.exe"
        } else {
            "NacreCraft.NcMod.LangServer"
        }
        Copy-Item (Join-Path $temp $launcher) $target -Force
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
    }

    #the platform apphosts above replace the launchers a plain publish leaves in the server root
    Remove-Item (Join-Path $server "NacreCraft.NcMod.LangServer.exe") -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $server "NacreCraft.ModBuild.Tools.exe") -ErrorAction SilentlyContinue

    if (-not (Test-Path (Join-Path $here "node_modules"))) {
        Write-Host "installing npm dependencies"
        npm ci
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }

    Write-Host "bundling the textmate tokenizer"
    npx esbuild mediaSrc\textmate.js --bundle --format=iife --platform=browser --minify --outfile=media\textmate.js
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Copy-Item (Join-Path $here "node_modules\vscode-oniguruma\release\onig.wasm") (Join-Path $here "media\onig.wasm") -Force

    Write-Host "compiling"
    npm run compile
    if ($LASTEXITCODE -ne 0) { exit 1 }

    Write-Host "packaging"
    npx vsce package
    if ($LASTEXITCODE -ne 0) { exit 1 }

    Write-Host "built $vsix"

    if ($Install) {
        Write-Host "installing into the editor"
        code --install-extension $vsix --force
        if ($LASTEXITCODE -ne 0) { exit 1 }
    }
}
finally {
    Pop-Location
}
