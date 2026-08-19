using System;
using Baballonia.Contracts;
using Baballonia.Services.events;
using Baballonia.Services.Inference.Geometry;
using OpenCvSharp;

namespace Baballonia.Services.Inference.Normalization;

/// <summary>
/// Learns a session-local neutral reference per eye, measures camera-to-eye movement and aligns the
/// model input back to that reference. Reference capture starts automatically and remains invisible
/// to the user; the calibration's neutral-start phase can replace it with a cleaner reference.
/// </summary>
public sealed class RegistrationMonitorEyeFrameNormalizer : IEyeFrameNormalizer
{
    public const int StartupWarmupFrames = 12;
    public const int RequiredReferenceFrames = 36;
    public const int RequiredConsecutiveEyeFrames = 6;
    private const int MeasurementIntervalFrames = 6;
    private const double MinimumReliableConfidence = 0.70;
    private const float MaximumMeasuredTranslationPixels = 16.0f;
    private const float MaximumMeasuredRotationDegrees = 6.0f;
    private const float MaximumAppliedTranslationPixels = 12.0f;
    private const float MaximumAppliedRotationDegrees = 5.0f;
    private const int RequiredConsistentMeasurements = 3;
    private const float CandidateTranslationTolerancePixels = 1.75f;
    private const float CandidateRotationToleranceDegrees = 1.0f;
    private const float CorrectionSmoothing = 0.25f;
    private readonly IEyePipelineEventBus _eventBus;
    private readonly object _sync = new();
    private readonly RegistrationCorrectionTracker _leftCorrection = new();
    private readonly RegistrationCorrectionTracker _rightCorrection = new();
    private Mat? _leftAccumulator;
    private Mat? _rightAccumulator;
    private Mat? _leftReference;
    private Mat? _rightReference;
    private Mat? _pendingLeftReferenceCandidate;
    private Mat? _pendingRightReferenceCandidate;
    private Mat? _registrationMask;
    private int _referenceSamples;
    private int _referenceWarmupFrames;
    private int _consecutiveEyeFrames;
    private int _measurementFrame;
    private bool _captureNeutralReference = true;
    private bool _explicitCalibrationReference;
    private EyeFrameNormalizationState _state = UnavailableState;
    private bool _disposed;

    public RegistrationMonitorEyeFrameNormalizer(IEyePipelineEventBus eventBus)
    {
        _eventBus = eventBus;
        _eventBus.Subscribe<EyePipelineEvents.CalibrationPhaseChangedEvent>(OnPhaseChanged);
    }

    public string Id => "registration-corrector-v1";

    public EyeFrameNormalizationState State
    {
        get { lock (_sync) return _state; }
    }

    public EyeFrameNormalizationState Apply(Mat transformedStereoFrame)
    {
        if (_disposed || transformedStereoFrame.Empty() || transformedStereoFrame.Channels() < 2)
            return State;

        lock (_sync)
        {
            var buildingReference = _captureNeutralReference &&
                                    _referenceSamples < RequiredReferenceFrames;
            if (!buildingReference && (_leftReference == null || _rightReference == null))
                return _state;

            var measureThisFrame = !buildingReference &&
                                   ++_measurementFrame >= MeasurementIntervalFrames;
            if (measureThisFrame)
                _measurementFrame = 0;

            // Before correction is active we only need to split reference/measurement frames. Once
            // a correction exists, both channels must be available on every frame so the last safe
            // transform can be applied even between the 10 Hz registration measurements.
            if (!buildingReference && !measureThisFrame &&
                !_leftCorrection.HasCorrection && !_rightCorrection.HasCorrection)
                return _state;

            // Splitting a two-channel Mat allocates two images. Do it only for the short reference
            // capture or an actual 10 Hz measurement, never on the intervening inference frames.
            var channels = transformedStereoFrame.Split();
            try
            {
                if (channels.Length < 2)
                    return _state;

                if (buildingReference)
                {
                    PrepareReferenceCandidate(channels[0], channels[1]);
                    return _state;
                }

                if (measureThisFrame)
                {
                    _registrationMask ??= CreateStableRegionMask(channels[0].Size());
                    _leftCorrection.Observe(
                        Estimate(_leftReference!, channels[0], _registrationMask));
                    _rightCorrection.Observe(
                        Estimate(_rightReference!, channels[1], _registrationMask));
                }

                ApplyCorrection(channels[0], _leftCorrection);
                ApplyCorrection(channels[1], _rightCorrection);
                Cv2.Merge(channels, transformedStereoFrame);
                _state = new EyeFrameNormalizationState(
                    _leftCorrection.State,
                    _rightCorrection.State);
                return _state;
            }
            finally
            {
                foreach (var channel in channels)
                    channel.Dispose();
            }
        }
    }

    private void PrepareReferenceCandidate(Mat left, Mat right)
    {
        // ObserveTrackingResult must approve only the exact image used for this inference pass.
        // If inference was skipped for the preceding candidate, the visible-eye streak is broken.
        if (_pendingLeftReferenceCandidate != null ||
            _pendingRightReferenceCandidate != null)
            _consecutiveEyeFrames = 0;
        DisposePendingReferenceCandidate();
        if (_explicitCalibrationReference)
        {
            StoreReferenceCandidate(left, right);
            return;
        }

        // Give camera auto-exposure a short settling period. After that every frame is validated;
        // invalid or missing eyes reset the consecutive-eye gate instead of being averaged.
        if (_referenceWarmupFrames++ < StartupWarmupFrames)
            return;
        StoreReferenceCandidate(left, right);
    }

    private void StoreReferenceCandidate(Mat left, Mat right)
    {
        if (!ContainsReliableEye(left) || !ContainsReliableEye(right))
        {
            _consecutiveEyeFrames = 0;
            return;
        }

        // The model result arrives later in the same RunUpdate call. Keep only this one candidate;
        // a missing inference result simply causes it to be replaced by the next camera frame.
        _pendingLeftReferenceCandidate = left.Clone();
        _pendingRightReferenceCandidate = right.Clone();
    }

    public void ObserveTrackingResult(OrderedFloatMap modelRaw)
    {
        lock (_sync)
        {
            if (_pendingLeftReferenceCandidate == null ||
                _pendingRightReferenceCandidate == null)
                return;

            try
            {
                if (!ModelConfirmsVisibleEyes(modelRaw))
                {
                    _consecutiveEyeFrames = 0;
                    return;
                }

                _consecutiveEyeFrames++;
                if (_consecutiveEyeFrames < RequiredConsecutiveEyeFrames)
                    return;

                AccumulateReference(
                    _pendingLeftReferenceCandidate,
                    _pendingRightReferenceCandidate);
            }
            finally
            {
                DisposePendingReferenceCandidate();
            }
        }
    }

    private static bool ContainsReliableEye(Mat eye)
    {
        if (eye.Empty() || eye.Channels() != 1 || eye.Width < 24 || eye.Height < 24)
            return false;

        Cv2.MeanStdDev(eye, out var mean, out var deviation);
        Cv2.MinMaxLoc(eye, out double minimum, out double maximum);
        if (mean.Val0 < 10.0 || mean.Val0 > 245.0 || deviation.Val0 < 7.0 ||
            maximum - minimum < 28.0)
            return false;

        var pupil = AdaptivePupilGeometryTracker.Detect(eye);
        if (!pupil.IsReliable)
            return false;

        // Reject a compact eyebrow/hair shadow accidentally selected as a pupil. A real IR pupil
        // must be locally darker than the complete eye crop.
        var halfWidth = Math.Max(2, (int)MathF.Ceiling(pupil.RadiusX * 0.7f));
        var halfHeight = Math.Max(2, (int)MathF.Ceiling(pupil.RadiusY * 0.7f));
        var x = Math.Clamp((int)pupil.CenterX - halfWidth, 0, eye.Width - 1);
        var y = Math.Clamp((int)pupil.CenterY - halfHeight, 0, eye.Height - 1);
        var width = Math.Min(halfWidth * 2 + 1, eye.Width - x);
        var height = Math.Min(halfHeight * 2 + 1, eye.Height - y);
        if (width < 3 || height < 3)
            return false;
        using var pupilRegion = new Mat(eye, new Rect(x, y, width, height));
        return Cv2.Mean(pupilRegion).Val0 <= mean.Val0 - 3.0;
    }

    private static bool ModelConfirmsVisibleEyes(OrderedFloatMap modelRaw)
    {
        if (!TryFiniteInRange(modelRaw, "/leftEyeX", -0.15f, 1.15f, out _) ||
            !TryFiniteInRange(modelRaw, "/leftEyeY", -0.15f, 1.15f, out _) ||
            !TryFiniteInRange(modelRaw, "/rightEyeX", -0.15f, 1.15f, out _) ||
            !TryFiniteInRange(modelRaw, "/rightEyeY", -0.15f, 1.15f, out _) ||
            !TryFiniteInRange(modelRaw, "/leftEyeLid", -0.10f, 0.60f, out _) ||
            !TryFiniteInRange(modelRaw, "/rightEyeLid", -0.10f, 0.60f, out _))
            return false;

        // Visibility heads exist only in newer personal models. When present, they provide an
        // additional independent veto; legacy six-output models still rely on image-space pupil
        // detection plus their lid/gaze outputs.
        if (modelRaw.TryGetValue("/leftPupilVisibility", out var leftVisibility) &&
            (!float.IsFinite(leftVisibility) || leftVisibility < 0.18f))
            return false;
        if (modelRaw.TryGetValue("/rightPupilVisibility", out var rightVisibility) &&
            (!float.IsFinite(rightVisibility) || rightVisibility < 0.18f))
            return false;
        return true;
    }

    private static bool TryFiniteInRange(
        OrderedFloatMap values,
        string key,
        float minimum,
        float maximum,
        out float value) =>
        values.TryGetValue(key, out value) && float.IsFinite(value) &&
        value >= minimum && value <= maximum;

    private void DisposePendingReferenceCandidate()
    {
        _pendingLeftReferenceCandidate?.Dispose();
        _pendingRightReferenceCandidate?.Dispose();
        _pendingLeftReferenceCandidate = null;
        _pendingRightReferenceCandidate = null;
    }

    private void OnPhaseChanged(EyePipelineEvents.CalibrationPhaseChangedEvent phase)
    {
        lock (_sync)
        {
            var startsNeutralReference = phase.CaptureEnabled && phase.Phase == "neutralstart";
            if (startsNeutralReference)
            {
                // A repeated event for the same phase must not erase a partially collected
                // replacement. Keep the active reference until the new one is fully validated;
                // an interrupted calibration must never destroy working headset compensation.
                if (!_explicitCalibrationReference)
                    BeginReferenceCapture(explicitCalibration: true);
                return;
            }

            if (!_explicitCalibrationReference)
                return;

            // Do not continue an explicit neutral capture with gaze/expression frames. Normally
            // all 36 samples have completed long before this transition. If capture was interrupted,
            // quietly restart the automatic startup path instead of leaving normalization disabled.
            DiscardPendingReferenceCapture();
            if (_leftReference == null || _rightReference == null)
                BeginReferenceCapture(explicitCalibration: false);
        }
    }

    private void BeginReferenceCapture(bool explicitCalibration)
    {
        DiscardPendingReferenceCapture();
        _captureNeutralReference = true;
        _explicitCalibrationReference = explicitCalibration;
    }

    private void DiscardPendingReferenceCapture()
    {
        _leftAccumulator?.Dispose();
        _rightAccumulator?.Dispose();
        _leftAccumulator = null;
        _rightAccumulator = null;
        DisposePendingReferenceCandidate();
        _referenceSamples = 0;
        _referenceWarmupFrames = 0;
        _consecutiveEyeFrames = 0;
        _captureNeutralReference = false;
        _explicitCalibrationReference = false;
    }

    private void AccumulateReference(Mat left, Mat right)
    {
        _leftAccumulator ??= Mat.Zeros(left.Size(), MatType.CV_32FC1).ToMat();
        _rightAccumulator ??= Mat.Zeros(right.Size(), MatType.CV_32FC1).ToMat();
        using var leftFloat = new Mat();
        using var rightFloat = new Mat();
        left.ConvertTo(leftFloat, MatType.CV_32FC1);
        right.ConvertTo(rightFloat, MatType.CV_32FC1);
        Cv2.Add(_leftAccumulator, leftFloat, _leftAccumulator);
        Cv2.Add(_rightAccumulator, rightFloat, _rightAccumulator);
        _referenceSamples++;

        if (_referenceSamples != RequiredReferenceFrames)
            return;

        var newLeftReference = new Mat();
        var newRightReference = new Mat();
        _leftAccumulator.ConvertTo(newLeftReference, MatType.CV_8UC1,
            1.0 / RequiredReferenceFrames);
        _rightAccumulator.ConvertTo(newRightReference, MatType.CV_8UC1,
            1.0 / RequiredReferenceFrames);
        _leftReference?.Dispose();
        _rightReference?.Dispose();
        _leftReference = newLeftReference;
        _rightReference = newRightReference;
        _registrationMask?.Dispose();
        _registrationMask = CreateStableRegionMask(left.Size());
        _leftAccumulator.Dispose();
        _rightAccumulator.Dispose();
        _leftAccumulator = null;
        _rightAccumulator = null;
        _captureNeutralReference = false;
        _explicitCalibrationReference = false;
        _leftCorrection.Reset();
        _rightCorrection.Reset();
    }

    private static Mat CreateStableRegionMask(Size size)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.White);
        var center = new Point(size.Width / 2, size.Height / 2);
        var excludedEye = new Size(
            Math.Max(4, (int)(size.Width * 0.29)),
            Math.Max(4, (int)(size.Height * 0.23)));
        Cv2.Ellipse(mask, center, excludedEye, 0, 0, 360, Scalar.Black, -1);
        Cv2.Rectangle(mask, new Rect(0, 0, size.Width, size.Height), Scalar.Black,
            Math.Max(2, size.Width / 32));
        return mask;
    }

    private static EyeRegistrationEstimate Estimate(Mat reference, Mat current, Mat mask)
    {
        using var warp = Mat.Eye(2, 3, MatType.CV_32FC1).ToMat();
        try
        {
            var confidence = Cv2.FindTransformECC(
                reference,
                current,
                warp,
                MotionTypes.Euclidean,
                new TermCriteria(CriteriaTypes.Count | CriteriaTypes.Eps, 40, 0.0001),
                mask,
                3);
            var a = warp.At<float>(0, 0);
            var b = warp.At<float>(0, 1);
            var translationX = warp.At<float>(0, 2);
            var translationY = warp.At<float>(1, 2);
            var rotation = MathF.Atan2(b, a) * 180.0f / MathF.PI;
            if (!double.IsFinite(confidence) || confidence < MinimumReliableConfidence ||
                !float.IsFinite(translationX) || !float.IsFinite(translationY) ||
                !float.IsFinite(rotation) ||
                MathF.Abs(translationX) > MaximumMeasuredTranslationPixels ||
                MathF.Abs(translationY) > MaximumMeasuredTranslationPixels ||
                MathF.Abs(rotation) > MaximumMeasuredRotationDegrees)
                return UnavailableEstimate;

            return new EyeRegistrationEstimate(
                translationX,
                translationY,
                rotation,
                1.0f,
                (float)Math.Clamp(confidence, 0.0, 1.0),
                CorrectionApplied: false);
        }
        catch (OpenCVException)
        {
            return UnavailableEstimate;
        }
    }

    /// <summary>
    /// Temporal model/filter resets must not discard the reference learned immediately before the
    /// trainer loads its new model, so this only resets the latest estimate.
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            _measurementFrame = 0;
            _state = UnavailableState;
            // Model/filter resets keep the learned camera reference. The correction trackers are
            // reset so stale temporal state is never carried into a newly loaded model.
            _leftCorrection.Reset();
            _rightCorrection.Reset();
        }
    }

    public void ResetForCameraChange()
    {
        lock (_sync)
        {
            ClearReference();
            _captureNeutralReference = true;
        }
    }

    private void ClearReference()
    {
        _leftAccumulator?.Dispose();
        _rightAccumulator?.Dispose();
        _leftReference?.Dispose();
        _rightReference?.Dispose();
        _registrationMask?.Dispose();
        DisposePendingReferenceCandidate();
        _leftAccumulator = null;
        _rightAccumulator = null;
        _leftReference = null;
        _rightReference = null;
        _registrationMask = null;
        _referenceSamples = 0;
        _referenceWarmupFrames = 0;
        _consecutiveEyeFrames = 0;
        _measurementFrame = 0;
        _explicitCalibrationReference = false;
        _leftCorrection.Reset();
        _rightCorrection.Reset();
        _state = UnavailableState;
    }

    private static void ApplyCorrection(Mat eye, RegistrationCorrectionTracker tracker)
    {
        if (!tracker.HasCorrection)
            return;

        var correction = tracker.Correction;
        var radians = correction.RotationDegrees * MathF.PI / 180.0f;
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        using var warp = Mat.Zeros(2, 3, MatType.CV_32FC1).ToMat();
        // Reconstruct FindTransformECC's Euclidean matrix. WARP_INVERSE_MAP maps the current eye
        // image back into the neutral reference coordinate system.
        warp.Set(0, 0, cosine);
        warp.Set(0, 1, sine);
        warp.Set(0, 2, correction.TranslationX);
        warp.Set(1, 0, -sine);
        warp.Set(1, 1, cosine);
        warp.Set(1, 2, correction.TranslationY);
        using var aligned = new Mat();
        Cv2.WarpAffine(
            eye,
            aligned,
            warp,
            eye.Size(),
            InterpolationFlags.Linear | InterpolationFlags.WarpInverseMap,
            BorderTypes.Reflect101);
        aligned.CopyTo(eye);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _eventBus.Unsubscribe<EyePipelineEvents.CalibrationPhaseChangedEvent>(OnPhaseChanged);
        lock (_sync)
            ClearReference();
    }

    private static readonly EyeRegistrationEstimate UnavailableEstimate = new(
        0, 0, 0, 1, 0, CorrectionApplied: false);

    private static readonly EyeFrameNormalizationState UnavailableState = new(
        UnavailableEstimate, UnavailableEstimate);

    private sealed class RegistrationCorrectionTracker
    {
        private EyeRegistrationEstimate _candidate = UnavailableEstimate;
        private EyeRegistrationEstimate _confirmed = UnavailableEstimate;
        private int _candidateMeasurements;
        private float _lastConfidence;

        public bool HasCorrection { get; private set; }

        public EyeRegistrationEstimate Correction => _confirmed;

        public EyeRegistrationEstimate State => HasCorrection
            ? _confirmed with
            {
                Confidence = _lastConfidence,
                CorrectionApplied = HasMaterialCorrection(_confirmed)
            }
            : UnavailableEstimate;

        public void Observe(EyeRegistrationEstimate estimate)
        {
            if (estimate.Confidence < MinimumReliableConfidence)
                return;

            _lastConfidence = estimate.Confidence;
            if (_candidateMeasurements == 0 || !IsConsistent(_candidate, estimate))
            {
                _candidate = estimate;
                _candidateMeasurements = 1;
                return;
            }

            _candidate = Average(_candidate, estimate, _candidateMeasurements);
            _candidateMeasurements++;
            if (_candidateMeasurements < RequiredConsistentMeasurements)
                return;

            var bounded = _candidate with
            {
                TranslationX = Math.Clamp(
                    _candidate.TranslationX,
                    -MaximumAppliedTranslationPixels,
                    MaximumAppliedTranslationPixels),
                TranslationY = Math.Clamp(
                    _candidate.TranslationY,
                    -MaximumAppliedTranslationPixels,
                    MaximumAppliedTranslationPixels),
                RotationDegrees = Math.Clamp(
                    _candidate.RotationDegrees,
                    -MaximumAppliedRotationDegrees,
                    MaximumAppliedRotationDegrees)
            };
            _confirmed = HasCorrection
                ? Lerp(_confirmed, bounded, CorrectionSmoothing)
                : bounded;
            HasCorrection = true;
        }

        public void Reset()
        {
            _candidate = UnavailableEstimate;
            _confirmed = UnavailableEstimate;
            _candidateMeasurements = 0;
            _lastConfidence = 0;
            HasCorrection = false;
        }

        private static bool IsConsistent(
            EyeRegistrationEstimate left,
            EyeRegistrationEstimate right) =>
            MathF.Abs(left.TranslationX - right.TranslationX) <=
                CandidateTranslationTolerancePixels &&
            MathF.Abs(left.TranslationY - right.TranslationY) <=
                CandidateTranslationTolerancePixels &&
            MathF.Abs(left.RotationDegrees - right.RotationDegrees) <=
                CandidateRotationToleranceDegrees;

        private static EyeRegistrationEstimate Average(
            EyeRegistrationEstimate average,
            EyeRegistrationEstimate value,
            int previousSamples)
        {
            var amount = 1.0f / (previousSamples + 1.0f);
            return Lerp(average, value, amount);
        }

        private static EyeRegistrationEstimate Lerp(
            EyeRegistrationEstimate from,
            EyeRegistrationEstimate to,
            float amount) => new(
                TranslationX: from.TranslationX +
                    (to.TranslationX - from.TranslationX) * amount,
                TranslationY: from.TranslationY +
                    (to.TranslationY - from.TranslationY) * amount,
                RotationDegrees: from.RotationDegrees +
                    (to.RotationDegrees - from.RotationDegrees) * amount,
                Scale: 1.0f,
                Confidence: to.Confidence,
                CorrectionApplied: false);

        private static bool HasMaterialCorrection(EyeRegistrationEstimate estimate) =>
            MathF.Abs(estimate.TranslationX) >= 0.25f ||
            MathF.Abs(estimate.TranslationY) >= 0.25f ||
            MathF.Abs(estimate.RotationDegrees) >= 0.15f;
    }
}
