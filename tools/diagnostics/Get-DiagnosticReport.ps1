[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReportId,
    [string]$OutputDirectory = (Get-Location).Path,
    [string]$ProtectedPrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'Baballonia Diagnostics\Keys\baballonia-diagnostics-2026-08-v1.pkcs8.dpapi')
)

$ErrorActionPreference = 'Stop'
$parsedId = [Guid]::Empty
if (-not [Guid]::TryParseExact($ReportId, 'D', [ref]$parsedId)) {
    throw 'ReportId must be a UUID in D format.'
}

$resolvedOutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutputDirectory) | Out-Null
$encryptedPath = Join-Path $resolvedOutputDirectory "$ReportId.babdiag"
$zipPath = Join-Path $resolvedOutputDirectory "$ReportId.decrypted.zip"
if ((Test-Path -LiteralPath $encryptedPath) -or (Test-Path -LiteralPath $zipPath)) {
    throw 'Refusing to replace an existing encrypted report or decrypted ZIP.'
}

$workerDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\services\diagnostic-upload'))
$wrangler = Join-Path $workerDirectory 'node_modules\.bin\wrangler.cmd'
if (-not (Test-Path -LiteralPath $wrangler -PathType Leaf)) {
    throw 'Wrangler is not installed. Run npm ci in services/diagnostic-upload first.'
}

& $wrangler r2 object get "baballonia-diagnostic-reports-eu/reports/$ReportId.babdiag" `
    --remote --file $encryptedPath --jurisdiction eu
if ($LASTEXITCODE -ne 0) { throw 'Encrypted diagnostic download failed.' }

$expandScript = Join-Path $PSScriptRoot 'Expand-DiagnosticReport.ps1'
& $expandScript -ReportPath $encryptedPath -OutputPath $zipPath `
    -ProtectedPrivateKeyPath $ProtectedPrivateKeyPath

Write-Host "Encrypted report retained at: $encryptedPath"
Write-Host "Decrypted diagnostic ZIP: $zipPath"
