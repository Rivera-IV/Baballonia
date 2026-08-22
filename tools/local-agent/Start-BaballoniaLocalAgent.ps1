[CmdletBinding()]
param(
    [ValidateSet('Developer', 'Diagnostic')]
    [string]$Mode = 'Developer',
    [string]$AnalysisDirectory,
    [string]$Model = 'baballonia-qwen3.6',
    [string]$Prompt,
    [string]$OutputPath,
    [ValidateRange(1024, 65535)]
    [int]$ProxyPort = 1235
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$rulesPath = Join-Path $PSScriptRoot 'BABALLONIA_LOCAL_AGENT.md'
if (-not (Test-Path -LiteralPath $rulesPath -PathType Leaf)) {
    throw "Local agent rules are missing: $rulesPath"
}

$status = (& lms status 2>&1 | Out-String)
if ($status -notmatch 'ON') {
    throw 'The LM Studio server is not running. Run Initialize-BaballoniaLocalAi.ps1 first.'
}
try {
    $proxyHealth = Invoke-RestMethod -Uri "http://127.0.0.1:$ProxyPort/health" -TimeoutSec 2
} catch {
    throw 'The local Codex compatibility proxy is not running. Run Initialize-BaballoniaLocalAi.ps1 first.'
}
if ($proxyHealth.status -ne 'ok') { throw 'The local Codex compatibility proxy is unhealthy.' }

$rules = Get-Content -LiteralPath $rulesPath -Raw
$arguments = @(
    '--model', $Model,
    '--cd', $repositoryRoot,
    '--ignore-user-config',
    '--disable', 'apps',
    '--disable', 'plugins',
    '--disable', 'remote_plugin',
    '--disable', 'recommended_plugins',
    '-c', 'model_provider="baballonia_local"',
    '-c', 'model_providers.baballonia_local.name="Baballonia Local"',
    '-c', "model_providers.baballonia_local.base_url=`"http://127.0.0.1:$ProxyPort/v1`"",
    '-c', 'model_providers.baballonia_local.wire_api="responses"',
    '-c', 'model_providers.baballonia_local.requires_openai_auth=false',
    '-c', 'model_reasoning_effort="medium"'
)

if ($Mode -eq 'Diagnostic') {
    if ([string]::IsNullOrWhiteSpace($AnalysisDirectory)) {
        throw 'AnalysisDirectory is required in Diagnostic mode.'
    }
    $resolvedAnalysis = [IO.Path]::GetFullPath($AnalysisDirectory)
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedAnalysis 'analysis.json') -PathType Leaf)) {
        throw "analysis.json was not found in $resolvedAnalysis"
    }
    $resolvedOutput = if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        Join-Path $resolvedAnalysis 'local-ai-review.md'
    } else {
        [IO.Path]::GetFullPath($OutputPath)
    }
    if (Test-Path -LiteralPath $resolvedOutput) {
        throw "Refusing to replace an existing AI review: $resolvedOutput"
    }
    $arguments += @(
        '--sandbox', 'read-only', '--ephemeral', '--output-last-message', $resolvedOutput,
        '--disable', 'shell_tool'
    )
    $analysisJson = Get-Content -LiteralPath (Join-Path $resolvedAnalysis 'analysis.json') -Raw
    $analysisData = $analysisJson | ConvertFrom-Json
    $analysisPrompt = Get-Content -LiteralPath (Join-Path $resolvedAnalysis 'LOCAL_AI_PROMPT.md') -Raw
    $evidenceDirectory = [IO.Path]::GetFullPath((Join-Path $resolvedAnalysis 'evidence'))
    $prioritySequences = @($analysisData.findings | Where-Object severity -eq 'error' |
        ForEach-Object sequences | Select-Object -Unique)
    $orderedEvidence = @()
    foreach ($sequence in $prioritySequences) {
        $orderedEvidence += @($analysisData.evidence | Where-Object sequence -eq $sequence)
    }
    $orderedEvidence += @($analysisData.evidence |
        Where-Object sequence -notin $prioritySequences | Sort-Object sequence)

    $imageArguments = @()
    $selectedImages = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $attachedSequences = [Collections.Generic.List[int]]::new()
    foreach ($record in $orderedEvidence) {
        $recordAdded = $false
        foreach ($relativeFile in $record.files) {
            if ($selectedImages.Count -ge 12) { break }
            $imagePath = [IO.Path]::GetFullPath((Join-Path $resolvedAnalysis $relativeFile))
            if (-not $imagePath.StartsWith($evidenceDirectory + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase)) { continue }
            if ((Test-Path -LiteralPath $imagePath -PathType Leaf) -and $selectedImages.Add($imagePath)) {
                $imageArguments += @('--image', $imagePath)
                $recordAdded = $true
            }
        }
        if ($recordAdded -and -not $attachedSequences.Contains([int]$record.sequence)) {
            $attachedSequences.Add([int]$record.sequence)
        }
        if ($selectedImages.Count -ge 12) { break }
    }
    $task = @"
$rules

MODE: DIAGNOSTIC (read-only)
Analysis directory: $resolvedAnalysis
The deterministic analysis and reviewer prompt are embedded below. Inspect the attached evidence
images. Do not use shell or filesystem tools; all required diagnostic data is already in this prompt.
The attached left/right image pairs are for these frame sequences only: $($attachedSequences -join ', ').
Never claim to have visually inspected another frame. Severity precedence is error, then warning,
then info; always name an error before a warning regardless of frame count. State counts alone do
not prove an invalid transition. Do not invent source filenames or code locations that are absent
from the supplied data. Clearly label uncertain visual interpretations as hypotheses.
$(if ($Prompt) { "Additional request: $Prompt" })

<deterministic-analysis-json>
$analysisJson
</deterministic-analysis-json>

<reviewer-prompt>
$analysisPrompt
</reviewer-prompt>
"@
} else {
    $arguments += @('--sandbox', 'workspace-write', '--ask-for-approval', 'on-request')
    $task = @"
$rules

MODE: DEVELOPER
Work only inside the Baballonia repository. Begin by reading git status and the relevant existing code.
$(if ($Prompt) { "Task: $Prompt" } else { 'Ask the user which concrete Baballonia task to work on.' })
"@
}

if ($Mode -eq 'Diagnostic') {
    # Pass the potentially large JSON prompt through stdin. The '-' positional marker must precede
    # the variadic --image option, otherwise Codex parses the prompt itself as another image path.
    $diagnosticArguments = @('exec') + $arguments + @('-') + $imageArguments
    $task | & codex @diagnosticArguments
    if ($LASTEXITCODE -eq 0) { Write-Host "Local AI review: $resolvedOutput" }
} else {
    & codex @arguments $task
}
exit $LASTEXITCODE
