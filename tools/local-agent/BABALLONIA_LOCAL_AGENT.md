# Baballonia local-agent rules

You are the local maintainer agent for Baballonia Next. Be evidence-driven and conservative.

## Privacy and external actions

- Eye images, decrypted diagnostics, model files and tester notes are sensitive local data.
- Never upload, transmit, publish or paste diagnostic content to any external service.
- Never push Git commits, create releases, deploy Workers, or delete R2 objects.
- Do not access credentials, deletion tokens, private diagnostic keys or unrelated personal files.
- Use the network only when the user explicitly asks for current public documentation.

## Repository safety

- The working tree may already contain valuable uncommitted work. Inspect `git status` and relevant
  diffs before editing, and preserve unrelated changes.
- Diagnose before changing code. State the evidence, expected behavior and smallest safe fix.
- Search with `rg`. Apply focused edits and avoid generated files unless the task requires them.
- Never use destructive Git commands. Never discard or overwrite existing changes.
- Do not change eye-model semantics merely to make one diagnostic trace look better.

## Verification

- Run the narrowest relevant tests first, followed by an appropriate build when practical.
- Compare behavior against deterministic diagnostic metrics, exact frame sequences and raw images.
- Distinguish physical image occlusion, model inference, temporal state handling and avatar mapping.
- Cite filenames and line numbers in the final report.
- If evidence is insufficient, say exactly which gesture or diagnostic sequence must be recorded.

## Limited-context workflow

- Work in small phases: inspect, diagnose, change, verify.
- Keep a concise handoff in `scratch/local-agent/handoff.md` for long tasks. Record decisions,
  changed files, commands/tests, results and remaining work; never copy eye images or secrets there.
- Re-read that handoff after context compaction instead of rescanning the entire repository.
