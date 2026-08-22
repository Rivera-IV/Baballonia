using Baballonia.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Baballonia.Desktop.Services;

public sealed class VelopackUpdateService(ILogger<VelopackUpdateService> logger) : IAppUpdateService
{
    private const string RepositoryUrl = "https://github.com/Elreen/Baballonia";
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    public Uri ReleasesUri { get; } = new($"{RepositoryUrl}/releases");

    public bool IsInstalled
    {
        get
        {
            try
            {
                return CreateManager(includePrereleases: false).IsInstalled;
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Could not determine Velopack installation state");
                return false;
            }
        }
    }

    public async Task<AppUpdateCheckResult> CheckForUpdatesAsync(
        bool includePrereleases,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            _manager = CreateManager(includePrereleases);
            _pendingUpdate = null;

            if (!_manager.IsInstalled)
            {
                return new AppUpdateCheckResult(AppUpdateCheckState.NotInstalled);
            }

            var update = await _manager.CheckForUpdatesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (update is null)
            {
                return new AppUpdateCheckResult(AppUpdateCheckState.UpToDate);
            }

            _pendingUpdate = update;
            var release = update.TargetFullRelease;
            return new AppUpdateCheckResult(
                AppUpdateCheckState.UpdateAvailable,
                release.Version.ToString(),
                release.NotesMarkdown);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Baballonia Next update check failed");
            return new AppUpdateCheckResult(
                AppUpdateCheckState.Failed,
                ErrorMessage: exception.Message);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task DownloadUpdateAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            if (_manager is null || _pendingUpdate is null)
            {
                throw new InvalidOperationException("No checked update is ready for download.");
            }

            await _manager.DownloadUpdatesAsync(
                _pendingUpdate,
                progress is null ? null : progress.Report,
                cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void ScheduleApplyAndRestart()
    {
        if (_manager is null || _pendingUpdate is null)
        {
            throw new InvalidOperationException("No downloaded update is ready to apply.");
        }

        // Let Baballonia shut its cameras and save settings normally. Velopack waits for this
        // process to exit, swaps the files, and then starts the updated version.
        _manager.WaitExitThenApplyUpdates(
            _pendingUpdate.TargetFullRelease,
            silent: false,
            restart: true);
    }

    private static UpdateManager CreateManager(bool includePrereleases) =>
        new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: includePrereleases));
}
