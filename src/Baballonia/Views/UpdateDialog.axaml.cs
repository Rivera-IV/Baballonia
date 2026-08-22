using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Baballonia.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Views;

public partial class UpdateDialog : Window
{
    private IAppUpdateService _updateService = null!;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _downloadStarted;

    public UpdateDialog()
    {
        InitializeComponent();
    }

    private UpdateDialog(IAppUpdateService updateService, AppUpdateCheckResult update) : this()
    {
        _updateService = updateService;

        Title = Text("AppUpdate_WindowTitle", "Baballonia Next update");
        HeadingText.Text = Text("AppUpdate_Available", "A new version is available");
        VersionText.Text = string.Format(
            Text("AppUpdate_Version", "Baballonia Next {0} is ready."),
            update.Version ?? "");
        ReleaseNotesHeadingText.Text = Text("AppUpdate_ReleaseNotes", "What's new");
        ReleaseNotesText.Text = string.IsNullOrWhiteSpace(update.ReleaseNotes)
            ? Text("AppUpdate_NoReleaseNotes", "No release notes were provided for this version.")
            : update.ReleaseNotes;
        LaterButton.Content = Text("AppUpdate_Later", "Later");
        InstallButton.Content = Text("AppUpdate_Install", "Download and install");
        StatusText.Text = Text(
            "AppUpdate_ConsentNotice",
            "The update is downloaded only after you confirm. Your settings and models are kept.");

        LaterButton.Click += (_, _) => Close();
        InstallButton.Click += InstallButtonOnClick;
        Closing += (_, _) =>
        {
            if (_downloadStarted)
                _cancellation.Cancel();
        };
        Closed += (_, _) => _cancellation.Dispose();
    }

    public static Task ShowAsync(
        Window owner,
        IAppUpdateService updateService,
        AppUpdateCheckResult update) =>
        new UpdateDialog(updateService, update).ShowDialog(owner);

    private async void InstallButtonOnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_downloadStarted)
            return;

        _downloadStarted = true;
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        DownloadProgress.IsVisible = true;
        StatusText.Text = Text("AppUpdate_Downloading", "Downloading update…");

        try
        {
            var progress = new Progress<int>(value =>
            {
                DownloadProgress.Value = value;
                StatusText.Text = string.Format(
                    Text("AppUpdate_DownloadProgress", "Downloading update… {0}%"),
                    value);
            });

            await _updateService.DownloadUpdateAsync(progress, _cancellation.Token);
            DownloadProgress.Value = 100;
            StatusText.Text = Text(
                "AppUpdate_Restarting",
                "Download complete. Baballonia Next will restart to finish the update.");

            _updateService.ScheduleApplyAndRestart();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
        catch (OperationCanceledException)
        {
            Close();
        }
        catch (Exception exception)
        {
            _downloadStarted = false;
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
            StatusText.Text = string.Format(
                Text("AppUpdate_Failed", "The update failed: {0}"),
                exception.Message);
        }
    }

    private static string Text(string key, string fallback) =>
        Baballonia.Assets.Resources.ResourceManager.GetString(
            key,
            Baballonia.Assets.Resources.Culture) ?? fallback;
}
