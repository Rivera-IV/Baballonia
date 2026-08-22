using Avalonia.Threading;
using Baballonia.Services;
using Baballonia.Services.Inference.VideoSources;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.ViewModels.SplitViewPane;

/// <summary>One row in the per-thread CPU table.</summary>
public partial class ThreadCpuRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _cpuPercent;
}

/// <summary>One camera card (eye left/right or mouth).</summary>
public partial class CameraRow : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _deliveredFps;
    [ObservableProperty] private string _droppedSummary = "—";
    [ObservableProperty] private double _negotiatedFps;
    [ObservableProperty] private string _resolution = "—";
    [ObservableProperty] private string _format = "—";
}

/// <summary>
/// Live performance page. Rates are monotonic counter deltas measured against a Stopwatch clock and
/// EWMA-smoothed; per-thread CPU comes from the always-on <see cref="ThreadProfiler"/>.
/// </summary>
public partial class DebugViewModel : ViewModelBase, IDisposable
{
    private const int MaxThreadRows = 14;
    private const int DiagnosticDurationSeconds = 20;

    // EWMA weight per 500 ms sample: smooths jitter but still reacts within ~1-2 s.
    private const double Smoothing = 0.3;

    private static readonly long DropWindowTicks = 60 * Stopwatch.Frequency;

    private sealed class CamState
    {
        public long PrevFrames;
        public double FpsEwma;
        public readonly Queue<(long Timestamp, long Dropped)> DropWindow = new();
    }

    private readonly PipelineMetrics _metrics;
    private readonly ThreadProfiler _profiler;
    private readonly EyeDiagnosticRecorder _eyeDiagnosticRecorder;
    private readonly DiagnosticPackageEncryptor _diagnosticEncryptor;
    private readonly DiagnosticReportUploadService _diagnosticUploadService;
    private readonly ILogger<DebugViewModel> _logger;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<SingleCameraSource, CamState> _camStates = new();
    private readonly SemaphoreSlim _diagnosticGate = new(1, 1);
    private CancellationTokenSource? _diagnosticTimerCancellation;
    private string? _lastDiagnosticPackagePath;
    private EncryptedDiagnosticPackage? _lastEncryptedPackage;
    private DiagnosticUploadReceipt? _lastUploadReceipt;

    private long _prevUi, _prevEye, _prevFace, _prevRender;
    private long _prevTimestamp;

    // Incremented once per compositor frame by the view; surfaces Avalonia's render-thread fps.
    public long RenderTicks;

    [ObservableProperty] private double _renderFps;
    [ObservableProperty] private double _uiLoopFps;
    [ObservableProperty] private double _eyeInferenceFps;
    [ObservableProperty] private double _faceInferenceFps;

    // CPU hotspots.
    [ObservableProperty] private double _processCpuPercent;
    [ObservableProperty] private int _processorCount;
    public ObservableCollection<ThreadCpuRow> Threads { get; } = new();

    // One card per active camera (eye left/right or mouth).
    public ObservableCollection<CameraRow> Cameras { get; } = new();

    // Eye pipeline stage timings (ms).
    [ObservableProperty] private double _eyeCaptureMs;
    [ObservableProperty] private double _eyeTransformMs;
    [ObservableProperty] private double _eyeInferenceMs;
    [ObservableProperty] private double _eyePostMs;

    // Face pipeline stage timings (ms).
    [ObservableProperty] private double _faceCaptureMs;
    [ObservableProperty] private double _faceTransformMs;
    [ObservableProperty] private double _faceInferenceMs;
    [ObservableProperty] private double _facePostMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartEyeDiagnostic))]
    private bool _diagnosticConsent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUploadEyeDiagnostic))]
    private bool _diagnosticUploadConsent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartEyeDiagnostic))]
    [NotifyPropertyChangedFor(nameof(CanStopEyeDiagnostic))]
    private bool _isEyeDiagnosticRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartEyeDiagnostic))]
    [NotifyPropertyChangedFor(nameof(CanStopEyeDiagnostic))]
    [NotifyPropertyChangedFor(nameof(CanUploadEyeDiagnostic))]
    [NotifyPropertyChangedFor(nameof(CanDeleteUploadedDiagnostic))]
    private bool _isEyeDiagnosticBusy;

    [ObservableProperty] private int _diagnosticSecondsRemaining;
    [ObservableProperty] private string _diagnosticNotes = "";
    [ObservableProperty] private string _diagnosticStatus =
        "Ready. Start tracking first, then begin the 20-second recording.";

    public bool CanStartEyeDiagnostic =>
        DiagnosticConsent && !IsEyeDiagnosticRecording && !IsEyeDiagnosticBusy &&
        !_eyeDiagnosticRecorder.IsRecording;

    public bool CanStopEyeDiagnostic => IsEyeDiagnosticRecording && !IsEyeDiagnosticBusy;

    public bool CanUploadEyeDiagnostic =>
        DiagnosticUploadConsent && _lastDiagnosticPackagePath != null &&
        _lastUploadReceipt == null && !IsEyeDiagnosticRecording && !IsEyeDiagnosticBusy;

    public bool CanDeleteUploadedDiagnostic =>
        _lastUploadReceipt != null && !IsEyeDiagnosticRecording && !IsEyeDiagnosticBusy;

    public DebugViewModel(
        PipelineMetrics metrics,
        ThreadProfiler profiler,
        EyeDiagnosticRecorder eyeDiagnosticRecorder,
        DiagnosticPackageEncryptor diagnosticEncryptor,
        DiagnosticReportUploadService diagnosticUploadService,
        ILogger<DebugViewModel> logger)
    {
        _metrics = metrics;
        _profiler = profiler;
        _eyeDiagnosticRecorder = eyeDiagnosticRecorder;
        _diagnosticEncryptor = diagnosticEncryptor;
        _diagnosticUploadService = diagnosticUploadService;
        _logger = logger;
        ProcessorCount = profiler.ProcessorCount;
        _prevTimestamp = Stopwatch.GetTimestamp();
        _prevUi = metrics.UiTicks;
        _prevEye = metrics.EyeInferences;
        _prevFace = metrics.FaceInferences;
        _prevRender = RenderTicks;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += Sample;
        _timer.Start();
    }

    [RelayCommand]
    private async Task StartEyeDiagnosticAsync()
    {
        if (!CanStartEyeDiagnostic)
            return;

        try
        {
            _eyeDiagnosticRecorder.StartLiveRecording();
            IsEyeDiagnosticRecording = true;
            DiagnosticSecondsRemaining = DiagnosticDurationSeconds;
            DiagnosticStatus =
                "Recording eye images and synchronized model values. Reproduce the problem now.";
            _diagnosticTimerCancellation?.Dispose();
            _diagnosticTimerCancellation = new CancellationTokenSource();
            var token = _diagnosticTimerCancellation.Token;

            for (var remaining = DiagnosticDurationSeconds; remaining > 0; remaining--)
            {
                DiagnosticSecondsRemaining = remaining;
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }

            await CompleteEyeDiagnosticAsync();
        }
        catch (OperationCanceledException)
        {
            // Manual stop owns finalization.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not record the eye diagnostic package");
            DiagnosticStatus = $"Diagnostic failed: {exception.Message}";
            IsEyeDiagnosticRecording = false;
            IsEyeDiagnosticBusy = false;
        }
    }

    [RelayCommand]
    private async Task StopEyeDiagnosticAsync()
    {
        if (!CanStopEyeDiagnostic)
            return;
        _diagnosticTimerCancellation?.Cancel();
        await CompleteEyeDiagnosticAsync();
    }

    [RelayCommand]
    private void OpenEyeDiagnosticFolder()
    {
        var directory = _lastDiagnosticPackagePath == null
            ? Path.Combine(Utils.UserAccessibleDataDirectory, "Diagnostics")
            : Path.GetDirectoryName(_lastDiagnosticPackagePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not open the eye diagnostic export directory");
        }
    }

    [RelayCommand]
    private async Task UploadEyeDiagnosticAsync()
    {
        if (!CanUploadEyeDiagnostic || _lastDiagnosticPackagePath == null)
            return;

        await _diagnosticGate.WaitAsync();
        try
        {
            IsEyeDiagnosticBusy = true;
            DiagnosticStatus = "Encrypting the report locally. The unencrypted ZIP is not uploaded...";
            _lastEncryptedPackage ??=
                await _diagnosticEncryptor.EncryptAsync(_lastDiagnosticPackagePath);
            _lastDiagnosticPackagePath = _lastEncryptedPackage.FilePath;

            DiagnosticStatus = "Uploading the end-to-end encrypted report...";
            _lastUploadReceipt = await _diagnosticUploadService.UploadAsync(_lastEncryptedPackage);
            DiagnosticStatus =
                $"Encrypted report uploaded. Report ID: {_lastUploadReceipt.ReportId}. " +
                $"Scheduled deletion: {_lastUploadReceipt.ExpiresAt.LocalDateTime:g}. " +
                $"A deletion receipt was saved at {_lastUploadReceipt.LocalReceiptPath}";
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not encrypt or upload the eye diagnostic report");
            DiagnosticStatus = $"Encrypted report upload failed: {exception.Message}";
        }
        finally
        {
            IsEyeDiagnosticBusy = false;
            _diagnosticGate.Release();
            OnPropertyChanged(nameof(CanUploadEyeDiagnostic));
            OnPropertyChanged(nameof(CanDeleteUploadedDiagnostic));
        }
    }

    [RelayCommand]
    private async Task DeleteUploadedDiagnosticAsync()
    {
        if (!CanDeleteUploadedDiagnostic || _lastUploadReceipt == null)
            return;

        await _diagnosticGate.WaitAsync();
        try
        {
            IsEyeDiagnosticBusy = true;
            DiagnosticStatus = "Deleting the encrypted report from the server...";
            await _diagnosticUploadService.DeleteAsync(
                _lastUploadReceipt.ReportId, _lastUploadReceipt.DeleteToken);
            DiagnosticStatus =
                $"Report {_lastUploadReceipt.ReportId} was deleted from the server. " +
                "Local diagnostic files were kept.";
            _lastUploadReceipt = null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not delete the uploaded eye diagnostic report");
            DiagnosticStatus = $"Could not delete the encrypted report: {exception.Message}";
        }
        finally
        {
            IsEyeDiagnosticBusy = false;
            _diagnosticGate.Release();
            OnPropertyChanged(nameof(CanUploadEyeDiagnostic));
            OnPropertyChanged(nameof(CanDeleteUploadedDiagnostic));
        }
    }

    private async Task CompleteEyeDiagnosticAsync()
    {
        await _diagnosticGate.WaitAsync();
        try
        {
            if (!IsEyeDiagnosticRecording || !_eyeDiagnosticRecorder.IsLiveRecording)
                return;

            IsEyeDiagnosticBusy = true;
            DiagnosticStatus = "Finishing the recording and creating the ZIP package...";
            var package = await _eyeDiagnosticRecorder.StopLiveAndCreatePackageAsync(DiagnosticNotes);
            IsEyeDiagnosticRecording = false;
            DiagnosticSecondsRemaining = 0;
            if (package == null)
            {
                DiagnosticStatus = "No live diagnostic recording was available.";
                return;
            }

            _lastDiagnosticPackagePath = package.ZipPath;
            _lastEncryptedPackage = null;
            _lastUploadReceipt = null;
            DiagnosticUploadConsent = false;
            var modelStatus = package.IncludedEyeModel
                ? "The selected eye model is included."
                : "The selected eye model could not be included.";
            DiagnosticStatus =
                $"ZIP ready ({package.SampleCount} frames): {package.ZipPath} {modelStatus}";
            OpenEyeDiagnosticFolder();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not finalize the eye diagnostic package");
            DiagnosticStatus = $"Could not create the diagnostic ZIP: {exception.Message}";
            IsEyeDiagnosticRecording = _eyeDiagnosticRecorder.IsLiveRecording;
        }
        finally
        {
            IsEyeDiagnosticBusy = false;
            _diagnosticGate.Release();
            OnPropertyChanged(nameof(CanStartEyeDiagnostic));
            OnPropertyChanged(nameof(CanStopEyeDiagnostic));
            OnPropertyChanged(nameof(CanUploadEyeDiagnostic));
            OnPropertyChanged(nameof(CanDeleteUploadedDiagnostic));
        }
    }

    /// <summary>EWMA that seeds from the first sample (so it converges immediately, not from zero).</summary>
    private static double Smooth(double previous, double sample) =>
        previous <= 0 ? sample : previous + Smoothing * (sample - previous);

    private void Sample(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _prevTimestamp) / (double)Stopwatch.Frequency;
        if (dt <= 0)
            return;

        var eye = _metrics.EyeInferences;
        RenderFps = Smooth(RenderFps, (RenderTicks - _prevRender) / dt);
        UiLoopFps = Smooth(UiLoopFps, (_metrics.UiTicks - _prevUi) / dt);
        EyeInferenceFps = Smooth(EyeInferenceFps, (eye - _prevEye) / dt);
        FaceInferenceFps = Smooth(FaceInferenceFps, (_metrics.FaceInferences - _prevFace) / dt);

        UpdateCameras(now, dt);

        EyeCaptureMs = _metrics.EyeCaptureMs;
        EyeTransformMs = _metrics.EyeTransformMs;
        EyeInferenceMs = _metrics.EyeInferenceMs;
        EyePostMs = _metrics.EyePostMs;

        FaceCaptureMs = _metrics.FaceCaptureMs;
        FaceTransformMs = _metrics.FaceTransformMs;
        FaceInferenceMs = _metrics.FaceInferenceMs;
        FacePostMs = _metrics.FacePostMs;

        UpdateThreads();

        _prevRender = RenderTicks;
        _prevUi = _metrics.UiTicks;
        _prevEye = eye;
        _prevFace = _metrics.FaceInferences;
        _prevTimestamp = now;
    }

    private void UpdateCameras(long now, double dt)
    {
        var current = new List<(string Label, SingleCameraSource Source)>(3);
        if (_metrics.EyeDual)
        {
            if (_metrics.EyeLeftSource is { } el) current.Add(("Eye (left)", el));
            if (_metrics.EyeRightSource is { } er) current.Add(("Eye (right)", er));
        }
        else if (_metrics.EyeLeftSource is { } single)
        {
            current.Add(("Eye", single));
        }
        if (_metrics.FaceSource is { } mouth) current.Add(("Mouth", mouth));

        while (Cameras.Count < current.Count) Cameras.Add(new CameraRow());
        while (Cameras.Count > current.Count) Cameras.RemoveAt(Cameras.Count - 1);

        var active = new HashSet<SingleCameraSource>();
        for (var i = 0; i < current.Count; i++)
        {
            var (label, src) = current[i];
            active.Add(src);
            var cap = src.Capture;

            if (!_camStates.TryGetValue(src, out var st))
            {
                st = new CamState { PrevFrames = cap.FramesProduced };
                st.DropWindow.Enqueue((now, cap.FramesDropped));
                _camStates[src] = st;
            }

            var frames = cap.FramesProduced;
            st.FpsEwma = Smooth(st.FpsEwma, (frames - st.PrevFrames) / dt);
            st.PrevFrames = frames;

            var drop = cap.FramesDropped;
            st.DropWindow.Enqueue((now, drop));
            while (st.DropWindow.Count > 1 && st.DropWindow.Peek().Timestamp < now - DropWindowTicks)
                st.DropWindow.Dequeue();
            var lost = Math.Max(0, drop - st.DropWindow.Peek().Dropped);

            var row = Cameras[i];
            row.Name = label;
            row.DeliveredFps = st.FpsEwma;
            row.DroppedSummary = $"{lost} frames";
            row.NegotiatedFps = cap.TargetFps;
            row.Resolution = src.CameraSize.Width > 0 ? $"{src.CameraSize.Width} x {src.CameraSize.Height}" : "—";
            row.Format = string.IsNullOrEmpty(cap.PixelFormatName) ? "—" : cap.PixelFormatName;
        }

        // Drop state for cameras that went away (swap/disconnect).
        if (_camStates.Count > active.Count)
        {
            var stale = new List<SingleCameraSource>();
            foreach (var key in _camStates.Keys)
                if (!active.Contains(key)) stale.Add(key);
            foreach (var key in stale) _camStates.Remove(key);
        }
    }

    /// <summary>Reconcile the hottest threads into the bound collection, reusing rows to avoid UI churn.</summary>
    private void UpdateThreads()
    {
        ProcessCpuPercent = _profiler.ProcessCpuPercent;

        var samples = _profiler.Snapshot;
        var show = Math.Min(samples.Count, MaxThreadRows);

        while (Threads.Count < show) Threads.Add(new ThreadCpuRow());
        while (Threads.Count > show) Threads.RemoveAt(Threads.Count - 1);

        for (var i = 0; i < show; i++)
        {
            var s = samples[i];
            Threads[i].Name = s.Count > 1 ? $"{s.Name} (×{s.Count})" : s.Name;
            Threads[i].CpuPercent = s.CpuPercent;
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= Sample;
        // A timed diagnostic owns this view model until it has finalized its ZIP. Do not dispose
        // its cancellation source or gate merely because the user navigated away from the page.
    }
}
