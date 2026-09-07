namespace ClaudeStatus.Security;

/// <summary>
/// A token source that may answer from something it already holds.
/// </summary>
/// <remarks>
/// <para>
/// Reading a credential is normally free - a file, a decrypt - and a source that
/// re-reads every time is the simplest thing that can work. The macOS Keychain
/// breaks that assumption: reading another application's item can cost a
/// permission prompt, so the source has to hold what it read rather than pay
/// again on the next poll.
/// </para>
/// <para>
/// Which leaves one problem this interface exists to solve. A cache is invisible
/// to the user, so "Test" would report on a token read minutes ago rather than on
/// the credential as it stands now - and a user pressing Test after fixing
/// something would be told the old answer. <see cref="Forget"/> is how a
/// deliberate act reaches past the cache.
/// </para>
/// </remarks>
public interface ICachingAccessTokenSource : IAccessTokenSource
{
    /// <summary>
    /// Drops everything held, so the next read goes back to the real source.
    /// </summary>
    /// <remarks>
    /// Called for a user-initiated check, never on the poll path - the point of
    /// the cache is that polling does not reach the source.
    /// </remarks>
    void Forget();
}
