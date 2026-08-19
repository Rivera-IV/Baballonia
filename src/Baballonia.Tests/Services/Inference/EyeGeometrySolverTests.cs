using Baballonia.Contracts;
using Baballonia.Services.Inference.Geometry;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class EyeGeometrySolverTests
{
    private static readonly EyeGeometryCalibration Calibration = new(
        NeutralX: 64,
        NeutralY: 64,
        ProjectedRadiusX: 40,
        ProjectedRadiusY: 40,
        MaximumYawDegrees: 45,
        MaximumPitchDegrees: 45);

    [TestMethod]
    public void NeutralPupilProducesForwardUnitVector()
    {
        var result = EyeGeometrySolver.Solve(
            new PupilObservation(64, 64, 8, 8, 1, 1), Calibration);

        Assert.IsTrue(result.Valid);
        Assert.AreEqual(0, result.YawDegrees, 0.001);
        Assert.AreEqual(0, result.PitchDegrees, 0.001);
        Assert.AreEqual(0, result.DirectionX, 0.001);
        Assert.AreEqual(0, result.DirectionY, 0.001);
        Assert.AreEqual(1, result.DirectionZ, 0.001);
    }

    [TestMethod]
    public void ProjectedEdgeMapsToConfiguredAngularLimit()
    {
        var result = EyeGeometrySolver.Solve(
            new PupilObservation(104, 24, 8, 8, 1, 0.9f), Calibration);

        Assert.IsTrue(result.Valid);
        Assert.AreEqual(45, result.YawDegrees, 0.01);
        Assert.AreEqual(45, result.PitchDegrees, 0.01);
    }

    [TestMethod]
    public void UnreliablePupilDoesNotProduceGuessedGaze()
    {
        var result = EyeGeometrySolver.Solve(
            new PupilObservation(100, 64, 8, 8, 0.1f, 0.2f), Calibration);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual(0, result.Confidence);
    }

    [TestMethod]
    public void BeyondVisibleRangeIsClampedWithoutDivergence()
    {
        var result = EyeGeometrySolver.Solve(
            new PupilObservation(200, 64, 8, 8, 1, 1), Calibration);

        Assert.IsTrue(result.Valid);
        Assert.AreEqual(45, result.YawDegrees, 0.01);
        Assert.IsGreaterThanOrEqualTo(0, result.DirectionX);
        Assert.IsGreaterThanOrEqualTo(0, result.DirectionZ);
    }
}
