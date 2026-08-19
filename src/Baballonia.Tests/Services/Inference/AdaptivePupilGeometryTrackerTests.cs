using Baballonia.Services;
using Baballonia.Services.events;
using Baballonia.Services.Inference.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using System;
using System.IO;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class AdaptivePupilGeometryTrackerTests
{
    [TestMethod]
    public void DetectsCompactDarkPupilAndRejectsFeaturelessFrame()
    {
        var bus = new EyePipelineEventBus();
        using var tracker = new AdaptivePupilGeometryTracker(bus, calibrationPath: string.Empty);
        using var eye = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(175));
        Cv2.Ellipse(eye, new Point(78, 54), new Size(11, 9), 0, 0, 360,
            Scalar.All(18), -1);
        using var stereo = new Mat();
        Cv2.Merge([eye, eye], stereo);

        var observation = tracker.Observe(stereo).LeftPupil;

        Assert.IsTrue(observation.IsReliable);
        Assert.AreEqual(78, observation.CenterX, 2);
        Assert.AreEqual(54, observation.CenterY, 2);

        using var blank = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(100));
        using var blankStereo = new Mat();
        Cv2.Merge([blank, blank], blankStereo);
        Assert.IsFalse(tracker.Observe(blankStereo).LeftPupil.IsReliable);
    }

    [TestMethod]
    public void LearnsNeutralButRemainsIndependentFromLegacyModel()
    {
        var bus = new EyePipelineEventBus();
        using var tracker = new AdaptivePupilGeometryTracker(bus, calibrationPath: string.Empty);
        using var left = CreateEye(58, 64);
        using var right = CreateEye(70, 64);
        using var stereo = new Mat();
        Cv2.Merge([left, right], stereo);

        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "neutralstart", CaptureEnabled: true));
        for (var index = 0; index < 20; index++)
            tracker.Observe(stereo);
        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "gaze", CaptureEnabled: true));
        var state = tracker.Observe(stereo);

        Assert.IsTrue(state.LeftGaze.Valid);
        Assert.IsTrue(state.RightGaze.Valid);
        Assert.AreEqual(0, state.LeftGaze.YawDegrees, 0.5);
        Assert.AreEqual(0, state.RightGaze.YawDegrees, 0.5);
    }

    [TestMethod]
    public void PersistsCompletedCameraGeometryForTheNextApplicationStart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"baballonia-geometry-{Guid.NewGuid():N}.json");
        try
        {
            var bus = new EyePipelineEventBus();
            using (var tracker = new AdaptivePupilGeometryTracker(bus, path))
            {
                bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
                    "gaze", CaptureEnabled: true));
                for (var pitch = -30; pitch <= 30; pitch += 6)
                for (var yaw = -30; yaw <= 30; yaw += 6)
                {
                    bus.Publish(new EyePipelineEvents.CalibrationTargetChangedEvent(
                        pitch, yaw, 2, pitch, yaw, pitch, yaw));
                    using var eye = CreateEye(
                        64 + (int)(yaw * 0.6f),
                        64 + (int)(pitch * 0.6f));
                    using var stereo = new Mat();
                    Cv2.Merge([eye, eye], stereo);
                    // The production tracker samples every third camera frame.
                    tracker.Observe(stereo);
                    tracker.Observe(stereo);
                    tracker.Observe(stereo);
                }
            }

            Assert.IsTrue(File.Exists(path));
            var restoredBus = new EyePipelineEventBus();
            using var restored = new AdaptivePupilGeometryTracker(restoredBus, path);
            using var centred = CreateEye(64, 64);
            using var centredStereo = new Mat();
            Cv2.Merge([centred, centred], centredStereo);
            var state = restored.Observe(centredStereo);

            Assert.IsTrue(state.LeftCalibration.Ready);
            Assert.IsTrue(state.RightCalibration.Ready);
            Assert.IsTrue(state.LeftGaze.Valid);
            Assert.IsTrue(state.RightGaze.Valid);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".tmp");
        }
    }

    private static Mat CreateEye(int x, int y)
    {
        var eye = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(175));
        Cv2.Ellipse(eye, new Point(x, y), new Size(10, 8), 0, 0, 360,
            Scalar.All(15), -1);
        return eye;
    }
}
