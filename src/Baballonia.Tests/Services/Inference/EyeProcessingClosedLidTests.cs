using System.Reflection;
using System;
using Baballonia.Services;
using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Backends;
using Baballonia.Services.Inference.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class EyeProcessingClosedLidTests
{
    [TestMethod]
    public void ClosedEyeSuppressesItsExpressionsAndLeavesOpenEyeGazeUnchanged()
    {
        using var pipeline = CreatePipeline();
        OrderedFloatMap? values = null;
        for (var frame = 0; frame < 16; frame++)
        {
            values = CreateExpressions(
                leftX: 0.15f, rightX: 0.8f,
                leftClosure: 1.0f, rightClosure: 0.0f,
                convergence: 1.0f,
                leftWiden: 0.9f, rightWiden: 0.75f,
                leftSquint: 0.8f, rightSquint: 0.6f);
            Process(pipeline, values);
        }
        Assert.IsNotNull(values);

        Assert.AreEqual(0.6f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.6f, values["/rightEyeX"], 0.0001f);
        Assert.AreEqual(0.0f, values["/leftEyeWiden"], 0.0001f);
        Assert.AreEqual(0.0f, values["/leftEyeSquint"], 0.0001f);
        Assert.AreEqual(0.15f, values["/rightEyeWiden"], 0.0001f);
        Assert.AreEqual(0.0f, values["/rightEyeSquint"], 0.0001f);
    }

    [TestMethod]
    public void RightClosedEyeSuppressesItsExpressionsAndLeavesLeftEyeGazeUnchanged()
    {
        using var pipeline = CreatePipeline();
        OrderedFloatMap? values = null;
        for (var frame = 0; frame < 16; frame++)
        {
            values = CreateExpressions(
                leftX: 0.2f, rightX: 0.85f,
                leftClosure: 0.0f, rightClosure: 1.0f,
                convergence: 1.0f,
                leftWiden: 0.7f, rightWiden: 0.9f,
                leftSquint: 0.5f, rightSquint: 0.8f);
            Process(pipeline, values);
        }
        Assert.IsNotNull(values);

        Assert.AreEqual(-0.6f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.6f, values["/rightEyeX"], 0.0001f);
        Assert.AreEqual(0.2f, values["/leftEyeWiden"], 0.0001f);
        Assert.AreEqual(0.0f, values["/leftEyeSquint"], 0.0001f);
        Assert.AreEqual(0.0f, values["/rightEyeWiden"], 0.0001f);
        Assert.AreEqual(0.0f, values["/rightEyeSquint"], 0.0001f);
    }

    [TestMethod]
    public void BothOpenEyesStillApplyTrainedConvergenceAndSymmetricExpressions()
    {
        using var pipeline = CreatePipeline();
        OrderedFloatMap? values = null;
        for (var frame = 0; frame < 14; frame++)
        {
            values = CreateExpressions(
                leftX: 0.7f,
                rightX: 0.3f,
                leftClosure: 0.0f,
                rightClosure: 0.0f,
                convergence: 1.0f,
                leftWiden: 0.2f,
                rightWiden: 0.4f,
                leftSquint: 0.3f,
                rightSquint: 0.7f);
            Process(pipeline, values);
        }
        Assert.IsNotNull(values);

        Assert.AreEqual(1.0f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-1.0f, values["/rightEyeX"], 0.0001f);
        Assert.AreEqual(0.0f, values["/leftEyeWiden"], 0.0001f);
        Assert.AreEqual(0.0f, values["/rightEyeWiden"], 0.0001f);
        Assert.AreEqual(0.2f, values["/leftEyeSquint"], 0.0001f);
        Assert.AreEqual(0.2f, values["/rightEyeSquint"], 0.0001f);
    }

    [TestMethod]
    public void NeutralConvergenceClampsPhysicallyImpossibleIndependentGaze()
    {
        var values = CreateExpressions(
            leftX: 0.2f,
            rightX: 0.75f,
            leftClosure: 0.0f,
            rightClosure: 0.0f,
            convergence: 0.0f,
            leftWiden: 0.0f,
            rightWiden: 0.0f,
            leftSquint: 0.0f,
            rightSquint: 0.0f);

        Process(values);

        Assert.AreEqual(-0.09f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.01f, values["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void PartialConvergenceBlendsEachEyeFromItsOwnGazeValue()
    {
        // A model value of 0.66 maps to exactly 0.5 after the convergence dead zone. Smoothstep
        // also remains exactly 0.5 at its midpoint.
        using var pipeline = CreatePipeline();
        OrderedFloatMap? values = null;
        for (var frame = 0; frame < 10; frame++)
        {
            values = CreateExpressions(
                leftX: 0.7f,
                rightX: 0.3f,
                leftClosure: 0.0f,
                rightClosure: 0.0f,
                convergence: 0.66f,
                leftWiden: 0.0f,
                rightWiden: 0.0f,
                leftSquint: 0.0f,
                rightSquint: 0.0f);
            Process(pipeline, values);
        }
        Assert.IsNotNull(values);

        Assert.AreEqual(0.52f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.52f, values["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void LowTrainedConvergenceDoesNotEnterCrossEyeMode()
    {
        using var pipeline = CreatePipeline();
        OrderedFloatMap? values = null;
        for (var frame = 0; frame < 8; frame++)
        {
            values = CreateExpressions(
                leftX: 0.8f,
                rightX: 0.2f,
                leftClosure: 0.0f,
                rightClosure: 0.0f,
                convergence: 0.31f,
                leftWiden: 0.0f,
                rightWiden: 0.0f,
                leftSquint: 0.0f,
                rightSquint: 0.0f);
            Process(pipeline, values);
        }
        Assert.IsNotNull(values);

        Assert.AreEqual(0.04f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.04f, values["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void SingleConvergenceSpikeDoesNotEnterCrossEyeMode()
    {
        using var pipeline = CreatePipeline();
        var neutral = CreateExpressions(0.8f, 0.2f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, neutral);

        var spike = CreateExpressions(0.8f, 0.2f, 0, 0, 1, 0, 0, 0, 0);
        Process(pipeline, spike);

        Assert.AreEqual(0.04f, spike["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.04f, spike["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void SustainedHighConvergenceWithoutInwardGazeEvidenceDoesNotActivate()
    {
        using var pipeline = CreatePipeline();
        OrderedFloatMap? values = null;
        for (var frame = 0; frame < 12; frame++)
        {
            values = CreateExpressions(0.55f, 0.5f, 0, 0, 1, 0, 0, 0, 0);
            Process(pipeline, values);
        }
        Assert.IsNotNull(values);

        Assert.AreEqual(0.09f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.01f, values["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void MissingInwardGazeEvidenceReleasesStaleConvergenceHead()
    {
        using var pipeline = CreatePipeline();
        for (var frame = 0; frame < 8; frame++)
        {
            var converged = CreateExpressions(0.8f, 0.2f, 0, 0, 1, 0, 0, 0, 0);
            Process(pipeline, converged);
        }

        OrderedFloatMap? normal = null;
        for (var frame = 0; frame < 14; frame++)
        {
            // Simulate the measured failure: the trained head remains high after the pupils have
            // returned to an ordinary parallel direction.
            normal = CreateExpressions(0.55f, 0.5f, 0, 0, 1, 0, 0, 0, 0);
            Process(pipeline, normal);
        }
        Assert.IsNotNull(normal);

        Assert.AreEqual(0.09f, normal["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.01f, normal["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void WinkCandidateDisablesConvergenceAndLeavesVisibleEyeLive()
    {
        using var pipeline = CreatePipeline();
        OrderedFloatMap? converged = null;
        for (var frame = 0; frame < 8; frame++)
        {
            converged = CreateExpressions(0.8f, 0.2f, 0, 0, 1, 0, 0, 0, 0);
            Process(pipeline, converged);
        }
        Assert.IsNotNull(converged);

        var leftClosing = CreateExpressions(0.95f, 0.8f, 1, 0, 1, 0, 0, 0, 0);
        Process(pipeline, leftClosing);

        // The closed pupil is held during wink confirmation. The visible right pupil immediately
        // returns to its own gaze and is never modified by convergence or a blink guard.
        Assert.AreEqual(0.6f, leftClosing["/rightEyeX"], 0.0001f);
        Assert.AreEqual(converged["/leftEyeX"], leftClosing["/leftEyeX"], 0.0001f);
    }

    [TestMethod]
    public void UnconfirmedCoveredEyeKeepsLastReliableGaze()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(
            leftX: 0.2f,
            rightX: 0.8f,
            leftClosure: 0.0f,
            rightClosure: 0.0f,
            convergence: 0.0f,
            leftWiden: 0.0f,
            rightWiden: 0.0f,
            leftSquint: 0.0f,
            rightSquint: 0.0f);
        Process(pipeline, open);

        var values = CreateExpressions(
            leftX: 0.95f,
            rightX: 0.75f,
            leftClosure: 0.6f,
            rightClosure: 0.0f,
            convergence: 0.0f,
            leftWiden: 0.8f,
            rightWiden: 0.8f,
            leftSquint: 0.0f,
            rightSquint: 0.0f);
        Process(pipeline, values);

        Assert.AreEqual(open["/leftEyeX"], values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.5f, values["/rightEyeX"], 0.0001f);
        Assert.IsTrue(values["/leftEyeWiden"] < 0.4f);
        Assert.AreEqual(0.8f, values["/rightEyeWiden"], 0.0001f);
    }

    [TestMethod]
    public void BilateralBlinkHoldsBothPupilsUntilTheyAreVisibleAgain()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        var blink = CreateExpressions(0.9f, 0.1f, 1, 1, 1, 0, 0, 0, 0);
        Process(pipeline, blink);

        Assert.AreEqual(open["/leftEyeX"], blink["/leftEyeX"], 0.0001f);
        Assert.AreEqual(open["/rightEyeX"], blink["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void ClosingLidImmediatelyHoldsPupilInsteadOfPullingItTowardsCentre()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        // The model gaze changes strongly on this partially covered frame. Closing must retain
        // the preceding visible position; blending is reserved for the reopening direction.
        var closing = CreateExpressions(0.9f, 0.1f, 0.6f, 0.6f, 1, 0, 0, 0, 0);
        Process(pipeline, closing);

        Assert.AreEqual(open["/leftEyeX"], closing["/leftEyeX"], 0.0001f);
        Assert.AreEqual(open["/rightEyeX"], closing["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void SmallLidMovementDoesNotFreezeOtherwiseVisiblePupils()
    {
        using var pipeline = CreatePipeline();
        var relaxed = CreateExpressions(0.2f, 0.8f, 0.4f, 0.4f, 0, 0, 0, 0, 0);
        Process(pipeline, relaxed);

        // A tiny lid change is not proof that either pupil is covered. Treating it as occlusion was
        // one source of overlapping modes and prevented valid gaze while squinting.
        var lidMovement = CreateExpressions(0.55f, 0.45f, 0.42f, 0.42f, 0, 0, 0, 0, 0);
        Process(pipeline, lidMovement);

        Assert.AreEqual(0.04f, lidMovement["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.04f, lidMovement["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void WinkUsesOpenPupilDirectionForCoveredEye()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        OrderedFloatMap? wink = null;
        for (var frame = 0; frame < 16; frame++)
        {
            wink = CreateExpressions(0.9f, 0.9f, 1, 0, 1, 0, 0, 0, 0);
            Process(pipeline, wink);
        }
        Assert.IsNotNull(wink);

        Assert.AreEqual(0.8f, wink["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.8f, wink["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void WinkCopiesVisibleEyesHorizontalAndVerticalCoordinates()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        open["/leftEyeY"] = 0.2f;
        open["/rightEyeY"] = 0.8f;
        Process(pipeline, open);

        OrderedFloatMap? wink = null;
        for (var frame = 0; frame < 16; frame++)
        {
            wink = CreateExpressions(0.9f, 0.8f, 0.8f, 0, 0, 0, 0, 0, 0);
            wink["/leftEyeY"] = 0.1f;  // unusable covered-eye estimate
            wink["/rightEyeY"] = 0.8f; // visible-eye coordinate
            Process(pipeline, wink);
        }
        Assert.IsNotNull(wink);

        Assert.AreEqual(wink["/rightEyeX"], wink["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.6f, wink["/leftEyeY"], 0.0001f);
        Assert.AreEqual(0.6f, wink["/rightEyeY"], 0.0001f);
    }

    [TestMethod]
    public void WinkKeepsFollowingVisibleEyeUntilCoveredPupilIsReliablyOpen()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.7f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        OrderedFloatMap? wink = null;
        for (var frame = 0; frame < 16; frame++)
        {
            wink = CreateExpressions(0.95f, 0.7f, 1, 0, 0, 0, 0, 0, 0);
            Process(pipeline, wink);
        }
        Assert.IsNotNull(wink);
        Assert.AreEqual(wink["/rightEyeX"], wink["/leftEyeX"], 0.0001f);

        // The formerly covered pupil is only partly visible and still reports a bad coordinate.
        // It must continue to follow the visible eye instead of switching back early.
        var partial = CreateExpressions(0.95f, 0.65f, 0.4f, 0, 0, 0, 0, 0, 0);
        Process(pipeline, partial);
        Assert.AreEqual(partial["/rightEyeX"], partial["/leftEyeX"], 0.0001f);

        // Even the first fully-open frame remains coupled; two stable frames are required.
        var firstOpen = CreateExpressions(0.95f, 0.6f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, firstOpen);
        Assert.AreEqual(firstOpen["/rightEyeX"], firstOpen["/leftEyeX"], 0.0001f);

        var secondOpen = CreateExpressions(0.95f, 0.55f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, secondOpen);
        Assert.AreEqual(secondOpen["/rightEyeX"], secondOpen["/leftEyeX"], 0.0001f);
    }

    [TestMethod]
    public void ReopeningPupilBlendsBeforeReturningToLiveGaze()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        for (var frame = 0; frame < 16; frame++)
        {
            var closed = CreateExpressions(0.9f, 0.8f, 1, 0, 0, 0, 0, 0, 0);
            Process(pipeline, closed);
        }

        var reopening = CreateExpressions(0.9f, 0.8f, 0.5f, 0, 0, 0, 0, 0, 0);
        Process(pipeline, reopening);
        Assert.IsTrue(reopening["/leftEyeX"] > open["/leftEyeX"]);
        Assert.IsTrue(reopening["/leftEyeX"] < 0.8f);

        // The first open frame enters recovery; eight subsequent clean frames validate the model.
        for (var frame = 0; frame < 9; frame++)
        {
            var cleanOpen = CreateExpressions(0.9f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
            Process(pipeline, cleanOpen);
            Assert.AreEqual(cleanOpen["/rightEyeX"], cleanOpen["/leftEyeX"], 0.0001f);
        }

        var firstBlend = CreateExpressions(0.9f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, firstBlend);
        Assert.IsTrue(firstBlend["/leftEyeX"] > firstBlend["/rightEyeX"]);
        Assert.IsTrue(firstBlend["/leftEyeX"] < 0.8f);

        OrderedFloatMap? fullyBlended = null;
        for (var frame = 1; frame < 8; frame++)
        {
            fullyBlended = CreateExpressions(0.9f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
            Process(pipeline, fullyBlended);
        }
        Assert.IsNotNull(fullyBlended);
        Assert.AreEqual(0.8f, fullyBlended["/leftEyeX"], 0.0001f);
    }

    [TestMethod]
    public void BilateralBlinkWaitsForStableReopeningAndThenBlends()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        var closed = CreateExpressions(0.9f, 0.1f, 1, 1, 1, 0, 0, 0, 0);
        Process(pipeline, closed);

        // First fully-open frame can still contain temporal-model blink residue and remains held.
        var firstOpen = CreateExpressions(0.8f, 0.2f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, firstOpen);
        Assert.AreEqual(open["/leftEyeX"], firstOpen["/leftEyeX"], 0.0001f);
        Assert.AreEqual(open["/rightEyeX"], firstOpen["/rightEyeX"], 0.0001f);

        // Two clean frames validate reopening without changing the held coordinates.
        var secondOpen = CreateExpressions(0.8f, 0.2f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, secondOpen);
        Assert.AreEqual(open["/leftEyeX"], secondOpen["/leftEyeX"], 0.0001f);
        Assert.AreEqual(open["/rightEyeX"], secondOpen["/rightEyeX"], 0.0001f);

        var thirdOpen = CreateExpressions(0.8f, 0.2f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, thirdOpen);
        Assert.AreEqual(open["/leftEyeX"], thirdOpen["/leftEyeX"], 0.0001f);
        Assert.AreEqual(open["/rightEyeX"], thirdOpen["/rightEyeX"], 0.0001f);

        // The following frame starts one common smooth release.
        var firstBlend = CreateExpressions(0.8f, 0.2f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, firstBlend);
        Assert.IsTrue(firstBlend["/leftEyeX"] > open["/leftEyeX"]);
        Assert.IsTrue(firstBlend["/leftEyeX"] < 0.6f);
        Assert.IsTrue(firstBlend["/rightEyeX"] < open["/rightEyeX"]);
        Assert.IsTrue(firstBlend["/rightEyeX"] > -0.6f);
    }

    [TestMethod]
    public void OneFrameLidOffsetDuringBlinkDoesNotLatchWinkFollower()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        // Left leads the bilateral blink by one frame. This must remain only a candidate, not a
        // persistent wink that copies one pupil across the later reopening.
        var asymmetricOnset = CreateExpressions(0.9f, 0.8f, 1, 0, 0, 0, 0, 0, 0);
        Process(pipeline, asymmetricOnset);
        var bothClosed = CreateExpressions(0.9f, 0.1f, 1, 1, 0, 0, 0, 0, 0);
        Process(pipeline, bothClosed);

        OrderedFloatMap? recovered = null;
        for (var frame = 0; frame < 5; frame++)
        {
            recovered = CreateExpressions(0.75f, 0.25f, 0, 0, 0, 0, 0, 0, 0);
            Process(pipeline, recovered);
        }
        Assert.IsNotNull(recovered);
        Assert.AreNotEqual(recovered["/leftEyeX"], recovered["/rightEyeX"], 0.05f);
    }

    [TestMethod]
    public void BilateralBlinkBecomingConfirmedWinkReleasesVisibleEye()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.8f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        var bothClosed = CreateExpressions(0.9f, 0.1f, 1, 1, 1, 0, 0, 0, 0);
        Process(pipeline, bothClosed);

        // Left remains closed, right becomes visible and looks to a new position. Both output
        // pupils must immediately follow the newly visible right eye instead of remaining held by
        // the former bilateral-blink guard.
        OrderedFloatMap? rightReopened = null;
        for (var frame = 0; frame < 16; frame++)
        {
            rightReopened = CreateExpressions(0.1f, 0.65f, 1, 0, 0, 0, 0, 0, 0);
            Process(pipeline, rightReopened);
        }
        Assert.IsNotNull(rightReopened);

        Assert.AreEqual(0.3f, rightReopened["/rightEyeX"], 0.0001f);
        Assert.AreEqual(rightReopened["/rightEyeX"], rightReopened["/leftEyeX"], 0.0001f);
    }

    [TestMethod]
    public void NormallyOpenLidsDoNotRetainStalePupilPositions()
    {
        using var pipeline = CreatePipeline();
        // Establish a naturally lower personal resting openness (~70%). It must remain a live
        // baseline rather than being treated as permanently occluded by a global threshold.
        var initial = CreateExpressions(0.2f, 0.8f, 0.3f, 0.3f, 0, 0, 0, 0, 0);
        Process(pipeline, initial);

        var moved = CreateExpressions(0.9f, 0.1f, 0.3f, 0.3f, 0, 0, 0, 0, 0);
        Process(pipeline, moved);

        Assert.AreEqual(0.04f, moved["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.04f, moved["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void BothMissingPupilsHoldGazeEvenWhenLidHeadsClaimOpen()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        var missedBlink = CreateExpressions(
            0.95f, 0.05f, 0, 0, 0, 0, 0, 0, 0,
            leftPupilVisibility: 0.001f,
            rightPupilVisibility: 0.002f);
        Process(pipeline, missedBlink);

        Assert.AreEqual(open["/leftEyeX"], missedBlink["/leftEyeX"], 0.0001f);
        Assert.AreEqual(open["/rightEyeX"], missedBlink["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void OnePupilReturningAfterBilateralLossCannotReleaseAStaleJump()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.2f, 0.2f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);
        var heldX = open["/leftEyeX"];

        var lost = CreateExpressions(
            0.95f, 0.05f, 1, 1, 0, 0, 0, 0, 0,
            leftPupilVisibility: 0.001f,
            rightPupilVisibility: 0.001f);
        Process(pipeline, lost);

        float previousLeft = heldX;
        float previousRight = heldX;
        for (var frame = 0; frame < 24; frame++)
        {
            // Reproduce the live trace: lids report fully open, but only one pupil has crossed the
            // reliable visibility boundary. The stale hidden-eye prediction points the other way.
            var returning = CreateExpressions(
                0.9f, 0.1f, 0, 0, 0, 0, 0, 0, 0,
                leftPupilVisibility: 1.0f,
                rightPupilVisibility: 0.04f);
            Process(pipeline, returning);

            Assert.IsTrue(Math.Abs(returning["/leftEyeX"] - previousLeft) <= 0.1201f);
            Assert.IsTrue(Math.Abs(returning["/rightEyeX"] - previousRight) <= 0.1201f);
            Assert.AreEqual(returning["/leftEyeX"], returning["/rightEyeX"], 0.0001f);
            previousLeft = returning["/leftEyeX"];
            previousRight = returning["/rightEyeX"];
        }

        Assert.AreEqual(0.8f, previousLeft, 0.0001f);
        Assert.AreEqual(0.8f, previousRight, 0.0001f);
    }

    [TestMethod]
    public void OpenPupilRecoveryBlendsFromVisiblePartnerInsteadOfSnapping()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.25f, 0.25f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        // Lose the left pupil while both lid heads remain open. It follows the reliable right eye.
        var missing = CreateExpressions(
            0.95f, 0.7f, 0, 0, 0, 0, 0, 0, 0,
            leftPupilVisibility: 0.02f,
            rightPupilVisibility: 1.0f);
        Process(pipeline, missing);
        var beforeRecovery = missing["/leftEyeX"];

        var recovered = CreateExpressions(
            0.05f, 0.7f, 0, 0, 0, 0, 0, 0, 0,
            leftPupilVisibility: 0.08f,
            rightPupilVisibility: 1.0f);
        Process(pipeline, recovered);

        Assert.IsTrue(Math.Abs(recovered["/leftEyeX"] - beforeRecovery) <= 0.1201f);
        Assert.IsTrue(
            Math.Abs(recovered["/leftEyeX"] - recovered["/rightEyeX"]) <
            Math.Abs(beforeRecovery - missing["/rightEyeX"]));
    }

    [TestMethod]
    public void UnreliableOpenPupilFollowsReliableEyeWithoutBlockingSource()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        OrderedFloatMap? missingLeft = null;
        for (var frame = 0; frame < 7; frame++)
        {
            missingLeft = CreateExpressions(
                0.95f, 0.8f, 0, 0, 0, 0, 0, 0, 0,
                leftPupilVisibility: 0.001f,
                rightPupilVisibility: 1.0f);
            Process(pipeline, missingLeft);

            // The reliable right eye remains at its live model coordinate on every frame.
            Assert.AreEqual(0.6f, missingLeft["/rightEyeX"], 0.0001f);
        }
        Assert.IsNotNull(missingLeft);

        Assert.AreEqual(0.6f, missingLeft["/leftEyeX"], 0.0001f);
    }

    [TestMethod]
    public void UnreliableWinkSourceFallsBackToBilateralHold()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        for (var frame = 0; frame < 10; frame++)
        {
            var asymmetricBlink = CreateExpressions(
                0.95f, 0.05f, 1, 0, 0, 0, 0, 0, 0,
                leftPupilVisibility: 0.001f,
                rightPupilVisibility: 0.002f);
            Process(pipeline, asymmetricBlink);

            Assert.AreEqual(open["/leftEyeX"], asymmetricBlink["/leftEyeX"], 0.0001f);
            Assert.AreEqual(open["/rightEyeX"], asymmetricBlink["/rightEyeX"], 0.0001f);
        }
    }

    [TestMethod]
    public void ConfirmedWinkMovesCoveredPupilWithoutOneFrameJump()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.4f, 0.4f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        OrderedFloatMap? firstConfirmed = null;
        for (var frame = 0; frame < 6; frame++)
        {
            firstConfirmed = CreateExpressions(0.95f, 0.9f, 1, 0, 0, 0, 0, 0, 0);
            Process(pipeline, firstConfirmed);
        }
        Assert.IsNotNull(firstConfirmed);

        Assert.AreEqual(open["/leftEyeX"] + 0.12f,
            firstConfirmed["/leftEyeX"], 0.0001f);
        Assert.AreEqual(0.8f, firstConfirmed["/rightEyeX"], 0.0001f);

        OrderedFloatMap? followed = null;
        for (var frame = 0; frame < 8; frame++)
        {
            followed = CreateExpressions(0.95f, 0.9f, 1, 0, 0, 0, 0, 0, 0);
            Process(pipeline, followed);
        }
        Assert.IsNotNull(followed);
        Assert.AreEqual(followed["/rightEyeX"], followed["/leftEyeX"], 0.0001f);
    }

    [TestMethod]
    public void ConfirmedConvergenceCanUseOneReliablePupil()
    {
        using var pipeline = CreatePipeline();
        var open = CreateExpressions(0.5f, 0.5f, 0, 0, 0, 0, 0, 0, 0);
        Process(pipeline, open);

        OrderedFloatMap? converged = null;
        for (var frame = 0; frame < 14; frame++)
        {
            converged = CreateExpressions(
                0.8f, 0.2f, 0, 0, 1, 0, 0, 0, 0,
                leftPupilVisibility: 1.0f,
                rightPupilVisibility: 0.001f);
            Process(pipeline, converged);
        }
        Assert.IsNotNull(converged);

        Assert.AreEqual(1.0f, converged["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-1.0f, converged["/rightEyeX"], 0.0001f);
    }

    [TestMethod]
    public void BootstrapModelWithoutWidenOrSquintStillProcessesGazeAndLids()
    {
        string[] keys =
        [
            "/leftEyeX", "/leftEyeY", "/leftEyeLid",
            "/rightEyeX", "/rightEyeY", "/rightEyeLid"
        ];
        var values = new OrderedFloatMap(keys);
        values["/leftEyeX"] = 0.75f;
        values["/leftEyeY"] = 0.5f;
        values["/leftEyeLid"] = 0.0f;
        values["/rightEyeX"] = 0.25f;
        values["/rightEyeY"] = 0.5f;
        values["/rightEyeLid"] = 0.0f;

        Process(values);

        Assert.AreEqual(0.475f, values["/leftEyeX"], 0.0001f);
        Assert.AreEqual(-0.475f, values["/rightEyeX"], 0.0001f);
        Assert.AreEqual(1.0f, values["/leftEyeLid"], 0.0001f);
        Assert.AreEqual(1.0f, values["/rightEyeLid"], 0.0001f);
        Assert.IsFalse(values.ContainsKey("/leftEyeWiden"));
        Assert.IsFalse(values.ContainsKey("/leftEyeSquint"));
    }

    private static void Process(OrderedFloatMap values)
    {
        using var pipeline = CreatePipeline();
        Process(pipeline, values);
    }

    private static EyeProcessingPipeline CreatePipeline() =>
        new(
            new EyePipelineEventBus(), new PipelineMetrics(),
            new LegacyTemporalEyeTrackingBackend(),
            new PassthroughEyeFrameNormalizer());

    private static void Process(EyeProcessingPipeline pipeline, OrderedFloatMap values)
    {
        var method = typeof(EyeProcessingPipeline).GetMethod(
            "ProcessExpressions", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        object?[] arguments = [values];
        method.Invoke(pipeline, arguments);
    }

    private static OrderedFloatMap CreateExpressions(
        float leftX,
        float rightX,
        float leftClosure,
        float rightClosure,
        float convergence,
        float leftWiden,
        float rightWiden,
        float leftSquint,
        float rightSquint,
        float leftPupilVisibility = 1.0f,
        float rightPupilVisibility = 1.0f)
    {
        string[] keys =
        [
            "/leftEyeX", "/leftEyeY", "/leftEyeLid", "/leftEyeWiden", "/leftEyeSquint",
            "/leftPupilVisibility",
            "/rightEyeX", "/rightEyeY", "/rightEyeLid", "/rightEyeWiden", "/rightEyeSquint",
            "/rightPupilVisibility",
            "/eyeConvergence"
        ];
        var values = new OrderedFloatMap(keys);
        values["/leftEyeX"] = leftX;
        values["/leftEyeY"] = 0.5f;
        values["/leftEyeLid"] = leftClosure;
        values["/leftEyeWiden"] = leftWiden;
        values["/leftEyeSquint"] = leftSquint;
        values["/leftPupilVisibility"] = leftPupilVisibility;
        values["/rightEyeX"] = rightX;
        values["/rightEyeY"] = 0.5f;
        values["/rightEyeLid"] = rightClosure;
        values["/rightEyeWiden"] = rightWiden;
        values["/rightEyeSquint"] = rightSquint;
        values["/rightPupilVisibility"] = rightPupilVisibility;
        values["/eyeConvergence"] = convergence;
        return values;
    }
}
