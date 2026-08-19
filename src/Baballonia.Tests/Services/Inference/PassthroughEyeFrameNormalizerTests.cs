using Baballonia.Services.Inference.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class PassthroughEyeFrameNormalizerTests
{
    [TestMethod]
    public void LeavesInputUntouchedAndReportsIdentity()
    {
        using var frame = new Mat(16, 16, MatType.CV_8UC2, Scalar.All(73));
        using var before = frame.Clone();
        using var normalizer = new PassthroughEyeFrameNormalizer();

        var state = normalizer.Apply(frame);

        using var difference = new Mat();
        Cv2.Absdiff(frame, before, difference);
        using var flattenedDifference = difference.Reshape(1);
        Assert.AreEqual(0, Cv2.CountNonZero(flattenedDifference));
        Assert.AreEqual(0f, state.Left.TranslationX);
        Assert.AreEqual(0f, state.Right.TranslationY);
        Assert.AreEqual(1f, state.Left.Scale);
        Assert.IsFalse(state.Left.CorrectionApplied);
        Assert.AreEqual("disabled", normalizer.Id);
    }
}
