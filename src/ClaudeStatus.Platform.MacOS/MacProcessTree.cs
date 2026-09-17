namespace ClaudeStatus.Platform.MacOS;

/// <summary>
/// Picks the process that stands for a session's terminal out of its ancestors.
/// </summary>
/// <remarks>
/// <para>
/// Pure, over two lookups, so the rules can be tested against the process trees
/// real terminals produce without needing those terminals - and on any OS.
/// </para>
/// <para>
/// The rule is "the topmost ancestor inside the first application bundle above the
/// hook". The first process found inside a bundle is often not the application
/// itself: VS Code's integrated terminal hangs off a <c>Code Helper</c> that lives in
/// a nested bundle, and iTerm2's shells hang off an <c>iTermServer</c> that launchd,
/// not iTerm2, is the parent of. Climbing while the parent is still inside the same
/// outer bundle reaches the main application where there is one, and otherwise
/// leaves a process whose executable still names that bundle - which is enough for
/// the app side to find the running application.
/// </para>
/// </remarks>
internal static class MacProcessTree
{
    /// <summary>Deep enough for any real shell nesting; a guard against a cycle, not a limit anyone reaches.</summary>
    private const int MaxDepth = 64;

    private const string BundleMarker = ".app/Contents/MacOS/";

    /// <summary>
    /// The outermost <c>.app</c> bundle an executable belongs to, or null when it
    /// is not a bundle's executable.
    /// </summary>
    /// <remarks>
    /// Outermost, because helpers nest their own bundles inside the application's:
    /// <c>/Applications/Visual Studio Code.app/Contents/Frameworks/Code Helper.app/Contents/MacOS/Code Helper</c>
    /// belongs to <c>/Applications/Visual Studio Code.app</c>.
    /// </remarks>
    internal static string? OuterBundle(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath)
            || !executablePath.Contains(BundleMarker, StringComparison.Ordinal))
        {
            return null;
        }

        int end = executablePath.IndexOf(".app/", StringComparison.Ordinal) + ".app".Length;
        return executablePath[..end];
    }

    /// <summary>
    /// The pid standing for the terminal that <paramref name="hookProcessId"/> runs under, or 0.
    /// </summary>
    /// <param name="hookProcessId">The hook process itself. Its own executable is never the answer.</param>
    /// <param name="parentOf">A process's parent pid, or null when it cannot be read.</param>
    /// <param name="executableOf">A process's executable path, or null when it cannot be read.</param>
    internal static int FindTerminal(int hookProcessId, Func<int, int?> parentOf, Func<int, string?> executableOf)
    {
        ArgumentNullException.ThrowIfNull(parentOf);
        ArgumentNullException.ThrowIfNull(executableOf);

        int found = 0;
        string? foundBundle = null;
        int? current = parentOf(hookProcessId);

        for (int depth = 0; depth < MaxDepth && current is > 1; depth++)
        {
            string? bundle = OuterBundle(executableOf(current.Value));

            if (foundBundle is null)
            {
                if (bundle is not null)
                {
                    (found, foundBundle) = (current.Value, bundle);
                }
            }
            else if (string.Equals(bundle, foundBundle, StringComparison.Ordinal))
            {
                found = current.Value;
            }
            else
            {
                // Out of the terminal's bundle: whatever started it is not the terminal.
                break;
            }

            current = parentOf(current.Value);
        }

        return found;
    }
}
