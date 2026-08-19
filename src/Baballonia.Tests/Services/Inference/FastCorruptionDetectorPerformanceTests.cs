using Baballonia.FastCorruptionDetector;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using System;
using System.Linq;
using CorruptionDetector = Baballonia.FastCorruptionDetector.FastCorruptionDetector;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class FastCorruptionDetectorPerformanceTests
{
    [TestMethod]
    public void ReducedImplementationPreservesOriginalMetric()
    {
        using var image = new Mat<byte>(96, 160);
        new Random(42).NextBytes(image.AsSpan<byte>());
        using var detector = new CorruptionDetector(useAdaptive: false);

        var result = detector.IsCorrupted(image);
        var expected = OriginalMetric(image);

        Assert.AreEqual(expected, result.metricValue, 1e-7);
    }

    private static double OriginalMetric(Mat image)
    {
        using var normalized = new Mat();
        image.ConvertTo(normalized, MatType.CV_32F, 1.0 / 255.0);
        var rowMeans = new double[normalized.Rows];
        for (var rowIndex = 0; rowIndex < normalized.Rows; rowIndex++)
        {
            using var row = normalized.Row(rowIndex);
            rowMeans[rowIndex] = Cv2.Mean(row).Val0;
        }

        var differences = new double[rowMeans.Length - 1];
        for (var i = 0; i < differences.Length; i++)
            differences[i] = rowMeans[i + 1] - rowMeans[i];
        var mean = differences.Average();
        return Math.Sqrt(differences.Select(value => Math.Pow(value - mean, 2)).Average());
    }
}
