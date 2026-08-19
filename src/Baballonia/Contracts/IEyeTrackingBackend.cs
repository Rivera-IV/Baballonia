using Baballonia.Services.Inference;
using OpenCvSharp;
using System;

namespace Baballonia.Contracts;

/// <summary>
/// Input shared by eye-tracking implementations. <see cref="CurrentStereo"/> contains the current
/// transformed left/right grayscale images as two channels. <see cref="TemporalStereo"/> contains
/// the established four-frame, right/left interleaved history used by Baballonia's legacy model.
/// A future backend can deliberately use either representation without taking ownership of it.
/// </summary>
public readonly record struct EyeTrackingBackendInput(
    Mat CurrentStereo,
    Mat TemporalStereo);

/// <summary>
/// Model-space output from an eye backend. The maps may be reusable backend-owned buffers and are
/// valid only until the next <see cref="IEyeTrackingBackend.Run"/> call.
/// </summary>
public readonly record struct EyeTrackingBackendOutput(
    OrderedFloatMap ModelRaw,
    OrderedFloatMap ModelFiltered,
    bool FilterApplied);

/// <summary>
/// Replaceable eye inference stage. Camera acquisition, image orientation, diagnostics, avatar
/// remapping and face tracking remain owned by Baballonia's existing pipelines.
/// </summary>
public interface IEyeTrackingBackend : IDisposable
{
    string Id { get; }

    EyeTrackingBackendOutput? Run(EyeTrackingBackendInput input);

    void Reset();
}
