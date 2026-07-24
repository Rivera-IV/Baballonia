using Baballonia.Contracts;
using Baballonia.Helpers;
using Baballonia.Services;
using Microsoft.Extensions.Logging;
using OverlaySDK;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OverlaySDK.Packets;

namespace Baballonia.Desktop.Calibration;

public class OverlayTrainerService(
    ILogger<OverlayTrainerService> logger,
    IOverlayProgram overlayProgram,
    ILocalSettingsService localSettingsService,
    EyePipelineManager eyePipelineManager,
    EyeCalibration eyeCalibration,
    DataUploaderService dataUploaderService,
    EyeDiagnosticRecorder eyeDiagnosticRecorder)
    : IVROverlay
{

    public event Action<CalibrationTrainingProgress>? TrainingProgressChanged;

    private readonly CancellationTokenSource _tokenSource = new();

    public void Dispose()
    {
        overlayProgram.Dispose();
    }

    public async Task<(bool success, string status)> EyeTrackingCalibrationRequested(
        CalibrationRoutine.Routines routine)
    {
        if (!overlayProgram.CanStart())
        {
            return (false, "Cannot start Overlay");
        }

        var overlayLogger = new OverlayLogger(logger);

        var sfactory = new SocketFactory();
        // Start binding/listening before launching Godot. CreateServer waits for the client, so run
        // it in the background; this removes the first-launch race caused by the old fixed delay.
        var serverTask = Task.Run(() => sfactory.CreateServer("127.0.0.1", 2425), _tokenSource.Token);

        overlayProgram.Start();

        var sock = await serverTask.WaitAsync(TimeSpan.FromSeconds(30), _tokenSource.Token);
        overlayLogger.Info("Accepted connection");

        var tcp = new EventDrivenTcpClient(sock);
        var client = new EventDrivenJsonClient(tcp);

        var messageDispatcher = new OverlayMessageDispatcher(overlayLogger, client);

        var readyHandler = new OverlayReadyHandler();
        messageDispatcher.RegisterHandler(readyHandler);
        await readyHandler.WaitAsync(_tokenSource.Token);
        messageDispatcher.UnRegisterHandler(readyHandler);
        overlayLogger.Info("Calibration overlay is ready");

        if (!Directory.Exists(Utils.ModelDataDirectory)) Directory.CreateDirectory(Utils.ModelDataDirectory);
        if (!Directory.Exists(Utils.ModelsDirectory)) Directory.CreateDirectory(Utils.ModelsDirectory);

        var steps = routine switch
        {
            CalibrationRoutine.Routines.BasicCalibration => eyeCalibration.BasicAllCalibration(ReportTrainingProgress),
            CalibrationRoutine.Routines.BasicCalibrationNoTutorial => eyeCalibration.BasicAllCalibrationQuick(ReportTrainingProgress),
            CalibrationRoutine.Routines.GazeOnly => eyeCalibration.GazeCalibration(ReportTrainingProgress),
            CalibrationRoutine.Routines.BlinkOnly => eyeCalibration.BlinkCalibration(ReportTrainingProgress),
            _ => eyeCalibration.BasicAllCalibration(ReportTrainingProgress)
        };

        var diagnosticTargetHandler = new DiagnosticTargetHandler(eyeDiagnosticRecorder);
        var diagnosticActive = false;
        try
        {
            messageDispatcher.RegisterHandler(diagnosticTargetHandler);
            var diagnosticPath = eyeDiagnosticRecorder.StartCalibrationRecording();
            diagnosticActive = true;
            logger.LogInformation("Recording local eye calibration diagnostic to {Path}", diagnosticPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not start the local eye calibration diagnostic");
        }
        try
        {
            foreach (var calibrationStep in steps)
            {
                if (calibrationStep.Name == "trainer" && diagnosticActive)
                {
                    try
                    {
                        var diagnostic = await eyeDiagnosticRecorder.StopAsync();
                        logger.LogInformation("Eye calibration diagnostic saved to {Path} with {Samples} samples",
                            diagnostic?.DirectoryPath, diagnostic?.SampleCount);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Could not finish the local eye calibration diagnostic");
                    }
                    diagnosticActive = false;
                }

                var isMeasurement = calibrationStep is BaseCaptureStep or PositionalAwareCaptureStep;
                if (diagnosticActive)
                    eyeDiagnosticRecorder.SetCalibrationPhase(calibrationStep.Name, isMeasurement);

                logger.LogInformation("Starting calibration step {Step}", calibrationStep.Name);
                await calibrationStep.ExecuteAsync(messageDispatcher, _tokenSource.Token);
                logger.LogInformation("Finished calibration step {Step}", calibrationStep.Name);
            }
        }
        finally
        {
            messageDispatcher.UnRegisterHandler(diagnosticTargetHandler);
            if (diagnosticActive && eyeDiagnosticRecorder.IsRecording)
            {
                try
                {
                    await eyeDiagnosticRecorder.StopAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not finish the local eye calibration diagnostic");
                }
            }
        }

        var srcPath = Path.Combine(Utils.ModelDataDirectory, "tuned_temporal_eye_tracking_latest.onnx");
        var destPath = Path.Combine(Utils.ModelsDirectory,
            $"tuned_temporal_eye_tracking_{DateTime.Now:yyyyMMdd_HHmmss}.onnx");

        File.Move(srcPath, destPath);

        localSettingsService.SaveSetting("EyeHome_EyeModel", destPath);
        await eyePipelineManager.LoadInferenceAsync();

        if (localSettingsService.ReadSetting<bool>("AppSettings_ShareEyeData"))
        {
            var userCal = Path.Combine(Utils.ModelDataDirectory, "user_cal.bin");
            await dataUploaderService.UploadDataAsync(userCal);
        }

        await overlayProgram.WaitForExitAsync();

        return (true, string.Empty);
    }

    private void ReportTrainingProgress(CalibrationTrainingProgress progress) =>
        TrainingProgressChanged?.Invoke(progress);

    private sealed class DiagnosticTargetHandler(EyeDiagnosticRecorder recorder) : PacketHandlerAdapter
    {
        public override void OnHmdPositionalData(HmdPositionalDataPacket packet) =>
            recorder.UpdateCalibrationTarget(
                packet.RoutinePitch, packet.RoutineYaw, packet.RoutineDistance,
                packet.LeftEyePitch, packet.LeftEyeYaw,
                packet.RightEyePitch, packet.RightEyeYaw);
    }

    private sealed class OverlayReadyHandler : PacketHandlerAdapter
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitAsync(CancellationToken cancellationToken) =>
            _ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

        public override void OnRoutineFinishedPacket(RoutineFinishedPacket packet)
        {
            if (string.Equals(packet.RoutineName, "ready", StringComparison.OrdinalIgnoreCase))
                _ready.TrySetResult();
        }
    }
}
