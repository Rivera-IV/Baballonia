using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Baballonia.Contracts;
using Baballonia.Services.events;
using Baballonia.Services.Inference.Enums;

namespace Baballonia.Services.Inference;

public class EyeProcessingPipeline(
    IEyePipelineEventBus eyePipelineEventBus,
    PipelineMetrics metrics,
    IEyeTrackingBackend eyeTrackingBackend,
    IEyeFrameNormalizer eyeFrameNormalizer,
    IEyeGeometryTracker? eyeGeometryTracker = null) : DefaultProcessingPipeline, IDisposable
{
    private readonly FastCorruptionDetector.FastCorruptionDetector _fastCorruptionDetector = new();
    private readonly ImageCollector _imageCollector = new();
    // Raw (native/DFR) and OneEuro-filtered output are processed once each per camera frame.
    // They must not share temporal state: otherwise the filtered pass interprets the raw pass as
    // another point in time and can store a blink-corrupted pupil position as the last reliable
    // value. That showed up as a pupil jump on both bilateral blinks and winks.
    private readonly EyeTemporalState _filteredTemporalState = new();
    private readonly EyeTemporalState _rawTemporalState = new();

    public bool StabilizeEyes { get; set; } = true;

    /// <summary>
    /// For single-camera (split) eye feeds, swap which half drives which eye. Off by default — most
    /// split devices (e.g. BSB2E) want the unswapped orientation. User-controlled via the
    /// "Split Eye Video Swap" advanced setting; has no effect on dual-camera setups.
    /// </summary>
    public bool SwapSplitEyes { get; set; }

    public void ResetTemporalState()
    {
        _imageCollector.Reset();
        _filteredTemporalState.Reset();
        _rawTemporalState.Reset();
        RawEyeResult = null;
        eyeTrackingBackend.Reset();
        eyeFrameNormalizer.Reset();
        eyeGeometryTracker?.Reset();
    }

    public void ResetForCameraChange()
    {
        ResetTemporalState();
        eyeFrameNormalizer.ResetForCameraChange();
    }

    /// <summary>
    /// The raw (un-smoothed) result of the most recent <see cref="RunUpdate"/> — geometry-corrected
    /// exactly like the returned map but with the OneEuroFilter skipped. Native eye tracking (DFR /
    /// VRChat native) reads this for lowest latency. Shares RunUpdate's reused-buffer lifetime: only
    /// valid until the next RunUpdate on this pipeline.
    /// </summary>
    public OrderedFloatMap? RawEyeResult { get; private set; }

    public OrderedFloatMap? RunUpdate()
    {
        var sw = Stopwatch.StartNew();

        // `frame` is owned by us (AcquireRawMat contract); `using` frees it on every exit path.
        // Subscribers to the published events copy the Mat synchronously during Publish, so it is
        // safe to dispose afterwards.
        using var frame = VideoSource?.GetFrame(ColorType.Gray8);
        if(frame == null)
            return null;

        if (_fastCorruptionDetector.IsCorrupted(frame).isCorrupted)
            return null;

        eyePipelineEventBus.Publish(new EyePipelineEvents.NewFrameEvent(frame));
        metrics.EyeCaptureMs = PipelineMetrics.Ewma(metrics.EyeCaptureMs, sw.Elapsed.TotalMilliseconds);

        // A single-camera (split-eye) feed drives both eyes from one sensor. Whether to swap which
        // half feeds which eye is user-controlled (SwapSplitEyes / "Split Eye Video Swap"), defaulting
        // to unswapped. Dual-camera setups assign each eye explicitly, so they are always left as-is.
        if (ImageTransformer is DualImageTransformer splitTransformer)
            splitTransformer.SwapEyes = VideoSource is VideoSources.SingleCameraSource && SwapSplitEyes;

        sw.Restart();
        using var transformed = ImageTransformer?.Apply(frame);
        if(transformed == null)
            return null;
        var diagnosticEnabled =
            eyePipelineEventBus.HasSubscribers<EyePipelineEvents.DiagnosticFrameEvent>();


        // Canonicalize camera-to-eye registration before temporal history and inference. The active
        // implementation is confidence-gated and remains a no-op until a persistent headset shift
        // has been confirmed, so ordinary eye motion cannot immediately move the model input.
        eyeFrameNormalizer.Apply(transformed);
        // The mathematical observer remains diagnostic-only. It must never alter the established
        // personalized neural gaze path unless a future, separately validated backend is selected.
        var geometryState = diagnosticEnabled && eyeGeometryTracker != null
            ? eyeGeometryTracker.Observe(transformed)
            : EyeGeometryTrackingState.Unavailable;
        eyePipelineEventBus.Publish(new EyePipelineEvents.NewTransformedFrameEvent(transformed));
        // ImageCollector copies `transformed` into its temporal queue. Keep this current input frame
        // alive until the end so an explicitly enabled diagnostic recorder can pair it with the
        // exact inference result generated from this temporal step.
        using var collected = _imageCollector.Apply(transformed);
        if (collected == null)
            return null;
        metrics.EyeTransformMs = PipelineMetrics.Ewma(metrics.EyeTransformMs, sw.Elapsed.TotalMilliseconds);

        sw.Restart();
        var backendOutput = eyeTrackingBackend.Run(new EyeTrackingBackendInput(transformed, collected));
        if (backendOutput == null)
            return null;
        // A startup-reference candidate is accepted only after the eye model has confirmed that
        // this exact frame contains two plausibly open, trackable eyes.
        eyeFrameNormalizer.ObserveTrackingResult(backendOutput.Value.ModelRaw);
        metrics.EyeInferenceMs = PipelineMetrics.Ewma(metrics.EyeInferenceMs, sw.Elapsed.TotalMilliseconds);

        var modelRawSnapshot = diagnosticEnabled ? CloneMap(backendOutput.Value.ModelRaw) : null;

        sw.Restart();
        // A backend filter returns its own buffer and leaves the model map untouched. Process both:
        // the raw map feeds native eye tracking (DFR), while the filtered map feeds VRCFT/UI.
        OrderedFloatMap? rawForDfr = null;
        OrderedFloatMap? processedRawSnapshot = null;
        var inferenceResult = backendOutput.Value.ModelFiltered;
        if (backendOutput.Value.FilterApplied)
        {
            var raw = backendOutput.Value.ModelRaw;
            ProcessExpressionsCore(ref raw, _rawTemporalState);
            rawForDfr = raw;
            if (diagnosticEnabled)
                processedRawSnapshot = CloneMap(raw);

            // Closing and pupil loss must reach the filtered state machine immediately. Waiting
            // for OneEuro to catch up lets a short bilateral blink look like a wink for several
            // frames and can make the covered pupil copy a garbage coordinate. Recovery remains
            // filtered because Math.Min only selects the less-visible observation.
            raw.TryGetValue("/leftPupilVisibility", out var rawLeftPupilVisibility);
            raw.TryGetValue("/rightPupilVisibility", out var rawRightPupilVisibility);
            ProcessExpressionsCore(
                ref inferenceResult,
                _filteredTemporalState,
                raw["/leftEyeLid"],
                raw["/rightEyeLid"],
                raw.ContainsKey("/leftPupilVisibility") ? rawLeftPupilVisibility : null,
                raw.ContainsKey("/rightPupilVisibility") ? rawRightPupilVisibility : null);
        }
        else
        {
            ProcessExpressionsCore(ref inferenceResult, _filteredTemporalState);
        }

        // A natural blink may last only a handful of camera frames, while a held wink must survive
        // isolated raw inference misses. Lid values are openness, so the smaller of raw and filtered
        // closes immediately when either sees the blink and only reopens after the filtered history
        // agrees. Each eye is combined independently; no binocular averaging is introduced here.
        if (rawForDfr != null)
        {
            inferenceResult["/leftEyeLid"] = Math.Min(
                inferenceResult["/leftEyeLid"], rawForDfr["/leftEyeLid"]);
            inferenceResult["/rightEyeLid"] = Math.Min(
                inferenceResult["/rightEyeLid"], rawForDfr["/rightEyeLid"]);
        }

        RawEyeResult = rawForDfr ?? inferenceResult; // filter off: raw == filtered

        eyePipelineEventBus.Publish(new EyePipelineEvents.NewFilteredResultEvent(inferenceResult));
        if (diagnosticEnabled)
        {
            processedRawSnapshot ??= CloneMap(inferenceResult);
            eyePipelineEventBus.Publish(new EyePipelineEvents.DiagnosticFrameEvent(
                transformed,
                modelRawSnapshot!,
                processedRawSnapshot,
                CloneMap(inferenceResult),
                eyeFrameNormalizer.State,
                geometryState));
        }
        metrics.EyePostMs = PipelineMetrics.Ewma(metrics.EyePostMs, sw.Elapsed.TotalMilliseconds);

        return inferenceResult;
    }

    private static OrderedFloatMap CloneMap(OrderedFloatMap source)
    {
        var copy = new OrderedFloatMap(source.Keys.ToArray());
        foreach (var item in source)
            copy[item.Key] = item.Value;
        return copy;
    }

    private bool ProcessExpressions(ref OrderedFloatMap arKitExpressions)
        => ProcessExpressionsCore(ref arKitExpressions, _filteredTemporalState);

    private bool ProcessExpressionsCore(
        ref OrderedFloatMap arKitExpressions,
        EyeTemporalState temporalState,
        float? immediateLeftLid = null,
        float? immediateRightLid = null,
        float? immediateLeftPupilVisibility = null,
        float? immediateRightPupilVisibility = null)
    {
        const float mulV = 2.0f;
        const float mulY = 2.0f;
        const float minimumOpenWeight = 0.05f;
        // A target at the calibration routine's normal 2 m distance already requires roughly one
        // degree of physical convergence. Do not reproduce that tiny baseline on the avatar: it
        // looks permanently cross-eyed. 0.025 of the normalized +/-45 degree gaze range is about
        // 1.1 degrees, while deliberate convergence remains available above the neutral zone.
        const float neutralConvergence = 0.025f;

        var hasTrainedConvergence = arKitExpressions.TryGetValue(
            "/eyeConvergence", out var trainedConvergence);

        // Model outputs can overshoot their nominal 0..1 range, especially for expressions at the
        // edge of the calibration set. Clamp before these values are used as interpolation weights.
        var leftX = Math.Clamp(arKitExpressions["/leftEyeX"], 0.0f, 1.0f) * mulY - mulY / 2;
        var leftY = Math.Clamp(arKitExpressions["/leftEyeY"], 0.0f, 1.0f) * mulV - mulV / 2;
        var leftLid = 1.0f - Math.Clamp(arKitExpressions["/leftEyeLid"], 0.0f, 1.0f);
        if (immediateLeftLid.HasValue)
            leftLid = Math.Min(leftLid, immediateLeftLid.Value);

        var rightX = Math.Clamp(arKitExpressions["/rightEyeX"], 0.0f, 1.0f) * mulY - mulY / 2;
        var rightY = Math.Clamp(arKitExpressions["/rightEyeY"], 0.0f, 1.0f) * mulV - mulV / 2;
        var rightClosure = Math.Clamp(arKitExpressions["/rightEyeLid"], 0.0f, 1.0f);

        if (hasTrainedConvergence)
            rightClosure = Math.Clamp(rightClosure * 1.03f, 0.0f, 1.0f);

        var rightLid = 1.0f - rightClosure;
        if (immediateRightLid.HasValue)
            rightLid = Math.Min(rightLid, immediateRightLid.Value);

        var hasLeftPupilVisibility = arKitExpressions.TryGetValue(
            "/leftPupilVisibility", out var leftPupilVisibility);
        var hasRightPupilVisibility = arKitExpressions.TryGetValue(
            "/rightPupilVisibility", out var rightPupilVisibility);
        if (hasLeftPupilVisibility)
        {
            leftPupilVisibility = Math.Clamp(leftPupilVisibility, 0.0f, 1.0f);
            if (immediateLeftPupilVisibility.HasValue)
                leftPupilVisibility = Math.Min(
                    leftPupilVisibility, immediateLeftPupilVisibility.Value);
        }
        if (hasRightPupilVisibility)
        {
            rightPupilVisibility = Math.Clamp(rightPupilVisibility, 0.0f, 1.0f);
            if (immediateRightPupilVisibility.HasValue)
                rightPupilVisibility = Math.Min(
                    rightPupilVisibility, immediateRightPupilVisibility.Value);
        }

        var openWeight = leftLid + rightLid;
        // With both eyes closed the former division approached 0/0, turning harmless model noise
        // into NaN or large jumps. Gaze is invisible then, so use the neutral two-eye average.
        var eyeY = openWeight > minimumOpenWeight
            ? (leftY * leftLid + rightY * rightLid) / openWeight
            : (leftY + rightY) / 2.0f;

        var leftEyeXCorrected = leftX;
        var rightEyeXCorrected = rightX;

        if (!hasTrainedConvergence && leftLid > 0.5f && rightLid > 0.5f)
        {
            // Compatibility path for existing 12-output models.
            var rawConvergence = (leftEyeXCorrected - rightEyeXCorrected) / 2.0f;
            var neutralCorrection = Math.Clamp(rawConvergence,
                -neutralConvergence, neutralConvergence);
            leftEyeXCorrected -= neutralCorrection;
            rightEyeXCorrected += neutralCorrection;

            if (StabilizeEyes)
            {
                var midpoint = (leftEyeXCorrected + rightEyeXCorrected) / 2.0f;
                var convergence = Math.Max(
                    (leftEyeXCorrected - rightEyeXCorrected) / 2.0f, 0.0f);
                leftEyeXCorrected = midpoint + convergence;
                rightEyeXCorrected = midpoint - convergence;
            }
        }

        // Exactly one state owns the final gaze coordinates. Blink, wink, reopening and convergence
        // are mutually exclusive here, so independent guards can no longer overwrite one another.
        temporalState.Resolver.Apply(
            ref leftEyeXCorrected, ref rightEyeXCorrected, ref eyeY,
            leftY, rightY, leftLid, rightLid,
            hasLeftPupilVisibility ? leftPupilVisibility : null,
            hasRightPupilVisibility ? rightPupilVisibility : null,
            hasTrainedConvergence ? trainedConvergence : null,
            StabilizeEyes);

        // update the dict
        arKitExpressions["/leftEyeX"] = leftEyeXCorrected;
        arKitExpressions["/leftEyeY"] = eyeY;

        arKitExpressions["/rightEyeX"] = rightEyeXCorrected;
        arKitExpressions["/rightEyeY"] = eyeY;

        arKitExpressions["/leftEyeLid"] = leftLid;
        arKitExpressions["/rightEyeLid"] = rightLid;

        // The bundled bootstrap model predates the optional Widen/Squint heads. Those outputs only
        // become available after a user has completed calibration and trained a newer model, so
        // never let their absence stop the base gaze/lid pipeline (and close the camera).
        ProcessOptionalEyeExpressions(arKitExpressions, leftLid, rightLid);

        return true;
    }

    private static void ProcessOptionalEyeExpressions(
        OrderedFloatMap expressions, float leftLid, float rightLid)
    {
        var hasLeftWiden = expressions.TryGetValue("/leftEyeWiden", out var leftWiden);
        var hasRightWiden = expressions.TryGetValue("/rightEyeWiden", out var rightWiden);
        var hasLeftSquint = expressions.TryGetValue("/leftEyeSquint", out var leftSquint);
        var hasRightSquint = expressions.TryGetValue("/rightEyeSquint", out var rightSquint);
        if (!hasLeftWiden && !hasRightWiden && !hasLeftSquint && !hasRightSquint)
            return;

        leftWiden = Math.Clamp(leftWiden, 0.0f, 1.0f);
        rightWiden = Math.Clamp(rightWiden, 0.0f, 1.0f);
        leftSquint = Math.Clamp(leftSquint, 0.0f, 1.0f);
        rightSquint = Math.Clamp(rightSquint, 0.0f, 1.0f);

        // These calibration routines are symmetric. Keep their former binocular normalization
        // while both pupils are visible, but never allow the physically opposing Wide and Squint
        // shapes to drive the same eye at once.
        if (leftLid > 0.5f && rightLid > 0.5f)
        {
            if (hasLeftWiden && hasRightWiden)
                leftWiden = rightWiden = (leftWiden + rightWiden) / 2.0f;
            if (hasLeftSquint && hasRightSquint)
                leftSquint = rightSquint = (leftSquint + rightSquint) / 2.0f;
        }

        ResolveOpposingExpressions(ref leftWiden, ref leftSquint);
        ResolveOpposingExpressions(ref rightWiden, ref rightSquint);

        if (hasLeftWiden)
            expressions["/leftEyeWiden"] = leftWiden * EyeVisibility(leftLid);
        if (hasRightWiden)
            expressions["/rightEyeWiden"] = rightWiden * EyeVisibility(rightLid);
        if (hasLeftSquint)
            expressions["/leftEyeSquint"] = leftSquint * EyeVisibility(leftLid);
        if (hasRightSquint)
            expressions["/rightEyeSquint"] = rightSquint * EyeVisibility(rightLid);
    }

    private static void ResolveOpposingExpressions(ref float widen, ref float squint)
    {
        // Subtract the weaker activation instead of abruptly selecting a winner. The result is
        // mutually exclusive but remains continuous when model noise crosses the balance point.
        var overlap = Math.Min(widen, squint);
        widen -= overlap;
        squint -= overlap;
    }

    private static float EyeVisibility(float lidOpenness)
    {
        // Below 25% openness the pupil is effectively covered. Above 65% it is reliable enough
        // for full expression/convergence output. Smoothstep keeps the transition continuous.
        var t = Math.Clamp((lidOpenness - 0.25f) / 0.4f, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private static float Lerp(float from, float to, float amount) =>
        from + (to - from) * amount;

    private sealed class EyeTemporalState
    {
        public EyeStateResolver Resolver { get; } = new();

        public void Reset() => Resolver.Reset();
    }

    private sealed class EyeStateResolver
    {
        private const float CoveredOpenness = 0.45f;
        private const float SourceVisibleOpenness = 0.60f;
        private const float RecoveredOpenness = 0.75f;
        // At the measured ~95 Hz, three frames were only about 30 ms. Natural bilateral blinks
        // routinely have a larger left/right onset offset and were therefore confirmed as winks.
        private const int WinkConfirmationFrames = 6;
        // The live diagnostic showed that estimates in the 0.01..0.03 visibility band are still
        // unstable, especially for the right camera. Treat that band as unavailable and require a
        // clearly visible pupil before releasing held gaze again. The gap is intentional hysteresis:
        // it prevents a pupil near the camera edge from repeatedly dropping in and out each frame.
        private const float PupilMissingThreshold = 0.03f;
        private const float PupilRecoveredThreshold = 0.08f;
        private const float PupilFollowStepPerFrame = 0.12f;
        private const float RecoveryStepPerFrame = 0.12f;
        private const float MaximumNormalInwardDifference = 0.08f;
        private const float MaximumNormalOutwardDifference = 0.08f;
        private const int ConfirmedEyeRecoveryFrames = 8;
        private const int CandidateEyeRecoveryFrames = 2;
        private const int EyeRecoveryBlendFrames = 8;
        private const int BothEyeRecoveryFrames = 2;
        private const int BothEyeRecoveryBlendFrames = 6;

        // Convergence is a modifier of the fully-open state, never an independent owner of gaze.
        // Hysteresis and confirmation prevent ordinary model noise from briefly entering cross-eye.
        private const float ConvergenceEnter = 0.32f;
        private const float ConvergenceExit = 0.20f;
        // The trained head sometimes remains high during ordinary gaze. Confirm it with the two
        // independent horizontal predictions: inward convergence means left X is measurably to
        // the right of right X. Values here use the pipeline's normalized -1..+1 coordinates.
        private const float ConvergenceGazeEvidenceEnter = 0.36f;
        private const float ConvergenceGazeEvidenceExit = 0.16f;
        private const int ConvergenceConfirmationFrames = 6;
        private const int ConvergenceReleaseFrames = 6;
        private const float ConvergenceStepPerFrame = 0.12f;

        private EyeState _state = EyeState.Open;
        private bool _initialized;
        private int _stateFrames;
        private int _stableRecoveryFrames;
        private int _recoveryBlendFrame;
        private int _requiredRecoveryFrames;
        private bool _recoveryUsesVisibleEye;
        private bool _leftPupilReliable = true;
        private bool _rightPupilReliable = true;
        private float _heldLeftX;
        private float _heldRightX;
        private float _heldY;
        private float _lastOutputLeftX;
        private float _lastOutputRightX;
        private float _lastOutputY;
        private int _convergenceEnterFrames;
        private int _convergenceExitFrames;
        private bool _convergenceActive;
        private float _convergenceAmount;

        public void Apply(
            ref float leftX,
            ref float rightX,
            ref float y,
            float leftY,
            float rightY,
            float leftLid,
            float rightLid,
            float? leftPupilVisibility,
            float? rightPupilVisibility,
            float? trainedConvergence,
            bool stabilizeEyes)
        {
            leftX = FiniteOr(leftX, _lastOutputLeftX);
            rightX = FiniteOr(rightX, _lastOutputRightX);
            y = FiniteOr(y, _lastOutputY);
            leftY = FiniteOr(leftY, y);
            rightY = FiniteOr(rightY, y);

            if (!_initialized)
            {
                _lastOutputLeftX = leftX;
                _lastOutputRightX = rightX;
                _lastOutputY = y;
                _heldLeftX = leftX;
                _heldRightX = rightX;
                _heldY = y;
                _initialized = true;
            }

            var wasLeftPupilReliable = _leftPupilReliable;
            var wasRightPupilReliable = _rightPupilReliable;
            _leftPupilReliable = UpdatePupilReliability(
                wasLeftPupilReliable, leftPupilVisibility);
            _rightPupilReliable = UpdatePupilReliability(
                wasRightPupilReliable, rightPupilVisibility);

            UpdateState(
                leftX, rightX, y, leftLid, rightLid,
                _leftPupilReliable, _rightPupilReliable,
                !wasLeftPupilReliable && _leftPupilReliable,
                !wasRightPupilReliable && _rightPupilReliable);

            switch (_state)
            {
                case EyeState.Open:
                    ApplyOpenGaze(
                        ref leftX, ref rightX, ref y, leftY, rightY,
                        _leftPupilReliable, _rightPupilReliable,
                        trainedConvergence, stabilizeEyes);
                    break;

                case EyeState.LeftWinkCandidate:
                    ResetConvergence();
                    leftX = _heldLeftX;
                    y = rightY;
                    break;

                case EyeState.RightWinkCandidate:
                    ResetConvergence();
                    rightX = _heldRightX;
                    y = leftY;
                    break;

                case EyeState.LeftWink:
                    ResetConvergence();
                    leftX = MoveTowards(
                        _lastOutputLeftX, rightX, PupilFollowStepPerFrame);
                    y = rightY;
                    break;

                case EyeState.RightWink:
                    ResetConvergence();
                    rightX = MoveTowards(
                        _lastOutputRightX, leftX, PupilFollowStepPerFrame);
                    y = leftY;
                    break;

                case EyeState.BothClosed:
                    ResetConvergence();
                    HoldBoth(ref leftX, ref rightX, ref y);
                    break;

                case EyeState.RecoverLeft:
                    ResetConvergence();
                    ApplyLeftRecovery(ref leftX, rightX, ref y, rightY);
                    break;

                case EyeState.RecoverRight:
                    ResetConvergence();
                    ApplyRightRecovery(leftX, ref rightX, ref y, leftY);
                    break;

                case EyeState.RecoverBoth:
                    ResetConvergence();
                    ApplyBothRecovery(
                        ref leftX, ref rightX, ref y, leftY, rightY,
                        _leftPupilReliable, _rightPupilReliable, stabilizeEyes);
                    break;
            }

            // State transitions always start from the exact coordinates emitted on the preceding
            // frame. This guarantees continuity even if a blink interrupts a reopening blend.
            RememberOutput(leftX, rightX, y);
        }

        public void Reset()
        {
            _state = EyeState.Open;
            _initialized = false;
            _stateFrames = 0;
            _stableRecoveryFrames = 0;
            _recoveryBlendFrame = 0;
            _requiredRecoveryFrames = 0;
            _recoveryUsesVisibleEye = false;
            _leftPupilReliable = true;
            _rightPupilReliable = true;
            _heldLeftX = 0.0f;
            _heldRightX = 0.0f;
            _heldY = 0.0f;
            _lastOutputLeftX = 0.0f;
            _lastOutputRightX = 0.0f;
            _lastOutputY = 0.0f;
            ResetConvergence();
        }

        private void UpdateState(
            float leftX,
            float rightX,
            float y,
            float leftLid,
            float rightLid,
            bool leftPupilReliable,
            bool rightPupilReliable,
            bool leftPupilRecovered,
            bool rightPupilRecovered)
        {
            var leftCovered = leftLid <= CoveredOpenness;
            var rightCovered = rightLid <= CoveredOpenness;
            var leftVisible = leftLid >= SourceVisibleOpenness && leftPupilReliable;
            var rightVisible = rightLid >= SourceVisibleOpenness && rightPupilReliable;
            var leftRecovered = leftLid >= RecoveredOpenness && leftPupilReliable;
            var rightRecovered = rightLid >= RecoveredOpenness && rightPupilReliable;

            switch (_state)
            {
                case EyeState.Open:
                    if ((!leftPupilReliable && !rightPupilReliable) ||
                        (leftCovered && rightCovered))
                        EnterBothClosed();
                    else if (leftCovered && rightVisible)
                        EnterCandidate(EyeState.LeftWinkCandidate);
                    else if (rightCovered && leftVisible)
                        EnterCandidate(EyeState.RightWinkCandidate);
                    else if (leftCovered || rightCovered)
                        EnterBothClosed();
                    else if (leftPupilRecovered && rightVisible)
                        // A pupil can disappear at an extreme gaze even while the lid head remains
                        // open. Its first recognized coordinate is just as unsafe as a post-blink
                        // coordinate, so blend it back from the continuously visible partner.
                        EnterEyeRecovery(EyeState.RecoverLeft, confirmedWink: true);
                    else if (rightPupilRecovered && leftVisible)
                        EnterEyeRecovery(EyeState.RecoverRight, confirmedWink: true);
                    break;

                case EyeState.LeftWinkCandidate:
                    if (leftCovered && rightCovered)
                        EnterBothClosed();
                    else if (!leftCovered)
                        EnterEyeRecovery(EyeState.RecoverLeft, confirmedWink: false);
                    else if (!rightVisible)
                        EnterBothClosed();
                    else if (++_stateFrames >= WinkConfirmationFrames)
                        Enter(EyeState.LeftWink);
                    break;

                case EyeState.RightWinkCandidate:
                    if (leftCovered && rightCovered)
                        EnterBothClosed();
                    else if (!rightCovered)
                        EnterEyeRecovery(EyeState.RecoverRight, confirmedWink: false);
                    else if (!leftVisible)
                        EnterBothClosed();
                    else if (++_stateFrames >= WinkConfirmationFrames)
                        Enter(EyeState.RightWink);
                    break;

                case EyeState.LeftWink:
                    if (!rightVisible)
                        EnterBothClosed();
                    else if (leftRecovered && rightVisible)
                        EnterEyeRecovery(EyeState.RecoverLeft, confirmedWink: true);
                    break;

                case EyeState.RightWink:
                    if (!leftVisible)
                        EnterBothClosed();
                    else if (rightRecovered && leftVisible)
                        EnterEyeRecovery(EyeState.RecoverRight, confirmedWink: true);
                    break;

                case EyeState.BothClosed:
                    if (leftLid >= RecoveredOpenness &&
                        rightLid >= RecoveredOpenness &&
                        (leftPupilReliable || rightPupilReliable))
                        // Even if only one pupil is recognized first, release it through the same
                        // guarded blend. Entering Open immediately exposed a stale filter value and
                        // caused the measured full-range pupil snap after a blink.
                        Enter(EyeState.RecoverBoth);
                    else if (leftCovered && rightRecovered)
                        EnterCandidate(EyeState.LeftWinkCandidate);
                    else if (rightCovered && leftRecovered)
                        EnterCandidate(EyeState.RightWinkCandidate);
                    break;

                case EyeState.RecoverLeft:
                    if (!rightVisible)
                        EnterBothClosed();
                    else if (leftCovered && rightVisible)
                        Enter(EyeState.LeftWink);
                    else
                        CountEyeRecovery(leftRecovered && rightVisible);
                    break;

                case EyeState.RecoverRight:
                    if (!leftVisible)
                        EnterBothClosed();
                    else if (rightCovered && leftVisible)
                        Enter(EyeState.RightWink);
                    else
                        CountEyeRecovery(rightRecovered && leftVisible);
                    break;

                case EyeState.RecoverBoth:
                    if (leftCovered && rightCovered)
                    {
                        _stableRecoveryFrames = 0;
                        _recoveryBlendFrame = 0;
                    }
                    else if (leftCovered && rightRecovered)
                        EnterCandidate(EyeState.LeftWinkCandidate);
                    else if (rightCovered && leftRecovered)
                        EnterCandidate(EyeState.RightWinkCandidate);
                    else
                        CountBothRecovery(
                            leftLid >= RecoveredOpenness &&
                            rightLid >= RecoveredOpenness &&
                            (leftPupilReliable || rightPupilReliable));
                    break;
            }

        }

        private void EnterCandidate(EyeState candidate)
        {
            _heldLeftX = _lastOutputLeftX;
            _heldRightX = _lastOutputRightX;
            _heldY = _lastOutputY;
            Enter(candidate);
            _stateFrames = 1;
        }

        private void EnterBothClosed()
        {
            _heldLeftX = _lastOutputLeftX;
            _heldRightX = _lastOutputRightX;
            _heldY = _lastOutputY;
            Enter(EyeState.BothClosed);
        }

        private void EnterEyeRecovery(EyeState recoveryState, bool confirmedWink)
        {
            Enter(recoveryState);
            _requiredRecoveryFrames = confirmedWink
                ? ConfirmedEyeRecoveryFrames
                : CandidateEyeRecoveryFrames;
            _recoveryUsesVisibleEye = confirmedWink;
        }

        private void Enter(EyeState state)
        {
            _state = state;
            _stateFrames = 0;
            _stableRecoveryFrames = 0;
            _recoveryBlendFrame = 0;
        }

        private void CountEyeRecovery(bool reliable)
        {
            if (!reliable)
            {
                _stableRecoveryFrames = 0;
                _recoveryBlendFrame = 0;
                return;
            }

            if (_stableRecoveryFrames < _requiredRecoveryFrames)
                _stableRecoveryFrames++;
            else if (_recoveryBlendFrame < EyeRecoveryBlendFrames)
                _recoveryBlendFrame++;
        }

        private void CountBothRecovery(bool reliable)
        {
            if (!reliable)
            {
                _stableRecoveryFrames = 0;
                _recoveryBlendFrame = 0;
                return;
            }

            if (_stableRecoveryFrames < BothEyeRecoveryFrames)
                _stableRecoveryFrames++;
            else if (_recoveryBlendFrame < BothEyeRecoveryBlendFrames)
                _recoveryBlendFrame++;
        }

        private void ApplyLeftRecovery(ref float leftX, float rightX, ref float y, float rightY)
        {
            var blend = RecoveryBlend(EyeRecoveryBlendFrames);
            var sourceX = _recoveryUsesVisibleEye ? rightX : _heldLeftX;
            var sourceY = _recoveryUsesVisibleEye ? rightY : _heldY;
            var targetX = Lerp(sourceX, leftX, blend);
            var targetY = Lerp(sourceY, y, blend);
            leftX = MoveTowards(_lastOutputLeftX, targetX, RecoveryStepPerFrame);
            y = MoveTowards(_lastOutputY, targetY, RecoveryStepPerFrame);
            if (_recoveryBlendFrame >= EyeRecoveryBlendFrames &&
                NearlyEqual(leftX, targetX) && NearlyEqual(y, targetY))
                Enter(EyeState.Open);
        }

        private void ApplyRightRecovery(float leftX, ref float rightX, ref float y, float leftY)
        {
            var blend = RecoveryBlend(EyeRecoveryBlendFrames);
            var sourceX = _recoveryUsesVisibleEye ? leftX : _heldRightX;
            var sourceY = _recoveryUsesVisibleEye ? leftY : _heldY;
            var targetX = Lerp(sourceX, rightX, blend);
            var targetY = Lerp(sourceY, y, blend);
            rightX = MoveTowards(_lastOutputRightX, targetX, RecoveryStepPerFrame);
            y = MoveTowards(_lastOutputY, targetY, RecoveryStepPerFrame);
            if (_recoveryBlendFrame >= EyeRecoveryBlendFrames &&
                NearlyEqual(rightX, targetX) && NearlyEqual(y, targetY))
                Enter(EyeState.Open);
        }

        private void ApplyBothRecovery(
            ref float leftX,
            ref float rightX,
            ref float y,
            float leftY,
            float rightY,
            bool leftPupilReliable,
            bool rightPupilReliable,
            bool stabilizeEyes)
        {
            var targetLeftX = leftX;
            var targetRightX = rightX;
            var targetY = y;

            if (!leftPupilReliable && rightPupilReliable)
            {
                targetLeftX = rightX;
                targetRightX = rightX;
                targetY = rightY;
            }
            else if (leftPupilReliable && !rightPupilReliable)
            {
                targetLeftX = leftX;
                targetRightX = leftX;
                targetY = leftY;
            }
            else if (!leftPupilReliable && !rightPupilReliable)
            {
                targetLeftX = _heldLeftX;
                targetRightX = _heldRightX;
                targetY = _heldY;
            }
            else if (stabilizeEyes)
            {
                ConstrainNormalBinocularDifference(ref targetLeftX, ref targetRightX);
            }

            var blend = RecoveryBlend(BothEyeRecoveryBlendFrames);
            var desiredLeftX = Lerp(_heldLeftX, targetLeftX, blend);
            var desiredRightX = Lerp(_heldRightX, targetRightX, blend);
            var desiredY = Lerp(_heldY, targetY, blend);
            leftX = MoveTowards(_lastOutputLeftX, desiredLeftX, RecoveryStepPerFrame);
            rightX = MoveTowards(_lastOutputRightX, desiredRightX, RecoveryStepPerFrame);
            y = MoveTowards(_lastOutputY, desiredY, RecoveryStepPerFrame);

            if (_recoveryBlendFrame >= BothEyeRecoveryBlendFrames &&
                NearlyEqual(leftX, desiredLeftX) &&
                NearlyEqual(rightX, desiredRightX) &&
                NearlyEqual(y, desiredY))
                Enter(EyeState.Open);
        }

        private float RecoveryBlend(int blendFrames)
        {
            if (_recoveryBlendFrame <= 0)
                return 0.0f;

            var amount = Math.Clamp(_recoveryBlendFrame / (float)blendFrames, 0.0f, 1.0f);
            return amount * amount * (3.0f - 2.0f * amount);
        }

        private void ApplyOpenGaze(
            ref float leftX,
            ref float rightX,
            ref float y,
            float leftY,
            float rightY,
            bool leftPupilReliable,
            bool rightPupilReliable,
            float? trainedConvergence,
            bool stabilizeEyes)
        {
            if (!trainedConvergence.HasValue)
            {
                ResetConvergence();
                ApplyPupilReliabilityFallback(
                    ref leftX, ref rightX, ref y, leftY, rightY,
                    leftPupilReliable, rightPupilReliable,
                    constrainBinocularDifference: false);
                return;
            }

            var value = Math.Clamp(trainedConvergence.Value, 0.0f, 1.0f);
            // Capture evidence before replacing an unavailable pupil. It is used only to confirm
            // the separately trained convergence head; it is never sent directly to the avatar.
            var gazeEvidence = leftX - rightX;
            if (_convergenceActive)
            {
                _convergenceEnterFrames = 0;
                _convergenceExitFrames =
                    value <= ConvergenceExit ||
                    gazeEvidence <= ConvergenceGazeEvidenceExit
                    ? _convergenceExitFrames + 1
                    : 0;
                if (_convergenceExitFrames >= ConvergenceReleaseFrames)
                {
                    _convergenceActive = false;
                    _convergenceExitFrames = 0;
                }
            }
            else
            {
                _convergenceExitFrames = 0;
                _convergenceEnterFrames =
                    value >= ConvergenceEnter &&
                    gazeEvidence >= ConvergenceGazeEvidenceEnter
                    ? _convergenceEnterFrames + 1
                    : 0;
                if (_convergenceEnterFrames >= ConvergenceConfirmationFrames)
                {
                    _convergenceActive = true;
                    _convergenceEnterFrames = 0;
                }
            }

            // Outside confirmed convergence, both eyes should describe one conjugate gaze. If one
            // pupil is unavailable, only its coordinate follows the reliable eye; the reliable eye
            // remains completely live. With both pupils available, clamp only the physiologically
            // implausible difference while preserving the shared midpoint.
            ApplyPupilReliabilityFallback(
                ref leftX, ref rightX, ref y, leftY, rightY,
                leftPupilReliable, rightPupilReliable,
                constrainBinocularDifference: stabilizeEyes);

            var target = 0.0f;
            if (_convergenceActive && _convergenceExitFrames == 0)
            {
                target = Math.Clamp(
                    (value - ConvergenceEnter) / (1.0f - ConvergenceEnter), 0.0f, 1.0f);
                target = target * target * (3.0f - 2.0f * target);
            }

            _convergenceAmount = MoveTowards(
                _convergenceAmount, target, ConvergenceStepPerFrame);
            leftX = Lerp(leftX, 1.0f, _convergenceAmount);
            rightX = Lerp(rightX, -1.0f, _convergenceAmount);
        }

        private void ApplyPupilReliabilityFallback(
            ref float leftX,
            ref float rightX,
            ref float y,
            float leftY,
            float rightY,
            bool leftPupilReliable,
            bool rightPupilReliable,
            bool constrainBinocularDifference)
        {
            if (!leftPupilReliable && rightPupilReliable)
            {
                leftX = MoveTowards(
                    _lastOutputLeftX, rightX, PupilFollowStepPerFrame);
                y = rightY;
                return;
            }

            if (leftPupilReliable && !rightPupilReliable)
            {
                rightX = MoveTowards(
                    _lastOutputRightX, leftX, PupilFollowStepPerFrame);
                y = leftY;
                return;
            }

            if (!constrainBinocularDifference)
                return;

            ConstrainNormalBinocularDifference(ref leftX, ref rightX);
        }

        private static void ConstrainNormalBinocularDifference(
            ref float leftX, ref float rightX)
        {
            var difference = leftX - rightX;
            var constrainedDifference = Math.Clamp(
                difference,
                -MaximumNormalOutwardDifference,
                MaximumNormalInwardDifference);
            var midpoint = (leftX + rightX) / 2.0f;
            leftX = midpoint + constrainedDifference / 2.0f;
            rightX = midpoint - constrainedDifference / 2.0f;
        }

        private static bool UpdatePupilReliability(bool wasReliable, float? visibility)
        {
            if (!visibility.HasValue)
                return true;
            if (!float.IsFinite(visibility.Value))
                return false;

            return wasReliable
                ? visibility.Value > PupilMissingThreshold
                : visibility.Value >= PupilRecoveredThreshold;
        }

        private void ResetConvergence()
        {
            _convergenceEnterFrames = 0;
            _convergenceExitFrames = 0;
            _convergenceActive = false;
            _convergenceAmount = 0.0f;
        }

        private void RememberOutput(float leftX, float rightX, float y)
        {
            _lastOutputLeftX = leftX;
            _lastOutputRightX = rightX;
            _lastOutputY = y;
        }

        private void HoldBoth(ref float leftX, ref float rightX, ref float y)
        {
            leftX = _heldLeftX;
            rightX = _heldRightX;
            y = _heldY;
        }

        private static float FiniteOr(float value, float fallback) =>
            float.IsFinite(value) ? value : fallback;

        private static bool NearlyEqual(float left, float right) =>
            MathF.Abs(left - right) <= 0.0001f;

        private static float MoveTowards(float current, float target, float maximumDelta)
        {
            if (MathF.Abs(target - current) <= maximumDelta)
                return target;
            return current + MathF.CopySign(maximumDelta, target - current);
        }

        private enum EyeState
        {
            Open,
            LeftWinkCandidate,
            RightWinkCandidate,
            LeftWink,
            RightWink,
            BothClosed,
            RecoverLeft,
            RecoverRight,
            RecoverBoth,
        }
    }


    public void Dispose()
    {
        TryDisposeObject(VideoSource);
        TryDisposeObject(ImageTransformer);
        TryDisposeObject(ImageConverter);
        TryDisposeObject(eyeTrackingBackend);
        TryDisposeObject(eyeFrameNormalizer);
        TryDisposeObject(_fastCorruptionDetector);
        TryDisposeObject(_imageCollector);
        TryDisposeObject(eyeGeometryTracker);
    }

    private void TryDisposeObject(object? obj)
    {
        (obj as IDisposable)?.Dispose();
    }
}
