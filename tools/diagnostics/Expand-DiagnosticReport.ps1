[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReportPath,
    [string]$OutputPath,
    [string]$ProtectedPrivateKeyPath = (Join-Path $env:LOCALAPPDATA 'Baballonia Diagnostics\Keys\baballonia-diagnostics-2026-08-v1.pkcs8.dpapi')
)

$ErrorActionPreference = 'Stop'
$resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = [IO.Path]::ChangeExtension($resolvedReportPath, '.decrypted.zip')
}
$resolvedOutputPath = [IO.Path]::GetFullPath($OutputPath)
$toolProject = Join-Path $PSScriptRoot '..\Baballonia.DiagnosticTool\Baballonia.DiagnosticTool.csproj'

& dotnet run --project $toolProject --configuration Release -- decrypt $resolvedReportPath $resolvedOutputPath $ProtectedPrivateKeyPath
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic report decryption failed.' }
