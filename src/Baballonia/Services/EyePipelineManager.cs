using Baballonia.Contracts;
using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Backends;
using Baballonia.Services.Inference.Filters;
using Baballonia.Services.Inference.Models;
using Baballonia.Services.Inference.VideoSources;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;

namespace Baballonia.Services;

/// <summary>
/// This class should be the only place where direct Pipeline modifications happen
/// </summary>
public class EyePipelineManager
{
    private readonly ILogger<EyePipelineManager> _logger;
    private readonly EyeProcessingPipeline _pipeline;
    private readonly LegacyTemporalEyeTrackingBackend _eyeBackend;
    private readonly ILocalSettingsService _localSettings;
    private readonly InferenceFactory _inferenceFactory;
    private readonly SingleCameraSourceFactory _singleCameraSourceFactory;
    private readonly SemaphoreSlim _inferenceLoadGate = new(1, 1);
    private readonly SemaphoreSlim _videoSourceGate = new(1, 1);

    private string? _currentLeftAddress;
    private string? _currentRightAddress;

    public EyePipelineManager(ILogger<EyePipelineManager> logger, EyeProcessingPipeline pipeline,
        LegacyTemporalEyeTrackingBackend eyeBackend,
        ILocalSettingsService localSettings, InferenceFactory inferenceFactory,
        SingleCameraSourceFactory singleCameraSourceFactory)
    {
        _logger = logger;
        _pipeline = pipeline;
        _eyeBackend = eyeBackend;
        _localSettings = localSettings;
        _inferenceFactory = inferenceFactory;
        _singleCameraSourceFactory = singleCameraSourceFactory;

        InitializePipeline();
    }


    public void InitializePipeline()
    {
        var dualTransformer = new DualImageTransformer();
        dualTransformer.LeftTransformer.TargetSize = new Size(128, 128);
        dualTransformer.RightTransformer.TargetSize = new Size(128, 128);
        _pipeline.ImageTransformer = dualTransformer;

        _ = LoadInferenceOnStartupAsync();
        LoadFilter();
        LoadEyeStabilization();
        LoadSplitEyeSwap();
    }

    private async Task LoadInferenceOnStartupAsync()
    {
        try
        {
            await LoadInferenceAsync();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not load the eye inference model during startup");
        }
    }

    public async Task LoadInferenceAsync()
    {
        await _inferenceLoadGate.WaitAsync();
        try
        {
            var inference = await Task.Run(CreateInference);
            lock (_pipeline.SyncRoot)
            {
                var previous = _eyeBackend.InferenceService;
                _eyeBackend.InferenceService = inference;
                _pipeline.ResetTemporalState();
                (previous as IDisposable)?.Dispose();
            }
        }
        finally
        {
            _inferenceLoadGate.Release();
        }
    }

    private DefaultInferenceRunner CreateInference()
    {
        const string defaultEyeModelName = "eyeModel.onnx";
        var modelName = _localSettings.ReadSetting<string>(
            "EyeHome_EyeModel", defaultEyeModelName);
        var modelPath = ResolveModelPath(modelName);
        if (File.Exists(modelPath))
            return _inferenceFactory.Create(modelPath);

        _logger.LogError("{ModelPath} does not exist, loading default model", modelPath);
        return _inferenceFactory.Create(Path.Combine(AppContext.BaseDirectory, defaultEyeModelName));
    }

    private static string ResolveModelPath(string modelNameOrPath) =>
        Path.IsPathRooted(modelNameOrPath)
            ? modelNameOrPath
            : Path.Combine(AppContext.BaseDirectory, modelNameOrPath);


    public void LoadInference()
    {
        var inference = CreateInference();
        lock (_pipeline.SyncRoot)
        {
            var previous = _eyeBackend.InferenceService;
            _eyeBackend.InferenceService = inference;
            _pipeline.ResetTemporalState();
            (previous as IDisposable)?.Dispose();
        }
    }

    public void LoadFilter()
    {
        var enabled = _localSettings.ReadSetting<bool>("AppSettings_OneEuroEnabled");
        var cutoff = _localSettings.ReadSetting<float>("AppSettings_OneEuroMinFreqCutoff");
        var speedCutoff = _localSettings.ReadSetting<float>("AppSettings_OneEuroSpeedCutoff");

        if (!enabled)
        {
            lock (_pipeline.SyncRoot)
            {
                (_eyeBackend.Filter as IDisposable)?.Dispose();
                _eyeBackend.Filter = null;
            }
            return;
        }

        var eyeFilter = new OneEuroFilter(
            minCutoff: cutoff,
            beta: speedCutoff,
            // Widen, Squint and Brow use the user's configured One-Euro response. Forcing a high
            // beta here made small model fluctuations temporarily disable most smoothing and
            // caused visible expression jumps.
            // Lids and convergence remain latency-sensitive and use dedicated pipeline handling.
            // Pupil X/Y intentionally pass through One-Euro again so the user's cutoff/beta values
            // control gaze jitter without slowing blink closure or reopening.
            bypassForKey: key =>
                key.EndsWith("EyeLid", StringComparison.Ordinal) ||
                key.EndsWith("PupilVisibility", StringComparison.Ordinal) ||
                string.Equals(key, "/eyeConvergence", StringComparison.Ordinal),
            // The model's lid outputs are closure (0=open, 1=closed). Stop X/Y filter history as
            // soon as the corresponding lid covers the pupil or the independent visibility head
            // says that it has left the camera view. Otherwise hidden model noise accumulates
            // inside OneEuro and is released as a jump when the pupil becomes visible again.
            freezeForKey: static (key, values) =>
            {
                // Each gaze channel is gated only by its own lid/visibility. Cross-eye gating made
                // tiny noise freeze the still-visible pupil throughout a held wink. Bilateral
                // blinks have their own reopening guard after post-processing and need no coupling
                // here.
                // This matches EyeStateResolver's covered-eye boundary (45% openness). The filter
                // only protects its private history here; all public state decisions are made by
                // the resolver after inference.
                const float lidClosureThreshold = 0.55f;
                // Keep the filter frozen throughout the visibility band that proved unstable in
                // the live diagnostic. EyeStateResolver independently requires 0.08 to declare a
                // pupil recovered, giving OneEuro several clean frames to resume before release.
                const float pupilMissingThreshold = 0.03f;
                values.TryGetValue("/leftEyeLid", out var leftLid);
                values.TryGetValue("/rightEyeLid", out var rightLid);
                var hasLeftPupilVisibility = values.TryGetValue(
                    "/leftPupilVisibility", out var leftPupilVisibility);
                var hasRightPupilVisibility = values.TryGetValue(
                    "/rightPupilVisibility", out var rightPupilVisibility);

                var leftUnavailable = leftLid >= lidClosureThreshold ||
                                      hasLeftPupilVisibility &&
                                      leftPupilVisibility <= pupilMissingThreshold;
                var rightUnavailable = rightLid >= lidClosureThreshold ||
                                       hasRightPupilVisibility &&
                                       rightPupilVisibility <= pupilMissingThreshold;

                if (key.StartsWith("/leftEye", StringComparison.Ordinal) &&
                    (key.EndsWith("X", StringComparison.Ordinal) ||
                     key.EndsWith("Y", StringComparison.Ordinal)))
                    return leftUnavailable;

                if (key.StartsWith("/rightEye", StringComparison.Ordinal) &&
                    (key.EndsWith("X", StringComparison.Ordinal) ||
                     key.EndsWith("Y", StringComparison.Ordinal)))
                    return rightUnavailable;

                return false;
            },
            // OneEuro intentionally becomes responsive during fast motion, but the diagnostic
            // captured filtered pupil changes of 0.25 native units (0.5 avatar units) in a single
            // frame. Cap only X/Y filter state: ordinary smaller motion is untouched. When either
            // pupil/lid is uncertain one eye drives both outputs, so use the stricter step.
            maximumDeltaForKey: static (key, values) =>
            {
                if ((!key.EndsWith("EyeX", StringComparison.Ordinal) &&
                     !key.EndsWith("EyeY", StringComparison.Ordinal)))
                    return null;

                const float lidClosureThreshold = 0.55f;
                const float pupilReliableThreshold = 0.08f;
                values.TryGetValue("/leftEyeLid", out var leftLid);
                values.TryGetValue("/rightEyeLid", out var rightLid);
                var hasLeftVisibility = values.TryGetValue(
                    "/leftPupilVisibility", out var leftVisibility);
                var hasRightVisibility = values.TryGetValue(
                    "/rightPupilVisibility", out var rightVisibility);

                var lowConfidence =
                    leftLid >= lidClosureThreshold || rightLid >= lidClosureThreshold ||
                    hasLeftVisibility && leftVisibility < pupilReliableThreshold ||
                    hasRightVisibility && rightVisibility < pupilReliableThreshold;

                // Model coordinates are 0..1 and become -1..+1 in post-processing. These native
                // limits therefore correspond to 0.12 / 0.16 in the avatar coordinate range.
                return lowConfidence ? 0.06f : 0.08f;
            }
        );

        lock (_pipeline.SyncRoot)
        {
            (_eyeBackend.Filter as IDisposable)?.Dispose();
            _eyeBackend.Filter = eyeFilter;
        }
    }

    public void LoadEyeStabilization()
    {
        var stabilizeEyes = _localSettings.ReadSetting<bool>("AppSettings_StabilizeEyes", true);
        _pipeline.StabilizeEyes = stabilizeEyes;
    }

    public void LoadSplitEyeSwap()
    {
        // Default unswapped: split devices like BSB2E expect the left/right halves as-is.
        _pipeline.SwapSplitEyes = _localSettings.ReadSetting<bool>("AppSettings_SplitEyeVideoSwap", false);
    }

    public void SetLeftTransformation(CameraSettings cameraSettings)
    {
        lock (_pipeline.SyncRoot)
        {
            if (_pipeline.ImageTransformer is DualImageTransformer dualImageTransformer)
            {
                dualImageTransformer.LeftTransformer.Transformation = cameraSettings;
            }
        }
    }
    public void SetRightTransformation(CameraSettings cameraSettings)
    {
        lock (_pipeline.SyncRoot)
        {
            if (_pipeline.ImageTransformer is DualImageTransformer dualImageTransformer)
            {
                dualImageTransformer.RightTransformer.Transformation = cameraSettings;
            }
        }
    }

    public async Task<bool> StartLeftVideoSource(string cameraAddress, string preferredBackend)
    {
        await _videoSourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartLeftVideoSourceCore(cameraAddress, preferredBackend).ConfigureAwait(false);
        }
        finally
        {
            _videoSourceGate.Release();
        }
    }

    private async Task<bool> StartLeftVideoSourceCore(string cameraAddress, string preferredBackend)
    {
        if (string.IsNullOrEmpty(cameraAddress))
            return false;

        if (_pipeline.VideoSource == null)
        {
            SingleCameraSource cam;
            if (string.IsNullOrEmpty(preferredBackend))
                cam = await _singleCameraSourceFactory.CreateStart(cameraAddress).ConfigureAwait(false);
            else
                cam = await _singleCameraSourceFactory.CreateStart(cameraAddress, preferredBackend).ConfigureAwait(false);

            if (cam == null)
                return false;

            var source = new DualCameraSource();
            source.LeftCam = cam;
            lock (_pipeline.SyncRoot)
            {
                _pipeline.VideoSource = source;
                _pipeline.ResetForCameraChange();
            }
            _currentLeftAddress = cameraAddress;
            return true;
        }

        if (_pipeline.VideoSource is DualCameraSource dualCameraSource)
            if (cameraAddress == _currentRightAddress && _currentRightAddress != null)
            {
                var tmp = dualCameraSource.RightCam;
                lock (_pipeline.SyncRoot)
                {
                    var unused = ReferenceEquals(dualCameraSource.LeftCam, tmp)
                        ? null
                        : dualCameraSource.LeftCam;
                    dualCameraSource.LeftCam = null;
                    dualCameraSource.RightCam = null;
                    dualCameraSource.Dispose();
                    unused?.Dispose();
                    _pipeline.VideoSource = tmp;
                    _pipeline.ResetForCameraChange();
                }
                _currentLeftAddress = cameraAddress;
                return true;
            }
            else
            {
                var cam = await _singleCameraSourceFactory.CreateStart(cameraAddress, preferredBackend).ConfigureAwait(false);
                if (cam == null)
                    return false;
                IVideoSource? previous;
                lock (_pipeline.SyncRoot)
                {
                    previous = dualCameraSource.LeftCam;
                    dualCameraSource.LeftCam = cam;
                    _pipeline.ResetForCameraChange();
                }
                previous?.Dispose();
                _currentLeftAddress = cameraAddress;
                return true;
            }

        if (_pipeline.VideoSource is SingleCameraSource singleCameraSource)
        {
            if (_currentLeftAddress == cameraAddress && _currentLeftAddress != null)
                return true;

            var cam = await _singleCameraSourceFactory.CreateStart(cameraAddress, preferredBackend).ConfigureAwait(false);
            if (cam == null)
                return false;

            var source = new DualCameraSource();
            source.LeftCam = cam;
            source.RightCam = singleCameraSource;
            lock (_pipeline.SyncRoot)
            {
                _pipeline.VideoSource = source;
                _pipeline.ResetForCameraChange();
            }

            _currentLeftAddress = cameraAddress;
            return true;
        }

        return true;
    }

    public async Task<bool> StartRightVideoSource(string cameraAddress, string preferredBackend)
    {
        await _videoSourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await StartRightVideoSourceCore(cameraAddress, preferredBackend).ConfigureAwait(false);
        }
        finally
        {
            _videoSourceGate.Release();
        }
    }

    private async Task<bool> StartRightVideoSourceCore(string cameraAddress, string preferredBackend)
    {
        if (string.IsNullOrEmpty(cameraAddress))
            return false;

        if (_pipeline.VideoSource == null)
        {
            SingleCameraSource cam;
            if (string.IsNullOrEmpty(preferredBackend))
                cam = await _singleCameraSourceFactory.CreateStart(cameraAddress).ConfigureAwait(false);
            else
                cam = await _singleCameraSourceFactory.CreateStart(cameraAddress, preferredBackend).ConfigureAwait(false);

            if (cam == null)
                return false;

            var source = new DualCameraSource();
            source.RightCam = cam;
            lock (_pipeline.SyncRoot)
            {
                _pipeline.VideoSource = source;
                _pipeline.ResetForCameraChange();
            }
            _currentRightAddress = cameraAddress;
            return true;
        }

        if (_pipeline.VideoSource is DualCameraSource dualCameraSource)
            if (cameraAddress == _currentLeftAddress && _currentLeftAddress != null)
            {
                var tmp = dualCameraSource.LeftCam;
                lock (_pipeline.SyncRoot)
                {
                    var unused = ReferenceEquals(dualCameraSource.RightCam, tmp)
                        ? null
                        : dualCameraSource.RightCam;
                    dualCameraSource.LeftCam = null;
                    dualCameraSource.RightCam = null;
                    dualCameraSource.Dispose();
                    unused?.Dispose();
                    _pipeline.VideoSource = tmp;
                    _pipeline.ResetForCameraChange();
                }
                _currentRightAddress = cameraAddress;
                return true;
            }
            else
            {
                var cam = await _singleCameraSourceFactory.CreateStart(cameraAddress, preferredBackend).ConfigureAwait(false);
                if (cam == null)
                    return false;
                IVideoSource? previous;
                lock (_pipeline.SyncRoot)
                {
                    previous = dualCameraSource.RightCam;
                    dualCameraSource.RightCam = cam;
                    _pipeline.ResetForCameraChange();
                }
                previous?.Dispose();
                _currentRightAddress = cameraAddress;
                return true;
            }

        if (_pipeline.VideoSource is SingleCameraSource singleCameraSource)
        {
            if (_currentRightAddress == cameraAddress && _currentRightAddress != null)
                return true;

            var cam = await _singleCameraSourceFactory.CreateStart(cameraAddress, preferredBackend).ConfigureAwait(false);
            if (cam == null)
                return false;

            var source = new DualCameraSource();
            source.RightCam = cam;
            source.LeftCam = singleCameraSource;
            lock (_pipeline.SyncRoot)
            {
                _pipeline.VideoSource = source;
                _pipeline.ResetForCameraChange();
            }

            _currentRightAddress = cameraAddress;
            return true;
        }

        return true;
    }

    public async Task<bool> TryStartLeftIfNotRunning(string cameraAddress, string preferredBackend)
    {
        await _videoSourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (_pipeline.VideoSource)
            {
                case SingleCameraSource when _currentLeftAddress == cameraAddress:
                case DualCameraSource { LeftCam: not null }:
                    return true;
                default:
                    return await StartLeftVideoSourceCore(cameraAddress, preferredBackend).ConfigureAwait(false);
            }
        }
        finally
        {
            _videoSourceGate.Release();
        }
    }
    public async Task<bool> TryStartRightIfNotRunning(string cameraAddress, string preferredBackend)
    {
        await _videoSourceGate.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (_pipeline.VideoSource)
            {
                case SingleCameraSource when _currentRightAddress == cameraAddress:
                case DualCameraSource { RightCam: not null }:
                    return true;
                default:
                    return await StartRightVideoSourceCore(cameraAddress, preferredBackend).ConfigureAwait(false);
            }
        }
        finally
        {
            _videoSourceGate.Release();
        }
    }
    public void StopLeftCamera()
    {
        _videoSourceGate.Wait();
        try
        {
            _currentLeftAddress = null;
            lock (_pipeline.SyncRoot)
            {
                if (_pipeline.VideoSource is DualCameraSource dualCameraSource)
                {
                    dualCameraSource.LeftCam?.Dispose();
                    dualCameraSource.LeftCam = null;
                    _pipeline.ResetForCameraChange();
                }

                if (_pipeline.VideoSource is SingleCameraSource singleCameraSource)
                {
                    singleCameraSource.Dispose();
                    _pipeline.VideoSource = null;
                    _currentRightAddress = null;
                    _pipeline.ResetForCameraChange();
                }
            }
        }
        finally { _videoSourceGate.Release(); }
    }

    public void StopRightCamera()
    {
        _videoSourceGate.Wait();
        try
        {
            _currentRightAddress = null;
            lock (_pipeline.SyncRoot)
            {
                if (_pipeline.VideoSource is DualCameraSource dualCameraSource)
                {
                    dualCameraSource.RightCam?.Dispose();
                    dualCameraSource.RightCam = null;
                    _pipeline.ResetForCameraChange();
                }

                if (_pipeline.VideoSource is SingleCameraSource singleCameraSource)
                {
                    singleCameraSource.Dispose();
                    _pipeline.VideoSource = null;
                    _currentLeftAddress = null;
                    _pipeline.ResetForCameraChange();
                }
            }
        }
        finally { _videoSourceGate.Release(); }
    }

    public void StopAllCameras()
    {
        _videoSourceGate.Wait();
        try
        {
            _currentRightAddress = null;
            _currentLeftAddress = null;
            lock (_pipeline.SyncRoot)
            {
                _pipeline.VideoSource?.Dispose();
                _pipeline.VideoSource = null;
                _pipeline.ResetForCameraChange();
            }
        }
        finally { _videoSourceGate.Release(); }
    }

    /// <summary>
    /// Stops only the eye feed(s) backed by a serial camera, releasing the serial handle so the
    /// firmware page can open it. For a DualCameraSource each eye is evaluated independently so a
    /// non-serial eye keeps running. UVC/IP eyes are left untouched.
    /// </summary>
    public bool StopSerialCameras()
    {
        _videoSourceGate.Wait();
        try
        {
            var stopped = false;
            lock (_pipeline.SyncRoot)
            {
                switch (_pipeline.VideoSource)
                {
                    case SingleCameraSource single when IsSerialAddress(single.Capture?.Source):
                        single.Dispose();
                        _pipeline.VideoSource = null;
                        _currentLeftAddress = null;
                        _currentRightAddress = null;
                        stopped = true;
                        break;
                    case DualCameraSource dual:
                        // Same physical serial cam used for both eyes -> one shared instance; null both
                        // refs after a single dispose so the right branch can't double-dispose it.
                        var shared = ReferenceEquals(dual.LeftCam, dual.RightCam);
                        if (dual.LeftCam is SingleCameraSource l && IsSerialAddress(l.Capture?.Source))
                        {
                            l.Dispose();
                            dual.LeftCam = null;
                            _currentLeftAddress = null;
                            stopped = true;
                            if (shared) { dual.RightCam = null; _currentRightAddress = null; }
                        }
                        if (dual.RightCam is SingleCameraSource r && IsSerialAddress(r.Capture?.Source))
                        {
                            r.Dispose();
                            dual.RightCam = null;
                            _currentRightAddress = null;
                            stopped = true;
                        }
                        if (dual.LeftCam == null && dual.RightCam == null)
                        {
                            dual.Dispose();
                            _pipeline.VideoSource = null;
                        }
                        break;
                }
                if (stopped)
                    _pipeline.ResetForCameraChange();
            }
            return stopped;
        }
        finally { _videoSourceGate.Release(); }
    }

    // Mirrors SerialCameraCaptureFactory.CanConnect: serial camera addresses are COM* / /dev/tty* / /dev/cu*.
    private static bool IsSerialAddress(string? address)
    {
        if (string.IsNullOrEmpty(address)) return false;
        var a = address.ToLowerInvariant();
        return a.StartsWith("com") || a.StartsWith("/dev/tty") || a.StartsWith("/dev/cu");
    }

    public bool IsUsingSameCamera()
    {
        return _currentLeftAddress == _currentRightAddress && _currentLeftAddress != null;
    }

    public void SetFilter(IFilter? filter)
    {
        lock (_pipeline.SyncRoot)
        {
            (_eyeBackend.Filter as IDisposable)?.Dispose();
            _eyeBackend.Filter = filter;
            _pipeline.ResetTemporalState();
        }
    }
}
