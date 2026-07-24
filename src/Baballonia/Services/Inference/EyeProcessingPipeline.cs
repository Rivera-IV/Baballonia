using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Baballonia.Services.events;
using Baballonia.Services.Inference.Enums;

namespace Baballonia.Services.Inference;

public class EyeProcessingPipeline(IEyePipelineEventBus eyePipelineEventBus, PipelineMetrics metrics) : DefaultProcessingPipeline, IDisposable
{
    private readonly FastCorruptionDetector.FastCorruptionDetector _fastCorruptionDetector = new();
    private readonly ImageCollector _imageCollector = new();
    private readonly Stopwatch _lidReleaseClock = Stopwatch.StartNew();
    private float _stableLeftLid = 1.0f;
    private float _stableRightLid = 1.0f;
    private float _leftOpenCandidateSeconds;
    private float _rightOpenCandidateSeconds;
    private bool _lidStateInitialized;
    private long _lastLidReleaseTicks;

    public bool StabilizeEyes { get; set; } = true;

    /// <summary>
    /// For single-camera (split) eye feeds, swap which half drives which eye. Off by default — most
    /// split devices (e.g. BSB2E) want the unswapped orientation. User-controlled via the
    /// "Split Eye Video Swap" advanced setting; has no effect on dual-camera setups.
    /// </summary>
    public bool SwapSplitEyes { get; set; }

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

        eyePipelineEventBus.Publish(new EyePipelineEvents.NewTransformedFrameEvent(transformed));
        // ImageCollector copies `transformed` into its temporal queue. Keep this current input frame
        // alive until the end so an explicitly enabled diagnostic recorder can pair it with the
        // exact inference result generated from this temporal step.
        using var collected = _imageCollector.Apply(transformed);
        if (collected == null)
            return null;
        metrics.EyeTransformMs = PipelineMetrics.Ewma(metrics.EyeTransformMs, sw.Elapsed.TotalMilliseconds);

        if (InferenceService == null)
            return null;

        sw.Restart();
        ImageConverter?.Convert(collected, InferenceService.GetInputTensor());

        var inferenceResult = InferenceService?.Run();
        if(inferenceResult == null)
            return null;
        metrics.EyeInferenceMs = PipelineMetrics.Ewma(metrics.EyeInferenceMs, sw.Elapsed.TotalMilliseconds);

        var diagnosticEnabled = eyePipelineEventBus.HasSubscribers<EyePipelineEvents.DiagnosticFrameEvent>();
        var modelRawSnapshot = diagnosticEnabled ? CloneMap(inferenceResult) : null;

        sw.Restart();
        // OneEuroFilter returns its own buffer and leaves the input untouched, so the runner's map
        // still holds the raw values. Process both: the raw map feeds native eye tracking (DFR), the
        // filtered map feeds VRCFT/UI as before.
        OrderedFloatMap? rawForDfr = null;
        OrderedFloatMap? processedRawSnapshot = null;
        if (Filter != null)
        {
            var filtered = Filter.Filter(inferenceResult);
            ProcessExpressions(ref inferenceResult);
            rawForDfr = inferenceResult;
            if (diagnosticEnabled)
                processedRawSnapshot = CloneMap(inferenceResult);
            inferenceResult = filtered;
        }

        ProcessExpressions(ref inferenceResult);

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

        StabilizeLidRelease(inferenceResult);

        RawEyeResult = rawForDfr ?? inferenceResult; // filter off: raw == filtered

        eyePipelineEventBus.Publish(new EyePipelineEvents.NewFilteredResultEvent(inferenceResult));
        if (diagnosticEnabled)
        {
            processedRawSnapshot ??= CloneMap(inferenceResult);
            eyePipelineEventBus.Publish(new EyePipelineEvents.DiagnosticFrameEvent(
                transformed,
                modelRawSnapshot!,
                processedRawSnapshot,
                CloneMap(inferenceResult)));
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

    private void StabilizeLidRelease(OrderedFloatMap expressions)
    {
        var now = _lidReleaseClock.ElapsedTicks;
        var deltaSeconds = _lastLidReleaseTicks == 0
            ? 0.0f
            : Math.Clamp((float)(now - _lastLidReleaseTicks) / Stopwatch.Frequency,
                0.0f, 0.1f);
        _lastLidReleaseTicks = now;

        var left = Math.Clamp(expressions["/leftEyeLid"], 0.0f, 1.0f);
        var right = Math.Clamp(expressions["/rightEyeLid"], 0.0f, 1.0f);
        if (!_lidStateInitialized)
        {
            _stableLeftLid = left;
            _stableRightLid = right;
            _lidStateInitialized = true;
        }

        _stableLeftLid = StabilizeOneLid(
            left, _stableLeftLid, deltaSeconds, ref _leftOpenCandidateSeconds);
        _stableRightLid = StabilizeOneLid(
            right, _stableRightLid, deltaSeconds, ref _rightOpenCandidateSeconds);

        expressions["/leftEyeLid"] = _stableLeftLid;
        expressions["/rightEyeLid"] = _stableRightLid;
    }

    private static float StabilizeOneLid(
        float current, float stable, float deltaSeconds, ref float openCandidateSeconds)
    {
        // Closing must remain immediate for natural blinks and winks. Reopening is accepted only
        // after clearly open input persists briefly; this rejects isolated live-camera misses while
        // an eye is held closed. Once accepted, the short 45 ms release remains visually responsive.
        if (current <= stable)
        {
            openCandidateSeconds = 0.0f;
            return current;
        }

        if (current < 0.6f)
        {
            openCandidateSeconds = 0.0f;
            return stable;
        }

        openCandidateSeconds += deltaSeconds;
        if (openCandidateSeconds < 0.06f)
            return stable;

        var releaseAlpha = 1.0f - MathF.Exp(-deltaSeconds / 0.045f);
        return stable + (current - stable) * releaseAlpha;
    }

    private bool ProcessExpressions(ref OrderedFloatMap arKitExpressions)
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

        var rightX = Math.Clamp(arKitExpressions["/rightEyeX"], 0.0f, 1.0f) * mulY - mulY / 2;
        var rightY = Math.Clamp(arKitExpressions["/rightEyeY"], 0.0f, 1.0f) * mulV - mulV / 2;
        var rightClosure = Math.Clamp(arKitExpressions["/rightEyeLid"], 0.0f, 1.0f);

        if (hasTrainedConvergence)
        {
            // The independently recorded right closed-eye samples average about 0.975 versus 0.993
            // on the left, so a tiny gain makes a full blink reach 1 without touching either side's
            // direction or the already-correct independent wink assignment.
            rightClosure = Math.Clamp(rightClosure * 1.03f, 0.0f, 1.0f);
        }

        var rightLid = 1.0f - rightClosure;

        var openWeight = leftLid + rightLid;
        // With both eyes closed the former division approached 0/0, turning harmless model noise
        // into NaN or large jumps. Gaze is invisible then, so use the neutral two-eye average.
        var eyeY = openWeight > minimumOpenWeight
            ? (leftY * leftLid + rightY * rightLid) / openWeight
            : (leftY + rightY) / 2.0f;

        var leftEyeXCorrected = leftX;
        var rightEyeXCorrected = rightX;

        if (hasTrainedConvergence)
        {
            // The trained binocular signal remains valid even if a lid head briefly reports a
            // partially closed eye. Applying it unconditionally is safe: with truly closed eyes
            // pupil direction is invisible, and removing the former >0.5 lid gate prevents live
            // lid noise from disabling deliberate convergence.
            var convergence = Math.Clamp((trainedConvergence - 0.08f) / 0.92f, 0.0f, 1.0f);
            var midpoint = (leftEyeXCorrected + rightEyeXCorrected) / 2.0f;
            midpoint *= 1.0f - convergence;
            leftEyeXCorrected = midpoint + convergence;
            rightEyeXCorrected = midpoint - convergence;
        }
        else if (leftLid > 0.5f && rightLid > 0.5f)
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

        // update the dict
        arKitExpressions["/leftEyeX"] = leftEyeXCorrected;
        arKitExpressions["/leftEyeY"] = eyeY;

        arKitExpressions["/rightEyeX"] = rightEyeXCorrected;
        arKitExpressions["/rightEyeY"] = eyeY;

        arKitExpressions["/leftEyeLid"] = leftLid;
        arKitExpressions["/rightEyeLid"] = rightLid;

        // Widen and squint are calibrated only as symmetric expressions. The two model heads can
        // nevertheless amplify tiny lighting/perspective differences between the camera images,
        // making an otherwise neutral avatar show visibly different eye sizes. Normalize those
        // symmetric expressions while both eyes are clearly visible. Do not do this during a
        // blink/wink: lid independence must remain intact and the covered eye is unreliable.
        if (leftLid > 0.5f && rightLid > 0.5f)
        {
            static float SymmetricAverage(float left, float right) =>
                (Math.Clamp(left, 0.0f, 1.0f) + Math.Clamp(right, 0.0f, 1.0f)) / 2.0f;

            var widen = SymmetricAverage(
                arKitExpressions["/leftEyeWiden"], arKitExpressions["/rightEyeWiden"]);
            arKitExpressions["/leftEyeWiden"] = widen;
            arKitExpressions["/rightEyeWiden"] = widen;

            var squint = SymmetricAverage(
                arKitExpressions["/leftEyeSquint"], arKitExpressions["/rightEyeSquint"]);
            arKitExpressions["/leftEyeSquint"] = squint;
            arKitExpressions["/rightEyeSquint"] = squint;
        }

        return true;
    }


    public void Dispose()
    {
        TryDisposeObject(VideoSource);
        TryDisposeObject(ImageTransformer);
        TryDisposeObject(ImageConverter);
        TryDisposeObject(InferenceService);
        TryDisposeObject(Filter);
        TryDisposeObject(_fastCorruptionDetector);
        TryDisposeObject(_imageCollector);
    }

    private void TryDisposeObject(object? obj)
    {
        (obj as IDisposable)?.Dispose();
    }
}
