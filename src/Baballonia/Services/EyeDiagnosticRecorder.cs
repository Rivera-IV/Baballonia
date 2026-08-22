using Baballonia.Contracts;
using Baballonia.Helpers;
using Baballonia.Services.events;
using Baballonia.Services.Inference;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Baballonia.Services;

public sealed record EyeDiagnosticRecordingResult(string DirectoryPath, int SampleCount);

public sealed record EyeDiagnosticPackageResult(
    string ZipPath,
    string DirectoryPath,
    int SampleCount,
    bool IncludedEyeModel);

/// <summary>
/// Creates a local-only, synchronized record of what the eye model actually saw and produced.
/// Nothing is uploaded. The result can be inspected without guessing whether a problem originates
/// in camera transformation, ONNX inference, post-processing, smoothing, or calibration remapping.
/// </summary>
public sealed class EyeDiagnosticRecorder(
    IEyePipelineEventBus eyePipelineEventBus,
    ICalibrationService calibrationService,
    ILocalSettingsService localSettingsService) : IDisposable
{
    private readonly object _sync = new();
    private Channel<DiagnosticSample>? _channel;
    private Task? _writerTask;
    private Stopwatch? _clock;
    private string? _sessionDirectory;
    private string _phase = "";
    private CalibrationTarget _target = CalibrationTarget.Empty;
    private bool _captureEnabled;
    private double _lastCaptureMs = double.NegativeInfinity;
    private int _sequence;
    private bool _recording;
    private RecordingKind _recordingKind;
    private string? _activeModelPath;
    private DateTime _recordingStartedLocal;
    private readonly SemaphoreSlim _recordingPruneGate = new(1, 1);

    public bool IsRecording
    {
        get { lock (_sync) return _recording; }
    }

    public bool IsLiveRecording
    {
        get { lock (_sync) return _recording && _recordingKind == RecordingKind.Live; }
    }

    public string StartCalibrationRecording() => StartRecording(RecordingKind.Calibration);

    private string StartRecording(RecordingKind recordingKind)
    {
        string sessionDirectory;
        lock (_sync)
        {
            if (_recording)
                throw new InvalidOperationException("An eye diagnostic recording is already running.");

            var diagnosticsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ProjectBabble", "Diagnostics");
            sessionDirectory = Path.Combine(diagnosticsRoot, $"eye_{DateTime.Now:yyyyMMdd_HHmmss}");
            Directory.CreateDirectory(Path.Combine(sessionDirectory, "frames"));
            _activeModelPath = ResolveActiveEyeModelPath();
            _recordingStartedLocal = DateTime.Now;
            File.WriteAllText(Path.Combine(sessionDirectory, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = 2,
                    createdLocal = _recordingStartedLocal,
                    recordingKind = recordingKind.ToString().ToLowerInvariant(),
                    applicationVersion = GetApplicationVersion(),
                    operatingSystem = RuntimeInformation.OSDescription,
                    processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    eyeModelFileName = Path.GetFileName(_activeModelPath),
                    oneEuroEnabled = localSettingsService.ReadSetting<bool>("AppSettings_OneEuroEnabled"),
                    oneEuroMinCutoff = localSettingsService.ReadSetting<float>("AppSettings_OneEuroMinFreqCutoff"),
                    oneEuroBeta = localSettingsService.ReadSetting<float>("AppSettings_OneEuroSpeedCutoff"),
                    stabilizeEyes = localSettingsService.ReadSetting<bool>("AppSettings_StabilizeEyes", true),
                    swapSplitEyes = localSettingsService.ReadSetting<bool>("AppSettings_SplitEyeVideoSwap", false),
                    eyeWidenGain = localSettingsService.ReadSetting<float>("AppSettings_EyeWidenGain", 1.0f),
                    eyeSquintGain = localSettingsService.ReadSetting<float>("AppSettings_EyeSquintGain", 1.0f),
                    eyeBrowGain = localSettingsService.ReadSetting<float>("AppSettings_EyeBrowGain", 1.0f),
                    leftCameraTransform = localSettingsService.ReadSetting<object?>("LeftCamera"),
                    rightCameraTransform = localSettingsService.ReadSetting<object?>("RightCamera"),
                    calibration = CaptureEyeCalibration(),
                }, new JsonSerializerOptions { WriteIndented = true }));

            _sessionDirectory = sessionDirectory;
            _sequence = 0;
            _phase = "";
            _target = CalibrationTarget.Empty;
            _captureEnabled = false;
            _lastCaptureMs = double.NegativeInfinity;
            _clock = Stopwatch.StartNew();
            _channel = Channel.CreateBounded<DiagnosticSample>(new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            _writerTask = WriteSamplesAsync(_channel.Reader, sessionDirectory);
            _recordingKind = recordingKind;
            _recording = true;
            eyePipelineEventBus.Subscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
        }
        return sessionDirectory;
    }

    private static void PruneOldRecordings(string diagnosticsRoot, int keepNewest)
    {
        if (!Directory.Exists(diagnosticsRoot))
            return;

        foreach (var directory in new DirectoryInfo(diagnosticsRoot)
                     .EnumerateDirectories("eye_*")
                     .OrderByDescending(item => item.CreationTimeUtc)
                     .Skip(keepNewest))
        {
            try
            {
                directory.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A recording can still be open after a crash/restart race. It is safe to retry
                // retention on the next recording instead of disrupting calibration.
            }
            catch (UnauthorizedAccessException)
            {
                // Diagnostics are optional; a protected old directory must not block calibration.
            }
        }
    }

    public string StartLiveRecording()
    {
        var directory = StartRecording(RecordingKind.Live);
        SetCalibrationPhase("live", captureEnabled: true);
        return directory;
    }

    public void SetCalibrationPhase(string phase, bool captureEnabled)
    {
        lock (_sync)
        {
            _phase = phase;
            _captureEnabled = captureEnabled;
            _target = CalibrationTarget.Empty;
            _lastCaptureMs = double.NegativeInfinity;
        }
        eyePipelineEventBus.Publish(
            new EyePipelineEvents.CalibrationPhaseChangedEvent(phase, captureEnabled));
    }

    public void UpdateCalibrationTarget(
        float routinePitch, float routineYaw, float routineDistance,
        float leftEyePitch, float leftEyeYaw,
        float rightEyePitch, float rightEyeYaw)
    {
        lock (_sync)
            _target = new CalibrationTarget(routinePitch, routineYaw, routineDistance,
                leftEyePitch, leftEyeYaw, rightEyePitch, rightEyeYaw);
        eyePipelineEventBus.Publish(new EyePipelineEvents.CalibrationTargetChangedEvent(
            routinePitch, routineYaw, routineDistance,
            leftEyePitch, leftEyeYaw, rightEyePitch, rightEyeYaw));
    }

    private void OnDiagnosticFrame(EyePipelineEvents.DiagnosticFrameEvent frame)
    {
        Channel<DiagnosticSample>? channel;
        Stopwatch? clock;
        int sequence;
        string phase;
        CalibrationTarget target;
        lock (_sync)
        {
            if (!_recording || !_captureEnabled || _channel == null || _clock == null) return;
            var elapsedMs = _clock.Elapsed.TotalMilliseconds;
            // Thirty samples per second preserve several frames of a natural blink without making
            // live diagnostics compete with tracking for CPU and disk bandwidth. Guided blink and
            // wink calibration phases still retain every inference frame for training quality.
            var minimumIntervalMs = _phase switch
            {
                "live" => 1000.0 / 30.0,
                "blink" or "winkleft" or "winkright" => 0.0,
                _ => 100.0,
            };
            if (elapsedMs - _lastCaptureMs < minimumIntervalMs) return;
            _lastCaptureMs = elapsedMs;
            channel = _channel;
            clock = _clock;
            sequence = ++_sequence;
            phase = _phase;
            target = _target;
        }

        // The transformed model input is a two-channel Mat: channel 0 is the left eye and channel
        // 1 is the right eye. PNG does not support that channel layout, so preserve both channels
        // as independent grayscale images while the synchronous event is still valid.
        var eyeImages = frame.TransformedImage.Split();
        if (eyeImages.Length < 2)
        {
            foreach (var eyeImage in eyeImages)
                eyeImage.Dispose();
            return;
        }

        var leftImage = eyeImages[0];
        var rightImage = eyeImages[1];
        for (var i = 2; i < eyeImages.Length; i++)
            eyeImages[i].Dispose();
        var avatar = RemapForAvatar(frame.ProcessedFiltered);
        if (!channel.Writer.TryWrite(new DiagnosticSample(
                sequence,
                clock.Elapsed.TotalMilliseconds,
                phase,
                target,
                ExpectedExpressions.ForPhase(phase),
                leftImage,
                rightImage,
                frame.ModelRaw.ToArray(),
                frame.ProcessedRaw.ToArray(),
                frame.ProcessedFiltered.ToArray(),
                avatar,
                frame.RawTemporalState,
                frame.FilteredTemporalState,
                frame.Normalization,
                frame.Geometry)))
        {
            leftImage.Dispose();
            rightImage.Dispose();
        }
    }

    private KeyValuePair<string, float>[] RemapForAvatar(OrderedFloatMap expressions)
    {
        var output = new KeyValuePair<string, float>[expressions.Count];
        var index = 0;
        foreach (var expression in expressions)
        {
            var settings = calibrationService.GetExpressionSettings(expression.Key);
            var remapped = expression.Value.Remap(
                settings.Lower, settings.Upper, settings.Min, settings.Max);
            output[index++] = new KeyValuePair<string, float>(expression.Key,
                Math.Clamp(remapped, settings.Min, settings.Max));
        }
        return output;
    }

    private static async Task WriteSamplesAsync(
        ChannelReader<DiagnosticSample> reader,
        string sessionDirectory)
    {
        var csvPath = Path.Combine(sessionDirectory, "eye_pipeline.csv");
        var errorsPath = Path.Combine(sessionDirectory, "errors.log");
        await using var csv = new StreamWriter(csvPath, false, new UTF8Encoding(false));
        var wroteHeader = false;
        var rowsSinceFlush = 0;

        await foreach (var sample in reader.ReadAllAsync())
        {
            try
            {
                var leftImageName = $"left_{sample.Sequence:D6}.png";
                var rightImageName = $"right_{sample.Sequence:D6}.png";
                Cv2.ImWrite(Path.Combine(sessionDirectory, "frames", leftImageName), sample.LeftImage);
                Cv2.ImWrite(Path.Combine(sessionDirectory, "frames", rightImageName), sample.RightImage);

                if (!wroteHeader)
                {
                    var columns = new List<string>
                    {
                        "sequence", "time_ms", "phase", "left_image", "right_image",
                        "target_pitch", "target_yaw", "target_distance",
                        "measured_left_pitch", "measured_left_yaw",
                        "measured_right_pitch", "measured_right_yaw",
                        "expected_left_lid", "expected_right_lid", "expected_widen",
                        "expected_squint", "expected_brow",
                        "raw_state", "raw_left_pupil_reliable", "raw_right_pupil_reliable",
                        "raw_left_pupil_missing_frames", "raw_right_pupil_missing_frames",
                        "raw_state_frames", "raw_recovery_frames", "raw_recovery_blend_frame",
                        "raw_convergence_active", "raw_convergence_amount",
                        "raw_convergence_enter_frames", "raw_convergence_exit_frames",
                        "filtered_state", "filtered_left_pupil_reliable", "filtered_right_pupil_reliable",
                        "filtered_left_pupil_missing_frames", "filtered_right_pupil_missing_frames",
                        "filtered_state_frames", "filtered_recovery_frames", "filtered_recovery_blend_frame",
                        "filtered_convergence_active", "filtered_convergence_amount",
                        "filtered_convergence_enter_frames", "filtered_convergence_exit_frames",
                        "registration_left_tx", "registration_left_ty",
                        "registration_left_rotation", "registration_left_confidence",
                        "registration_right_tx", "registration_right_ty",
                        "registration_right_rotation", "registration_right_confidence",
                        "geometry_left_pupil_x", "geometry_left_pupil_y",
                        "geometry_left_pupil_rx", "geometry_left_pupil_ry",
                        "geometry_left_visibility", "geometry_left_pupil_confidence",
                        "geometry_left_yaw", "geometry_left_pitch", "geometry_left_gaze_confidence",
                        "geometry_right_pupil_x", "geometry_right_pupil_y",
                        "geometry_right_pupil_rx", "geometry_right_pupil_ry",
                        "geometry_right_visibility", "geometry_right_pupil_confidence",
                        "geometry_right_yaw", "geometry_right_pitch", "geometry_right_gaze_confidence",
                        "geometry_left_calibration_samples", "geometry_left_span_x", "geometry_left_span_y",
                        "geometry_left_calibration_rmse", "geometry_left_calibration_confidence",
                        "geometry_left_calibration_ready",
                        "geometry_right_calibration_samples", "geometry_right_span_x", "geometry_right_span_y",
                        "geometry_right_calibration_rmse", "geometry_right_calibration_confidence",
                        "geometry_right_calibration_ready",
                        "comparison_model_left_yaw", "comparison_model_left_pitch",
                        "comparison_model_left_error_degrees",
                        "comparison_model_right_yaw", "comparison_model_right_pitch",
                        "comparison_model_right_error_degrees",
                        "comparison_geometry_left_error_degrees",
                        "comparison_geometry_right_error_degrees"
                    };
                    columns.AddRange(sample.ModelRaw.Select(v => "model_raw" + v.Key));
                    columns.AddRange(sample.ProcessedRaw.Select(v => "processed_raw" + v.Key));
                    columns.AddRange(sample.Filtered.Select(v => "filtered" + v.Key));
                    columns.AddRange(sample.Avatar.Select(v => "avatar" + v.Key));
                    await csv.WriteLineAsync(string.Join(',', columns.Select(EscapeCsv)));
                    wroteHeader = true;
                }

                var values = new List<string>
                {
                    sample.Sequence.ToString(CultureInfo.InvariantCulture),
                    sample.TimeMs.ToString("F3", CultureInfo.InvariantCulture),
                    EscapeCsv(sample.Phase),
                    leftImageName,
                    rightImageName,
                    FormatNullable(sample.Target.RoutinePitch),
                    FormatNullable(sample.Target.RoutineYaw),
                    FormatNullable(sample.Target.RoutineDistance),
                    FormatNullable(sample.Target.LeftEyePitch),
                    FormatNullable(sample.Target.LeftEyeYaw),
                    FormatNullable(sample.Target.RightEyePitch),
                    FormatNullable(sample.Target.RightEyeYaw),
                    FormatNullable(sample.Expected.LeftLid),
                    FormatNullable(sample.Expected.RightLid),
                    FormatNullable(sample.Expected.Widen),
                    FormatNullable(sample.Expected.Squint),
                    FormatNullable(sample.Expected.Brow),
                };
                AddTemporalStateValues(values, sample.RawTemporalState);
                AddTemporalStateValues(values, sample.FilteredTemporalState);
                values.AddRange([
                    FormatRegistration(sample.Normalization.Left.TranslationX),
                    FormatRegistration(sample.Normalization.Left.TranslationY),
                    FormatRegistration(sample.Normalization.Left.RotationDegrees),
                    FormatRegistration(sample.Normalization.Left.Confidence),
                    FormatRegistration(sample.Normalization.Right.TranslationX),
                    FormatRegistration(sample.Normalization.Right.TranslationY),
                    FormatRegistration(sample.Normalization.Right.RotationDegrees),
                    FormatRegistration(sample.Normalization.Right.Confidence),
                ]);
                AddGeometryValues(values, sample.Geometry, sample.Target, sample.ModelRaw);
                values.AddRange(sample.ModelRaw.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.AddRange(sample.ProcessedRaw.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.AddRange(sample.Filtered.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.AddRange(sample.Avatar.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                await csv.WriteLineAsync(string.Join(',', values));

                // Windows camera shutdown can require force-terminating the process. Persist live
                // diagnostic rows continuously so a forced camera teardown cannot discard the
                // entire in-memory CSV buffer. Thirty full-rate rows are roughly half a second.
                if (++rowsSinceFlush >= 30)
                {
                    await csv.FlushAsync();
                    rowsSinceFlush = 0;
                }
            }
            catch (Exception exception)
            {
                // Keep consuming later frames if one image or row cannot be written. Diagnostics
                // must never interfere with calibration or training.
                await File.AppendAllTextAsync(errorsPath,
                    $"{DateTime.Now:O} sample {sample.Sequence}: {exception}\n");
            }
            finally
            {
                sample.LeftImage.Dispose();
                sample.RightImage.Dispose();
            }
        }

        await csv.FlushAsync();
        await File.WriteAllTextAsync(Path.Combine(sessionDirectory, "README.txt"),
            "Local Baballonia eye diagnostic. Each CSV row references separate transformed " +
            "left/right grayscale model-input PNGs. Columns contain raw ONNX outputs, " +
            "unfiltered post-processing, filtered post-processing, calibrated avatar output, " +
            "the raw/filtered temporal eye state, active calibration phase, expected expression " +
            "labels, and the latest VR target. " +
            "Registration columns are monitor-only camera-to-eye movement estimates in model-input " +
            "pixels. Geometry columns contain experimental pupil observations and mathematical gaze " +
            "angles. Both systems are shadow-only and do not alter tracking in this build. " +
            "This folder was not uploaded automatically.");
    }

    private static string FormatNullable(float? value) => value.HasValue
        ? value.Value.ToString("R", CultureInfo.InvariantCulture)
        : "";

    private static string FormatRegistration(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static void AddTemporalStateValues(
        ICollection<string> values, EyeTemporalDiagnosticState state)
    {
        values.Add(EscapeCsv(state.State));
        values.Add(state.LeftPupilReliable ? "1" : "0");
        values.Add(state.RightPupilReliable ? "1" : "0");
        values.Add(state.LeftPupilMissingFrames.ToString(CultureInfo.InvariantCulture));
        values.Add(state.RightPupilMissingFrames.ToString(CultureInfo.InvariantCulture));
        values.Add(state.StateFrames.ToString(CultureInfo.InvariantCulture));
        values.Add(state.StableRecoveryFrames.ToString(CultureInfo.InvariantCulture));
        values.Add(state.RecoveryBlendFrame.ToString(CultureInfo.InvariantCulture));
        values.Add(state.ConvergenceActive ? "1" : "0");
        values.Add(state.ConvergenceAmount.ToString("R", CultureInfo.InvariantCulture));
        values.Add(state.ConvergenceEnterFrames.ToString(CultureInfo.InvariantCulture));
        values.Add(state.ConvergenceExitFrames.ToString(CultureInfo.InvariantCulture));
    }

    private static void AddGeometryValues(
        ICollection<string> values,
        EyeGeometryTrackingState geometry,
        CalibrationTarget target,
        KeyValuePair<string, float>[] modelRaw)
    {
        values.Add(FormatGeometry(geometry.LeftPupil.CenterX));
        values.Add(FormatGeometry(geometry.LeftPupil.CenterY));
        values.Add(FormatGeometry(geometry.LeftPupil.RadiusX));
        values.Add(FormatGeometry(geometry.LeftPupil.RadiusY));
        values.Add(FormatGeometry(geometry.LeftPupil.Visibility));
        values.Add(FormatGeometry(geometry.LeftPupil.Confidence));
        values.Add(FormatGeometry(geometry.LeftGaze.YawDegrees));
        values.Add(FormatGeometry(geometry.LeftGaze.PitchDegrees));
        values.Add(FormatGeometry(geometry.LeftGaze.Confidence));
        values.Add(FormatGeometry(geometry.RightPupil.CenterX));
        values.Add(FormatGeometry(geometry.RightPupil.CenterY));
        values.Add(FormatGeometry(geometry.RightPupil.RadiusX));
        values.Add(FormatGeometry(geometry.RightPupil.RadiusY));
        values.Add(FormatGeometry(geometry.RightPupil.Visibility));
        values.Add(FormatGeometry(geometry.RightPupil.Confidence));
        values.Add(FormatGeometry(geometry.RightGaze.YawDegrees));
        values.Add(FormatGeometry(geometry.RightGaze.PitchDegrees));
        values.Add(FormatGeometry(geometry.RightGaze.Confidence));
        AddCalibrationStatus(values, geometry.LeftCalibration);
        AddCalibrationStatus(values, geometry.RightCalibration);

        var modelLeftYaw = ModelDegrees(modelRaw, "/leftEyeX");
        var modelLeftPitch = ModelDegrees(modelRaw, "/leftEyeY");
        var modelRightYaw = ModelDegrees(modelRaw, "/rightEyeX");
        var modelRightPitch = ModelDegrees(modelRaw, "/rightEyeY");
        values.Add(FormatNullable(modelLeftYaw));
        values.Add(FormatNullable(modelLeftPitch));
        values.Add(FormatNullable(AngularError(
            modelLeftPitch, modelLeftYaw, target.LeftEyePitch, target.LeftEyeYaw)));
        values.Add(FormatNullable(modelRightYaw));
        values.Add(FormatNullable(modelRightPitch));
        values.Add(FormatNullable(AngularError(
            modelRightPitch, modelRightYaw, target.RightEyePitch, target.RightEyeYaw)));
        values.Add(FormatNullable(geometry.LeftGaze.Valid
            ? AngularError(geometry.LeftGaze.PitchDegrees, geometry.LeftGaze.YawDegrees,
                target.LeftEyePitch, target.LeftEyeYaw)
            : null));
        values.Add(FormatNullable(geometry.RightGaze.Valid
            ? AngularError(geometry.RightGaze.PitchDegrees, geometry.RightGaze.YawDegrees,
                target.RightEyePitch, target.RightEyeYaw)
            : null));
    }

    private static void AddCalibrationStatus(
        ICollection<string> values, EyeGeometryCalibrationStatus status)
    {
        values.Add(status.SampleCount.ToString(CultureInfo.InvariantCulture));
        values.Add(FormatGeometry(status.HorizontalSpanPixels));
        values.Add(FormatGeometry(status.VerticalSpanPixels));
        values.Add(FormatGeometry(status.RootMeanSquareErrorDegrees));
        values.Add(FormatGeometry(status.Confidence));
        values.Add(status.Ready ? "1" : "0");
    }

    private static float? ModelDegrees(
        IEnumerable<KeyValuePair<string, float>> modelRaw, string key)
    {
        foreach (var value in modelRaw)
            if (value.Key == key && float.IsFinite(value.Value))
                return (value.Value - 0.5f) * 90.0f;
        return null;
    }

    private static float? AngularError(
        float? pitch, float? yaw, float? targetPitch, float? targetYaw)
    {
        if (!pitch.HasValue || !yaw.HasValue || !targetPitch.HasValue || !targetYaw.HasValue)
            return null;
        var pitchError = pitch.Value - targetPitch.Value;
        var yawError = yaw.Value - targetYaw.Value;
        return MathF.Sqrt(pitchError * pitchError + yawError * yawError);
    }

    private static string FormatGeometry(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private Dictionary<string, object> CaptureEyeCalibration()
    {
        string[] names =
        [
            "/leftEyeX", "/leftEyeY", "/rightEyeX", "/rightEyeY",
            "/leftEyeLid", "/rightEyeLid",
            "/leftEyeWiden", "/rightEyeWiden",
            "/leftEyeSquint", "/rightEyeSquint",
            "/leftEyeBrow", "/rightEyeBrow",
            "/eyeConvergence"
        ];
        var result = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var setting = calibrationService.GetExpressionSettings(name);
            result[name] = new
            {
                setting.Lower,
                setting.Upper,
                setting.Min,
                setting.Max,
            };
        }
        return result;
    }

    private string ResolveActiveEyeModelPath()
    {
        const string defaultEyeModelName = "eyeModel.onnx";
        var configured = localSettingsService.ReadSetting<string>(
            "EyeHome_EyeModel", defaultEyeModelName);
        var configuredPath = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        return File.Exists(configuredPath)
            ? configuredPath
            : Path.Combine(AppContext.BaseDirectory, defaultEyeModelName);
    }

    private static string GetApplicationVersion() =>
        typeof(EyeDiagnosticRecorder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        typeof(EyeDiagnosticRecorder).Assembly.GetName().Version?.ToString() ?? "unknown";

    public async Task<EyeDiagnosticRecordingResult?> StopAsync()
    {
        Task? writerTask;
        string? directory;
        int sampleCount;
        lock (_sync)
        {
            if (!_recording) return null;
            _recording = false;
            eyePipelineEventBus.Unsubscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
            _clock?.Stop();
            _channel?.Writer.TryComplete();
            writerTask = _writerTask;
            directory = _sessionDirectory;
            sampleCount = _sequence;
        }

        if (writerTask != null)
            await writerTask;
        if (directory != null)
        {
            try
            {
                sampleCount = Directory.EnumerateFiles(
                    Path.Combine(directory, "frames"), "left_*.png").Count();
            }
            catch (IOException)
            {
                // Keep the attempted-frame count if the directory cannot be enumerated.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the attempted-frame count if permissions changed during finalization.
            }
        }
        QueueOldRecordingPrune();
        return directory == null ? null : new EyeDiagnosticRecordingResult(directory, sampleCount);
    }

    private void QueueOldRecordingPrune()
    {
        _ = Task.Run(async () =>
        {
            // Let packaging or training begin without competing with a potentially large recursive
            // delete. If another recording starts meanwhile, its own stop will schedule a retry.
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (IsRecording)
                return;

            await _recordingPruneGate.WaitAsync();
            try
            {
                if (IsRecording)
                    return;

                var diagnosticsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ProjectBabble", "Diagnostics");
                PruneOldRecordings(diagnosticsRoot, keepNewest: 5);
            }
            catch (IOException)
            {
                // Retention is optional and can be retried after the next recording.
            }
            catch (UnauthorizedAccessException)
            {
                // A protected diagnostic folder must never interrupt tracking or the UI.
            }
            finally
            {
                _recordingPruneGate.Release();
            }
        });
    }

    /// <summary>
    /// Finishes a user-started live recording and creates a local ZIP that can be sent manually.
    /// The selected personalized eye model is included so the recorded frames can be replayed
    /// against the exact network that produced the CSV values. No upload is performed.
    /// </summary>
    public async Task<EyeDiagnosticPackageResult?> StopLiveAndCreatePackageAsync(string? notes)
    {
        string? activeModelPath;
        DateTime startedLocal;
        lock (_sync)
        {
            if (!_recording || _recordingKind != RecordingKind.Live)
                return null;
            activeModelPath = _activeModelPath;
            startedLocal = _recordingStartedLocal;
        }

        var recording = await StopAsync();
        if (recording == null)
            return null;

        if (!string.IsNullOrWhiteSpace(notes))
            await File.WriteAllTextAsync(
                Path.Combine(recording.DirectoryPath, "tester_notes.txt"),
                notes.Trim(), new UTF8Encoding(false));

        var includedModel = false;
        string? modelHash = null;
        long? modelSize = null;
        string? modelCopyError = null;
        if (!string.IsNullOrWhiteSpace(activeModelPath) && File.Exists(activeModelPath))
        {
            try
            {
                var modelDirectory = Path.Combine(recording.DirectoryPath, "model");
                Directory.CreateDirectory(modelDirectory);
                var packagedModelPath = Path.Combine(modelDirectory, "eye-model.onnx");
                await CopyReadableFileAsync(activeModelPath, packagedModelPath);
                await using var modelStream = new FileStream(
                    packagedModelPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 81920, useAsync: true);
                modelHash = Convert.ToHexString(await SHA256.HashDataAsync(modelStream));
                modelSize = modelStream.Length;
                includedModel = true;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // The frame/output trace remains useful even if antivirus or another process has
                // temporarily locked the ONNX file. Keep paths private; record only the error type.
                modelCopyError = exception.GetType().Name;
            }
        }
        else
        {
            modelCopyError = "ModelFileNotFound";
        }

        await File.WriteAllTextAsync(
            Path.Combine(recording.DirectoryPath, "package_manifest.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                createdLocal = DateTime.Now,
                recordingStartedLocal = startedLocal,
                applicationVersion = GetApplicationVersion(),
                sampleCount = recording.SampleCount,
                includesEyeImages = true,
                includesRawAndProcessedModelOutputs = true,
                includesPersonalizedEyeModel = includedModel,
                eyeModelFile = includedModel ? "model/eye-model.onnx" : null,
                eyeModelSha256 = modelHash,
                eyeModelBytes = modelSize,
                eyeModelCopyError = modelCopyError,
                automaticUpload = false,
            }, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var exportRoot = Path.Combine(Utils.UserAccessibleDataDirectory, "Diagnostics");
        Directory.CreateDirectory(exportRoot);
        PruneOldPackages(exportRoot, keepNewest: 4);
        var zipPath = UniquePackagePath(exportRoot, startedLocal);
        await Task.Run(() => ZipFile.CreateFromDirectory(
            recording.DirectoryPath,
            zipPath,
            CompressionLevel.Fastest,
            includeBaseDirectory: false));

        return new EyeDiagnosticPackageResult(
            zipPath, recording.DirectoryPath, recording.SampleCount, includedModel);
    }

    private static async Task CopyReadableFileAsync(string sourcePath, string destinationPath)
    {
        await using var source = new FileStream(
            sourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 81920, useAsync: true);
        await using var destination = new FileStream(
            destinationPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);
        await source.CopyToAsync(destination);
    }

    private static string UniquePackagePath(string exportRoot, DateTime startedLocal)
    {
        var stem = $"Baballonia-Eye-Diagnostic-{startedLocal:yyyyMMdd-HHmmss}";
        var path = Path.Combine(exportRoot, stem + ".zip");
        for (var suffix = 2; File.Exists(path); suffix++)
            path = Path.Combine(exportRoot, $"{stem}-{suffix}.zip");
        return path;
    }

    private static void PruneOldPackages(string exportRoot, int keepNewest)
    {
        foreach (var file in new DirectoryInfo(exportRoot)
                     .EnumerateFiles("Baballonia-Eye-Diagnostic-*.zip")
                     .OrderByDescending(item => item.CreationTimeUtc)
                     .Skip(keepNewest))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // An open package can be pruned the next time a recording is exported.
            }
            catch (UnauthorizedAccessException)
            {
                // Diagnostics must never interfere with tracking.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_recording) return;
            _recording = false;
            eyePipelineEventBus.Unsubscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
            _channel?.Writer.TryComplete();
        }
    }

    private sealed record DiagnosticSample(
        int Sequence,
        double TimeMs,
        string Phase,
        CalibrationTarget Target,
        ExpectedExpressions Expected,
        Mat LeftImage,
        Mat RightImage,
        KeyValuePair<string, float>[] ModelRaw,
        KeyValuePair<string, float>[] ProcessedRaw,
        KeyValuePair<string, float>[] Filtered,
        KeyValuePair<string, float>[] Avatar,
        EyeTemporalDiagnosticState RawTemporalState,
        EyeTemporalDiagnosticState FilteredTemporalState,
        EyeFrameNormalizationState Normalization,
        EyeGeometryTrackingState Geometry);

    private readonly record struct CalibrationTarget(
        float? RoutinePitch,
        float? RoutineYaw,
        float? RoutineDistance,
        float? LeftEyePitch,
        float? LeftEyeYaw,
        float? RightEyePitch,
        float? RightEyeYaw)
    {
        public static CalibrationTarget Empty => new(null, null, null, null, null, null, null);
    }

    private enum RecordingKind
    {
        Calibration,
        Live,
    }

    private readonly record struct ExpectedExpressions(
        float? LeftLid,
        float? RightLid,
        float? Widen,
        float? Squint,
        float? Brow)
    {
        public static ExpectedExpressions ForPhase(string phase) => phase switch
        {
            "blink" => new(0, 0, 0, 0, 0),
            "winkleft" => new(1, 0, 0, 0, 0),
            "winkright" => new(0, 1, 0, 0, 0),
            "widen" => new(1, 1, 1, 0, 0),
            "squint" => new(1, 1, 0, 1, 0),
            "brow" => new(1, 1, 0, 0, 1),
            "gazeexpr" => new(null, null, null, null, null),
            _ => new(1, 1, 0, 0, 0),
        };
    }
}
