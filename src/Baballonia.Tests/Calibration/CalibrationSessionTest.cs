using Baballonia.CaptureBin.IO;
using Baballonia.Contracts;
using Baballonia.Desktop.Calibration;
using Baballonia.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Tests.Calibration;

[TestClass]
public class CalibrationSessionTest
{
    [TestMethod]
    public void FullCalibration_UsesCapturedConvergenceAndExplicitExpressionValidity()
    {
        var bus = Mock.Of<IEyePipelineEventBus>();
        var calibration = new EyeCalibration(
            new EyeCaptureStepFactory(bus), Mock.Of<ITrainerService>(), bus);
        var steps = calibration.BasicAllCalibration().ToArray();

        var convergence = (PositionalAwareCaptureStep)steps.Single(step => step.Name == "convergence");
        Assert.AreNotEqual(0u, convergence.Flags & CaptureFlags.FLAG_CONVERGENCE);
        Assert.AreNotEqual(0u, convergence.Flags & CaptureFlags.FLAG_LID_LABELS_VALID);
        CollectionAssert.DoesNotContain(steps.Select(step => step.Name).ToArray(), "gazeexpr");

        foreach (var name in new[] { "neutralstart", "guidedgaze", "convergence",
                     "blink", "winkleft", "winkright", "widen", "squint", "brow", "neutralend" })
        {
            var step = steps.Single(item => item.Name == name);
            var flags = step switch
            {
                BaseCaptureStep capture => capture.Flags,
                PositionalAwareCaptureStep capture => capture.Flags,
                _ => 0u,
            };
            Assert.AreNotEqual(0u, flags & CaptureFlags.FLAG_LID_LABELS_VALID, name);
        }

        var rangeCapture = (PositionalAwareCaptureStep)steps.Single(item => item.Name == "gaze");
        Assert.AreEqual(0u, rangeCapture.Flags & CaptureFlags.FLAG_LID_LABELS_VALID,
            "The long range measurement permits natural blinks and must not supervise lids.");

        foreach (var name in new[] { "widen", "squint" })
        {
            var step = (PositionalAwareCaptureStep)steps.Single(item => item.Name == name);
            Assert.AreNotEqual(0u, step.Flags & CaptureFlags.FLAG_EYE_SHAPE_LABELS_VALID, name);
        }
        var brow = (PositionalAwareCaptureStep)steps.Single(item => item.Name == "brow");
        Assert.AreNotEqual(0u, brow.Flags & CaptureFlags.FLAG_BROW_LABELS_VALID);

        var resultIndex = Array.FindIndex(steps, step => step.Name == "results");
        var closeIndex = Array.FindIndex(steps, step => step.Name == "close");
        Assert.IsTrue(resultIndex > 0, "The VR result summary is missing.");
        Assert.IsTrue(closeIndex > resultIndex,
            "The overlay must remain open until the result summary has been shown.");
    }

    [TestMethod]
    public async Task MergeBins_UsesOnlyNamedSessionInputsAndReplacesOldResult()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BaballoniaTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Baballonia.CaptureBin.IO.CaptureBin.WriteAll(Path.Combine(directory, "first.bin"), [CreateFrame(1)]);
            Baballonia.CaptureBin.IO.CaptureBin.WriteAll(Path.Combine(directory, "second.bin"), [CreateFrame(2)]);
            Baballonia.CaptureBin.IO.CaptureBin.WriteAll(Path.Combine(directory, "unrelated.bin"), [CreateFrame(99)]);
            Baballonia.CaptureBin.IO.CaptureBin.WriteAll(Path.Combine(directory, "user_cal.bin"), [CreateFrame(100)]);

            var step = new MergeBinsStep(directory, "first.bin", "second.bin");
            await step.ExecuteAsync(null!, CancellationToken.None);

            var frames = Baballonia.CaptureBin.IO.CaptureBin.ReadAll(Path.Combine(directory, "user_cal.bin"));
            CollectionAssert.AreEqual(new byte[] { 1, 2 }, frames.Select(frame => frame.LeftJpeg.Single()).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Frame CreateFrame(byte marker) => new()
    {
        LeftJpeg = [marker],
        RightJpeg = [marker],
    };
}
