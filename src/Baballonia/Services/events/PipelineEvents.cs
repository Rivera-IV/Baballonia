using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace Baballonia.Services.events;

public class FacePipelineEvents
{
    public record NewFrameEvent(Mat image);

    public record NewTransformedFrameEvent(Mat image);

    public record NewFilteredResultEvent(OrderedFloatMap result);

    public record ExceptionEvent(Exception exception);
}
public class EyePipelineEvents
{
    public record NewFrameEvent(Mat image);

    public record NewTransformedFrameEvent(Mat image);

    public record NewFilteredResultEvent(OrderedFloatMap result);

    /// <summary>
    /// Synchronized diagnostic snapshot for one real inference step. The image is the transformed
    /// two-eye input frame; all maps are independent copies and remain valid after the callback.
    /// </summary>
    public record DiagnosticFrameEvent(
        Mat TransformedImage,
        OrderedFloatMap ModelRaw,
        OrderedFloatMap ProcessedRaw,
        OrderedFloatMap ProcessedFiltered);

    public record ExceptionEvent(Exception exception);
}
