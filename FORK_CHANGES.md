# Baballonia Next

Baballonia Next is an unofficial development line focused on personalized eye
tracking, eye-expression calibration, and runtime stability. It remains based on Project Babble's
Baballonia and is not an official Project Babble release.

## Preview 1 replacement update (2026-08-19)

The original Preview 1 downloads were rebuilt after live testing. This replacement removes
periodic registration work from the inference thread, prevents weak open-eye visibility estimates
from causing long gaze holds, and makes full-rate eye recording developer-only.

## Comparison baseline

Baballonia Next directly extends Project Babble's optimized Expressions Alpha development line:

- technical base: Expressions Alpha `1.1.1.1-3`
- upstream branch: `expr-opt`
- base source commit included in this branch: `b1ef0c9`

For comparison with the regular public release line, the latest official GitHub release available
when this summary was prepared was:

- official release: `v1.1.1.0rc6`
- official release commit: `7339b07`
- comparison reviewed: 2026-08-19

The list below describes user-visible behavior in Baballonia Next rather than attributing every
source commit. Its independent Semantic Versioning sequence does not imply another official
Project Babble `1.1.1.1` Alpha release.

## Eye calibration

- Extends the VR calibration into eleven guided capture phases: neutral start, full gaze range,
  guided gaze, convergence, bilateral blink, left wink, right wink, eyes wide, eye squint,
  eyebrow movement, and neutral end.
- Captures left and right winks independently so one eyelid is not inferred from the other.
- Guides gaze during the wide, squint, and eyebrow phases to collect expressions across more of the
  usable eye-camera range.
- Uses localized VR instructions, longer reading intervals, consistent target placement, and
  per-test progress feedback.
- Applies bounded quality checks to capture phases and can repeat an insufficient take instead of
  silently accepting too little useful data.
- Records each calibration in its own session directory, merges only that session's accepted data,
  and validates the resulting model before installing it.
- Keeps the previous working eye model if training, validation, or model reload fails.
- Shows training state, epoch, batch, and overall progress in the main application.

## Eye tracking and expressions

- Uses explicit runtime states for normal gaze, bilateral blink, held wink, reopening, and
  convergence so those behaviors do not compete for pupil ownership.
- Holds pupil coordinates during bilateral occlusion and blends back to live tracking after the
  eyes become reliably visible.
- Keeps ordinary open-eye gaze responsive when the model's auxiliary pupil-visibility output is
  temporarily weak, while retaining the stricter recovery guard after a real blink.
- During a wink, derives the hidden pupil direction from the visible eye while allowing the visible
  eye to continue tracking normally.
- Adds hysteresis and confirmation to convergence to reduce false cross-eye activation during
  ordinary horizontal gaze.
- Corrects legacy right-eye range settings that could remove half of the right eye's horizontal or
  vertical motion.
- Adds configurable output gains for eyes-wide, eye-squint, and eyebrow expressions.
- Preserves compatibility with legacy six-output eye models while using model metadata for extended
  personalized models.
- Keeps the mathematical pupil/geometry observer diagnostic-only; it does not replace the trained
  personalized eye model in this preview.

## Headset movement and diagnostics

- Learns a session-local, confidence-gated eye-camera reference after valid eyes are visible.
- Detects persistent camera-to-eye translation caused by small headset shifts and aligns inference
  input back toward the validated reference.
- Measures camera-to-eye registration on a bounded background worker so correction checks cannot
  periodically stall the inference thread.
- Rejects reference frames with missing pupils, closed lids, unsuitable brightness, or implausible
  model output instead of calibrating against uncertain images.
- Adds an explicitly consented, 20-second local eye diagnostic export on the opt-in Debug page.
  The ZIP contains synchronized input images, raw model output, post-processed/avatar values,
  temporal state, camera-registration data, calibration/camera settings, and the selected eye
  model so a tester's exact problem can be replayed offline. Nothing is uploaded automatically.
- Provides a separate, second consent action that encrypts a chosen report locally with a fresh
  AES-256-GCM key, wraps that key with the maintainer's RSA public key, and uploads only the
  encrypted container to a private EU-jurisdiction store. Reports are scheduled for deletion after
  14 days and receive a one-time token for earlier deletion.
- Keeps the `BABALLONIA_LIVE_EYE_DIAGNOSTIC=1` environment-variable path for developer sessions.
- Adds an opt-in Debug/Performance page with camera and processing metrics.

## Capture, reliability, and performance

- Tries compatible camera backends in order and falls back when a backend opens without delivering
  frames.
- Improves serial-camera disposal, camera conflict handling, Vive Facial Tracker selection, IP
  camera handling, and Linux V4L2/Steam Runtime compatibility.
- Prevents eye and face pipelines from opening the same physical camera simultaneously.
- Reduces avoidable frame copies, preview relayout work, polling, and stale frame reuse.
- Hardens OSC send/receive recovery, calibration-overlay connection handling, firmware-session
  cleanup, configuration writes, and application shutdown.

## Privacy differences

- Removes the calibration-data uploader and its embedded server credentials.
- Removes the eye/face data-sharing prompt, setting, machine identifier, and AWS S3 dependency.
- Eye diagnostics remain local unless the tester separately consents to encrypt and upload a
  completed report. The server has no diagnostic private key or public download route, and uploads
  are never automatic.
- Network features required for selected IP cameras, OSC/OSCQuery, update checks, and explicit
  firmware/module downloads remain separate from calibration-data collection.

## Packaging and version

- product name: `Baballonia Next`
- upstream technical base: `1.1.1.1-3`
- application version: `0.1.0`
- release tag: `v0.1.0`
- release branch: `release/baballonia-next`
- Windows installer: `Baballonia-Next-0.1.0-Setup-x64.exe`
- portable archive: `Baballonia-Next-0.1.0-Portable-win-x64.zip`
- installed builds can check the fork's GitHub releases for updates; downloading and restarting
  always requires explicit confirmation from the user
- Windows releases are packaged with Velopack and include the installer, portable archive,
  full update package, and `win-x64` update feed required by the in-app updater
- release versions follow Semantic Versioning (`MAJOR.MINOR.PATCH`), beginning at `0.1.0`
- removes the old in-app Alpha warning banner; development status remains explicit in the `0.x`
  version sequence and release page

The installer uses a distinct product/install name so it does not replace the official Baballonia
installation entry. Application settings and personalized models are currently shared with
Baballonia through the existing ProjectBabble user-data directories.

## Download verification

The release is currently unsigned, so Windows or antivirus software may show a reputation warning.

- `Baballonia-Next-0.1.0-Setup-x64.exe` — SHA-256:
  `FC69FE78AD4A30BC09015AFD5352984C620D44822F8785999709A8E6AA92BABB`
- `Baballonia-Next-0.1.0-Portable-win-x64.zip` — SHA-256:
  `3D019F682326EFA2C6BF4044E719902C6D60BAA92F0E7086A3E3A230A2932D53`
- `Baballonia.Next-0.1.0-win-x64-full.nupkg` — SHA-256:
  `2910301448BEC4F8C26392CCEF0927FA4D28E362DA1F812B44BE076FCAE2C88D`

## Verification performed

- self-contained Windows x64 Release publish: succeeded with 0 errors
- non-hardware .NET test suite: 100 passed, 0 failed
- diagnostic-upload Worker test suite: 6 passed, 0 failed
- portable archive contains the application, trainer, and VR calibration executables
- published application smoke test: remained stable and closed cleanly
- a 20-second live diagnostic verified stable bilateral pupil visibility and smooth convergence output
