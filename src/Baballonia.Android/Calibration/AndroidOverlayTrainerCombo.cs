using System;
using System.Threading.Tasks;
using Baballonia.Contracts;
using Baballonia.Helpers;
namespace Baballonia.Android.Calibration;

public class AndroidOverlayTrainerCombo : IVROverlay, IVRCalibrator, IDisposable
{
    public event Action<CalibrationTrainingProgress>? TrainingProgressChanged;

    public Task EyeTrackingCalibrationRequested(string calibrationRoutine)
    {
        return Task.CompletedTask;
    }

    public Task<(bool success, string status)> EyeTrackingCalibrationRequested(CalibrationRoutine.Routines calibrationRoutine)
    {
        return Task.FromResult((false, "Not Supported"));
    }

    public void Dispose()
    {

    }
}
