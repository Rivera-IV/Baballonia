using System;
using System.Diagnostics;
using System.Threading;
using Baballonia.Contracts;
using Baballonia.Services;
using Baballonia.Services.events;
using Baballonia.Services.Inference.Normalization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace Baballonia.Tests.Services.Inference;

[TestClass]
public class RegistrationMonitorEyeFrameNormalizerTests
{
    [TestMethod]
    public void LearnsNeutralReferenceAndCorrectsConfirmedTranslation()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var referenceEye = CreateTexturedEyeFrame();
        using var referenceStereo = MergeStereo(referenceEye, referenceEye);

        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "neutralstart", CaptureEnabled: true));
        for (var index = 0; index < ReferenceCandidateCount;
             index++)
            ApplyAndConfirmEyes(normalizer, referenceStereo);
        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "gaze", CaptureEnabled: true));

        using var transform = Mat.Eye(2, 3, MatType.CV_64FC1).ToMat();
        transform.Set(0, 2, 4.0);
        transform.Set(1, 2, -3.0);
        using var shiftedEye = new Mat();
        Cv2.WarpAffine(referenceEye, shiftedEye, transform, referenceEye.Size(),
            InterpolationFlags.Linear, BorderTypes.Reflect101);
        using var shiftedStereo = MergeStereo(shiftedEye, shiftedEye);
        EyeFrameNormalizationState state = default;
        // Before the first scheduled measurement, uncertain registration must leave the model input
        // byte-for-byte unchanged.
        using var shiftedBeforeConfirmation = shiftedStereo.Clone();
        for (var index = 0; index < 5; index++)
            state = normalizer.Apply(shiftedStereo);
        using (var prematureDifference = new Mat())
        {
            Cv2.Absdiff(shiftedStereo, shiftedBeforeConfirmation, prematureDifference);
            using var flattenedPrematureDifference = prematureDifference.Reshape(1);
            Assert.AreEqual(0, Cv2.CountNonZero(flattenedPrematureDifference));
        }
        Assert.IsFalse(state.Left.CorrectionApplied);
        state = WaitForState(
            normalizer, shiftedStereo,
            value => value.Left.CorrectionApplied && value.Right.CorrectionApplied);

        using var difference = new Mat();
        Cv2.Absdiff(shiftedStereo, referenceStereo, difference);
        using var flattened = difference.Reshape(1);
        Assert.IsLessThan(1200, Cv2.CountNonZero(flattened));
        Assert.IsGreaterThan(0.8f, state.Left.Confidence);
        Assert.IsGreaterThan(0.8f, state.Right.Confidence);
        Assert.AreEqual(4f, Math.Abs(state.Left.TranslationX), 1.0f);
        Assert.AreEqual(3f, Math.Abs(state.Left.TranslationY), 1.0f);
        Assert.IsTrue(state.Left.CorrectionApplied);
    }

    [TestMethod]
    public void ReportsUnavailableBeforeNeutralReferenceExists()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var eye = CreateTexturedEyeFrame();
        using var stereo = MergeStereo(eye, eye);

        var state = normalizer.Apply(stereo);

        Assert.AreEqual(0f, state.Left.Confidence);
        Assert.AreEqual(0f, state.Right.Confidence);
    }

    [TestMethod]
    public void RejectsAFrameWithoutMatchingPeriocularStructure()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var referenceEye = CreateTexturedEyeFrame();
        using var referenceStereo = MergeStereo(referenceEye, referenceEye);
        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "neutralstart", CaptureEnabled: true));
        for (var index = 0; index < ReferenceCandidateCount;
             index++)
            ApplyAndConfirmEyes(normalizer, referenceStereo);
        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "gaze", CaptureEnabled: true));

        using var featurelessEye = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(127));
        using var featurelessStereo = MergeStereo(featurelessEye, featurelessEye);
        var state = PumpFor(normalizer, featurelessStereo, TimeSpan.FromMilliseconds(250));

        Assert.AreEqual(0f, state.Left.Confidence);
        Assert.AreEqual(0f, state.Right.Confidence);
        Assert.IsFalse(state.Left.CorrectionApplied);
    }

    [TestMethod]
    public void AutomaticallyLearnsStartupReferenceWithoutCalibrationEvent()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var eye = CreateTexturedEyeFrame();
        using var stereo = MergeStereo(eye, eye);

        var requiredFrames = StartupReferenceFrameCount;
        for (var index = 0; index < requiredFrames; index++)
            ApplyAndConfirmEyes(normalizer, stereo);

        var state = WaitForState(
            normalizer, stereo,
            value => value.Left.Confidence > 0.9f && value.Right.Confidence > 0.9f);

        Assert.IsGreaterThan(0.9f, state.Left.Confidence);
        Assert.IsGreaterThan(0.9f, state.Right.Confidence);
        Assert.IsFalse(state.Left.CorrectionApplied);
        Assert.IsFalse(state.Right.CorrectionApplied);
    }

    [TestMethod]
    public void UnrelatedCalibrationPhaseDoesNotCancelAutomaticStartupReference()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var eye = CreateTexturedEyeFrame();
        using var stereo = MergeStereo(eye, eye);

        for (var index = 0; index < RegistrationMonitorEyeFrameNormalizer.StartupWarmupFrames;
             index++)
            normalizer.Apply(stereo);
        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "gaze", CaptureEnabled: true));
        for (var index = 0;
             index < ReferenceCandidateCount * 2;
             index++)
            ApplyAndConfirmEyes(normalizer, stereo);

        var state = WaitForState(
            normalizer, stereo,
            value => value.Left.Confidence > 0.9f && value.Right.Confidence > 0.9f);

        Assert.IsGreaterThan(0.9f, state.Left.Confidence);
        Assert.IsGreaterThan(0.9f, state.Right.Confidence);
    }

    [TestMethod]
    public void CameraChangeDiscardsOldReferenceAndLearnsTheNewPosition()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var originalEye = CreateTexturedEyeFrame();
        using var originalStereo = MergeStereo(originalEye, originalEye);
        CaptureStartupReference(normalizer, originalStereo);

        using var transform = Mat.Eye(2, 3, MatType.CV_64FC1).ToMat();
        transform.Set(0, 2, 5.0);
        using var movedEye = new Mat();
        Cv2.WarpAffine(originalEye, movedEye, transform, originalEye.Size(),
            InterpolationFlags.Linear, BorderTypes.Reflect101);
        using var movedStereo = MergeStereo(movedEye, movedEye);

        normalizer.ResetForCameraChange();
        CaptureStartupReference(normalizer, movedStereo);
        var state = WaitForState(
            normalizer, movedStereo,
            value => value.Left.Confidence > 0.9f);

        Assert.IsGreaterThan(0.9f, state.Left.Confidence);
        Assert.IsFalse(state.Left.CorrectionApplied);
        Assert.AreEqual(0f, state.Left.TranslationX, 0.5f);
    }

    [TestMethod]
    public void ClosedEyeResultBreaksReferenceApprovalStreak()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var eye = CreateTexturedEyeFrame();
        using var stereo = MergeStereo(eye, eye);
        bus.Publish(new EyePipelineEvents.CalibrationPhaseChangedEvent(
            "neutralstart", CaptureEnabled: true));

        for (var index = 0;
             index < RegistrationMonitorEyeFrameNormalizer.RequiredConsecutiveEyeFrames - 1;
             index++)
            ApplyAndConfirmEyes(normalizer, stereo);
        normalizer.Apply(stereo);
        normalizer.ObserveTrackingResult(CreateVisibleEyeResult(leftLid: 0.95f));

        // After the invalid frame, the complete consecutive gate is required again. These frames
        // therefore produce 35 accepted samples, one short of a usable reference.
        for (var index = 0; index < ReferenceCandidateCount - 1; index++)
            ApplyAndConfirmEyes(normalizer, stereo);
        var state = normalizer.State;
        Assert.AreEqual(0f, state.Left.Confidence);

        ApplyAndConfirmEyes(normalizer, stereo);
        state = WaitForState(
            normalizer, stereo,
            value => value.Left.Confidence > 0.9f);
        Assert.IsGreaterThan(0.9f, state.Left.Confidence);
    }

    [TestMethod]
    public void FeaturelessFramesNeverBecomeAReferenceEvenWhenModelLooksValid()
    {
        var bus = new EyePipelineEventBus();
        using var normalizer = new RegistrationMonitorEyeFrameNormalizer(bus);
        using var featurelessEye = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(127));
        using var featurelessStereo = MergeStereo(featurelessEye, featurelessEye);

        for (var index = 0; index < StartupReferenceFrameCount * 2; index++)
            ApplyAndConfirmEyes(normalizer, featurelessStereo);

        Assert.AreEqual(0f, normalizer.State.Left.Confidence);
        Assert.AreEqual(0f, normalizer.State.Right.Confidence);
    }

    private static void CaptureStartupReference(
        RegistrationMonitorEyeFrameNormalizer normalizer,
        Mat stereo)
    {
        var requiredFrames = StartupReferenceFrameCount;
        for (var index = 0; index < requiredFrames; index++)
            ApplyAndConfirmEyes(normalizer, stereo);
    }

    private static void ApplyAndConfirmEyes(
        RegistrationMonitorEyeFrameNormalizer normalizer,
        Mat stereo)
    {
        normalizer.Apply(stereo);
        normalizer.ObserveTrackingResult(CreateVisibleEyeResult());
    }

    private static EyeFrameNormalizationState WaitForState(
        RegistrationMonitorEyeFrameNormalizer normalizer,
        Mat stereo,
        Func<EyeFrameNormalizationState, bool> predicate)
    {
        var timeout = Stopwatch.StartNew();
        EyeFrameNormalizationState state = default;
        while (timeout.Elapsed < TimeSpan.FromSeconds(3))
        {
            state = normalizer.Apply(stereo);
            if (predicate(state))
                return state;
            Thread.Sleep(1);
        }

        Assert.Fail("The asynchronous registration measurement did not complete in time.");
        return state;
    }

    private static EyeFrameNormalizationState PumpFor(
        RegistrationMonitorEyeFrameNormalizer normalizer,
        Mat stereo,
        TimeSpan duration)
    {
        var timer = Stopwatch.StartNew();
        EyeFrameNormalizationState state = default;
        while (timer.Elapsed < duration)
        {
            state = normalizer.Apply(stereo);
            Thread.Sleep(1);
        }
        return state;
    }

    private static OrderedFloatMap CreateVisibleEyeResult(
        float leftLid = 0.05f,
        float rightLid = 0.05f)
    {
        var values = new OrderedFloatMap([
            "/leftEyeX", "/leftEyeY", "/leftEyeLid",
            "/rightEyeX", "/rightEyeY", "/rightEyeLid",
            "/leftPupilVisibility", "/rightPupilVisibility"]);
        values["/leftEyeX"] = values["/leftEyeY"] = 0.5f;
        values["/rightEyeX"] = values["/rightEyeY"] = 0.5f;
        values["/leftEyeLid"] = leftLid;
        values["/rightEyeLid"] = rightLid;
        values["/leftPupilVisibility"] = values["/rightPupilVisibility"] = 1.0f;
        return values;
    }

    private const int ReferenceCandidateCount =
        RegistrationMonitorEyeFrameNormalizer.RequiredReferenceFrames +
        RegistrationMonitorEyeFrameNormalizer.RequiredConsecutiveEyeFrames - 1;

    private const int StartupReferenceFrameCount =
        RegistrationMonitorEyeFrameNormalizer.StartupWarmupFrames +
        ReferenceCandidateCount;

    private static Mat CreateTexturedEyeFrame()
    {
        var image = new Mat(128, 128, MatType.CV_8UC1, Scalar.All(145));
        Cv2.Rectangle(image, new Rect(8, 8, 30, 24), Scalar.All(210), -1);
        Cv2.Circle(image, new Point(105, 22), 12, Scalar.All(95), -1);
        Cv2.Line(image, new Point(4, 105), new Point(50, 82), Scalar.All(225), 6);
        Cv2.Rectangle(image, new Rect(88, 91, 31, 25), Scalar.All(185), -1);
        // A central feature represents gaze-dependent pupil content and should be excluded by the
        // registration mask; the outer periocular texture still determines the translation.
        Cv2.Circle(image, new Point(64, 64), 15, Scalar.All(22), -1);
        return image;
    }

    private static Mat MergeStereo(Mat left, Mat right)
    {
        var stereo = new Mat();
        Cv2.Merge([left, right], stereo);
        return stereo;
    }
}
