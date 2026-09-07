using ClaudeStatus.Update;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Sources;

namespace ClaudeStatus.App.Update;

/// <summary>
/// Checks GitHub Releases for a newer version and stages it for the next start.
/// </summary>
/// <remarks>
/// <para>
/// The user chose "check, then apply on quit". That is exactly Velopack's own
/// model and needs almost no code: <c>DownloadUpdatesAsync</c> stages the new
/// version beside the installed one, and <c>VelopackApp.Build().Run()</c> in
/// <see cref="Program"/> installs it on the next launch. The app is never
/// restarted underneath anyone; <see cref="ApplyAndRestart"/> exists only so
/// somebody who wants it now can skip the wait.
/// </para>
/// <para>
/// The feed is read anonymously. An access token would raise GitHub's rate limit
/// from 60 requests an hour to 5000, and would also mean shipping a credential
/// inside the application - which is the one thing this project does not do
/// (<c>docs/manual.md</c> §8). At one check every six hours, sixty is ample.
/// </para>
/// </remarks>
public sealed class VelopackUpdateService : IUpdateService
{
    /// <summary>
    /// How long between checks.
    /// </summary>
    /// <remarks>
    /// Deliberately unrelated to the usage poll interval. That one is about how
    /// fresh a number is and runs every minute or two; this is about a release
    /// that happens a few times a year, and every six hours is already generous.
    /// </remarks>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly UpdateManager _manager;
    private readonly ILogger<VelopackUpdateService> _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _staged;
    private bool _disposed;

    /// <param name="repositoryUrl">The GitHub repository holding the releases.</param>
    /// <param name="log">Where failures go. They are logged, never surfaced.</param>
    public VelopackUpdateService(string repositoryUrl, ILogger<VelopackUpdateService>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        _log = log ?? NullLogger<VelopackUpdateService>.Instance;

        // A GitHub repository needs GithubSource, which knows how to turn a repo
        // into a releases API call. Anything else - a plain folder or a static
        // web directory - is what UpdateManager's own string overload handles.
        //
        // The distinction exists so the update path can be exercised against a
        // local folder of real packages, which is the only way to test it without
        // publishing to the production repository. It opens nothing up: the value
        // is a compile-time constant in a shipped build, never configuration.
        _manager = IsHttpUrl(repositoryUrl)
            ? new UpdateManager(new GithubSource(repositoryUrl, null, false))
            : new UpdateManager(repositoryUrl);

        // IsInstalled is false when running from bin/, from a zip, or anywhere
        // Velopack did not put us. There is no update path from there, so the
        // service reports Unsupported rather than failing a check every six hours.
        Status = _manager.IsInstalled ? new UpdateStatus(UpdateState.Idle) : UpdateStatus.Unsupported;
    }

    /// <summary>Whether a feed location is a web URL rather than a path.</summary>
    internal static bool IsHttpUrl(string feed)
        => Uri.TryCreate(feed, UriKind.Absolute, out Uri? uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <inheritdoc />
    public UpdateStatus Status { get; private set; }

    /// <inheritdoc />
    public event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>The version this build reports, for the Info window.</summary>
    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    /// <inheritdoc />
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_disposed || Status.State == UpdateState.Unsupported)
        {
            return;
        }

        // One check at a time. The timer and a manual check can otherwise overlap
        // and race each other through the status transitions.
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            // Already staged. Re-downloading the same version on every tick would
            // burn bandwidth to reach the state we are already in.
            if (_staged is not null)
            {
                return;
            }

            Publish(new UpdateStatus(UpdateState.Checking));

            UpdateInfo? available = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (available is null)
            {
                Publish(new UpdateStatus(UpdateState.Idle));
                return;
            }

            string version = available.TargetFullRelease.Version.ToString();
            _log.LogInformation("Update available: {Version}. Downloading.", version);
            Publish(new UpdateStatus(UpdateState.Downloading, version));

            await _manager.DownloadUpdatesAsync(available, cancelToken: ct).ConfigureAwait(false);

            _staged = available;
            _log.LogInformation("Update {Version} staged; it will apply on next start.", version);
            Publish(new UpdateStatus(UpdateState.ReadyToApply, version));
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a failure. Leave the status where it was.
            throw;
        }
        catch (Exception ex)
        {
            // Deliberately broad. An update check runs unprompted in the
            // background, so no failure of it may ever reach the user as a dialog
            // or take the app down - the endpoint being down, GitHub rate limiting
            // us and a corrupt download all mean the same thing here: try later.
            _log.LogWarning(ex, "Update check failed. Will retry at the next interval.");
            Publish(new UpdateStatus(UpdateState.Failed));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void ApplyAndRestart()
    {
        if (_disposed || _staged is null)
        {
            return;
        }

        _log.LogInformation("Applying update and restarting at the user's request.");

        // Does not return: the process is replaced.
        _manager.ApplyUpdatesAndRestart(_staged.TargetFullRelease);
    }

    private void Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
