using Baballonia.Desktop.Calibration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Baballonia.Tests.Calibration;

[TestClass]
public class CalibrationCaptureQualityTests
{
    private static readonly string[] Keys =
    [
        "leftEyeLid", "rightEyeLid",
        "leftEyeWiden", "rightEyeWiden",
        "leftEyeSquint", "rightEyeSquint",
        "leftEyeBrow", "rightEyeBrow",
        "eyeConvergence",
    ];

    [TestMethod]
    public void WinkRules_RequireOnlyTheRequestedLidToClose()
    {
        var profile = NeutralProfile();
        var leftWink = Tracker(profile, CalibrationExpressionKind.WinkLeft);
        var rightWink = Tracker(profile, CalibrationExpressionKind.WinkRight);

        var leftOnly = Values(leftLid: 0.55f, rightLid: 0.10f);
        var rightOnly = Values(leftLid: 0.10f, rightLid: 0.55f);
        var bothClosed = Values(leftLid: 0.55f, rightLid: 0.55f);

        Assert.IsTrue(leftWink.MatchesExpression(leftOnly));
        Assert.IsFalse(leftWink.MatchesExpression(rightOnly));
        Assert.IsFalse(leftWink.MatchesExpression(bothClosed));
        Assert.IsTrue(rightWink.MatchesExpression(rightOnly));
        Assert.IsFalse(rightWink.MatchesExpression(leftOnly));
        Assert.IsFalse(rightWink.MatchesExpression(bothClosed));
    }

    [TestMethod]
    public void WidenRule_RejectsClosedEyes()
    {
        var tracker = Tracker(NeutralProfile(), CalibrationExpressionKind.Widen);

        Assert.IsTrue(tracker.MatchesExpression(Values(
            leftLid: 0.10f, rightLid: 0.10f, leftWiden: 0.40f, rightWiden: 0.40f)));
        Assert.IsFalse(tracker.MatchesExpression(Values(
            leftLid: 0.55f, rightLid: 0.55f, leftWiden: 0.40f, rightWiden: 0.40f)));
    }

    [TestMethod]
    public void ConvergenceRule_UsesPersonalNeutralBaseline()
    {
        var tracker = Tracker(NeutralProfile(), CalibrationExpressionKind.Convergence);

        Assert.IsFalse(tracker.MatchesExpression(Values(convergence: 0.15f)));
        Assert.IsTrue(tracker.MatchesExpression(Values(convergence: 0.40f)));
    }

    [TestMethod]
    public void QualityRules_AcceptRuntimeNamesWithLeadingSlash()
    {
        var profile = new CalibrationQualityProfile();
        for (var i = 0; i < 12; i++)
            profile.ObserveNeutral(SlashValues(0.10f, 0.10f));
        var tracker = Tracker(profile, CalibrationExpressionKind.WinkRight);

        Assert.IsTrue(tracker.MatchesExpression(SlashValues(0.10f, 0.55f)));
    }

    [TestMethod]
    public void HardWidenRule_KeepsOpenEyeFramesTheOldModelScoresWeakly()
    {
        var tracker = Tracker(NeutralProfile(), CalibrationExpressionKind.Widen);
        var weakButOpen = Values(
            leftLid: 0.11f, rightLid: 0.12f,
            leftWiden: 0.11f, rightWiden: 0.10f);

        Assert.IsFalse(tracker.MatchesExpression(weakButOpen));
        Assert.IsTrue(tracker.MatchesHardExample(weakButOpen));
    }

    [TestMethod]
    public void HardBlinkRule_KeepsPartialBilateralClosureButRejectsNeutralEyes()
    {
        var tracker = Tracker(NeutralProfile(), CalibrationExpressionKind.Blink);

        Assert.IsTrue(tracker.MatchesHardExample(Values(
            leftLid: 0.17f, rightLid: 0.18f)));
        Assert.IsFalse(tracker.MatchesHardExample(Values()));
    }

    [TestMethod]
    public void BlinkDecision_CapturesNaturalClosingTransitionBeforeStrictConfirmation()
    {
        var tracker = new CalibrationQualityTracker("blink", NeutralProfile(),
            new CalibrationQualityOptions(
                CalibrationExpressionKind.Blink, 10, TimeSpan.Zero, TimeSpan.FromSeconds(1),
                ConsecutiveFrames: 3, MinimumFrameSpacing: TimeSpan.Zero,
                HardExampleFrames: 5));
        tracker.StartAttempt();

        var decision = tracker.Observe(Values(leftLid: 0.17f, rightLid: 0.18f));

        Assert.AreEqual(CalibrationFrameDecision.HardExample, decision);
    }

    [TestMethod]
    public void WidenDecision_WaitsForStrictPoseBeforeCapturingWeakExamples()
    {
        var tracker = new CalibrationQualityTracker("widen", NeutralProfile(),
            new CalibrationQualityOptions(
                CalibrationExpressionKind.Widen, 10, TimeSpan.Zero, TimeSpan.FromSeconds(1),
                ConsecutiveFrames: 3, MinimumFrameSpacing: TimeSpan.Zero,
                HardExampleFrames: 5));
        tracker.StartAttempt();
        var weak = Values(leftLid: 0.11f, rightLid: 0.12f);
        var strict = Values(
            leftLid: 0.10f, rightLid: 0.10f, leftWiden: 0.40f, rightWiden: 0.40f);

        Assert.AreEqual(CalibrationFrameDecision.Ignore, tracker.Observe(weak));
        tracker.Observe(strict);
        tracker.Observe(strict);
        Assert.AreEqual(CalibrationFrameDecision.Accepted, tracker.Observe(strict));
        Assert.AreEqual(CalibrationFrameDecision.HardExample, tracker.Observe(weak));
    }

    [TestMethod]
    public void BestEffortAcceptance_EndsAnOtherwiseIncompleteLiveCheck()
    {
        var tracker = new CalibrationQualityTracker("widen", NeutralProfile(),
            new CalibrationQualityOptions(
                CalibrationExpressionKind.Widen, 100, TimeSpan.FromSeconds(14),
                TimeSpan.FromSeconds(40)));

        tracker.StartAttempt();
        Assert.IsFalse(tracker.IsComplete);

        tracker.AcceptBestEffort();

        Assert.IsTrue(tracker.IsComplete);
    }

    [TestMethod]
    public void WinkQuality_DoesNotRetryBasedOnTheModelThatNeedsReplacing()
    {
        var profile = NeutralProfile();

        Assert.IsFalse(Tracker(profile, CalibrationExpressionKind.WinkLeft)
            .RequiresAdvisoryRetry);
        Assert.IsFalse(Tracker(profile, CalibrationExpressionKind.WinkRight)
            .RequiresAdvisoryRetry);
        Assert.IsTrue(Tracker(profile, CalibrationExpressionKind.Blink)
            .RequiresAdvisoryRetry);
        Assert.IsTrue(Tracker(profile, CalibrationExpressionKind.Widen)
            .RequiresAdvisoryRetry);
    }

    private static CalibrationQualityProfile NeutralProfile()
    {
        var profile = new CalibrationQualityProfile();
        for (var i = 0; i < 12; i++)
            profile.ObserveNeutral(Values());
        return profile;
    }

    private static CalibrationQualityTracker Tracker(
        CalibrationQualityProfile profile, CalibrationExpressionKind kind) =>
        new("test", profile, new CalibrationQualityOptions(
            kind, 10, TimeSpan.Zero, TimeSpan.FromSeconds(1)));

    private static OrderedFloatMap Values(
        float leftLid = 0.10f,
        float rightLid = 0.10f,
        float leftWiden = 0.10f,
        float rightWiden = 0.10f,
        float leftSquint = 0.10f,
        float rightSquint = 0.10f,
        float leftBrow = 0.05f,
        float rightBrow = 0.05f,
        float convergence = 0.05f)
    {
        var values = new OrderedFloatMap(Keys);
        values["leftEyeLid"] = leftLid;
        values["rightEyeLid"] = rightLid;
        values["leftEyeWiden"] = leftWiden;
        values["rightEyeWiden"] = rightWiden;
        values["leftEyeSquint"] = leftSquint;
        values["rightEyeSquint"] = rightSquint;
        values["leftEyeBrow"] = leftBrow;
        values["rightEyeBrow"] = rightBrow;
        values["eyeConvergence"] = convergence;
        return values;
    }

    private static OrderedFloatMap SlashValues(float leftLid, float rightLid)
    {
        var values = new OrderedFloatMap(["/leftEyeLid", "/rightEyeLid"]);
        values["/leftEyeLid"] = leftLid;
        values["/rightEyeLid"] = rightLid;
        return values;
    }
}
