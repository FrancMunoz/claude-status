using System.Text;
using ClaudeStatus.Platform.MacOS;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The holding and refusing in <see cref="ClaudeCodeKeychainTokenSource"/>.
/// </summary>
/// <remarks>
/// Every test here counts reads, because the count is the behaviour: a read is a
/// permission prompt, and the bug these cover was one prompt per poll for a token
/// the app had already been given.
/// </remarks>
public class KeychainTokenSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_held_token_is_served_without_going_back_to_the_Keychain()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var clock = new StubClock(Now);
        var keychain = new CountingKeychain(Blob(Now.AddHours(1)));
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, clock);

        for (int poll = 0; poll < 5; poll++)
        {
            byte[]? token = await source.GetAccessTokenAsync(Ct);
            token.Should().NotBeNull();
            Encoding.UTF8.GetString(token!).Should().Be("tok");
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        keychain.Reads.Should().Be(1, "five polls inside one token's life is one prompt, not five");
    }

    [Fact]
    public async Task The_Keychain_is_read_again_once_the_held_token_runs_out()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var clock = new StubClock(Now);
        var keychain = new CountingKeychain(Blob(Now.AddMinutes(30)));
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, clock);

        (await source.GetAccessTokenAsync(Ct)).Should().NotBeNull();

        // Past the token's expiry, so what is held is no longer worth serving.
        clock.Advance(TimeSpan.FromMinutes(31));
        keychain.Blob = Blob(clock.GetUtcNow().AddHours(1), "fresh");

        byte[]? renewed = await source.GetAccessTokenAsync(Ct);
        Encoding.UTF8.GetString(renewed!).Should().Be("fresh");
        keychain.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Each_caller_gets_its_own_copy_so_a_disposed_lease_cannot_empty_the_cache()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var keychain = new CountingKeychain(Blob(Now.AddHours(1)));
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, new StubClock(Now));

        byte[]? first = await source.GetAccessTokenAsync(Ct);

        // What AccessTokenLease does to every token it is handed.
        Array.Clear(first!);

        byte[]? second = await source.GetAccessTokenAsync(Ct);
        Encoding.UTF8.GetString(second!).Should().Be("tok");
        keychain.Reads.Should().Be(1);
    }

    [Fact]
    public async Task A_refusal_is_not_asked_again()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var keychain = new CountingKeychain(null) { Outcome = KeychainReadOutcome.Denied };
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, new StubClock(Now));

        for (int poll = 0; poll < 5; poll++)
        {
            (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
        }

        keychain.Reads.Should().Be(1, "the user said no once, which is an answer");
    }

    [Fact]
    public async Task Forget_takes_a_refusal_back()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var keychain = new CountingKeychain(null) { Outcome = KeychainReadOutcome.Denied };
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, new StubClock(Now));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();

        // The user presses Test in Config, having changed their mind.
        source.Forget();
        keychain.Outcome = KeychainReadOutcome.Found;
        keychain.Blob = Blob(Now.AddHours(1));

        (await source.GetAccessTokenAsync(Ct)).Should().NotBeNull();
        keychain.Reads.Should().Be(2);
    }

    [Fact]
    public async Task Forget_drops_a_held_token_so_Test_reports_on_what_is_there_now()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var keychain = new CountingKeychain(Blob(Now.AddHours(1)));
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, new StubClock(Now));

        (await source.GetAccessTokenAsync(Ct)).Should().NotBeNull();

        source.Forget();
        keychain.Blob = Blob(Now.AddHours(1), "fresh");

        Encoding.UTF8.GetString((await source.GetAccessTokenAsync(Ct))!).Should().Be("fresh");
        keychain.Reads.Should().Be(2);
    }

    [Fact]
    public async Task A_missing_item_is_looked_for_again_because_Claude_Code_may_log_in()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var keychain = new CountingKeychain(null) { Outcome = KeychainReadOutcome.NotFound };
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, new StubClock(Now));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();

        keychain.Reads.Should().Be(2, "nobody was prompted, so there is nothing to spare them");
    }

    [Fact]
    public async Task An_expired_token_is_neither_served_nor_held()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS only.");
        }

        var keychain = new CountingKeychain(Blob(Now.AddMinutes(-1)));
        using var source = new ClaudeCodeKeychainTokenSource(keychain.Read, new StubClock(Now));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();

        keychain.Reads.Should().Be(2);
    }

    private static byte[] Blob(DateTimeOffset expiresAt, string token = "tok")
        => Encoding.UTF8.GetBytes(
            """{"claudeAiOauth":{"accessToken":"""
            + $"\"{token}\",\"expiresAt\":{expiresAt.ToUnixTimeMilliseconds()}"
            + "}}");

    /// <summary>A Keychain that counts how often it was asked.</summary>
    private sealed class CountingKeychain(byte[]? blob)
    {
        public byte[]? Blob { get; set; } = blob;

        public KeychainReadOutcome Outcome { get; set; } = KeychainReadOutcome.Found;

        public int Reads { get; private set; }

        public Task<KeychainReadResult> Read(string service, CancellationToken ct)
        {
            Reads++;

            // A fresh array every time: the source is expected to zero what it is
            // given, and a shared one would make the second read return blanks.
            return Task.FromResult(new KeychainReadResult(
                Outcome, Outcome == KeychainReadOutcome.Found ? Blob?.ToArray() : null));
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class StubClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
