namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The tests that change process-wide environment variables, run with nothing else
/// alongside them.
/// </summary>
/// <remarks>
/// <para>
/// An environment variable belongs to the process, not to the test that set it, and
/// xUnit runs test classes in parallel. A test that sets one and restores it in a
/// <c>finally</c> is tidy on its own and still changes it underneath every other test
/// running at that moment.
/// </para>
/// <para>
/// This is not hypothetical. <c>PlatformInfoTests</c> points <c>XDG_CONFIG_HOME</c> at
/// <c>/tmp/xdg-probe</c>, and <c>XdgAutostart</c> reads that variable each time it
/// builds its path. When the two overlapped, the autostart test created the directory
/// under one root and wrote the file under the other, and failed on the Linux leg with
/// <c>DirectoryNotFoundException: '/tmp/xdg-probe/autostart/claudestatus.desktop'</c> -
/// intermittently, in a test that had nothing wrong with it.
/// </para>
/// <para>
/// <c>DisableParallelization</c> runs this collection only after every parallel one has
/// finished, so the fix belongs on the tests that <i>write</i> the environment. Readers
/// need no marking, and a new test that sets a variable only has to join this
/// collection.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentTests
{
    public const string Name = "process-environment";
}
