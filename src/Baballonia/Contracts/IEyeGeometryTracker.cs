using OpenCvSharp;
using System;

namespace Baballonia.Contracts;

/// <summary>
/// Image-space pupil measurement. Coordinates are expressed in pixels of one transformed eye
/// image, before any avatar remapping. A low confidence observation must never drive gaze.
/// </summary>
public readonly record struct PupilObservation(
    float CenterX,
    float CenterY,
    float RadiusX,
    float RadiusY,
    float Visibility,
    float Confidence)
{
    public bool IsReliable => Confidence >= 0.35f && Visibility >= 0.25f;

    public static PupilObservation Unavailable => new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Camera-independent gaze estimate produced by the mathematical eyeball solver.
/// Angles are in degrees; the vector points forward along +Z.
/// </summary>
public readonly record struct GeometricGazeEstimate(
    float YawDegrees,
    float PitchDegrees,
    float DirectionX,
    float DirectionY,
    float DirectionZ,
    float Confidence,
    bool Valid)
{
    public static GeometricGazeEstimate Unavailable => new(0, 0, 0, 0, 1, 0, false);
}

public readonly record struct EyeGeometryCalibrationStatus(
    int SampleCount,
    float HorizontalSpanPixels,
    float VerticalSpanPixels,
    float RootMeanSquareErrorDegrees,
    float Confidence,
    bool Ready)
{
    public static EyeGeometryCalibrationStatus Unavailable => new(0, 0, 0, 0, 0, false);
}

public readonly record struct EyeGeometryTrackingState(
    PupilObservation LeftPupil,
    PupilObservation RightPupil,
    GeometricGazeEstimate LeftGaze,
    GeometricGazeEstimate RightGaze,
    EyeGeometryCalibrationStatus LeftCalibration,
    EyeGeometryCalibrationStatus RightCalibration)
{
    public static EyeGeometryTrackingState Unavailable => new(
        PupilObservation.Unavailable,
        PupilObservation.Unavailable,
        GeometricGazeEstimate.Unavailable,
        GeometricGazeEstimate.Unavailable,
        EyeGeometryCalibrationStatus.Unavailable,
        EyeGeometryCalibrationStatus.Unavailable);
}

/// <summary>
/// Experimental geometric tracker. It currently runs in diagnostic shadow mode: consumers may
/// record and compare its state, but it does not replace the personalized ONNX gaze output.
/// </summary>
public interface IEyeGeometryTracker : IDisposable
{
    string Id { get; }

    EyeGeometryTrackingState State { get; }

    EyeGeometryTrackingState Observe(Mat transformedStereoFrame);

    void Reset();
}
