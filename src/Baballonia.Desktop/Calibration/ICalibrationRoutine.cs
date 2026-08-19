using Baballonia.CaptureBin.IO;
using Baballonia.Contracts;
using Baballonia.Services;
using Baballonia.Services.events;
using OpenCvSharp;
using OverlaySDK;
using OverlaySDK.Packets;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Desktop.Calibration;

public interface ICalibrationStep
{
    string Name { get; }
    Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);
}

public sealed class BaseTutorialStep(string name, TimeSpan time) : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public TimeSpan TimeToRun { get; } = time;
    private readonly TaskCompletionSource Token = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public BaseTutorialStep(string name) : this(name, TimeSpan.FromSeconds(30))
    {
    }

    public async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);
        try
        {
            dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToRun));
            await WaitForRoutineFinishAsync(ct);
        }
        finally
        {
            dispatcher.UnRegisterHandler(this);
        }
    }

    private async Task WaitForRoutineFinishAsync(CancellationToken ct)
    {
        await Token.Task.WaitAsync(ct);
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        if (string.Equals(packet.RoutineName, Name, StringComparison.OrdinalIgnoreCase))
            Token.TrySetResult();
    }
}

public abstract class PositionalAwareCaptureStep(string name, uint flags, TimeSpan time,
    string? outputDirectory = null,
    CalibrationQualityTracker? qualityTracker = null)
    : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public uint Flags { get; } = flags;

    protected PositionalBinCollector PositionalBinCollector = new(flags);
    protected TaskCompletionSource Token = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected bool ShouldCollect = false;
    protected TimeSpan TimeToTun = time;
    protected string OutputDirectory { get; } = outputDirectory ?? Utils.ModelDataDirectory;
    protected CalibrationQualityTracker? QualityTracker { get; } = qualityTracker;
    protected OverlayMessageDispatcher? ActiveDispatcher { get; set; }
    private readonly object _attemptFramesLock = new();
    private readonly List<Frame> _attemptFrames = [];

    public abstract Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);

    public override void OnHmdPositionalData(HmdPositionalDataPacket positionalData)
    {
        if (!ShouldCollect)
            return;
        PositionalBinCollector.UpdatePositionalData(positionalData);
    }

    public virtual void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;

        var images = frame.image.Split();
        try
        {
            PositionalBinCollector.AddFrame(images[1], images[0]);
        }
        finally
        {
            foreach (var image in images)
                image.Dispose();
        }
    }

    public virtual void OnDiagnosticFrame(EyePipelineEvents.DiagnosticFrameEvent frame)
    {
        lock (_attemptFramesLock)
        {
            if (!ShouldCollect || QualityTracker == null || ActiveDispatcher == null)
                return;
            QualityTracker.Observe(frame.ModelRaw, ActiveDispatcher);

            var images = frame.TransformedImage.Split();
            try
            {
                var captured = AddQualityFrame(images);
                // The existing personal model is only a quality hint. It must never decide which
                // fresh calibration images are allowed into the replacement model, otherwise a
                // weak old expression head can make its own correction impossible.
                if (captured != null)
                    _attemptFrames.Add(captured);
            }
            finally
            {
                foreach (var image in images)
                    image.Dispose();
            }
        }
    }

    protected virtual Frame? AddQualityFrame(Mat[] images) =>
        PositionalBinCollector.AddFrame(images[1], images[0]);

    protected void BeginAttemptFrames()
    {
        lock (_attemptFramesLock)
            _attemptFrames.Clear();
    }

    protected void FinishAttemptFrames(bool keepAttempt)
    {
        lock (_attemptFramesLock)
        {
            if (!keepAttempt)
                PositionalBinCollector.RemoveFrames(_attemptFrames);
            _attemptFrames.Clear();
        }
    }

    protected void StartCollecting()
    {
        lock (_attemptFramesLock)
            ShouldCollect = true;
    }

    protected void StopCollecting()
    {
        lock (_attemptFramesLock)
            ShouldCollect = false;
    }

    protected async Task WaitForRoutineFinishAsync(CancellationToken ct)
    {
        await Token.Task.WaitAsync(ct);
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        if (string.Equals(packet.RoutineName, Name, StringComparison.OrdinalIgnoreCase))
            Token.TrySetResult();
    }

    public void Dispose()
    {
        Token.SetCanceled();
    }
}

public abstract class BaseCaptureStep(string name, uint flags, TimeSpan time,
    string? outputDirectory = null,
    CalibrationQualityTracker? qualityTracker = null) : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public uint Flags { get; } = flags;

    protected BinCollector BinCollector = new(flags);
    protected TaskCompletionSource Token = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected bool ShouldCollect = false;
    protected TimeSpan TimeToTun = time;
    protected string OutputDirectory { get; } = outputDirectory ?? Utils.ModelDataDirectory;
    protected CalibrationQualityTracker? QualityTracker { get; } = qualityTracker;
    protected OverlayMessageDispatcher? ActiveDispatcher { get; set; }
    private readonly object _attemptFramesLock = new();
    private readonly List<Frame> _attemptFrames = [];

    public abstract Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);

    public virtual void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;

        var images = frame.image.Split();
        try
        {
            AddFrame(images);
        }
        finally
        {
            foreach (var image in images)
                image.Dispose();
        }
    }

    public virtual Frame AddFrame(Mat[] images)
    {
        return BinCollector.AddFrame(images[1], images[0]);
    }

    public virtual void OnDiagnosticFrame(EyePipelineEvents.DiagnosticFrameEvent frame)
    {
        lock (_attemptFramesLock)
        {
            if (!ShouldCollect || QualityTracker == null || ActiveDispatcher == null)
                return;
            QualityTracker.Observe(frame.ModelRaw, ActiveDispatcher);

            var images = frame.TransformedImage.Split();
            try
            {
                var captured = AddFrame(images);
                _attemptFrames.Add(captured);
            }
            finally
            {
                foreach (var image in images)
                    image.Dispose();
            }
        }
    }

    protected void BeginAttemptFrames()
    {
        lock (_attemptFramesLock)
            _attemptFrames.Clear();
    }

    protected void FinishAttemptFrames(bool keepAttempt)
    {
        lock (_attemptFramesLock)
        {
            if (!keepAttempt)
                BinCollector.RemoveFrames(_attemptFrames);
            _attemptFrames.Clear();
        }
    }

    protected void StartCollecting()
    {
        lock (_attemptFramesLock)
            ShouldCollect = true;
    }

    protected void StopCollecting()
    {
        lock (_attemptFramesLock)
            ShouldCollect = false;
    }

    protected async Task WaitForRoutineFinishAsync(CancellationToken ct)
    {
        await Token.Task.WaitAsync(ct);
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        if (string.Equals(packet.RoutineName, Name, StringComparison.OrdinalIgnoreCase))
            Token.TrySetResult();
    }

    public void Dispose()
    {
        Token.SetCanceled();
    }
}

public class GazeCaptureStep(IEyePipelineEventBus bus, TimeSpan time, string name = "gaze", uint extraFlags = 0,
    string? outputDirectory = null,
    CalibrationQualityProfile? baselineProfile = null,
    CalibrationQualityTracker? qualityTracker = null) : BasePositionalAwareEyeCaptureStep(bus, name,
    CaptureFlags.FLAG_GOOD_DATA |
    CaptureFlags.FLAG_IN_MOVEMENT |
    CaptureFlags.FLAG_VERSION_BIT1 |
    CaptureFlags.FLAG_ROUTINE_BIT1 | extraFlags, time, outputDirectory, qualityTracker)
{
    private Stopwatch _posDataTimer = new();
    private readonly TimeSpan _posDataTimeout = TimeSpan.FromSeconds(0.2);

    public GazeCaptureStep(IEyePipelineEventBus bus) : this(bus, TimeSpan.FromSeconds(60))
    {
    }

    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        if (baselineProfile == null)
        {
            await base.ExecuteAsync(dispatcher, ct);
            return;
        }

        void ObserveBaseline(EyePipelineEvents.DiagnosticFrameEvent frame) =>
            baselineProfile.ObserveNeutral(frame.ModelRaw);

        EyePipelineEventBus.Subscribe<EyePipelineEvents.DiagnosticFrameEvent>(ObserveBaseline);
        try
        {
            await base.ExecuteAsync(dispatcher, ct);
        }
        finally
        {
            EyePipelineEventBus.Unsubscribe<EyePipelineEvents.DiagnosticFrameEvent>(ObserveBaseline);
        }
    }

    public override void OnHmdPositionalData(HmdPositionalDataPacket positionalData)
    {
        if (!ShouldCollect)
            return;

        PositionalBinCollector.UpdatePositionalData(positionalData);
        _posDataTimer.Restart();
    }

    public override void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;
        if (_posDataTimer.Elapsed <= _posDataTimeout)
        {
            var images = frame.image.Split();
            try
            {
                AddQualityFrame(images);
            }
            finally
            {
                foreach (var image in images)
                    image.Dispose();
            }
        }
    }
    public override void OnDiagnosticFrame(EyePipelineEvents.DiagnosticFrameEvent frame)
    {
        if (_posDataTimer.Elapsed <= _posDataTimeout)
            base.OnDiagnosticFrame(frame);
    }

    protected override Frame? AddQualityFrame(Mat[] images)
    {
        var captured = PositionalBinCollector.AddFrame(images[1], images[0]);
        if (captured is null)
            return null;
        captured.Header = captured.Header with
        {
            RoutineLeftLid = 1,
            RoutineRightLid = 1,
        };
        return captured;
    }
}

/// <summary>
/// Records frames with BOTH per-frame gaze ground-truth (injected from the overlay reticle
/// via <see cref="PositionalBinCollector"/>) AND a held-expression label stamped onto each frame.
/// Modeled on <see cref="GazeCaptureStep"/> (same fresh-positional-data gate) but with a fully
/// parameterized expression label so the gaze dot can be recorded during the squint/widen/brow passes.
/// </summary>
public class GazeExpressionCaptureStep(
    IEyePipelineEventBus bus,
    string name,
    uint flags,
    TimeSpan time,
    float lid = 0,
    float browRaise = 0,
    float browAngry = 0,
    float widen = 0,
    float squint = 0,
    float dilate = 0,
    string? outputDirectory = null,
    CalibrationQualityTracker? qualityTracker = null)
    // FLAG_ROUTINE_BIT1 (= gaze-valid / Python FLAG_GAZE_DATA) is forced ON: every GazeExpressionCaptureStep
    // records REAL per-frame gaze from the reticle, so the gaze net trains on these held-expression+gaze
    // frames (fixes "eye looks up while squinting"). They are NOT FLAG_FREE_EXPRESSION, so they keep their
    // real expr labels for the supervised expr net (kept by exclude_expr_unlabeled, not the unlabeled stream).
    : BasePositionalAwareEyeCaptureStep(bus, name, flags | CaptureFlags.FLAG_ROUTINE_BIT1, time,
        outputDirectory, qualityTracker)
{
    private readonly Stopwatch _posDataTimer = new();
    private readonly TimeSpan _posDataTimeout = TimeSpan.FromSeconds(0.2);
    public override void OnHmdPositionalData(HmdPositionalDataPacket positionalData)
    {
        if (!ShouldCollect)
            return;

        PositionalBinCollector.UpdatePositionalData(positionalData);
        _posDataTimer.Restart();
    }

    public override void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;
        if (_posDataTimer.Elapsed <= _posDataTimeout)
        {
            var images = frame.image.Split();
            try
            {
                AddQualityFrame(images);
            }
            finally
            {
                foreach (var image in images)
                    image.Dispose();
            }
        }
    }

    public override void OnDiagnosticFrame(EyePipelineEvents.DiagnosticFrameEvent frame)
    {
        if (_posDataTimer.Elapsed <= _posDataTimeout)
            base.OnDiagnosticFrame(frame);
    }

    protected override Frame? AddQualityFrame(Mat[] images)
    {
        var captured = PositionalBinCollector.AddFrame(images[1], images[0]);
        if (captured is null)
            return null;

        captured.Header = captured.Header with
        {
            RoutineLeftLid = lid,
            RoutineRightLid = lid,
            RoutineBrowRaise = browRaise,
            RoutineBrowAngry = browAngry,
            RoutineWiden = widen,
            RoutineSquint = squint,
            RoutineDilate = dilate,
        };
        return captured;
    }

}

public class BasePositionalAwareEyeCaptureStep(
    IEyePipelineEventBus eyePipelineEvent,
    string name,
    uint flags,
    TimeSpan time,
    string? outputDirectory = null,
    CalibrationQualityTracker? qualityTracker = null)
    : PositionalAwareCaptureStep(name, flags, time, outputDirectory, qualityTracker)
{
    protected IEyePipelineEventBus EyePipelineEventBus { get; } = eyePipelineEvent;

    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);
        ActiveDispatcher = dispatcher;
        if (QualityTracker == null)
            EyePipelineEventBus.Subscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
        else
            EyePipelineEventBus.Subscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
        try
        {
            var attempt = 0;
            do
            {
                attempt++;
                Token = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                // Failed attempts are retried, so the visible routine only needs to run for its
                // intended measurement duration. Using the safety timeout here made every
                // 14-second expression pass last 40 seconds even after it had enough samples.
                var runTime = TimeToTun;
                BeginAttemptFrames();
                QualityTracker?.StartAttempt();
                dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, runTime));
                QualityTracker?.ReportProgress(dispatcher);
                StartCollecting();
                await WaitForRoutineFinishAsync(ct);
                StopCollecting();
                QualityTracker?.StopAttempt();
                var attemptCompleted = QualityTracker?.IsComplete ?? true;
                if (QualityTracker != null && !attemptCompleted &&
                    (!QualityTracker.RequiresAdvisoryRetry || attempt >= 2))
                {
                    // Never let an old/weak personal model trap the user in an endless retry.
                    // The instructed fresh images from the final attempt are retained; the
                    // post-training holdout validation remains the authoritative quality gate.
                    QualityTracker.AcceptBestEffort(dispatcher);
                    attemptCompleted = true;
                }
                FinishAttemptFrames(attemptCompleted);

                if (QualityTracker != null && !attemptCompleted)
                {
                    await new BaseTutorialStep("qualityretry", TimeSpan.FromSeconds(10))
                        .ExecuteAsync(dispatcher, ct);
                }
            } while (QualityTracker != null && !QualityTracker.IsComplete);
        }
        finally
        {
            StopCollecting();
            QualityTracker?.StopAttempt();
            if (QualityTracker == null)
                EyePipelineEventBus.Unsubscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
            else
                EyePipelineEventBus.Unsubscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
            ActiveDispatcher = null;
            dispatcher.UnRegisterHandler(this);
        }

        if (ct.IsCancellationRequested)
            return;

        PositionalBinCollector.WriteBin(Path.Combine(OutputDirectory, Name + ".bin"));
    }
}

public class BaseEyeCaptureStep(
    IEyePipelineEventBus eyePipelineEvent,
    string name,
    uint flags,
    TimeSpan time,
    float lid = 0,
    float browRaise = 0,
    float browAngry = 0,
    float widen = 0,
    float squint = 0,
    float dilate = 0,
    float? leftLid = null,
    float? rightLid = null,
    string? outputDirectory = null,
    CalibrationQualityTracker? qualityTracker = null)
    : BaseCaptureStep(name, flags, time, outputDirectory, qualityTracker)
{
    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);
        ActiveDispatcher = dispatcher;
        if (QualityTracker == null)
            eyePipelineEvent.Subscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
        else
            eyePipelineEvent.Subscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
        try
        {
            var attempt = 0;
            do
            {
                attempt++;
                Token = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var runTime = TimeToTun;
                BeginAttemptFrames();
                QualityTracker?.StartAttempt();
                dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, runTime));
                QualityTracker?.ReportProgress(dispatcher);
                StartCollecting();
                await WaitForRoutineFinishAsync(ct);
                StopCollecting();
                QualityTracker?.StopAttempt();
                var attemptCompleted = QualityTracker?.IsComplete ?? true;
                if (QualityTracker != null && !attemptCompleted &&
                    (!QualityTracker.RequiresAdvisoryRetry || attempt >= 2))
                {
                    // Live outputs are advisory only. After one guided retry, keep the complete
                    // final take and let validation of the newly trained model decide the result.
                    QualityTracker.AcceptBestEffort(dispatcher);
                    attemptCompleted = true;
                }
                FinishAttemptFrames(attemptCompleted);

                if (QualityTracker != null && !attemptCompleted)
                {
                    await new BaseTutorialStep("qualityretry", TimeSpan.FromSeconds(10))
                        .ExecuteAsync(dispatcher, ct);
                }
            } while (QualityTracker != null && !QualityTracker.IsComplete);
        }
        finally
        {
            StopCollecting();
            QualityTracker?.StopAttempt();
            if (QualityTracker == null)
                eyePipelineEvent.Unsubscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
            else
                eyePipelineEvent.Unsubscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
            ActiveDispatcher = null;
            dispatcher.UnRegisterHandler(this);
        }

        if (ct.IsCancellationRequested)
            return;

        BinCollector.WriteBin(Path.Combine(OutputDirectory, Name + ".bin"));
    }

    public override Frame AddFrame(Mat[] images)
    {
        var frame = base.AddFrame(images);
        frame.Header = frame.Header with
        {
            RoutineLeftLid = leftLid ?? lid,
            RoutineRightLid = rightLid ?? lid,
            RoutineBrowRaise = browRaise,
            RoutineBrowAngry = browAngry,
            RoutineWiden = widen,
            RoutineSquint = squint,
            RoutineDilate = dilate,
        };
        return frame;
    }
}

public class CommandDispatchStep(string name) : ICalibrationStep
{
    public string Name { get; } = name;

    public Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.Dispatch(new RunFixedLenghtRoutinePacket(Name));
        return Task.CompletedTask;
    }
}

public class TrainerCalibrationStep(
    ITrainerService overlayTrainer,
    Action<CalibrationTrainingProgress>? progressCallback = null,
    string? workingDirectory = null) : ICalibrationStep
{
    public string Name => "trainer";
    private readonly string _workingDirectory = workingDirectory ?? Utils.ModelDataDirectory;

    public async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeSpan.FromSeconds(120)));
        // Godot creates the graph on its deferred UI queue. Give it one frame before sending the
        // initial values; the overlay also buffers packets, but this avoids a visible ??? flash.
        await Task.Delay(250, ct);
        var epoch = 0;
        var totalEpochs = 0;
        var batch = 0;
        var totalBatches = 0;
        var loss = 0d;

        void Report(CalibrationTrainingState state, double value) => progressCallback?.Invoke(
            new CalibrationTrainingProgress(state, Math.Clamp(value, 0, 1), epoch, totalEpochs,
                batch, totalBatches, loss));

        void OnProgress(TrainerProgressReportPacket packet)
        {
            switch (packet.ProgressName)
            {
                case "GlobalEpoch":
                    epoch = packet.CurrentProgress;
                    totalEpochs = packet.TargetProgress;
                    // The trainer's ordinary Epoch line restarts for each of its four sub-models.
                    // Present the global 1..144 counter to VR instead.
                    dispatcher.Dispatch(new TrainerProgressReportPacket(
                        "Epoch", epoch, totalEpochs, loss));
                    Report(CalibrationTrainingState.Running,
                        totalEpochs > 0 ? (double)(epoch - 1) / totalEpochs : 0);
                    break;
                case "Batch":
                    dispatcher.Dispatch(packet);
                    batch = packet.CurrentProgress;
                    totalBatches = packet.TargetProgress;
                    loss = packet.Loss;
                    var batchPart = totalBatches > 0 ? (double)batch / totalBatches : 0;
                    Report(CalibrationTrainingState.Running,
                        totalEpochs > 0 ? ((epoch - 1) + batchPart) / totalEpochs : 0);
                    break;
                case "Epoch":
                    loss = packet.Loss;
                    break;
            }
        }

        var onProgressHandler = (TrainerProgressReportPacket packet) => OnProgress(packet);
        overlayTrainer.OnProgress += onProgressHandler;
        // The trainer first scans and indexes the capture file, which can take a while before its
        // first batch line. Show immediately that training has started instead of an idle graph.
        dispatcher.Dispatch(new TrainerProgressReportPacket("Epoch", 0, 1, 0));
        dispatcher.Dispatch(new TrainerProgressReportPacket("Batch", 0, 1, 0));
        Report(CalibrationTrainingState.Preparing, 0);
        try
        {
            overlayTrainer.RunTraining(Path.Combine(_workingDirectory, "user_cal.bin"),
                Path.Combine(_workingDirectory, "tuned_temporal_eye_tracking_latest.onnx"));
            await overlayTrainer.WaitAsync();
            Report(CalibrationTrainingState.Completed, 1);
        }
        finally
        {
            overlayTrainer.OnProgress -= onProgressHandler;
        }
    }
}

public class EyeCaptureStepFactory(IEyePipelineEventBus eyePipelineEvent)
{
    public BaseEyeCaptureStep Create(string name, uint flags, TimeSpan time,
        float lid = 0,
        float browRaise = 0,
        float browAngry = 0,
        float widen = 0,
        float squint = 0,
        float dilate = 0,
        string? outputDirectory = null,
        CalibrationQualityTracker? qualityTracker = null) =>
        new(eyePipelineEvent, name, flags, time, lid, browRaise, browAngry, widen, squint, dilate,
            outputDirectory: outputDirectory, qualityTracker: qualityTracker);

    public BaseEyeCaptureStep CreateAsymmetricLids(string name, uint flags, TimeSpan time,
        float leftLid, float rightLid, float widen = 0, string? outputDirectory = null,
        CalibrationQualityTracker? qualityTracker = null) =>
        new(eyePipelineEvent, name, flags, time, widen: widen,
            leftLid: leftLid, rightLid: rightLid,
            outputDirectory: outputDirectory, qualityTracker: qualityTracker);

    /// <summary>
    /// Like <see cref="Create"/>, but the step also records per-frame gaze ground-truth from the
    /// overlay reticle (so the gaze dot is shown and captured during the expression pass).
    /// </summary>
    public GazeExpressionCaptureStep CreateGazeExpression(string name, uint flags, TimeSpan time,
        float lid = 0,
        float browRaise = 0,
        float browAngry = 0,
        float widen = 0,
        float squint = 0,
        float dilate = 0,
        string? outputDirectory = null,
        CalibrationQualityTracker? qualityTracker = null) =>
        new(eyePipelineEvent, name, flags, time, lid, browRaise, browAngry, widen, squint, dilate,
            outputDirectory, qualityTracker);
}

public sealed class ValidationResultsStep(string reportPath, TimeSpan? displayTime = null)
    : PacketHandlerAdapter, ICalibrationStep
{
    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Name => "results";

    public async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, ct));
        dispatcher.RegisterHandler(this);
        try
        {
            dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(
                Name, displayTime ?? TimeSpan.FromSeconds(30)));
            foreach (var result in report.RootElement.EnumerateObject())
            {
                if (string.Equals(result.Name, "overall", StringComparison.OrdinalIgnoreCase) ||
                    !result.Value.TryGetProperty("status", out var statusElement))
                    continue;

                var status = statusElement.GetString();
                var statusCode = status switch
                {
                    "pass" => 2,
                    "repeat" => 1,
                    _ => 0,
                };
                dispatcher.Dispatch(new TrainerProgressReportPacket(
                    $"Validation/{result.Name}", statusCode, 2, 0));
            }
            await _finished.Task.WaitAsync(ct);
        }
        finally
        {
            dispatcher.UnRegisterHandler(this);
        }
    }

    public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
    {
        if (string.Equals(packet.RoutineName, Name, StringComparison.OrdinalIgnoreCase))
            _finished.TrySetResult();
    }
}

public class MergeBinsStep(string outputDirectory, params string[] binNames) : ICalibrationStep
{
    public string Name => "bin_merger";

    public Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        MergeBins(outputDirectory, "user_cal.bin", binNames);
        return Task.CompletedTask;
    }

    private static void MergeBins(string directory, string result, params string[] inputs)
    {
        var resultPath = Path.Combine(directory, result);
        var inputPaths = inputs.Select(i => Path.Combine(directory, i)).ToArray();
        var temporaryPath = Path.Combine(directory, $".{result}.{Guid.NewGuid():N}.tmp");
        try
        {
            CaptureBin.IO.CaptureBin.Concatenate(temporaryPath, inputPaths);
            File.Move(temporaryPath, resultPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

}

public class EyeCalibration(
    EyeCaptureStepFactory eyeCaptureStepFactory,
    ITrainerService trainer,
    IEyePipelineEventBus eyePipelineEventBus)
{
    private const uint LidLabels = CaptureFlags.FLAG_LID_LABELS_VALID;
    private const uint EyeShapeLabels = CaptureFlags.FLAG_EYE_SHAPE_LABELS_VALID;
    private const uint BrowLabels = CaptureFlags.FLAG_BROW_LABELS_VALID;
    private const uint NeutralExpressionLabels = LidLabels | EyeShapeLabels | BrowLabels;

    private static CalibrationQualityTracker Quality(
        string routineName,
        CalibrationQualityProfile profile,
        CalibrationExpressionKind kind,
        int targetFrames,
        double minimumSeconds,
        double timeoutSeconds) =>
        new(routineName, profile, new CalibrationQualityOptions(
            kind,
            targetFrames,
            TimeSpan.FromSeconds(minimumSeconds),
            TimeSpan.FromSeconds(timeoutSeconds),
            ConsecutiveFrames: 4,
            // Spread accepted frames over most of the pose instead of filling the quota during
            // the first few gaze points and then recording nothing for the rest of the cycle.
            MinimumFrameSpacing: TimeSpan.FromSeconds(
                Math.Max(0.033, minimumSeconds * 0.85 / targetFrames)),
            HardExampleFrames: kind switch
            {
                CalibrationExpressionKind.Blink => targetFrames / 2,
                CalibrationExpressionKind.WinkLeft => targetFrames / 3,
                CalibrationExpressionKind.WinkRight => targetFrames / 3,
                CalibrationExpressionKind.Widen => targetFrames / 2,
                CalibrationExpressionKind.Squint => targetFrames / 2,
                CalibrationExpressionKind.Brow => targetFrames / 2,
                _ => 0,
            }));

    public IEnumerable<ICalibrationStep> BasicAllCalibration(Action<CalibrationTrainingProgress>? trainingProgress = null,
        string? workingDirectory = null)
    {
        var directory = workingDirectory ?? Utils.ModelDataDirectory;
        var qualityProfile = new CalibrationQualityProfile();
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("neutralstarttutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralstart",
                NeutralExpressionLabels, directory, baselineProfile: qualityProfile),
            new BaseTutorialStep("gazetutorial"),
            // The full routine needs enough time to cover the complete visible gaze range,
            // including the extreme directions where an open pupil can become occluded.
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(45),
                outputDirectory: directory),
            new BaseTutorialStep("guidedgazetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(14), "guidedgaze", NeutralExpressionLabels,
                directory),
            new BaseTutorialStep("convergencetutorial", TimeSpan.FromSeconds(15)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(12), "convergence",
                CaptureFlags.FLAG_CONVERGENCE | LidLabels,
                directory),
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.Create("blink",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), lid: 0, outputDirectory: directory,
                qualityTracker: Quality("blink", qualityProfile,
                    CalibrationExpressionKind.Blink, 60, 4, 30)
            ),
            new BaseTutorialStep("winklefttutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkleft",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), leftLid: 1, rightLid: 0, outputDirectory: directory,
                qualityTracker: Quality("winkleft", qualityProfile,
                    CalibrationExpressionKind.WinkLeft, 90, 5, 30)
            ),
            new BaseTutorialStep("winkrighttutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkright",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), leftLid: 0, rightLid: 1, outputDirectory: directory,
                qualityTracker: Quality("winkright", qualityProfile,
                    CalibrationExpressionKind.WinkRight, 90, 5, 30)
            ),

            new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(15)),
                eyeCaptureStepFactory.CreateGazeExpression("widen",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels | EyeShapeLabels,
                TimeSpan.FromSeconds(14), widen: 1, lid: 1, outputDirectory: directory,
                qualityTracker: Quality("widen", qualityProfile,
                    CalibrationExpressionKind.Widen, 180, 14, 40)),

            new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(15)),
                eyeCaptureStepFactory.CreateGazeExpression("squint",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels | EyeShapeLabels,
                TimeSpan.FromSeconds(14), squint: 1, lid: 1, outputDirectory: directory,
                qualityTracker: Quality("squint", qualityProfile,
                    CalibrationExpressionKind.Squint, 180, 14, 40)),

            new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(15)),
                eyeCaptureStepFactory.CreateGazeExpression("brow",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels | BrowLabels,
                TimeSpan.FromSeconds(14), browAngry: 1, lid: 1, outputDirectory: directory,
                qualityTracker: Quality("brow", qualityProfile,
                    CalibrationExpressionKind.Brow, 180, 14, 40)),
            new BaseTutorialStep("neutralendtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralend",
                NeutralExpressionLabels, directory),
            new MergeBinsStep(directory, "neutralstart.bin", "gaze.bin", "guidedgaze.bin", "convergence.bin",
                "blink.bin", "winkleft.bin", "winkright.bin", "widen.bin", "squint.bin", "brow.bin", "neutralend.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress, directory),
            new ValidationResultsStep(Path.Combine(directory,
                "tuned_temporal_eye_tracking_latest.onnx.validation.json")),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> BasicAllCalibrationQuick(Action<CalibrationTrainingProgress>? trainingProgress = null,
        string? workingDirectory = null)
    {
        var directory = workingDirectory ?? Utils.ModelDataDirectory;
        var qualityProfile = new CalibrationQualityProfile();
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("neutralstarttutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralstart",
                NeutralExpressionLabels, directory, baselineProfile: qualityProfile),
            new BaseTutorialStep("gazetutorialshort", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(10),
                outputDirectory: directory),
            new BaseTutorialStep("guidedgazetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(14), "guidedgaze", NeutralExpressionLabels,
                directory),
            new BaseTutorialStep("convergencetutorial", TimeSpan.FromSeconds(15)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(12), "convergence",
                CaptureFlags.FLAG_CONVERGENCE | LidLabels,
                directory),
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.Create("blink",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), outputDirectory: directory,
                qualityTracker: Quality("blink", qualityProfile,
                    CalibrationExpressionKind.Blink, 60, 4, 30)
            ),
            new BaseTutorialStep("winklefttutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkleft",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), leftLid: 1, rightLid: 0, outputDirectory: directory,
                qualityTracker: Quality("winkleft", qualityProfile,
                    CalibrationExpressionKind.WinkLeft, 90, 5, 30)
            ),
            new BaseTutorialStep("winkrighttutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkright",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), leftLid: 0, rightLid: 1, outputDirectory: directory,
                qualityTracker: Quality("winkright", qualityProfile,
                    CalibrationExpressionKind.WinkRight, 90, 5, 30)
            ),

            new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(15)),
                eyeCaptureStepFactory.CreateGazeExpression("widen",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels | EyeShapeLabels,
                TimeSpan.FromSeconds(14), widen: 1, lid: 1, outputDirectory: directory,
                qualityTracker: Quality("widen", qualityProfile,
                    CalibrationExpressionKind.Widen, 180, 14, 40)),

            new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(15)),
                eyeCaptureStepFactory.CreateGazeExpression("squint",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels | EyeShapeLabels,
                TimeSpan.FromSeconds(14), squint: 1, lid: 1, outputDirectory: directory,
                qualityTracker: Quality("squint", qualityProfile,
                    CalibrationExpressionKind.Squint, 180, 14, 40)),

            new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(15)),
                eyeCaptureStepFactory.CreateGazeExpression("brow",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels | BrowLabels,
                TimeSpan.FromSeconds(14), browAngry: 1, lid: 1, outputDirectory: directory,
                qualityTracker: Quality("brow", qualityProfile,
                    CalibrationExpressionKind.Brow, 180, 14, 40)),

            new BaseTutorialStep("neutralendtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralend",
                NeutralExpressionLabels, directory),
            new MergeBinsStep(directory, "neutralstart.bin", "gaze.bin", "guidedgaze.bin", "convergence.bin",
                "blink.bin", "winkleft.bin", "winkright.bin", "widen.bin", "squint.bin", "brow.bin", "neutralend.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress, directory),
            new ValidationResultsStep(Path.Combine(directory,
                "tuned_temporal_eye_tracking_latest.onnx.validation.json")),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> GazeCalibration(Action<CalibrationTrainingProgress>? trainingProgress = null,
        string? workingDirectory = null)
    {
        var directory = workingDirectory ?? Utils.ModelDataDirectory;
        var qualityProfile = new CalibrationQualityProfile();
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("neutralstarttutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralstart",
                NeutralExpressionLabels, directory, baselineProfile: qualityProfile),
            new BaseTutorialStep("gazetutorialshort", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(45),
                outputDirectory: directory),
            new BaseTutorialStep("guidedgazetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(14), "guidedgaze", NeutralExpressionLabels,
                directory),
            new BaseTutorialStep("convergencetutorial", TimeSpan.FromSeconds(15)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(12), "convergence",
                CaptureFlags.FLAG_CONVERGENCE | LidLabels,
                directory),

            new BaseTutorialStep("neutralendtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralend",
                NeutralExpressionLabels, directory),
            new MergeBinsStep(directory, "baseline.bin", "neutralstart.bin", "gaze.bin", "guidedgaze.bin", "convergence.bin",
                "neutralend.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress, directory),
            new ValidationResultsStep(Path.Combine(directory,
                "tuned_temporal_eye_tracking_latest.onnx.validation.json")),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> BlinkCalibration(Action<CalibrationTrainingProgress>? trainingProgress = null,
        string? workingDirectory = null)
    {
        var directory = workingDirectory ?? Utils.ModelDataDirectory;
        var qualityProfile = new CalibrationQualityProfile();
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.Create("blink",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), outputDirectory: directory,
                qualityTracker: Quality("blink", qualityProfile,
                    CalibrationExpressionKind.Blink, 60, 4, 30)
            ),

            new BaseTutorialStep("winklefttutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkleft",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), leftLid: 1, rightLid: 0, outputDirectory: directory,
                qualityTracker: Quality("winkleft", qualityProfile,
                    CalibrationExpressionKind.WinkLeft, 90, 5, 30)
            ),
            new BaseTutorialStep("winkrighttutorial", TimeSpan.FromSeconds(15)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkright",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                LidLabels,
                TimeSpan.FromSeconds(8), leftLid: 0, rightLid: 1, outputDirectory: directory,
                qualityTracker: Quality("winkright", qualityProfile,
                    CalibrationExpressionKind.WinkRight, 90, 5, 30)
            ),

            new MergeBinsStep(directory, "baseline.bin", "blink.bin", "winkleft.bin", "winkright.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress, directory),
            new ValidationResultsStep(Path.Combine(directory,
                "tuned_temporal_eye_tracking_latest.onnx.validation.json")),
            new CommandDispatchStep("close")

        ];

        return steps;
    }
}
