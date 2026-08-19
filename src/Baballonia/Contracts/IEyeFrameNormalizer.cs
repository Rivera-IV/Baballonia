using OpenCvSharp;
using System;

namespace Baballonia.Contracts;

public readonly record struct EyeRegistrationEstimate(
    float TranslationX,
    float TranslationY,
    float RotationDegrees,
    float Scale,
    float Confidence,
    bool CorrectionApplied)
{
    public static EyeRegistrationEstimate Identity => new(
        TranslationX: 0,
        TranslationY: 0,
        RotationDegrees: 0,
        Scale: 1,
        Confidence: 1,
        CorrectionApplied: false);
}

public readonly record struct EyeFrameNormalizationState(
    EyeRegistrationEstimate Left,
    EyeRegistrationEstimate Right)
{
    public static EyeFrameNormalizationState Identity => new(
        EyeRegistrationEstimate.Identity,
        EyeRegistrationEstimate.Identity);
}

/// <summary>
/// Optional in-place canonicalization stage between the user's fixed crop/orientation and eye
/// inference. An implementation may compensate persistent camera-to-eye movement but must leave the
/// frame untouched whenever its estimate is uncertain. In-place processing avoids a per-frame copy
/// when normalization is disabled.
/// </summary>
public interface IEyeFrameNormalizer : IDisposable
{
    string Id { get; }

    EyeFrameNormalizationState State { get; }

    EyeFrameNormalizationState Apply(Mat transformedStereoFrame);

    /// <summary>
    /// Supplies the raw model result for the same frame most recently passed to
    /// <see cref="Apply"/>. Implementations may use it to approve a pending startup-reference
    /// candidate, but must never modify the model output.
    /// </summary>
    void ObserveTrackingResult(OrderedFloatMap modelRaw);

    void Reset();

    /// <summary>
    /// Discards camera-position knowledge and starts a new automatic reference capture. Use this
    /// only when the physical video source changes or restarts; model/filter resets keep the
    /// existing reference.
    /// </summary>
    void ResetForCameraChange();
}
