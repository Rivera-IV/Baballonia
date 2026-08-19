using Baballonia.Desktop.Calibration;
using Baballonia.Services;
using Baballonia.Services.events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using OpenCvSharp;
using System;
using System.IO;

namespace Baballonia.Tests.Calibration;

[TestClass]
public class AsymmetricLidCaptureTests
{
    [DataTestMethod]
    [DataRow(0f, 1f)]
    [DataRow(1f, 0f)]
    public void AddFrame_PreservesIndependentLidLabels(float leftLid, float rightLid)
    {
        var step = new BaseEyeCaptureStep(
            Mock.Of<IEyePipelineEventBus>(),
            "wink",
            flags: 0,
            time: TimeSpan.FromSeconds(1),
            leftLid: leftLid,
            rightLid: rightLid);

        using var leftImage = new Mat(8, 8, MatType.CV_8UC1, Scalar.Black);
        using var rightImage = new Mat(8, 8, MatType.CV_8UC1, Scalar.Black);

        var frame = step.AddFrame([leftImage, rightImage]);

        Assert.AreEqual(leftLid, frame.Header.RoutineLeftLid);
        Assert.AreEqual(rightLid, frame.Header.RoutineRightLid);
    }

    [TestMethod]
    public void BinCollector_RemoveFrames_DropsOnlyRejectedAttemptFrames()
    {
        var collector = new BinCollector(0);
        using var leftImage = new Mat(8, 8, MatType.CV_8UC1, Scalar.Black);
        using var rightImage = new Mat(8, 8, MatType.CV_8UC1, Scalar.White);
        var first = collector.AddFrame(leftImage, rightImage);
        var rejected = collector.AddFrame(leftImage, rightImage);
        var last = collector.AddFrame(leftImage, rightImage);
        var path = Path.Combine(Path.GetTempPath(), $"baballonia-attempt-{Guid.NewGuid():N}.bin");

        try
        {
            collector.RemoveFrames([rejected]);
            collector.WriteBin(path);
            var frames = Baballonia.CaptureBin.IO.CaptureBin.ReadAll(path);

            Assert.AreEqual(2, frames.Count);
            Assert.AreEqual(first.LeftJpeg.Length, frames[0].LeftJpeg.Length);
            Assert.AreEqual(last.RightJpeg.Length, frames[1].RightJpeg.Length);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
