using System;
using System.Collections.Generic;
using System.Linq;
using Baballonia.Contracts;

namespace Baballonia.Services.Inference.Geometry;

/// <summary>
/// Learns pixel-to-angle camera geometry with a robust two-pass affine fit. The cross-axis terms
/// are important for headset cameras that are rotated or mounted diagonally relative to the eye.
/// </summary>
public sealed class EyeGeometryCalibrationAccumulator
{
    private const int MinimumSamples = 60;
    private const int MaximumSamples = 1200;
    private readonly List<Sample> _samples = [];

    public EyeAffineGeometryCalibration Calibration { get; private set; }

    public EyeGeometryCalibrationStatus Status { get; private set; } =
        EyeGeometryCalibrationStatus.Unavailable;

    public void Add(PupilObservation pupil, float pitchDegrees, float yawDegrees)
    {
        if (!pupil.IsReliable || !float.IsFinite(pitchDegrees) || !float.IsFinite(yawDegrees) ||
            MathF.Abs(pitchDegrees) > 70 || MathF.Abs(yawDegrees) > 70)
            return;

        if (_samples.Count == MaximumSamples)
            _samples.RemoveAt(0);
        _samples.Add(new Sample(pupil.CenterX, pupil.CenterY, pitchDegrees, yawDegrees));

        if (_samples.Count >= MinimumSamples && _samples.Count % 15 == 0)
            Refit();
        else if (Calibration.Ready)
            Status = Status with
            {
                SampleCount = _samples.Count,
                HorizontalSpanPixels = Span(_samples, sample => sample.X),
                VerticalSpanPixels = Span(_samples, sample => sample.Y),
            };
        else
            UpdatePendingStatus();
    }

    public void Reset()
    {
        _samples.Clear();
        Calibration = default;
        Status = EyeGeometryCalibrationStatus.Unavailable;
    }

    public void Restore(
        EyeAffineGeometryCalibration calibration,
        EyeGeometryCalibrationStatus status)
    {
        _samples.Clear();
        Calibration = calibration;
        Status = calibration.Ready && status.Ready
            ? status
            : EyeGeometryCalibrationStatus.Unavailable;
    }

    private void Refit()
    {
        var spanX = Span(_samples, sample => sample.X);
        var spanY = Span(_samples, sample => sample.Y);
        var targetYawSpan = Span(_samples, sample => sample.Yaw);
        var targetPitchSpan = Span(_samples, sample => sample.Pitch);
        if (spanX < 6 || spanY < 6 || targetYawSpan < 20 || targetPitchSpan < 20 ||
            !TryFit(_samples, out var initial))
        {
            UpdateStatus(spanX, spanY, rootMeanSquareError: 0, ready: false);
            return;
        }

        var residuals = _samples
            .Select(sample => new
            {
                Sample = sample,
                Error = Residual(initial, sample),
            })
            .OrderBy(item => item.Error)
            .ToArray();
        var median = residuals[residuals.Length / 2].Error;
        var threshold = Math.Clamp(median * 2.5f, 2.5f, 8.0f);
        var inliers = residuals
            .Where(item => item.Error <= threshold)
            .Select(item => item.Sample)
            .ToArray();
        if (inliers.Length < MinimumSamples * 3 / 4 || !TryFit(inliers, out var robust))
        {
            UpdateStatus(spanX, spanY, rootMeanSquareError: median, ready: false);
            return;
        }

        var rmse = MathF.Sqrt(inliers.Average(sample =>
        {
            var error = Residual(robust, sample);
            return error * error;
        }));
        var coverage = Math.Clamp(Math.Min(spanX, spanY) / 22.0f, 0.0f, 1.0f);
        var confidence = Math.Clamp((1.0f - rmse / 12.0f) * coverage, 0.0f, 1.0f);
        var ready = rmse <= 8.0f && confidence >= 0.25f;
        Calibration = robust with { Confidence = confidence, Ready = ready };
        UpdateStatus(spanX, spanY, rmse, ready, confidence);
    }

    private void UpdatePendingStatus() => UpdateStatus(
        Span(_samples, sample => sample.X),
        Span(_samples, sample => sample.Y),
        rootMeanSquareError: 0,
        ready: false);

    private void UpdateStatus(
        float spanX,
        float spanY,
        float rootMeanSquareError,
        bool ready,
        float confidence = 0)
    {
        Status = new EyeGeometryCalibrationStatus(
            _samples.Count, spanX, spanY, rootMeanSquareError, confidence, ready);
    }

    private static float Span(IEnumerable<Sample> samples, Func<Sample, float> selector)
    {
        var any = false;
        var minimum = float.MaxValue;
        var maximum = float.MinValue;
        foreach (var sample in samples)
        {
            var value = selector(sample);
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            any = true;
        }
        return any ? maximum - minimum : 0;
    }

    private static float Residual(EyeAffineGeometryCalibration calibration, Sample sample)
    {
        var yaw = calibration.YawFromX * sample.X +
                  calibration.YawFromY * sample.Y + calibration.YawBias;
        var pitch = calibration.PitchFromX * sample.X +
                    calibration.PitchFromY * sample.Y + calibration.PitchBias;
        return MathF.Sqrt(
            (yaw - sample.Yaw) * (yaw - sample.Yaw) +
            (pitch - sample.Pitch) * (pitch - sample.Pitch));
    }

    private static bool TryFit(
        IReadOnlyCollection<Sample> samples,
        out EyeAffineGeometryCalibration calibration)
    {
        calibration = default;
        double xx = 0, xy = 0, x = 0, yy = 0, y = 0;
        double xYaw = 0, yYaw = 0, yaw = 0;
        double xPitch = 0, yPitch = 0, pitch = 0;
        foreach (var sample in samples)
        {
            xx += sample.X * sample.X;
            xy += sample.X * sample.Y;
            x += sample.X;
            yy += sample.Y * sample.Y;
            y += sample.Y;
            xYaw += sample.X * sample.Yaw;
            yYaw += sample.Y * sample.Yaw;
            yaw += sample.Yaw;
            xPitch += sample.X * sample.Pitch;
            yPitch += sample.Y * sample.Pitch;
            pitch += sample.Pitch;
        }

        var matrix = new[,]
        {
            { xx, xy, x },
            { xy, yy, y },
            { x, y, samples.Count },
        };
        if (!Solve3x3(matrix, [xYaw, yYaw, yaw], out var yawCoefficients) ||
            !Solve3x3(matrix, [xPitch, yPitch, pitch], out var pitchCoefficients))
            return false;

        calibration = new EyeAffineGeometryCalibration(
            (float)yawCoefficients[0], (float)yawCoefficients[1], (float)yawCoefficients[2],
            (float)pitchCoefficients[0], (float)pitchCoefficients[1], (float)pitchCoefficients[2],
            Confidence: 0,
            Ready: false);
        return true;
    }

    private static bool Solve3x3(double[,] source, double[] sourceVector, out double[] solution)
    {
        var matrix = (double[,])source.Clone();
        var vector = (double[])sourceVector.Clone();
        solution = new double[3];
        for (var column = 0; column < 3; column++)
        {
            var pivot = column;
            for (var row = column + 1; row < 3; row++)
                if (Math.Abs(matrix[row, column]) > Math.Abs(matrix[pivot, column]))
                    pivot = row;
            if (Math.Abs(matrix[pivot, column]) < 1e-8)
                return false;
            if (pivot != column)
            {
                for (var index = column; index < 3; index++)
                    (matrix[column, index], matrix[pivot, index]) =
                        (matrix[pivot, index], matrix[column, index]);
                (vector[column], vector[pivot]) = (vector[pivot], vector[column]);
            }
            var divisor = matrix[column, column];
            for (var index = column; index < 3; index++)
                matrix[column, index] /= divisor;
            vector[column] /= divisor;
            for (var row = 0; row < 3; row++)
            {
                if (row == column)
                    continue;
                var factor = matrix[row, column];
                for (var index = column; index < 3; index++)
                    matrix[row, index] -= factor * matrix[column, index];
                vector[row] -= factor * vector[column];
            }
        }
        Array.Copy(vector, solution, 3);
        return solution.All(double.IsFinite);
    }

    private readonly record struct Sample(float X, float Y, float Pitch, float Yaw);
}
