[CmdletBinding(DefaultParameterSetName = 'Remote')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Remote')]
    [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')]
    [string]$ReportId,

    [Parameter(Mandatory, ParameterSetName = 'Local')]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ReportPath,

    [string]$OutputRoot = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'ProjectBabble\DiagnosticReviews'),
    [switch]$StartLocalAi
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$analyzerProject = Join-Path $repositoryRoot 'tools\Baballonia.DiagnosticAnalyzer\Baballonia.DiagnosticAnalyzer.csproj'
$reviewName = if ($PSCmdlet.ParameterSetName -eq 'Remote') {
    $ReportId
} else {
    [IO.Path]::GetFileNameWithoutExtension($ReportPath)
}
$reviewDirectory = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $reviewName
if (Test-Path -LiteralPath $reviewDirectory) {
    throw "Refusing to replace an existing review directory: $reviewDirectory"
}
[IO.Directory]::CreateDirectory($reviewDirectory) | Out-Null

try {
    if ($PSCmdlet.ParameterSetName -eq 'Remote') {
        $downloadDirectory = Join-Path $reviewDirectory 'download'
        & (Join-Path $PSScriptRoot 'Get-DiagnosticReport.ps1') `
            -ReportId $ReportId -OutputDirectory $downloadDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Diagnostic retrieval failed.' }
        $decryptedZip = Join-Path $downloadDirectory "$ReportId.decrypted.zip"
    } else {
        $resolvedReportPath = [IO.Path]::GetFullPath($ReportPath)
        if ([IO.Path]::GetExtension($resolvedReportPath) -ieq '.babdiag') {
            $downloadDirectory = Join-Path $reviewDirectory 'download'
            [IO.Directory]::CreateDirectory($downloadDirectory) | Out-Null
            $decryptedZip = Join-Path $downloadDirectory "$reviewName.decrypted.zip"
            & (Join-Path $PSScriptRoot 'Expand-DiagnosticReport.ps1') `
                -ReportPath $resolvedReportPath -OutputPath $decryptedZip
            if ($LASTEXITCODE -ne 0) { throw 'Diagnostic decryption failed.' }
        } elseif ([IO.Path]::GetExtension($resolvedReportPath) -ieq '.zip') {
            $decryptedZip = $resolvedReportPath
        } else {
            throw 'ReportPath must reference a .babdiag container or decrypted .zip package.'
        }
    }

    $analysisDirectory = Join-Path $reviewDirectory 'analysis'
    & dotnet run --project $analyzerProject --configuration Release -- $decryptedZip $analysisDirectory
    $analyzerExit = $LASTEXITCODE
    if ($analyzerExit -notin 0, 3) {
        throw "Diagnostic analysis failed with exit code $analyzerExit."
    }

    Write-Host "Review directory: $reviewDirectory"
    Write-Host "Analysis report: $(Join-Path $analysisDirectory 'analysis.md')"
    if ($analyzerExit -eq 3) {
        Write-Warning 'The deterministic analyzer found at least one high-severity tracking issue.'
    }

    if ($StartLocalAi) {
        & (Join-Path $repositoryRoot 'tools\local-agent\Start-BaballoniaLocalAgent.ps1') `
            -Mode Diagnostic -AnalysisDirectory $analysisDirectory
        if ($LASTEXITCODE -ne 0) { throw 'The local AI review failed.' }
    }

    exit 0
} catch {
    Write-Error $_
    Write-Warning "Incomplete files were retained for investigation: $reviewDirectory"
    exit 1
}
