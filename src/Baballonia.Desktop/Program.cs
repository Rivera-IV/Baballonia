using Avalonia;
using Baballonia.Contracts;
using Baballonia.Desktop.Calibration;
using Baballonia.Desktop.Captures;
using Baballonia.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using Velopack;
using TrainerService = Baballonia.Desktop.Calibration.TrainerService;

namespace Baballonia.Desktop;

sealed class Program
{
    /* Baballonia needs to be single-instanced, because:
     * a) There's no real reason to have several Baballonia instances running at a time.
     * b) Some users have opened/minimized multiple Baballonia instances on accident, breaking OSC. This is no good!
     * In the future, a file-lock mechanism might prove more robust (not to mention Linux support?),
     * but a Mutex should do the job until we have reason to roll one ourselves. Sources:
     * https://stackoverflow.com/questions/6486195/ensuring-only-one-application-instance
     * https://github.com/AvaloniaUI/Avalonia/discussions/17854#discussioncomment-11700510 */
    private static readonly Mutex Mutex = new(false, "baballonia-unique-id");

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack executes fast-exit lifecycle hooks through this entry point. It must run before
        // the mutex and every other normal-startup action. Pending updates are never applied without
        // the user's explicit confirmation in Baballonia Next.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        // Give the mutex some time to think, check if another process with an identical mutex ID exists
        bool ownsMutex;
        try
        {
            ownsMutex = Mutex.WaitOne(TimeSpan.FromSeconds(2), false);
        }
        catch (AbandonedMutexException)
        {
            // The previous process terminated while owning the mutex (for example after a camera
            // shutdown hang). WaitOne grants ownership before throwing, so startup can safely
            // recover instead of crashing on every first restart after that failure.
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            return 75; // Exit code for BSD's EX_TEMPFAIL, invite the user to try again later
        }

        App.RegisterRequiredPlatformServices<
            OverlayTrainerService,
            DesktopDeviceEnumerator,
            DesktopConnector
        >();

        App.RegisterPlatformSpecificServices(collection =>
        {
            collection.AddSingleton<IOverlayProgram, OverlayProgram>();
            collection.AddSingleton<ITrainerService, TrainerService>();
            collection.AddSingleton<IAppUpdateService, VelopackUpdateService>();
            collection.AddSingleton<EyeCaptureStepFactory>();
            collection.AddSingleton<EyeCalibration>();
        });

        try
        {
            var builder = BuildAvaloniaApp();
            return builder.StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Mutex.ReleaseMutex();
            Mutex.Dispose();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    private static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
