using Baballonia.Helpers;
using System;
using System.Threading.Tasks;

namespace Baballonia.Contracts;

public enum CalibrationTrainingState
{
    Preparing,
    Running,
    Completed,
}

public sealed record CalibrationTrainingProgress(
    CalibrationTrainingState State,
    double Progress,
    int Epoch = 0,
    int TotalEpochs = 0,
    int Batch = 0,
    int TotalBatches = 0,
    double Loss = 0);

public interface IVROverlay : IDisposable
{
    public event Action<CalibrationTrainingProgress>? TrainingProgressChanged;
    public Task<(bool success, string status)> EyeTrackingCalibrationRequested(CalibrationRoutine.Routines calibrationRoutine);
}
