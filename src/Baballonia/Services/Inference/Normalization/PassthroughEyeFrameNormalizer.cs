using Baballonia.Contracts;
using OpenCvSharp;

namespace Baballonia.Services.Inference.Normalization;

/// <summary>
/// Behavior-preserving default until an explicitly selected, calibrated normalizer is available.
/// </summary>
public sealed class PassthroughEyeFrameNormalizer : IEyeFrameNormalizer
{
    public string Id => "disabled";

    public EyeFrameNormalizationState State => EyeFrameNormalizationState.Identity;

    public EyeFrameNormalizationState Apply(Mat transformedStereoFrame) => State;

    public void ObserveTrackingResult(OrderedFloatMap modelRaw)
    {
    }

    public void Reset()
    {
    }

    public void ResetForCameraChange()
    {
    }

    public void Dispose()
    {
    }
}
