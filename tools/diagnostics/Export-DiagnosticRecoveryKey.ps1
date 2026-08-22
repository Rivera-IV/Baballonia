[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [string]$ProtectedPrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'Baballonia Diagnostics\Keys\baballonia-diagnostics-2026-08-v1.pkcs8.dpapi')
)

$ErrorActionPreference = 'Stop'
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$toolProject = Join-Path $PSScriptRoot '..\Baballonia.DiagnosticTool\Baballonia.DiagnosticTool.csproj'

& dotnet run --project $toolProject --configuration Release -- export-recovery $resolvedOutputPath $ProtectedPrivateKeyPath
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic recovery-key export failed.' }
