using Baballonia.Contracts;
using Baballonia.Services.Inference.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class EyeGeometryCalibrationAccumulatorTests
{
    [TestMethod]
    public void LearnsRotatedCameraMappingAndRejectsSparseOutliers()
    {
        var accumulator = new EyeGeometryCalibrationAccumulator();
        var index = 0;
        for (var pitch = -30; pitch <= 30; pitch += 6)
        for (var yaw = -30; yaw <= 30; yaw += 6)
        {
            var x = 64 + 0.60f * yaw + 0.15f * pitch;
            var y = 64 - 0.10f * yaw + 0.55f * pitch;
            if (++index % 17 == 0)
            {
                x += 30;
                y -= 25;
            }
            accumulator.Add(new PupilObservation(x, y, 8, 8, 1, 1), pitch, yaw);
        }
        // Reach a fit boundary after the 121 grid samples.
        for (var extra = 0; extra < 14; extra++)
            accumulator.Add(new PupilObservation(64, 64, 8, 8, 1, 1), 0, 0);

        Assert.IsTrue(accumulator.Status.Ready);
        Assert.IsGreaterThan(100, accumulator.Status.SampleCount);
        Assert.IsLessThan(2.0f, accumulator.Status.RootMeanSquareErrorDegrees);

        // Readiness must not flicker off between the periodic robust refits.
        accumulator.Add(new PupilObservation(64, 64, 8, 8, 1, 1), 0, 0);
        Assert.IsTrue(accumulator.Status.Ready);

        const float expectedPitch = 18;
        const float expectedYaw = -24;
        var pupil = new PupilObservation(
            64 + 0.60f * expectedYaw + 0.15f * expectedPitch,
            64 - 0.10f * expectedYaw + 0.55f * expectedPitch,
            8, 8, 1, 1);
        var gaze = EyeGeometrySolver.Solve(pupil, accumulator.Calibration);

        Assert.IsTrue(gaze.Valid);
        Assert.AreEqual(expectedYaw, gaze.YawDegrees, 1.0);
        Assert.AreEqual(expectedPitch, gaze.PitchDegrees, 1.0);
    }

    [TestMethod]
    public void DoesNotDeclareSuccessWithoutTwoDimensionalCoverage()
    {
        var accumulator = new EyeGeometryCalibrationAccumulator();
        for (var index = 0; index < 90; index++)
        {
            var yaw = -30 + index * 60.0f / 89;
            accumulator.Add(
                new PupilObservation(64 + yaw * 0.5f, 64, 8, 8, 1, 1),
                pitchDegrees: 0,
                yawDegrees: yaw);
        }

        Assert.IsFalse(accumulator.Status.Ready);
    }
}
