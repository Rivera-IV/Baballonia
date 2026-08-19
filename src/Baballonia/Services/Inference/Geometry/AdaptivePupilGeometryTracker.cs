using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Baballonia.Contracts;
using Baballonia.Services.events;
using OpenCvSharp;

namespace Baballonia.Services.Inference.Geometry;

/// <summary>
/// Conservative pupil observer used to collect real-world evidence for the geometric backend.
/// It deliberately reports "unavailable" instead of guessing when no plausible dark pupil blob
/// exists. Its output is diagnostic-only until it has been validated across headset camera layouts.
/// </summary>
public sealed class AdaptivePupilGeometryTracker : IEyeGeometryTracker
{
    private const int NeutralSamplesRequired = 20;
    private readonly IEyePipelineEventBus _eventBus;
    private readonly EyeGeometryCalibrationAccumulator _leftCalibration = new();
    private readonly EyeGeometryCalibrationAccumulator _rightCalibration = new();
    private readonly object _sync = new();
    private bool _captureNeutral;
    private string _phase = "";
    private CalibrationTarget _target;
    private long _targetTimestamp;
    private int _calibrationFrame;
    private float _leftNeutralX;
    private float _leftNeutralY;
    private float _rightNeutralX;
    private float _rightNeutralY;
    private int _leftNeutralSamples;
    private int _rightNeutralSamples;
    private EyeGeometryTrackingState _state = EyeGeometryTrackingState.Unavailable;
    private bool _disposed;
    private readonly string? _calibrationPath;
    private bool _calibrationSaved;

    public AdaptivePupilGeometryTracker(
        IEyePipelineEventBus eventBus,
        string? calibrationPath = null)
    {
        _eventBus = eventBus;
        _calibrationPath = calibrationPath ?? Path.Combine(
            Utils.ModelDataDirectory, "eye_geometry_v1.json");
        _eventBus.Subscribe<EyePipelineEvents.CalibrationPhaseChangedEvent>(OnPhaseChanged);
        _eventBus.Subscribe<EyePipelineEvents.CalibrationTargetChangedEvent>(OnTargetChanged);
        LoadCalibrationNoThrow();
    }

    public string Id => "adaptive-pupil-geometry-shadow-v1";

    public EyeGeometryTrackingState State
    {
        get { lock (_sync) return _state; }
    }

    public EyeGeometryTrackingState Observe(Mat transformedStereoFrame)
    {
        if (_disposed || transformedStereoFrame.Empty() || transformedStereoFrame.Channels() < 2)
            return State;

        var channels = transformedStereoFrame.Split();
        try
        {
            if (channels.Length < 2)
                return State;

            var left = Detect(channels[0]);
            var right = Detect(channels[1]);
            lock (_sync)
            {
                if (_captureNeutral)
                {
                    LearnNeutral(left, ref _leftNeutralX, ref _leftNeutralY,
                        ref _leftNeutralSamples);
                    LearnNeutral(right, ref _rightNeutralX, ref _rightNeutralY,
                        ref _rightNeutralSamples);
                }

                if (_phase == "gaze" && ++_calibrationFrame % 3 == 0 &&
                    _targetTimestamp != 0 &&
                    Stopwatch.GetElapsedTime(_targetTimestamp).TotalMilliseconds <= 250)
                {
                    _leftCalibration.Add(left, _target.LeftPitch, _target.LeftYaw);
                    _rightCalibration.Add(right, _target.RightPitch, _target.RightYaw);
                }

                if (!_calibrationSaved && _leftCalibration.Status.Ready &&
                    _rightCalibration.Status.Ready)
                {
                    SaveCalibrationNoThrow();
                    _calibrationSaved = true;
                }

                var leftCalibration = CreateCalibration(
                    channels[0], left, _leftNeutralX, _leftNeutralY, _leftNeutralSamples);
                var rightCalibration = CreateCalibration(
                    channels[1], right, _rightNeutralX, _rightNeutralY, _rightNeutralSamples);
                _state = new EyeGeometryTrackingState(
                    left,
                    right,
                    _leftCalibration.Status.Ready
                        ? EyeGeometrySolver.Solve(left, _leftCalibration.Calibration)
                        : EyeGeometrySolver.Solve(left, leftCalibration),
                    _rightCalibration.Status.Ready
                        ? EyeGeometrySolver.Solve(right, _rightCalibration.Calibration)
                        : EyeGeometrySolver.Solve(right, rightCalibration),
                    _leftCalibration.Status,
                    _rightCalibration.Status);
                return _state;
            }
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }

    private void OnPhaseChanged(EyePipelineEvents.CalibrationPhaseChangedEvent phase)
    {
        lock (_sync)
        {
            var capture = phase.CaptureEnabled && phase.Phase == "neutralstart";
            if (capture && !_captureNeutral)
            {
                _leftNeutralX = _leftNeutralY = _rightNeutralX = _rightNeutralY = 0;
                _leftNeutralSamples = _rightNeutralSamples = 0;
                _leftCalibration.Reset();
                _rightCalibration.Reset();
                _calibrationSaved = false;
            }
            _captureNeutral = capture;
            _phase = phase.CaptureEnabled ? phase.Phase : "";
            _calibrationFrame = 0;
        }
    }

    private void OnTargetChanged(EyePipelineEvents.CalibrationTargetChangedEvent target)
    {
        lock (_sync)
        {
            _target = new CalibrationTarget(
                target.LeftEyePitch, target.LeftEyeYaw,
                target.RightEyePitch, target.RightEyeYaw);
            _targetTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private static void LearnNeutral(
        PupilObservation observation, ref float x, ref float y, ref int samples)
    {
        if (!observation.IsReliable || samples >= NeutralSamplesRequired)
            return;
        samples++;
        x += (observation.CenterX - x) / samples;
        y += (observation.CenterY - y) / samples;
    }

    private static EyeGeometryCalibration CreateCalibration(
        Mat image, PupilObservation current, float neutralX, float neutralY, int neutralSamples)
    {
        // Until neutral capture is complete, centring on the current observation avoids emitting a
        // confidently wrong direction. The gaze becomes useful for comparison only after calibration.
        var hasNeutral = neutralSamples >= NeutralSamplesRequired;
        return new EyeGeometryCalibration(
            hasNeutral ? neutralX : current.CenterX,
            hasNeutral ? neutralY : current.CenterY,
            Math.Max(2, image.Width * 0.38f),
            Math.Max(2, image.Height * 0.38f));
    }

    internal static PupilObservation Detect(Mat eyeImage)
    {
        if (eyeImage.Empty() || eyeImage.Channels() != 1 || eyeImage.Width < 24 || eyeImage.Height < 24)
            return PupilObservation.Unavailable;

        using var blurred = new Mat();
        Cv2.GaussianBlur(eyeImage, blurred, new Size(7, 7), 0);
        Cv2.MeanStdDev(blurred, out var mean, out var deviation);
        if (deviation.Val0 < 5.0)
            return PupilObservation.Unavailable;

        // The pupil in Baballonia's IR feeds is normally the locally dark compact structure. An
        // adaptive threshold survives brightness changes better than a fixed camera-specific value.
        var threshold = Math.Clamp(mean.Val0 - deviation.Val0 * 0.55, 4.0, 245.0);
        using var binary = new Mat();
        Cv2.Threshold(blurred, binary, threshold, 255, ThresholdTypes.BinaryInv);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
        Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.External,
            ContourApproximationModes.ApproxSimple);

        var imageArea = eyeImage.Width * eyeImage.Height;
        var bestScore = 0.0;
        PupilObservation best = PupilObservation.Unavailable;
        foreach (var contour in contours)
        {
            var area = Cv2.ContourArea(contour);
            if (area < imageArea * 0.002 || area > imageArea * 0.16)
                continue;
            var bounds = Cv2.BoundingRect(contour);
            if (bounds.Width < 3 || bounds.Height < 3)
                continue;
            var aspect = (double)bounds.Width / bounds.Height;
            if (aspect < 0.30 || aspect > 3.3)
                continue;
            var perimeter = Cv2.ArcLength(contour, true);
            if (perimeter <= 0)
                continue;
            var circularity = Math.Clamp(4.0 * Math.PI * area / (perimeter * perimeter), 0, 1);
            var moments = Cv2.Moments(contour);
            if (Math.Abs(moments.M00) < double.Epsilon)
                continue;
            var centerX = (float)(moments.M10 / moments.M00);
            var centerY = (float)(moments.M01 / moments.M00);
            var edgeDistance = Math.Min(
                Math.Min(centerX, eyeImage.Width - 1 - centerX),
                Math.Min(centerY, eyeImage.Height - 1 - centerY));
            var radius = Math.Max(bounds.Width, bounds.Height) * 0.5f;
            var visibility = Math.Clamp(edgeDistance / Math.Max(radius, 1), 0.0f, 1.0f);
            var centreDistanceX = (centerX - eyeImage.Width * 0.5) / (eyeImage.Width * 0.5);
            var centreDistanceY = (centerY - eyeImage.Height * 0.5) / (eyeImage.Height * 0.5);
            var centrePrior = Math.Clamp(1.0 - 0.35 * Math.Sqrt(
                centreDistanceX * centreDistanceX + centreDistanceY * centreDistanceY), 0, 1);
            var fill = Math.Clamp(area / (bounds.Width * bounds.Height), 0, 1);
            var score = (0.50 * circularity + 0.30 * fill + 0.20 * centrePrior) *
                        (0.5 + 0.5 * visibility);
            if (score <= bestScore)
                continue;
            bestScore = score;
            best = new PupilObservation(
                centerX, centerY,
                bounds.Width * 0.5f, bounds.Height * 0.5f,
                visibility,
                (float)Math.Clamp((score - 0.35) / 0.55, 0, 1));
        }

        return best;
    }

    public void Reset()
    {
        lock (_sync)
            _state = EyeGeometryTrackingState.Unavailable;
    }

    private void LoadCalibrationNoThrow()
    {
        if (string.IsNullOrWhiteSpace(_calibrationPath) || !File.Exists(_calibrationPath))
            return;
        try
        {
            var document = JsonSerializer.Deserialize<PersistedGeometryCalibration>(
                File.ReadAllText(_calibrationPath));
            if (document is null || document.Version != 1)
                return;
            _leftCalibration.Restore(document.LeftCalibration, document.LeftStatus);
            _rightCalibration.Restore(document.RightCalibration, document.RightStatus);
            _calibrationSaved = _leftCalibration.Status.Ready && _rightCalibration.Status.Ready;
        }
        catch (IOException)
        {
            // Geometry is an optional refinement. A damaged/locked cache falls back to the neural
            // model and is replaced after the next successful calibration.
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void SaveCalibrationNoThrow()
    {
        if (string.IsNullOrWhiteSpace(_calibrationPath))
            return;
        try
        {
            var parent = Path.GetDirectoryName(_calibrationPath);
            if (!string.IsNullOrWhiteSpace(parent))
                Directory.CreateDirectory(parent);
            var document = new PersistedGeometryCalibration(
                1,
                _leftCalibration.Calibration,
                _rightCalibration.Calibration,
                _leftCalibration.Status,
                _rightCalibration.Status);
            var temporaryPath = _calibrationPath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document));
            File.Move(temporaryPath, _calibrationPath, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _eventBus.Unsubscribe<EyePipelineEvents.CalibrationPhaseChangedEvent>(OnPhaseChanged);
        _eventBus.Unsubscribe<EyePipelineEvents.CalibrationTargetChangedEvent>(OnTargetChanged);
    }

    private readonly record struct CalibrationTarget(
        float LeftPitch,
        float LeftYaw,
        float RightPitch,
        float RightYaw);

    private sealed record PersistedGeometryCalibration(
        int Version,
        EyeAffineGeometryCalibration LeftCalibration,
        EyeAffineGeometryCalibration RightCalibration,
        EyeGeometryCalibrationStatus LeftStatus,
        EyeGeometryCalibrationStatus RightStatus);
}
