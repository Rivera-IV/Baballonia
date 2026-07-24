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
    private TaskCompletionSource Token = new();

    public BaseTutorialStep(string name) : this(name, TimeSpan.FromSeconds(30))
    {
    }

    public async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);

        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToRun));
        await WaitForRoutineFinishAsync(ct);

        dispatcher.UnRegisterHandler(this);
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

public abstract class PositionalAwareCaptureStep(string name, uint flags, TimeSpan time)
    : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public uint Flags { get; } = flags;

    protected PositionalBinCollector PositionalBinCollector = new(flags);
    protected TaskCompletionSource Token = new();
    protected bool ShouldCollect = false;
    protected TimeSpan TimeToTun = time;

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
        PositionalBinCollector.AddFrame(images[1], images[0]);
    }

    protected void StartCollecting()
    {
        ShouldCollect = true;
    }

    protected void StopCollecting()
    {
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

public abstract class BaseCaptureStep(string name, uint flags, TimeSpan time) : PacketHandlerAdapter, ICalibrationStep
{
    public string Name { get; } = name;
    public uint Flags { get; } = flags;

    protected BinCollector BinCollector = new(flags);
    protected TaskCompletionSource Token = new();
    protected bool ShouldCollect = false;
    protected TimeSpan TimeToTun = time;

    public abstract Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct);

    public virtual void OnNewEyeFrame(EyePipelineEvents.NewTransformedFrameEvent frame)
    {
        if (!ShouldCollect)
            return;

        var images = frame.image.Split();
        AddFrame(images);
    }

    public virtual Frame AddFrame(Mat[] images)
    {
        return BinCollector.AddFrame(images[1], images[0]);
    }

    protected void StartCollecting()
    {
        ShouldCollect = true;
    }

    protected void StopCollecting()
    {
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

public class GazeCaptureStep(IEyePipelineEventBus bus, TimeSpan time, string name = "gaze", uint extraFlags = 0) : BasePositionalAwareEyeCaptureStep(bus, name,
    CaptureFlags.FLAG_GOOD_DATA |
    CaptureFlags.FLAG_IN_MOVEMENT |
    CaptureFlags.FLAG_VERSION_BIT1 |
    CaptureFlags.FLAG_ROUTINE_BIT1 | extraFlags, time)
{
    private Stopwatch _posDataTimer = new();
    private readonly TimeSpan _posDataTimeout = TimeSpan.FromSeconds(0.2);

    public GazeCaptureStep(IEyePipelineEventBus bus) : this(bus, TimeSpan.FromSeconds(60))
    {
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
            var f = PositionalBinCollector.AddFrame(images[1], images[0]);
            if (f is not null)
            {
                f.Header = f.Header with
                {
                    RoutineLeftLid = 1,
                    RoutineRightLid = 1,
                };
            }
        }
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
    float dilate = 0)
    // FLAG_ROUTINE_BIT1 (= gaze-valid / Python FLAG_GAZE_DATA) is forced ON: every GazeExpressionCaptureStep
    // records REAL per-frame gaze from the reticle, so the gaze net trains on these held-expression+gaze
    // frames (fixes "eye looks up while squinting"). They are NOT FLAG_FREE_EXPRESSION, so they keep their
    // real expr labels for the supervised expr net (kept by exclude_expr_unlabeled, not the unlabeled stream).
    : BasePositionalAwareEyeCaptureStep(bus, name, flags | CaptureFlags.FLAG_ROUTINE_BIT1, time)
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
            var f = PositionalBinCollector.AddFrame(images[1], images[0]);
            if (f is not null)
            {
                f.Header = f.Header with
                {
                    RoutineLeftLid = lid,
                    RoutineRightLid = lid,
                    RoutineBrowRaise = browRaise,
                    RoutineBrowAngry = browAngry,
                    RoutineWiden = widen,
                    RoutineSquint = squint,
                    RoutineDilate = dilate,
                };
            }
        }
    }

}

public class BasePositionalAwareEyeCaptureStep(
    IEyePipelineEventBus eyePipelineEvent,
    string name,
    uint flags,
    TimeSpan time)
    : PositionalAwareCaptureStep(name, flags, time)
{
    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);

        eyePipelineEvent.Subscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);

        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToTun));
        StartCollecting();
        await WaitForRoutineFinishAsync(ct);

        eyePipelineEvent.Unsubscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
        dispatcher.UnRegisterHandler(this);

        if (ct.IsCancellationRequested)
            return;

        PositionalBinCollector.WriteBin(Name + ".bin");
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
    float? rightLid = null)
    : BaseCaptureStep(name, flags, time)
{
    public override async Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        dispatcher.RegisterHandler(this);

        eyePipelineEvent.Subscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);

        dispatcher.Dispatch(new RunVariableLenghtRoutinePacket(Name, TimeToTun));
        StartCollecting();
        await WaitForRoutineFinishAsync(ct);

        eyePipelineEvent.Unsubscribe<EyePipelineEvents.NewTransformedFrameEvent>(OnNewEyeFrame);
        dispatcher.UnRegisterHandler(this);

        if (ct.IsCancellationRequested)
            return;

        BinCollector.WriteBin(Name + ".bin");
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
    Action<CalibrationTrainingProgress>? progressCallback = null) : ICalibrationStep
{
    public string Name => "trainer";

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
            overlayTrainer.RunTraining(Path.Combine(Utils.ModelDataDirectory, "user_cal.bin"),
                Path.Combine(Utils.ModelDataDirectory, "tuned_temporal_eye_tracking_latest.onnx"));
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
        float dilate = 0) =>
        new(eyePipelineEvent, name, flags, time, lid, browRaise, browAngry, widen, squint, dilate);

    public BaseEyeCaptureStep CreateAsymmetricLids(string name, uint flags, TimeSpan time,
        float leftLid, float rightLid) =>
        new(eyePipelineEvent, name, flags, time, leftLid: leftLid, rightLid: rightLid);

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
        float dilate = 0) =>
        new(eyePipelineEvent, name, flags, time, lid, browRaise, browAngry, widen, squint, dilate);
}

public class MergeBinsStep(params string[] binNames) : ICalibrationStep
{
    public string Name => "bin_merger";

    public Task ExecuteAsync(OverlayMessageDispatcher dispatcher, CancellationToken ct)
    {
        MergeBins("user_cal.bin", binNames);
        return Task.CompletedTask;
    }

    private static void MergeBins(string result, params string[] inputs)
    {
        var resultPath = Path.Combine(Utils.ModelDataDirectory, result);
        var inputPaths = inputs.Select(i => Path.Combine(Utils.ModelDataDirectory, i)).ToArray();
        CaptureBin.IO.CaptureBin.Concatenate(resultPath, inputPaths);
    }
}

public class EyeCalibration(
    EyeCaptureStepFactory eyeCaptureStepFactory,
    ITrainerService trainer,
    IEyePipelineEventBus eyePipelineEventBus)
{
    public IEnumerable<ICalibrationStep> BasicAllCalibration(Action<CalibrationTrainingProgress>? trainingProgress = null)
    {
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("neutralstarttutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralstart"),
            new BaseTutorialStep("gazetutorial"),
            // The full routine needs enough time to cover the complete visible gaze range,
            // including the extreme directions where an open pupil can become occluded.
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(60)),
            new BaseTutorialStep("guidedgazetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(24), "guidedgaze"),
            new BaseTutorialStep("convergencetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(14), "convergence",
                CaptureFlags.FLAG_CONVERGENCE),
            // Gaze-valid section with FREE/RANDOM (unlabeled) expressions: real expressions in the
            // images while gaze is labeled by the reticle. The qpro trainer uses this as its
            // unlabeled-expression section (gaze_valid=1 -> used for gaze robustness, excluded from
            // supervised expression). Same flags as the neutral gaze pass; only the name/duration differ.
            new BaseTutorialStep("gazeexprtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(60), "gazeexpr", CaptureFlags.FLAG_FREE_EXPRESSION),
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.Create("blink",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(8), lid: 0
            ),
            new BaseTutorialStep("winklefttutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkleft",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(8), leftLid: 1, rightLid: 0
            ),
            new BaseTutorialStep("winkrighttutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkright",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(8), leftLid: 0, rightLid: 1
            ),

            new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.CreateGazeExpression("widen",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(14), widen: 1, lid: 1),

            new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.CreateGazeExpression("squint",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(14), squint: 1, lid: 1),

            new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.CreateGazeExpression("brow",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(14), browAngry: 1, lid: 1),
            new BaseTutorialStep("neutralendtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralend"),
            new MergeBinsStep("neutralstart.bin", "gaze.bin", "guidedgaze.bin", "convergence.bin", "gazeexpr.bin",
                "blink.bin", "winkleft.bin", "winkright.bin", "widen.bin", "squint.bin", "brow.bin", "neutralend.bin"),
            // new MergeBinsStep("gaze.bin", "blink.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> BasicAllCalibrationQuick(Action<CalibrationTrainingProgress>? trainingProgress = null)
    {
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("neutralstarttutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralstart"),
            new BaseTutorialStep("gazetutorialshort", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(10)),
            new BaseTutorialStep("guidedgazetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(20), "guidedgaze"),
            new BaseTutorialStep("convergencetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(15), "convergence",
                CaptureFlags.FLAG_CONVERGENCE),
            new BaseTutorialStep("gazeexprtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(15), "gazeexpr", CaptureFlags.FLAG_FREE_EXPRESSION),
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.Create("blink",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                CaptureFlags.FLAG_ROUTINE_BIT1,
                TimeSpan.FromSeconds(20)
            ),
            new BaseTutorialStep("winklefttutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkleft",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(10), leftLid: 1, rightLid: 0
            ),
            new BaseTutorialStep("winkrighttutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkright",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(10), leftLid: 0, rightLid: 1
            ),

            new BaseTutorialStep("widentutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.CreateGazeExpression("widen",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), widen: 1, lid: 1),

            new BaseTutorialStep("squinttutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.CreateGazeExpression("squint",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), squint: 1, lid: 1),

            new BaseTutorialStep("browtutorial", TimeSpan.FromSeconds(10)),
                eyeCaptureStepFactory.CreateGazeExpression("brow",
                CaptureFlags.FLAG_GOOD_DATA | CaptureFlags.FLAG_IN_MOVEMENT | CaptureFlags.FLAG_VERSION_BIT1, TimeSpan.FromSeconds(20), browAngry: 1, lid: 1),

            new BaseTutorialStep("neutralendtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralend"),
            new MergeBinsStep("neutralstart.bin", "gaze.bin", "guidedgaze.bin", "convergence.bin", "gazeexpr.bin",
                "blink.bin", "winkleft.bin", "winkright.bin", "widen.bin", "squint.bin", "brow.bin", "neutralend.bin"),
            // new MergeBinsStep("gaze.bin", "blink.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> GazeCalibration(Action<CalibrationTrainingProgress>? trainingProgress = null)
    {
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("neutralstarttutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralstart"),
            new BaseTutorialStep("gazetutorialshort", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus),
            new BaseTutorialStep("guidedgazetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(30), "guidedgaze"),
            new BaseTutorialStep("convergencetutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(20), "convergence",
                CaptureFlags.FLAG_CONVERGENCE),

            new BaseTutorialStep("neutralendtutorial", TimeSpan.FromSeconds(10)),
            new GazeCaptureStep(eyePipelineEventBus, TimeSpan.FromSeconds(6), "neutralend"),
            new MergeBinsStep("neutralstart.bin", "gaze.bin", "guidedgaze.bin", "convergence.bin", "neutralend.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress),
            new CommandDispatchStep("close")

        ];

        return steps;
    }

    public IEnumerable<ICalibrationStep> BlinkCalibration(Action<CalibrationTrainingProgress>? trainingProgress = null)
    {
        List<ICalibrationStep> steps =
        [
            new BaseTutorialStep("blinktutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.Create("blink",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1 |
                CaptureFlags.FLAG_ROUTINE_BIT1,
                TimeSpan.FromSeconds(20)
            ),

            new BaseTutorialStep("winklefttutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkleft",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(10), leftLid: 1, rightLid: 0
            ),
            new BaseTutorialStep("winkrighttutorial", TimeSpan.FromSeconds(10)),
            eyeCaptureStepFactory.CreateAsymmetricLids("winkright",
                CaptureFlags.FLAG_GOOD_DATA |
                CaptureFlags.FLAG_IN_MOVEMENT |
                CaptureFlags.FLAG_VERSION_BIT1,
                TimeSpan.FromSeconds(10), leftLid: 0, rightLid: 1
            ),

            new MergeBinsStep("gaze.bin", "blink.bin", "winkleft.bin", "winkright.bin"),
            new TrainerCalibrationStep(trainer, trainingProgress),
            new CommandDispatchStep("close")

        ];

        return steps;
    }
}
