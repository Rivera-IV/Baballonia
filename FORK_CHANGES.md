# Baballonia Expressions Preview

Baballonia Expressions Preview is an unofficial experimental build focused on personalized eye
tracking, eye-expression calibration, and runtime stability. It remains based on Project Babble's
Baballonia and is not an official Project Babble release.

## Preview 1 replacement update (2026-08-19)

The original Preview 1 downloads were rebuilt after live testing. This replacement removes
periodic registration work from the inference thread, prevents weak open-eye visibility estimates
from causing long gaze holds, and makes full-rate eye recording developer-only.

## Comparison baseline

This preview directly extends Project Babble's optimized Expressions Alpha development line:

- technical base: Expressions Alpha `1.1.1.1-3`
- upstream branch: `expr-opt`
- base source commit included in this branch: `b1ef0c9`

For comparison with the regular public release line, the latest official GitHub release available
when this summary was prepared was:

- official release: `v1.1.1.0rc6`
- official release commit: `7339b07`
- comparison reviewed: 2026-08-19

The list below describes user-visible behavior in the preview rather than attributing every source
commit. The preview identifier is intentionally separate from Project Babble's numbered Alpha
builds and does not imply an official `1.1.1.1-4` release.

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
- Keeps a developer-only full-rate local eye diagnostic recorder with synchronized input images,
  raw model output, post-processed values, and state information. It is enabled only through the
  `BABALLONIA_LIVE_EYE_DIAGNOSTIC=1` environment variable and is not exposed to normal users.
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
- Eye diagnostics are stored locally only and are never uploaded automatically.
- Network features required for selected IP cameras, OSC/OSCQuery, update checks, and explicit
  firmware/module downloads remain separate from calibration-data collection.

## Packaging and version

- product name: `Baballonia Expressions Preview`
- upstream technical base: `1.1.1.1-3`
- preview version: `1.1.1.1-expressions.preview.1`
- release tag: `v1.1.1.1-expressions.preview.1`
- release branch: `release/expressions-preview`
- Windows installer: `Baballonia-Expressions-Preview-1-Setup-x64.exe`
- portable archive: `Baballonia-Expressions-Preview-1-Portable-x64.zip`
- removes the old in-app Alpha warning banner; the preview status remains explicit in the product
  name, version, installer, and release page

The installer uses a distinct product/install name so it does not replace the official Baballonia
installation entry. Application settings and personalized models are currently shared with
Baballonia through the existing ProjectBabble user-data directories.

## Download verification

These Windows artifacts are currently unsigned. Windows or antivirus software may therefore show
a reputation warning.

- `Baballonia-Expressions-Preview-1-Setup-x64.exe` — SHA-256:
  `1D8FE75C419FA560BD0DE79EC0E4CFF3DB7CBD556284E23A45C550243C77F92C`
- `Baballonia-Expressions-Preview-1-Portable-x64.zip` — SHA-256:
  `15A9E3586255859C2D5BCD773FC0FD3527DF4DAEB93625E3C1924D503C82016A`

## Verification performed

- self-contained Windows x64 Release publish: succeeded with 0 errors
- focused eye state and headset-registration tests: 40 passed, 0 failed
- portable archive contains the application, trainer, and VR calibration executables
- published application smoke test: started, displayed the expected version, and closed cleanly
