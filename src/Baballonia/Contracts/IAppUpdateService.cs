using System;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Contracts;

public enum AppUpdateCheckState
{
    UpToDate,
    UpdateAvailable,
    NotInstalled,
    Failed
}

public sealed record AppUpdateCheckResult(
    AppUpdateCheckState State,
    string? Version = null,
    string? ReleaseNotes = null,
    string? ErrorMessage = null);

public interface IAppUpdateService
{
    Uri ReleasesUri { get; }

    bool IsInstalled { get; }

    Task<AppUpdateCheckResult> CheckForUpdatesAsync(
        bool includePrereleases,
        CancellationToken cancellationToken = default);

    Task DownloadUpdateAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    void ScheduleApplyAndRestart();
}
