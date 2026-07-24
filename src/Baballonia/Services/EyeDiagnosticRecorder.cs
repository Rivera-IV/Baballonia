using Baballonia.Contracts;
using Baballonia.Helpers;
using Baballonia.Services.events;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Baballonia.Services;

public sealed record EyeDiagnosticRecordingResult(string DirectoryPath, int SampleCount);

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

    public bool IsRecording
    {
        get { lock (_sync) return _recording; }
    }

    public string StartCalibrationRecording()
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
            File.WriteAllText(Path.Combine(sessionDirectory, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    createdLocal = DateTime.Now,
                    eyeModel = localSettingsService.ReadSetting<string>("EyeHome_EyeModel", "eyeModel.onnx"),
                    oneEuroEnabled = localSettingsService.ReadSetting<bool>("AppSettings_OneEuroEnabled"),
                    oneEuroMinCutoff = localSettingsService.ReadSetting<float>("AppSettings_OneEuroMinFreqCutoff"),
                    oneEuroBeta = localSettingsService.ReadSetting<float>("AppSettings_OneEuroSpeedCutoff"),
                    stabilizeEyes = localSettingsService.ReadSetting<bool>("AppSettings_StabilizeEyes", true),
                    swapSplitEyes = localSettingsService.ReadSetting<bool>("AppSettings_SplitEyeVideoSwap", false),
                }, new JsonSerializerOptions { WriteIndented = true }));

            _sessionDirectory = sessionDirectory;
            _sequence = 0;
            _phase = "";
            _target = CalibrationTarget.Empty;
            _captureEnabled = false;
            _lastCaptureMs = double.NegativeInfinity;
            _clock = Stopwatch.StartNew();
            _channel = Channel.CreateUnbounded<DiagnosticSample>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            _writerTask = WriteSamplesAsync(_channel.Reader, sessionDirectory);
            _recording = true;
            eyePipelineEventBus.Subscribe<EyePipelineEvents.DiagnosticFrameEvent>(OnDiagnosticFrame);
        }
        return sessionDirectory;
    }

    public string StartLiveRecording()
    {
        var directory = StartCalibrationRecording();
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
    }

    public void UpdateCalibrationTarget(
        float routinePitch, float routineYaw, float routineDistance,
        float leftEyePitch, float leftEyeYaw,
        float rightEyePitch, float rightEyeYaw)
    {
        lock (_sync)
            _target = new CalibrationTarget(routinePitch, routineYaw, routineDistance,
                leftEyePitch, leftEyeYaw, rightEyePitch, rightEyeYaw);
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
            var fullRate = _phase is "blink" or "winkleft" or "winkright";
            if (!fullRate && elapsedMs - _lastCaptureMs < 100.0) return;
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
                avatar)))
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
                        "expected_squint", "expected_brow"
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
                values.AddRange(sample.ModelRaw.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.AddRange(sample.ProcessedRaw.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.AddRange(sample.Filtered.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                values.AddRange(sample.Avatar.Select(v => v.Value.ToString("R", CultureInfo.InvariantCulture)));
                await csv.WriteLineAsync(string.Join(',', values));
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
            "the active calibration phase, expected expression labels, and the latest VR target. " +
            "This folder was not uploaded automatically.");
    }

    private static string FormatNullable(float? value) => value.HasValue
        ? value.Value.ToString("R", CultureInfo.InvariantCulture)
        : "";

    private static string EscapeCsv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

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
        return directory == null ? null : new EyeDiagnosticRecordingResult(directory, sampleCount);
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
        KeyValuePair<string, float>[] Avatar);

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
