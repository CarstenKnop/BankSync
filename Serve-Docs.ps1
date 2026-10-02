<#
.SYNOPSIS
    Serves the whitepaper site on localhost without GitLab Pages.

.DESCRIPTION
    Creates a Python virtual environment in .venv (git-ignored), installs
    mkdocs-material into it on first run, then starts "mkdocs serve" and
    opens the browser. The site reloads automatically when a .md file changes.

    Requires Python 3.9+ on PATH (python or py launcher). No Docker needed.

.PARAMETER Port
    TCP port to listen on. Default 8000.

.PARAMETER Build
    Only build the static site into .\site (no server). Open site\index.html
    afterwards, or copy the folder anywhere.

.PARAMETER NoBrowser
    Do not open the browser automatically.

.EXAMPLE
    .\Serve-Docs.ps1
    .\Serve-Docs.ps1 -Port 8080
    .\Serve-Docs.ps1 -Build
#>
[CmdletBinding()]
param(
    [int]$Port = 8000,
    [switch]$Build,
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$venv = Join-Path $repo '.venv'
$venvPython = Join-Path $venv 'Scripts\python.exe'

function Find-Python {
    foreach ($candidate in @('python', 'py')) {
        $cmd = Get-Command $candidate -ErrorAction SilentlyContinue
        if ($cmd) {
            $version = & $cmd.Source --version 2>&1
            if ($version -match 'Python 3\.(\d+)' -and [int]$Matches[1] -ge 9) {
                return $cmd.Source
            }
        }
    }
    throw 'Python 3.9 or newer was not found on PATH. Install it from https://www.python.org/downloads/ and tick "Add to PATH".'
}

if (-not (Test-Path $venvPython)) {
    $python = Find-Python
    Write-Host "Creating virtual environment in $venv ..."
    & $python -m venv $venv
}

$installed = & $venvPython -m pip show mkdocs-material 2>$null
if (-not $installed) {
    Write-Host 'Installing mkdocs-material (first run only) ...'
    & $venvPython -m pip install --quiet --upgrade pip
    & $venvPython -m pip install --quiet mkdocs-material
}

Push-Location $repo
try {
    if ($Build) {
        & $venvPython -m mkdocs build --strict --site-dir site
        Write-Host "Static site written to $(Join-Path $repo 'site'). Open site\index.html in a browser."
        return
    }

    $url = "http://127.0.0.1:$Port/"
    Write-Host "Serving the documentation at $url  (Ctrl+C to stop)"
    if (-not $NoBrowser) {
        Start-Job -ScriptBlock {
            param($u)
            Start-Sleep -Seconds 3
            Start-Process $u
        } -ArgumentList $url | Out-Null
    }
    & $venvPython -m mkdocs serve --dev-addr "127.0.0.1:$Port"
}
finally {
    Pop-Location
}
