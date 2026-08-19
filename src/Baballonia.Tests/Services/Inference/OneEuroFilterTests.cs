using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Filters;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class OneEuroFilterTests
{
    [TestMethod]
    public void LidRemainsImmediateWhileGazeAndExpressionAreSmoothed()
    {
        string[] keys = ["/leftEyeX", "/leftEyeLid", "/leftEyeWiden"];
        var values = new OrderedFloatMap(keys);
        values["/leftEyeX"] = 0;
        values["/leftEyeLid"] = 0;
        values["/leftEyeWiden"] = 0;
        var filter = new OneEuroFilter(
            minCutoff: 0.1f,
            beta: 0,
            bypassForKey: key => key.EndsWith("EyeLid"));
        filter.Filter(values);

        values["/leftEyeX"] = 1;
        values["/leftEyeLid"] = 1;
        values["/leftEyeWiden"] = 1;
        var filtered = filter.Filter(values);

        Assert.IsLessThan(1.0f, filtered["/leftEyeX"]);
        Assert.AreEqual(1.0f, filtered["/leftEyeLid"], 0.0001f);
        Assert.IsLessThan(1.0f, filtered["/leftEyeWiden"]);
    }

    [TestMethod]
    public void CoveredPupilDoesNotPolluteGazeStateAndReopensSmoothly()
    {
        string[] keys = ["/leftEyeX", "/leftEyeLid"];
        var values = new OrderedFloatMap(keys);
        values["/leftEyeX"] = 0.2f;
        values["/leftEyeLid"] = 0.0f;
        var filter = new OneEuroFilter(
            minCutoff: 0.5f,
            beta: 3.0f,
            bypassForKey: key => key.EndsWith("EyeLid"),
            freezeForKey: (key, map) => key == "/leftEyeX" && map["/leftEyeLid"] > 0.08f);
        filter.Filter(values);

        // The invisible pupil repeatedly reports the opposite side. None of it may enter the
        // filter state while the lid is closed.
        values["/leftEyeLid"] = 1.0f;
        values["/leftEyeX"] = 0.95f;
        for (var i = 0; i < 20; i++)
            Assert.AreEqual(0.2f, filter.Filter(values)["/leftEyeX"], 0.0001f);

        // On reopening the first visible estimate must approach the new position without jumping
        // straight to it, even with a high speed/beta setting.
        values["/leftEyeLid"] = 0.0f;
        values["/leftEyeX"] = 0.3f;
        var reopened = filter.Filter(values)["/leftEyeX"];
        Assert.IsTrue(reopened >= 0.1999f);
        Assert.IsTrue(reopened < 0.3f);
    }

    [TestMethod]
    public void ClosedEyeNeverFreezesOtherEyesGazeFilter()
    {
        string[] keys =
        [
            "/leftEyeX", "/leftEyeLid",
            "/rightEyeX", "/rightEyeLid"
        ];
        var values = new OrderedFloatMap(keys);
        values["/leftEyeX"] = 0.2f;
        values["/leftEyeLid"] = 0.0f;
        values["/rightEyeX"] = 0.8f;
        values["/rightEyeLid"] = 0.0f;
        var filter = new OneEuroFilter(
            minCutoff: 1000.0f,
            beta: 0.0f,
            bypassForKey: key => key.EndsWith("EyeLid"),
            freezeForKey: (key, map) =>
                key.StartsWith("/leftEye")
                    ? map["/leftEyeLid"] > 0.08f
                    : key.StartsWith("/rightEye") && map["/rightEyeLid"] > 0.08f);
        filter.Filter(values);

        values["/leftEyeLid"] = 1.0f;
        values["/leftEyeX"] = 0.95f;
        values["/rightEyeX"] = 0.6f;
        var wink = filter.Filter(values);

        Assert.AreEqual(0.2f, wink["/leftEyeX"], 0.0001f);
        Assert.IsTrue(wink["/rightEyeX"] < 0.8f);
    }

    [TestMethod]
    public void OptionalMaximumDeltaLimitsOnlyLargeFilteredGazeChanges()
    {
        string[] keys = ["/leftEyeX", "/leftEyeLid"];
        var values = new OrderedFloatMap(keys);
        values["/leftEyeX"] = 0.2f;
        values["/leftEyeLid"] = 0.0f;
        var filter = new OneEuroFilter(
            minCutoff: 1000.0f,
            beta: 0.0f,
            bypassForKey: key => key.EndsWith("EyeLid", StringComparison.Ordinal),
            maximumDeltaForKey: (key, _) => key.EndsWith("EyeX", StringComparison.Ordinal)
                ? 0.06f
                : null);
        filter.Filter(values);

        Thread.Sleep(2);
        values["/leftEyeX"] = 0.9f;
        var previous = 0.2f;
        for (var frame = 0; frame < 4; frame++)
        {
            var current = filter.Filter(values)["/leftEyeX"];
            Assert.IsTrue(current > previous);
            Assert.IsTrue(current - previous <= 0.0601f);
            previous = current;
            Thread.Sleep(1);
        }

        // A small change below the configured ceiling remains responsive and is not quantized to
        // the maximum step.
        values["/leftEyeX"] = previous + 0.01f;
        Thread.Sleep(2);
        var smallChange = filter.Filter(values)["/leftEyeX"];
        Assert.IsTrue(smallChange > previous);
        Assert.IsTrue(smallChange - previous < 0.06f);
    }

}
