# Verifies the advice ncm prints for the deliberately broken cases in this project.
# Every `//expect:` comment must appear in the output, and every `//expect-not:` must not.

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$cases = Join-Path $root 'Cases'

if (-not (Get-Command ncm -ErrorAction SilentlyContinue)) {
    Write-Host 'ncm is not on PATH, install NacreCraft.ModBuild.Tools first' -ForegroundColor Red
    exit 1
}

Push-Location $root
try {
    # a non-zero exit code is expected, the project is broken on purpose
    $output = & ncm build --check-only --no-manifest --plain 2>&1 | Out-String
}
finally {
    Pop-Location
}

$expected = New-Object System.Collections.ArrayList
$forbidden = New-Object System.Collections.ArrayList

Get-ChildItem -Path $cases -Filter *.cs | ForEach-Object {
    $file = $_.Name
    foreach ($line in Get-Content $_.FullName) {
        if ($line -match '//\s*expect:\s*(.+)$') {
            [void]$expected.Add([pscustomobject]@{ Text = $Matches[1].Trim(); Where = $file })
        }
        elseif ($line -match '//\s*expect-not:\s*(.+)$') {
            [void]$forbidden.Add([pscustomobject]@{ Text = $Matches[1].Trim(); Where = $file })
        }
    }
}

$failures = 0

foreach ($item in $expected) {
    if ($output -notmatch [regex]::Escape($item.Text)) {
        Write-Host "MISSING    $($item.Where): $($item.Text)" -ForegroundColor Red
        $failures++
    }
}

foreach ($item in $forbidden) {
    if ($output -match [regex]::Escape($item.Text)) {
        Write-Host "UNEXPECTED $($item.Where): $($item.Text)" -ForegroundColor Red
        $failures++
    }
}

if ($failures -gt 0) {
    Write-Host "$failures expectation(s) failed" -ForegroundColor Red
    exit 1
}

Write-Host "all $($expected.Count) expectation(s) matched" -ForegroundColor Green
