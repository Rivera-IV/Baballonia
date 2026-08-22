using OpenCvSharp;
using System;
using System.Collections.Generic;
using Baballonia.Contracts;

namespace Baballonia.Services.events;

public readonly record struct EyeTemporalDiagnosticState(
    string State,
    bool Initialized,
    bool LeftPupilReliable,
    bool RightPupilReliable,
    int LeftPupilMissingFrames,
    int RightPupilMissingFrames,
    int StateFrames,
    int StableRecoveryFrames,
    int RecoveryBlendFrame,
    bool ConvergenceActive,
    float ConvergenceAmount,
    int ConvergenceEnterFrames,
    int ConvergenceExitFrames);

public class FacePipelineEvents
{
    public readonly record struct NewFrameEvent(Mat image);

    public readonly record struct NewTransformedFrameEvent(Mat image);

    public readonly record struct NewFilteredResultEvent(OrderedFloatMap result);

    public readonly record struct ExceptionEvent(Exception exception);
}
public class EyePipelineEvents
{
    public readonly record struct NewFrameEvent(Mat image);

    public readonly record struct NewTransformedFrameEvent(Mat image);

    public readonly record struct NewFilteredResultEvent(OrderedFloatMap result);

    public readonly record struct CalibrationPhaseChangedEvent(string Phase, bool CaptureEnabled);

    public readonly record struct CalibrationTargetChangedEvent(
        float RoutinePitch,
        float RoutineYaw,
        float RoutineDistance,
        float LeftEyePitch,
        float LeftEyeYaw,
        float RightEyePitch,
        float RightEyeYaw);

    /// <summary>
    /// Synchronized diagnostic snapshot for one real inference step. The image is the transformed
    /// two-eye input frame; all maps are independent copies and remain valid after the callback.
    /// </summary>
    public record DiagnosticFrameEvent(
        Mat TransformedImage,
        OrderedFloatMap ModelRaw,
        OrderedFloatMap ProcessedRaw,
        OrderedFloatMap ProcessedFiltered,
        EyeTemporalDiagnosticState RawTemporalState,
        EyeTemporalDiagnosticState FilteredTemporalState,
        EyeFrameNormalizationState Normalization,
        EyeGeometryTrackingState Geometry);

    public readonly record struct ExceptionEvent(Exception exception);
}
