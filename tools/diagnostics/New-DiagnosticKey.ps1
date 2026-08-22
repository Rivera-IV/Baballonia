[CmdletBinding()]
param(
    [string]$PublicKeyPath = (Join-Path $PSScriptRoot '..\..\src\Baballonia\Assets\DiagnosticUploadPublicKey.pem'),
    [string]$PrivateKeyDirectory = (Join-Path $env:LOCALAPPDATA 'Baballonia Diagnostics\Keys')
)

$ErrorActionPreference = 'Stop'
$keyId = 'baballonia-diagnostics-2026-08-v1'
$resolvedPublicPath = [IO.Path]::GetFullPath($PublicKeyPath)
$protectedPrivatePath = Join-Path $PrivateKeyDirectory "$keyId.pkcs8.dpapi"
$toolProject = Join-Path $PSScriptRoot '..\Baballonia.DiagnosticTool\Baballonia.DiagnosticTool.csproj'

& dotnet run --project $toolProject --configuration Release -- generate $resolvedPublicPath $protectedPrivatePath
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic key generation failed.' }
