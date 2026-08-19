using System;
using Baballonia.Contracts;

namespace Baballonia.Services.Inference.Geometry;

/// <summary>
/// Per-camera geometry learned during calibration. The projected radii describe how far the pupil
/// centre can travel in the transformed camera image before reaching the configured angular limit.
/// Keeping X and Y independent allows oblique headset cameras without assuming a circular crop.
/// </summary>
public readonly record struct EyeGeometryCalibration(
    float NeutralX,
    float NeutralY,
    float ProjectedRadiusX,
    float ProjectedRadiusY,
    float MaximumYawDegrees = 45.0f,
    float MaximumPitchDegrees = 45.0f);

/// <summary>
/// Affine camera calibration. Cross-axis terms compensate a rotated or oblique headset camera.
/// The final direction is still constructed on the mathematical unit sphere.
/// </summary>
public readonly record struct EyeAffineGeometryCalibration(
    float YawFromX,
    float YawFromY,
    float YawBias,
    float PitchFromX,
    float PitchFromY,
    float PitchBias,
    float Confidence,
    bool Ready);

public static class EyeGeometrySolver
{
    public static GeometricGazeEstimate Solve(
        PupilObservation observation,
        EyeAffineGeometryCalibration calibration,
        float maximumAngleDegrees = 50.0f)
    {
        if (!observation.IsReliable || !calibration.Ready || calibration.Confidence <= 0)
            return GeometricGazeEstimate.Unavailable;

        var yaw = calibration.YawFromX * observation.CenterX +
                  calibration.YawFromY * observation.CenterY + calibration.YawBias;
        var pitch = calibration.PitchFromX * observation.CenterX +
                    calibration.PitchFromY * observation.CenterY + calibration.PitchBias;
        yaw = Math.Clamp(yaw, -maximumAngleDegrees, maximumAngleDegrees);
        pitch = Math.Clamp(pitch, -maximumAngleDegrees, maximumAngleDegrees);
        return FromAngles(yaw, pitch,
            observation.Confidence * observation.Visibility * calibration.Confidence);
    }

    public static GeometricGazeEstimate Solve(
        PupilObservation observation,
        EyeGeometryCalibration calibration)
    {
        if (!observation.IsReliable ||
            calibration.ProjectedRadiusX <= 1.0f || calibration.ProjectedRadiusY <= 1.0f ||
            calibration.MaximumYawDegrees <= 0 || calibration.MaximumPitchDegrees <= 0)
            return GeometricGazeEstimate.Unavailable;

        var normalizedX = Math.Clamp(
            (observation.CenterX - calibration.NeutralX) / calibration.ProjectedRadiusX,
            -1.0f, 1.0f);
        var normalizedY = Math.Clamp(
            (observation.CenterY - calibration.NeutralY) / calibration.ProjectedRadiusY,
            -1.0f, 1.0f);

        // Orthographic projection of a point on a sphere: screen displacement is proportional to
        // sin(angle), not angle. This remains well behaved at the edge of the calibrated range.
        var maximumYaw = DegreesToRadians(Math.Clamp(calibration.MaximumYawDegrees, 1, 85));
        var maximumPitch = DegreesToRadians(Math.Clamp(calibration.MaximumPitchDegrees, 1, 85));
        var yaw = MathF.Asin(normalizedX * MathF.Sin(maximumYaw));
        var pitch = -MathF.Asin(normalizedY * MathF.Sin(maximumPitch));

        return FromAngles(
            RadiansToDegrees(yaw),
            RadiansToDegrees(pitch),
            observation.Confidence * observation.Visibility);
    }

    private static GeometricGazeEstimate FromAngles(
        float yawDegrees, float pitchDegrees, float confidence)
    {
        var yaw = DegreesToRadians(yawDegrees);
        var pitch = DegreesToRadians(pitchDegrees);
        var cosPitch = MathF.Cos(pitch);
        return new GeometricGazeEstimate(
            yawDegrees,
            pitchDegrees,
            MathF.Sin(yaw) * cosPitch,
            MathF.Sin(pitch),
            MathF.Cos(yaw) * cosPitch,
            Math.Clamp(confidence, 0, 1),
            Valid: true);
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180.0f;

    private static float RadiansToDegrees(float radians) => radians * 180.0f / MathF.PI;
}
