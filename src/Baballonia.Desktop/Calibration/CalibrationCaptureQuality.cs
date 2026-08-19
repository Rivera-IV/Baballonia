using OverlaySDK;
using OverlaySDK.Packets;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Baballonia.Desktop.Calibration;

public enum CalibrationExpressionKind
{
    Blink,
    WinkLeft,
    WinkRight,
    Widen,
    Squint,
    Brow,
    Convergence,
}

/// <summary>
/// Neutral model-output reference recorded at the start of a calibration session. Quality checks
/// use changes from this personal baseline instead of fixed absolute values wherever possible.
/// </summary>
public sealed class CalibrationQualityProfile
{
    private static readonly string[] Keys =
    [
        "leftEyeLid", "rightEyeLid",
        "leftEyeWiden", "rightEyeWiden",
        "leftEyeSquint", "rightEyeSquint",
        "leftEyeBrow", "rightEyeBrow",
        "leftEyeX", "rightEyeX",
        "eyeConvergence",
    ];

    private readonly object _lock = new();
    private readonly Dictionary<string, double> _sums = new(StringComparer.Ordinal);
    private int _samples;

    public int SampleCount
    {
        get
        {
            lock (_lock)
                return _samples;
        }
    }

    public void ObserveNeutral(OrderedFloatMap values)
    {
        lock (_lock)
        {
            foreach (var key in Keys)
            {
                if (TryRead(values, key, out var value) && float.IsFinite(value))
                    _sums[key] = _sums.GetValueOrDefault(key) + value;
            }
            _samples++;
        }
    }

    public float Baseline(string key, float fallback)
    {
        lock (_lock)
        {
            if (_samples < 10 || !_sums.TryGetValue(key, out var sum))
                return fallback;
            return (float)(sum / _samples);
        }
    }

    internal static bool TryRead(OrderedFloatMap values, string key, out float value) =>
        values.TryGetValue(key, out value) || values.TryGetValue("/" + key, out value);
}

public sealed record CalibrationQualityOptions(
    CalibrationExpressionKind Kind,
    int TargetFrames,
    TimeSpan MinimumDuration,
    TimeSpan AttemptTimeout,
    int ConsecutiveFrames = 3,
    TimeSpan? MinimumFrameSpacing = null,
    int HardExampleFrames = 0);

public enum CalibrationFrameDecision
{
    Ignore,
    Accepted,
    HardExample,
}

/// <summary>
/// Counts only frames whose live raw model output matches the requested held expression. The
/// current model acts as a weak quality assessor; the newly trained model is never used to approve
/// its own training data.
/// </summary>
public sealed class CalibrationQualityTracker(
    string routineName,
    CalibrationQualityProfile profile,
    CalibrationQualityOptions options)
{
    private readonly object _lock = new();
    private readonly Stopwatch _activeTimer = new();
    private TimeSpan _accumulatedActiveTime;
    private long _lastAcceptedTimestamp;
    private long _lastHardExampleTimestamp;
    private int _consecutiveValid;
    private int _acceptedFrames;
    private int _attemptHardExamples;
    private bool _attemptConfirmed;
    private bool _completionReported;
    private bool _bestEffortAccepted;

    public string RoutineName { get; } = routineName;
    public CalibrationQualityOptions Options { get; } = options;
    // Independent winks were added specifically because an existing personal model may not yet
    // recognize them. Using that old output to demand a retry creates a circular failure: a user
    // can perform the pose correctly but can never satisfy the model that needs replacing. Keep
    // all instructed frames and let the newly trained model's holdout validation judge the take.
    public bool RequiresAdvisoryRetry => Options.Kind is not
        (CalibrationExpressionKind.WinkLeft or CalibrationExpressionKind.WinkRight);
    public bool IsComplete
    {
        get
        {
            lock (_lock)
                return IsCompleteUnsafe();
        }
    }

    public int AcceptedFrames
    {
        get
        {
            lock (_lock)
                return _acceptedFrames;
        }
    }

    public void StartAttempt()
    {
        lock (_lock)
        {
            _consecutiveValid = 0;
            _attemptHardExamples = 0;
            _attemptConfirmed = false;
            _lastHardExampleTimestamp = 0;
            _activeTimer.Restart();
        }
    }

    public void StopAttempt()
    {
        lock (_lock)
        {
            if (!_activeTimer.IsRunning) return;
            _activeTimer.Stop();
            _accumulatedActiveTime += _activeTimer.Elapsed;
            _activeTimer.Reset();
        }
    }

    public CalibrationFrameDecision Observe(
        OrderedFloatMap values, OverlayMessageDispatcher? dispatcher = null)
    {
        lock (_lock)
        {
            var valid = MatchesExpression(values);
            _consecutiveValid = valid ? _consecutiveValid + 1 : 0;
            if (_consecutiveValid >= Options.ConsecutiveFrames)
                _attemptConfirmed = true;

            var now = Stopwatch.GetTimestamp();
            var spacing = Options.MinimumFrameSpacing ?? TimeSpan.FromMilliseconds(33);
            var spacingElapsed = _lastAcceptedTimestamp == 0 ||
                Stopwatch.GetElapsedTime(_lastAcceptedTimestamp, now) >= spacing;
            var accept = valid &&
                         _consecutiveValid >= Options.ConsecutiveFrames &&
                         _acceptedFrames < Options.TargetFrames &&
                         spacingElapsed;
            if (accept)
            {
                _acceptedFrames++;
                _lastAcceptedTimestamp = now;
                if (dispatcher != null)
                    ReportProgress(dispatcher);
            }

            var hardSpacingElapsed = _lastHardExampleTimestamp == 0 ||
                Stopwatch.GetElapsedTime(_lastHardExampleTimestamp, now) >= spacing;
            // For lids, the short closing transition before the strict pose is confirmed is the
            // exact geometry of a natural blink/wink and must be retained. Other expressions use
            // a much broader relaxed guard, so they wait for strict confirmation to avoid labeling
            // the user's initial neutral pose as widen/squint/brow.
            var allowBeforeConfirmation = Options.Kind is
                CalibrationExpressionKind.Blink or
                CalibrationExpressionKind.WinkLeft or
                CalibrationExpressionKind.WinkRight;
            var hardExample = !accept && !valid &&
                              (_attemptConfirmed || allowBeforeConfirmation) &&
                              _attemptHardExamples < Options.HardExampleFrames &&
                              hardSpacingElapsed && MatchesHardExample(values);
            if (hardExample)
            {
                _attemptHardExamples++;
                _lastHardExampleTimestamp = now;
            }

            if (IsCompleteUnsafe() && !_completionReported)
            {
                _completionReported = true;
                if (dispatcher != null)
                    ReportProgress(dispatcher);
            }

            return accept
                ? CalibrationFrameDecision.Accepted
                : hardExample
                    ? CalibrationFrameDecision.HardExample
                    : CalibrationFrameDecision.Ignore;
        }
    }

    public void ReportProgress(OverlayMessageDispatcher dispatcher)
    {
        lock (_lock)
        {
            var reportedFrames = IsCompleteUnsafe()
                ? Options.TargetFrames
                : Math.Min(_acceptedFrames, Options.TargetFrames - 1);
            dispatcher.Dispatch(new TrainerProgressReportPacket(
                $"Capture/{RoutineName}", reportedFrames, Options.TargetFrames, 0));
        }
    }

    /// <summary>
    /// Ends the advisory live check after the bounded retry. The captured images are still checked
    /// against the newly trained model before that model can be installed.
    /// </summary>
    public void AcceptBestEffort(OverlayMessageDispatcher? dispatcher = null)
    {
        lock (_lock)
        {
            _bestEffortAccepted = true;
            _completionReported = true;
            dispatcher?.Dispatch(new TrainerProgressReportPacket(
                $"Capture/{RoutineName}", Options.TargetFrames, Options.TargetFrames, 0));
        }
    }

    private bool IsCompleteUnsafe()
    {
        if (_bestEffortAccepted)
            return true;
        var activeTime = _accumulatedActiveTime +
                         (_activeTimer.IsRunning ? _activeTimer.Elapsed : TimeSpan.Zero);
        return _acceptedFrames >= Options.TargetFrames && activeTime >= Options.MinimumDuration;
    }

    public bool MatchesExpression(OrderedFloatMap values)
    {
        var hasLeftLid = TryChange(values, "leftEyeLid", 0.18f, out var leftLid);
        var hasRightLid = TryChange(values, "rightEyeLid", 0.18f, out var rightLid);
        var hasLeftWiden = TryChange(values, "leftEyeWiden", 0.10f, out var leftWiden);
        var hasRightWiden = TryChange(values, "rightEyeWiden", 0.10f, out var rightWiden);
        var hasLeftSquint = TryChange(values, "leftEyeSquint", 0.10f, out var leftSquint);
        var hasRightSquint = TryChange(values, "rightEyeSquint", 0.10f, out var rightSquint);
        var hasLeftBrow = TryChange(values, "leftEyeBrow", 0.05f, out var leftBrow);
        var hasRightBrow = TryChange(values, "rightEyeBrow", 0.05f, out var rightBrow);
        var hasConvergence = TryChange(values, "eyeConvergence", 0.05f, out var convergence);

        if (!hasLeftLid || !hasRightLid)
            return false;

        if (!hasConvergence &&
            CalibrationQualityProfile.TryRead(values, "leftEyeX", out var leftX) &&
            CalibrationQualityProfile.TryRead(values, "rightEyeX", out var rightX))
        {
            var neutralDifference = profile.Baseline("leftEyeX", 0.5f) -
                                    profile.Baseline("rightEyeX", 0.5f);
            convergence = Math.Abs((leftX - rightX) - neutralDifference);
        }

        return Options.Kind switch
        {
            CalibrationExpressionKind.Blink => leftLid >= 0.20f && rightLid >= 0.20f,
            CalibrationExpressionKind.WinkLeft => leftLid >= 0.20f && rightLid <= 0.12f,
            CalibrationExpressionKind.WinkRight => rightLid >= 0.20f && leftLid <= 0.12f,
            CalibrationExpressionKind.Widen =>
                ((!hasLeftWiden || !hasRightWiden) ||
                 (leftWiden + rightWiden) * 0.5f >= 0.14f) &&
                leftLid <= 0.18f && rightLid <= 0.18f,
            CalibrationExpressionKind.Squint =>
                ((hasLeftSquint && hasRightSquint)
                    ? (leftSquint + rightSquint) * 0.5f >= 0.14f
                    : leftLid >= 0.08f && rightLid >= 0.08f) &&
                leftLid <= 0.45f && rightLid <= 0.45f,
            CalibrationExpressionKind.Brow =>
                ((!hasLeftBrow || !hasRightBrow) ||
                 (leftBrow + rightBrow) * 0.5f >= 0.12f) &&
                leftLid <= 0.20f && rightLid <= 0.20f,
            CalibrationExpressionKind.Convergence => convergence >= 0.18f,
            _ => false,
        };
    }

    /// <summary>
    /// A deliberately relaxed guard for samples the current model does not recognize strongly.
    /// These are valuable precisely because they are outside its comfortable training domain. The
    /// strict pose must first have been confirmed in this attempt, and only a bounded number of
    /// these samples is retained if that attempt completes successfully.
    /// </summary>
    public bool MatchesHardExample(OrderedFloatMap values)
    {
        var hasLeftLid = TryChange(values, "leftEyeLid", 0.18f, out var leftLid);
        var hasRightLid = TryChange(values, "rightEyeLid", 0.18f, out var rightLid);
        if (!hasLeftLid || !hasRightLid)
            return false;

        return Options.Kind switch
        {
            CalibrationExpressionKind.Blink => leftLid >= 0.05f && rightLid >= 0.05f,
            CalibrationExpressionKind.WinkLeft => leftLid >= 0.05f && rightLid <= 0.18f,
            CalibrationExpressionKind.WinkRight => rightLid >= 0.05f && leftLid <= 0.18f,
            // Once the requested pose has been proven, open-lid frames that the old expression
            // head scores weakly are the hard examples the replacement model needs to learn.
            CalibrationExpressionKind.Widen => leftLid <= 0.25f && rightLid <= 0.25f,
            CalibrationExpressionKind.Squint => leftLid <= 0.55f && rightLid <= 0.55f,
            CalibrationExpressionKind.Brow => leftLid <= 0.25f && rightLid <= 0.25f,
            CalibrationExpressionKind.Convergence => false,
            _ => false,
        };
    }

    private bool TryChange(OrderedFloatMap values, string key, float fallback, out float change)
    {
        if (!CalibrationQualityProfile.TryRead(values, key, out var value) || !float.IsFinite(value))
        {
            change = 0;
            return false;
        }
        change = Math.Abs(value - profile.Baseline(key, fallback));
        return true;
    }
}
