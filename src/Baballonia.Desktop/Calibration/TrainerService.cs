using Baballonia.Contracts;
using Microsoft.Extensions.Logging;
using OverlaySDK.Packets;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Desktop.Calibration;

public partial class TrainerService(ILogger<TrainerService> logger) : ITrainerService
{
    private readonly object _lock = new();

    private Process? _trainerProcess;
    public event Action<TrainerProgressReportPacket>? OnProgress;

    [GeneratedRegex(@"Batch\s+(\d+)/(\d+),\s+Loss:\s+([0-9.]+)")]
    private static partial Regex ParseBatchRegex();
    TrainerProgressReportPacket? ParseBatch(string line)
    {
        var match = ParseBatchRegex().Match(line);
        if (!match.Success)
            return null;
        logger.LogDebug(line);

        var currentBatch = int.Parse(match.Groups[1].Value);
        var totalBatches = int.Parse(match.Groups[2].Value);
        var loss = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        return new TrainerProgressReportPacket("Batch", currentBatch, totalBatches, loss);
    }
    [GeneratedRegex(@"Epoch\s+(\d+)/(\d+)\s+completed\s+in\s+([\d.]+)s\.\s+Average\s+loss:\s+([\d.eE+-]+)")]
    private static partial Regex ParseEpochRegex();
    TrainerProgressReportPacket? ParseEpoch(string line)
    {
        var match = ParseEpochRegex().Match(line);
        if (!match.Success)
            return null;

        logger.LogDebug(line);

        var currentEpoch = int.Parse(match.Groups[1].Value);
        var totalEpochs = int.Parse(match.Groups[2].Value);
        var loss = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);

        return new TrainerProgressReportPacket("Epoch", currentEpoch, totalEpochs, loss);
    }

    [GeneratedRegex(@"===\s+Epoch\s+(\d+)/(\d+)\s+===")]
    private static partial Regex ParseGlobalEpochRegex();
    TrainerProgressReportPacket? ParseGlobalEpoch(string line)
    {
        var match = ParseGlobalEpochRegex().Match(line);
        if (!match.Success)
            return null;

        var currentEpoch = int.Parse(match.Groups[1].Value);
        var totalEpochs = int.Parse(match.Groups[2].Value);
        return new TrainerProgressReportPacket("GlobalEpoch", currentEpoch, totalEpochs, 0);
    }

    [GeneratedRegex(@"\s*Training\s+completed\s+successfully!\s*")]
    private static partial Regex ParseTrainingCompleteRegex();
    bool ParseTrainingComplete(string line)
    {
        var match = ParseTrainingCompleteRegex().Match(line);

        logger.LogDebug(line);

        return match.Success;
    }

    void NewLineEventHandler(object sender, DataReceivedEventArgs dataReceivedEventArgs)
    {
        logger.LogDebug(dataReceivedEventArgs.Data);
        if (dataReceivedEventArgs.Data == null)
            return;

        var progress = ParseGlobalEpoch(dataReceivedEventArgs.Data);
        if (progress == null)
            progress = ParseBatch(dataReceivedEventArgs.Data);
        if (progress == null)
            progress = ParseEpoch(dataReceivedEventArgs.Data);

        if (progress != null)
        {
            OnProgress?.Invoke(progress);
        }

        var isCompleted = ParseTrainingComplete(dataReceivedEventArgs.Data);
        if (isCompleted)
            return;
    }

    void ErrorLineEventHandler(object sender, DataReceivedEventArgs dataReceivedEventArgs)
    {
        if (!string.IsNullOrWhiteSpace(dataReceivedEventArgs.Data))
            logger.LogError("Trainer: {Error}", dataReceivedEventArgs.Data);
    }

    public void RunTraining(string usercalbinPath, string outputfilePath)
    {
        if (!File.Exists(usercalbinPath))
            throw new FileNotFoundException(usercalbinPath + " not found");

        var isWindows = OperatingSystem.IsWindows();
        var basePath = Path.Combine(AppContext.BaseDirectory, "Calibration", isWindows ? "Windows" : "Linux", "Trainer");
        var trainerPath = Path.Combine(basePath, isWindows ? $"BabbleTrainer.exe" : $"BabbleTrainer");
        if (!File.Exists(trainerPath))
            throw new FileNotFoundException(trainerPath + " not found");


        lock (_lock)
        {
            if (_trainerProcess != null && _trainerProcess.HasExited)
                _trainerProcess = null;

            if (_trainerProcess != null)
                throw new Exception("Training process already running");

            // Wrap both args in quotes to handle spaces in user names
            var launchArgs = $"\"{usercalbinPath}\" \"{outputfilePath}\"";
            var startInfo = new ProcessStartInfo
            {
                FileName = trainerPath,
                Arguments = launchArgs,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = basePath,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            _trainerProcess = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true,
            };
            _trainerProcess.OutputDataReceived += NewLineEventHandler;
            _trainerProcess.ErrorDataReceived += ErrorLineEventHandler;
            _trainerProcess.Exited += (sender, args) =>
            {
                if (_trainerProcess?.ExitCode is int exitCode && exitCode != 0)
                    logger.LogError("Trainer exited with code {ExitCode}", exitCode);
                Interlocked.Exchange(ref _trainerProcess, null);
            };

            _trainerProcess.Start();
            _trainerProcess.BeginOutputReadLine();
            _trainerProcess.BeginErrorReadLine();
        }
    }

    public async Task WaitAsync()
    {
        Process? process;
        lock (_lock)
        {
            process = _trainerProcess;
        }

        if (process == null)
            return;

        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Trainer exited with code {process.ExitCode}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _trainerProcess?.Kill();
        }
    }
}
