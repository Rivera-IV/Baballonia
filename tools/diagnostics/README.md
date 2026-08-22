# Diagnostic maintainer tools

The application contains only the public upload key. The matching private key is generated once
and stored outside the repository, protected by Windows DPAPI for the current user.

- `New-DiagnosticKey.ps1` creates the initial key pair and refuses to replace an existing key.
- `Export-DiagnosticRecoveryKey.ps1` exports a password-protected recovery copy. Keep the file and
  its password in separate secure locations.
- `Get-DiagnosticReport.ps1` retrieves a report by UUID from the private EU R2 bucket and decrypts
  it locally.
- `Expand-DiagnosticReport.ps1` decrypts an already downloaded `.babdiag` container.
- `Invoke-DiagnosticReview.ps1` retrieves or opens a report, runs the deterministic analyzer and
  optionally starts the read-only local AI reviewer.

The analyzer lives in `tools/Baballonia.DiagnosticAnalyzer`. It validates ZIP paths, parses the
recorded pipeline CSV, detects timing, visibility, registration and binocular-state anomalies, and
copies only a small set of relevant image pairs into an evidence directory. Its JSON result is the
input for the local multimodal reviewer; AI output never replaces the deterministic measurements.

The local Qwen/Codex integration is under `tools/local-agent`. Initialize the localhost-only model
server first, then start either the read-only diagnostic agent or the workspace-write developer
agent. Both modes prohibit uploads, pushes, releases, deployments, R2 deletion and access to
diagnostic private keys.

`Baballonia.LocalAiProxy` is a loopback-only compatibility layer between Codex and LM Studio. It
merges multiple Responses API system/developer instructions into one initial instruction block,
working around the Qwen prompt-template issue in LM Studio 0.4.21 without changing the model. The
proxy also prevents Codex from falling back to a cloud model catalog. It never listens on the LAN.

## Typical workflow

Start the local model once after installation:

```powershell
.\tools\local-agent\Initialize-BaballoniaLocalAi.ps1
```

Download and analyze an R2 report by its UUID:

```powershell
.\tools\diagnostics\Invoke-DiagnosticReview.ps1 -ReportId '<report UUID>'
```

Analyze a report already stored on this PC and immediately open the read-only AI reviewer:

```powershell
.\tools\diagnostics\Invoke-DiagnosticReview.ps1 `
    -ReportPath 'C:\path\to\report.babdiag' `
    -StartLocalAi
```

The deterministic results are written to `analysis.json` and `analysis.md`; an AI run additionally
writes `local-ai-review.md`. Only prioritized left/right image pairs are passed to the model, and
diagnostic mode disables shell access entirely.

Start the coding agent in the repository only when code changes are intended:

```powershell
.\tools\local-agent\Start-BaballoniaLocalAgent.ps1 -Mode Developer
```

Diagnostic mode is read-only. Developer mode can edit the workspace, but asks before operations
outside the workspace and inherits the no-upload/no-push rules from `BABALLONIA_LOCAL_AGENT.md`.

The private key, recovery key, decrypted ZIPs, and user reports must never be committed. Files with
`.dpapi` and `.decrypted.zip` suffixes are ignored by the repository as a last safety net.
