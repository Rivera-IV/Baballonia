[CmdletBinding()]
param(
    [string]$Model = 'baballonia-qwen3.6',
    [string]$Identifier = 'baballonia-qwen3.6',
    [ValidateRange(22000, 65536)]
    [int]$ContextLength = 22000,
    [ValidateRange(1024, 65535)]
    [int]$Port = 1234,
    [ValidateRange(1024, 65535)]
    [int]$ProxyPort = 1235
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
foreach ($command in 'lms', 'codex') {
    if (-not (Get-Command $command -ErrorAction SilentlyContinue)) {
        throw "$command is not installed or not available on PATH."
    }
}

& lms daemon up
if ($LASTEXITCODE -ne 0) { throw 'LM Studio daemon could not be started.' }

$availableModels = (& lms ls 2>&1 | Out-String)
if ($availableModels -notmatch [Regex]::Escape($Model)) {
    throw "The local Qwen3.6 model '$Model' is not registered in LM Studio."
}

$loadedModels = (& lms ps 2>&1 | Out-String)
if ($loadedModels -notmatch [Regex]::Escape($Identifier)) {
    # Omitting --gpu intentionally asks LM Studio to determine the highest safe offload ratio. This
    # keeps the context cache within the 24 GB VRAM limit instead of forcing full GPU residency.
    & lms load $Model --context-length $ContextLength --parallel 1 `
        --identifier $Identifier --yes
    if ($LASTEXITCODE -ne 0) { throw 'The local Qwen model could not be loaded.' }
}

& lms server start --port $Port --bind '127.0.0.1'
if ($LASTEXITCODE -ne 0) { throw 'The local LM Studio API server could not be started.' }

$deadline = [DateTime]::UtcNow.AddMinutes(3)
do {
    try {
        $models = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/v1/models" -TimeoutSec 5
        if ($models.data.id -contains $Identifier) { break }
    } catch {
        Start-Sleep -Seconds 2
    }
} while ([DateTime]::UtcNow -lt $deadline)

if ($models.data.id -notcontains $Identifier) {
    throw "LM Studio did not expose model '$Identifier' before the timeout."
}

Write-Host "Local model ready: $Identifier"
Write-Host "Local-only endpoint: http://127.0.0.1:$Port/v1"

$proxyProject = Join-Path $repositoryRoot 'tools\Baballonia.LocalAiProxy\Baballonia.LocalAiProxy.csproj'
$proxyExecutable = Join-Path $repositoryRoot 'tools\Baballonia.LocalAiProxy\bin\Release\net10.0\Baballonia.LocalAiProxy.exe'
& dotnet build $proxyProject --configuration Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'The local Codex compatibility proxy could not be built.' }

$proxyReady = $false
try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:$ProxyPort/health" -TimeoutSec 2
    $proxyReady = $health.status -eq 'ok'
} catch { }

if (-not $proxyReady) {
    Start-Process -FilePath $proxyExecutable `
        -ArgumentList '--listen', "http://127.0.0.1:$ProxyPort", '--upstream', "http://127.0.0.1:$Port" `
        -WindowStyle Hidden
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try {
            $health = Invoke-RestMethod -Uri "http://127.0.0.1:$ProxyPort/health" -TimeoutSec 2
            $proxyReady = $health.status -eq 'ok'
        } catch {
            Start-Sleep -Milliseconds 500
        }
    } while (-not $proxyReady -and [DateTime]::UtcNow -lt $deadline)
}

if (-not $proxyReady) { throw 'The localhost-only Codex compatibility proxy did not start.' }
Write-Host "Codex compatibility endpoint: http://127.0.0.1:$ProxyPort/v1"
Write-Host 'No cloud API credits are used by this endpoint.'
