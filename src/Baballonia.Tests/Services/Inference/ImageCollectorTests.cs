using Baballonia.Services.Inference;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class ImageCollectorTests
{
    [TestMethod]
    public void ProducesEstablishedNewestFirstRightLeftHistoryWithoutIntermediateFrames()
    {
        using var collector = new ImageCollector();
        Mat? temporal = null;
        for (var frame = 1; frame <= 5; frame++)
        {
            using var input = EyeFrame(frame * 10 + 1, frame * 10 + 2);
            temporal = collector.Apply(input);
            if (frame < 5)
                Assert.IsNull(temporal);
        }

        Assert.IsNotNull(temporal);
        using (temporal)
        {
            var channels = temporal.Split();
            try
            {
                var expected = new[] { 52, 51, 42, 41, 32, 31, 22, 21 };
                Assert.AreEqual(expected.Length, channels.Length);
                for (var i = 0; i < expected.Length; i++)
                    Assert.AreEqual(expected[i], Cv2.Mean(channels[i]).Val0, 0.001);
            }
            finally
            {
                foreach (var channel in channels)
                    channel.Dispose();
            }
        }
    }

    [TestMethod]
    public void ResetRestoresFiveFrameWarmup()
    {
        using var collector = new ImageCollector();
        for (var frame = 0; frame < 5; frame++)
        {
            using var input = EyeFrame(10, 20);
            collector.Apply(input)?.Dispose();
        }

        collector.Reset();
        for (var frame = 0; frame < 4; frame++)
        {
            using var input = EyeFrame(10, 20);
            Assert.IsNull(collector.Apply(input));
        }
    }

    private static Mat EyeFrame(int left, int right)
    {
        using var leftMat = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(left));
        using var rightMat = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(right));
        var combined = new Mat();
        Cv2.Merge([leftMat, rightMat], combined);
        return combined;
    }
}
