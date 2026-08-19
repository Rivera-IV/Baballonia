using Baballonia.Contracts;
using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Backends;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenCvSharp;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class LegacyTemporalEyeTrackingBackendTests
{
    [TestMethod]
    public void UsesTemporalInputAndPreservesUnfilteredRawOutput()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 8, 128, 128 });
        var raw = CreateMap(0.25f);
        var filtered = CreateMap(0.75f);

        var inference = new Mock<IInferenceRunner>();
        inference.Setup(item => item.GetInputTensor()).Returns(tensor);
        inference.Setup(item => item.Run()).Returns(raw);

        var filter = new Mock<IFilter>();
        filter.Setup(item => item.Filter(raw)).Returns(filtered);

        using var backend = new LegacyTemporalEyeTrackingBackend
        {
            InferenceService = inference.Object,
            Filter = filter.Object,
        };
        using var current = new Mat(128, 128, MatType.CV_8UC2, Scalar.All(10));
        using var temporal = CreateTemporalImage(127);

        var result = backend.Run(new EyeTrackingBackendInput(current, temporal));

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Value.FilterApplied);
        Assert.AreSame(raw, result.Value.ModelRaw);
        Assert.AreSame(filtered, result.Value.ModelFiltered);
        Assert.AreEqual(127f / 255f, tensor[0, 0, 0, 0], 0.0001f);
        inference.Verify(item => item.Run(), Times.Once);
        filter.Verify(item => item.Filter(raw), Times.Once);
    }

    [TestMethod]
    public void WithoutFilterReturnsOneSharedModelBuffer()
    {
        var tensor = new DenseTensor<float>(new[] { 1, 8, 128, 128 });
        var raw = CreateMap(0.5f);
        var inference = new Mock<IInferenceRunner>();
        inference.Setup(item => item.GetInputTensor()).Returns(tensor);
        inference.Setup(item => item.Run()).Returns(raw);

        using var backend = new LegacyTemporalEyeTrackingBackend
        {
            InferenceService = inference.Object,
        };
        using var current = new Mat(128, 128, MatType.CV_8UC2, Scalar.All(0));
        using var temporal = CreateTemporalImage(0);

        var result = backend.Run(new EyeTrackingBackendInput(current, temporal));

        Assert.IsNotNull(result);
        Assert.IsFalse(result.Value.FilterApplied);
        Assert.AreSame(raw, result.Value.ModelRaw);
        Assert.AreSame(raw, result.Value.ModelFiltered);
    }

    private static OrderedFloatMap CreateMap(float value)
    {
        var result = new OrderedFloatMap(["/leftEyeX", "/rightEyeX"]);
        result["/leftEyeX"] = value;
        result["/rightEyeX"] = value;
        return result;
    }

    private static Mat CreateTemporalImage(byte value)
    {
        var channels = new Mat[8];
        try
        {
            for (var index = 0; index < channels.Length; index++)
                channels[index] = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(value));

            var temporal = new Mat();
            Cv2.Merge(channels, temporal);
            return temporal;
        }
        finally
        {
            foreach (var channel in channels)
                channel?.Dispose();
        }
    }
}
