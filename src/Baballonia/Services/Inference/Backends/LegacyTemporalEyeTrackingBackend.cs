using Baballonia.Contracts;
using System;

namespace Baballonia.Services.Inference.Backends;

/// <summary>
/// Adapter for Baballonia's established personalized four-frame ONNX eye model. Keeping all legacy
/// tensor conversion, inference and optional One Euro filtering here gives future eye algorithms a
/// clean parallel implementation point without changing the known-good face pipeline.
/// </summary>
public sealed class LegacyTemporalEyeTrackingBackend : IEyeTrackingBackend
{
    private readonly MatToFloatTensorConverter _imageConverter = new();
    private bool _disposed;

    public string Id => "legacy-temporal";

    public IInferenceRunner? InferenceService { get; set; }

    public IFilter? Filter { get; set; }

    public EyeTrackingBackendOutput? Run(EyeTrackingBackendInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inference = InferenceService;
        if (inference == null)
            return null;

        _imageConverter.Convert(input.TemporalStereo, inference.GetInputTensor());
        var raw = inference.Run();
        if (raw == null)
            return null;

        var filter = Filter;
        if (filter == null)
            return new EyeTrackingBackendOutput(raw, raw, FilterApplied: false);

        return new EyeTrackingBackendOutput(raw, filter.Filter(raw), FilterApplied: true);
    }

    public void Reset()
    {
        // The temporal image history belongs to EyeProcessingPipeline. The current inference runner
        // and One Euro implementation do not expose mutable reset APIs; replacing either component
        // already creates a fresh instance in EyePipelineManager.
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        (InferenceService as IDisposable)?.Dispose();
        (Filter as IDisposable)?.Dispose();
        InferenceService = null;
        Filter = null;
    }
}
