using Baballonia.Contracts;
using Baballonia.Helpers;
using Baballonia.Services;
using Microsoft.Extensions.Logging;
using OverlaySDK;
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using OverlaySDK.Packets;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using JsonConvert = Newtonsoft.Json.JsonConvert;
using System.Linq;
using System.Text.Json;

namespace Baballonia.Desktop.Calibration;

public class OverlayTrainerService(
    ILogger<OverlayTrainerService> logger,
    IOverlayProgram overlayProgram,
    ILocalSettingsService localSettingsService,
    EyePipelineManager eyePipelineManager,
    EyeCalibration eyeCalibration,
    EyeDiagnosticRecorder eyeDiagnosticRecorder)
    : IVROverlay
{

    public event Action<CalibrationTrainingProgress>? TrainingProgressChanged;

    private readonly CancellationTokenSource _disposeTokenSource = new();
    private readonly SemaphoreSlim _calibrationGate = new(1, 1);
    private int _disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _disposeTokenSource.Cancel();
        overlayProgram.Dispose();
        // Do not dispose the token source or semaphore while a calibration can still be unwinding.
        // Its finally block releases the gate after cancellation. The service has application
        // lifetime, so the managed synchronization objects can be reclaimed with the host.
    }

    public async Task<(bool success, string status)> EyeTrackingCalibrationRequested(
        CalibrationRoutine.Routines routine)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return (false, "Calibration service is shutting down");

        if (!await _calibrationGate.WaitAsync(0))
            return (false, "A calibration is already running");

        try
        {
            return await RunCalibrationAsync(routine, _disposeTokenSource.Token);
        }
        catch
        {
            overlayProgram.Dispose();
            throw;
        }
        finally
        {
            _calibrationGate.Release();
        }
    }

    private async Task<(bool success, string status)> RunCalibrationAsync(
        CalibrationRoutine.Routines routine, CancellationToken cancellationToken)
    {
        if (!overlayProgram.CanStart())
        {
            return (false, "Cannot start Overlay");
        }

        var sessionDirectory = CreateCalibrationSession(routine);

        var overlayLogger = new OverlayLogger(logger);

        // Bind synchronously before launching Godot. Task.Run(CreateServer) still had a scheduling
        // race: on a busy machine the overlay could attempt its one connection before the server
        // task had started listening. CreateServer also blocks in Accept and cannot observe our
        // cancellation token, leaving port 2425 occupied after a failed attempt.
        using var sock = await AcceptOverlayConnectionAsync(overlayProgram, cancellationToken);
        overlayLogger.Info("Accepted connection");

        var tcp = new EventDrivenTcpClient(sock);
        var client = new EventDrivenJsonClient(tcp);

        var messageDispatcher = new OverlayMessageDispatcher(overlayLogger, client);

        var readyHandler = new OverlayReadyHandler();
        messageDispatcher.RegisterHandler(readyHandler);
        await readyHandler.WaitAsync(cancellationToken);
        messageDispatcher.UnRegisterHandler(readyHandler);
        overlayLogger.Info("Calibration overlay is ready");

        if (!Directory.Exists(Utils.ModelDataDirectory)) Directory.CreateDirectory(Utils.ModelDataDirectory);
        if (!Directory.Exists(Utils.ModelsDirectory)) Directory.CreateDirectory(Utils.ModelsDirectory);

        var steps = routine switch
        {
            CalibrationRoutine.Routines.BasicCalibration => eyeCalibration.BasicAllCalibration(ReportTrainingProgress, sessionDirectory),
            CalibrationRoutine.Routines.BasicCalibrationNoTutorial => eyeCalibration.BasicAllCalibrationQuick(ReportTrainingProgress, sessionDirectory),
            CalibrationRoutine.Routines.GazeOnly => eyeCalibration.GazeCalibration(ReportTrainingProgress, sessionDirectory),
            CalibrationRoutine.Routines.BlinkOnly => eyeCalibration.BlinkCalibration(ReportTrainingProgress, sessionDirectory),
            _ => eyeCalibration.BasicAllCalibration(ReportTrainingProgress, sessionDirectory)
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
                await calibrationStep.ExecuteAsync(messageDispatcher, cancellationToken);
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

        var srcPath = Path.Combine(sessionDirectory, "tuned_temporal_eye_tracking_latest.onnx");
        ValidateModel(srcPath,
        [
            "rightEyeY", "rightEyeX", "rightEyeLid",
            "leftEyeY", "leftEyeX", "leftEyeLid",
        ]);
        ValidateTrainingReport(srcPath + ".validation.json");
        var modelId = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}";
        var destPath = Path.Combine(Utils.ModelsDirectory,
            $"tuned_temporal_eye_tracking_{modelId}.onnx");
        File.Move(srcPath, destPath);

        var previousModel = localSettingsService.ReadSetting<string>("EyeHome_EyeModel", "eyeModel.onnx");
        localSettingsService.SaveSetting("EyeHome_EyeModel", destPath);
        try
        {
            await eyePipelineManager.LoadInferenceAsync();
        }
        catch
        {
            localSettingsService.SaveSetting("EyeHome_EyeModel", previousModel);
            await eyePipelineManager.LoadInferenceAsync();
            throw;
        }

        CopyAtomically(Path.Combine(sessionDirectory, "user_cal.bin"),
            Path.Combine(Utils.ModelDataDirectory, "last_successful_user_cal.bin"));

        WriteSessionManifest(sessionDirectory, routine, "completed", destPath);

        // The final "close" routine asks the Godot calibration overlay to quit. A lost packet or
        // an XR runtime that keeps its window alive must not leave the calibration request waiting
        // forever (and must never require the user to close Baballonia itself). Give the owned
        // overlay a short grace period, then stop only that child process.
        try
        {
            await overlayProgram.WaitForExitAsync()
                .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (TimeoutException)
        {
            logger.LogWarning(
                "Calibration overlay did not exit after the close command; stopping its process");
            overlayProgram.Dispose();
        }

        return (true, string.Empty);
    }

    private static async Task<Socket> AcceptOverlayConnectionAsync(
        IOverlayProgram overlayProgram,
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(IPAddress.Loopback, 2425);
        try
        {
            // Start is deliberately synchronous: once it returns, the client cannot beat the
            // listener to the port.
            listener.Start(backlog: 1);
            overlayProgram.Start();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                var socket = await listener.AcceptSocketAsync(timeout.Token);
                socket.NoDelay = true;
                return socket;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Calibration overlay did not connect to Baballonia on 127.0.0.1:2425 within 30 seconds.");
            }
        }
        finally
        {
            // Also releases the port when overlay startup throws or the calibration is cancelled.
            listener.Stop();
        }
    }

    private static string CreateCalibrationSession(CalibrationRoutine.Routines routine)
    {
        var sessionsRoot = Path.Combine(Utils.ModelDataDirectory, "CalibrationSessions");
        // Keep two previous sessions; the new one below becomes the third retained session.
        PruneOldSessions(sessionsRoot, keepNewest: 2);
        var sessionDirectory = Path.Combine(sessionsRoot,
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sessionDirectory);
        WriteSessionManifest(sessionDirectory, routine, "capturing");

        if (routine is CalibrationRoutine.Routines.GazeOnly or CalibrationRoutine.Routines.BlinkOnly)
        {
            var baseline = Path.Combine(Utils.ModelDataDirectory, "last_successful_user_cal.bin");
            if (!File.Exists(baseline))
            {
                // One-time compatibility with installations created before session manifests existed.
                baseline = Path.Combine(Utils.ModelDataDirectory, "user_cal.bin");
            }

            if (!File.Exists(baseline))
                throw new InvalidOperationException(
                    "A full eye calibration is required before running a partial calibration.");

            File.Copy(baseline, Path.Combine(sessionDirectory, "baseline.bin"));
        }

        return sessionDirectory;
    }

    private static void WriteSessionManifest(string sessionDirectory,
        CalibrationRoutine.Routines routine, string status, string? modelPath = null)
    {
        var destination = Path.Combine(sessionDirectory, "session.json");
        var temporary = Path.Combine(sessionDirectory, $".session.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                routine = routine.ToString(),
                status,
                updatedUtc = DateTime.UtcNow,
                modelPath,
            }, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void PruneOldSessions(string sessionsRoot, int keepNewest)
    {
        if (!Directory.Exists(sessionsRoot))
            return;

        foreach (var directory in new DirectoryInfo(sessionsRoot)
                     .EnumerateDirectories()
                     .OrderByDescending(item => item.CreationTimeUtc)
                     .Skip(keepNewest))
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // Retention is best effort and must never block a new calibration.
            }
            catch (UnauthorizedAccessException)
            {
                // A protected diagnostic session can be retried next time.
            }
        }
    }

    private static void ValidateModel(string path, string[] requiredNames)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidDataException("Trainer did not create a usable ONNX model.");

        using var options = new SessionOptions();
        options.AppendExecutionProvider_CPU();
        using var session = new InferenceSession(path, options);
        if (session.InputMetadata.Count == 0 || session.OutputMetadata.Count == 0)
            throw new InvalidDataException("The trained ONNX model has no input or output tensors.");

        var input = session.InputMetadata.First();
        var modelDimensions = input.Value.Dimensions.ToArray();
        if (modelDimensions.Length != 4 || modelDimensions.Skip(1).Any(dimension => dimension <= 0))
            throw new InvalidDataException("The trained ONNX model has an unsupported input shape.");

        string[]? names = null;
        if (session.ModelMetadata.CustomMetadataMap.TryGetValue("blendshape_names", out var metadataJson))
        {
            names = JsonConvert.DeserializeObject<string[]>(metadataJson);
            if (names == null || names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
                requiredNames.Any(required => !names.Contains(required, StringComparer.Ordinal)))
                throw new InvalidDataException("The trained ONNX model has invalid eye output metadata.");
        }
        else if (requiredNames.Length != 6 ||
                 session.OutputMetadata.First().Value.Dimensions.LastOrDefault() != 6)
        {
            throw new InvalidDataException("The trained ONNX model has no recognized eye output mapping.");
        }

        // PyTorch exports the batch dimension as the symbolic value "batch_size" (-1 in ORT's
        // metadata). Use a concrete batch of one for the smoke test while preserving the model.
        var validationDimensions = modelDimensions.Select(dimension => dimension > 0 ? dimension : 1).ToArray();
        var tensor = new DenseTensor<float>(validationDimensions);
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(input.Key, tensor)]);
        var outputLength = results.Count == 0 ? 0 : results.First().AsTensor<float>().Length;
        var expectedOutputLength = names?.Length ?? 6;
        if (outputLength != expectedOutputLength)
            throw new InvalidDataException("The trained ONNX model failed its test inference.");
    }

    private static void ValidateTrainingReport(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException("Trainer did not create a calibration validation report.");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.TryGetProperty("overall", out var overall) &&
            overall.TryGetProperty("status", out var overallStatus) &&
            string.Equals(overallStatus.GetString(), "pass", StringComparison.OrdinalIgnoreCase))
            return;

        var failedSteps = root.EnumerateObject()
            .Where(property => !string.Equals(property.Name, "overall", StringComparison.Ordinal) &&
                               property.Value.TryGetProperty("status", out var status) &&
                               !string.Equals(status.GetString(), "pass", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Name)
            .ToArray();
        throw new InvalidDataException(
            "Calibration validation failed: " +
            (failedSteps.Length == 0 ? "unknown" : string.Join(", ", failedSteps)));
    }

    private static void CopyAtomically(string sourcePath, string destinationPath)
    {
        var directory = Path.GetDirectoryName(destinationPath)
            ?? throw new InvalidOperationException("Calibration destination has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
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
