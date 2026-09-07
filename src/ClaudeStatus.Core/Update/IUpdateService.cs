namespace ClaudeStatus.Update;

/// <summary>Where the app is in the check-download-apply cycle.</summary>
public enum UpdateState
{
    /// <summary>
    /// Updating is not possible here, and no amount of retrying will change that.
    /// </summary>
    /// <remarks>
    /// A development build run from <c>bin/</c>, a portable copy, or a platform we
    /// do not package for. Distinct from <see cref="Failed"/> because there is
    /// nothing wrong and nothing to report: the UI shows nothing at all.
    /// </remarks>
    Unsupported = 0,

    /// <summary>Installed and up to date, as far as the last check could tell.</summary>
    Idle = 1,

    /// <summary>Asking the release feed what the newest version is.</summary>
    Checking = 2,

    /// <summary>A newer version exists and is being fetched in the background.</summary>
    Downloading = 3,

    /// <summary>
    /// A newer version is downloaded and staged.
    /// </summary>
    /// <remarks>
    /// It installs itself the next time the app starts, so this state is an offer
    /// of a shortcut - restart now rather than whenever you next would - and never
    /// an instruction.
    /// </remarks>
    ReadyToApply = 4,

    /// <summary>
    /// The last attempt failed.
    /// </summary>
    /// <remarks>
    /// Almost always the network, or GitHub's 60-requests-an-hour limit for
    /// unauthenticated callers. Not worth interrupting anyone over: the next
    /// check simply tries again.
    /// </remarks>
    Failed = 5,
}

/// <summary>What the updater is doing, and which version it is talking about.</summary>
/// <param name="State">Where in the cycle it is.</param>
/// <param name="Version">
/// The version being offered, when there is one. A bare version string, not prose,
/// so it is safe for Core to produce (see <c>docs/manual.md</c> §8).
/// </param>
public sealed record UpdateStatus(UpdateState State, string? Version = null)
{
    /// <summary>Nothing to say to the user.</summary>
    public static UpdateStatus Unsupported { get; } = new(UpdateState.Unsupported);

    /// <summary>True when there is something worth putting in front of someone.</summary>
    public bool IsNoteworthy => State is UpdateState.Downloading or UpdateState.ReadyToApply;
}

/// <summary>
/// Checks for, downloads and applies application updates.
/// </summary>
/// <remarks>
/// <para>
/// An interface for the same reason as everything else platform-shaped: Core must
/// not reference Velopack, and a Linux build packaged some other way is then a new
/// implementation rather than a change here.
/// </para>
/// <para>
/// Nothing on this interface ever blocks on the network from a caller's thread.
/// <see cref="CheckAsync"/> is the only thing that touches it, and the UI watches
/// <see cref="StatusChanged"/> rather than awaiting anything.
/// </para>
/// </remarks>
public interface IUpdateService : IDisposable
{
    /// <summary>Where the cycle currently is.</summary>
    UpdateStatus Status { get; }

    /// <summary>Raised on every transition, on an unspecified thread.</summary>
    event EventHandler<UpdateStatus>? StatusChanged;

    /// <summary>
    /// Asks the feed whether there is a newer version, and downloads it if so.
    /// </summary>
    /// <remarks>
    /// Never throws for an ordinary failure - a refused connection or a rate limit
    /// moves the status to <see cref="UpdateState.Failed"/> and returns. An update
    /// check is not something a user asked for, so it must not be something they
    /// have to dismiss.
    /// </remarks>
    Task CheckAsync(CancellationToken ct = default);

    /// <summary>
    /// Applies a staged update and restarts, if one is staged.
    /// </summary>
    /// <remarks>
    /// Does nothing unless <see cref="Status"/> is
    /// <see cref="UpdateState.ReadyToApply"/>. The process does not return from
    /// this call when it does act.
    /// </remarks>
    void ApplyAndRestart();
}

/// <summary>
/// An updater for builds that cannot update themselves.
/// </summary>
/// <remarks>
/// Used when the setting is off, when the app is running from a build output
/// rather than an install, and on platforms with no package yet. It reports
/// <see cref="UpdateState.Unsupported"/> forever, which the UI renders as nothing,
/// so no caller needs a null check or a platform test.
/// </remarks>
public sealed class NullUpdateService : IUpdateService
{
    /// <inheritdoc />
    public UpdateStatus Status => UpdateStatus.Unsupported;

    /// <inheritdoc />
    /// <remarks>Never raised. Declared only to satisfy the interface.</remarks>
    public event EventHandler<UpdateStatus>? StatusChanged
    {
        add { }
        remove { }
    }

    /// <inheritdoc />
    public Task CheckAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    public void ApplyAndRestart()
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
