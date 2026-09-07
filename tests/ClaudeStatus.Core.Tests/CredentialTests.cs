using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Every token literal in this file is synthetic. Never paste a real one -
/// <c>docs/security.md</c> threat T4.
/// </summary>
public class CredentialFormatTests
{
    private const string ValidToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Accepts_a_well_formed_access_token()
    {
        CredentialFormat.Validate(Encoding.UTF8.GetBytes(ValidToken))
            .Should().Be(CredentialFormatProblem.None);
    }

    [Fact]
    public void Rejects_an_empty_value()
    {
        CredentialFormat.Validate([]).Should().Be(CredentialFormatProblem.Empty);
    }

    [Fact]
    public void Rejects_a_refresh_token_with_its_own_message()
    {
        // Pasting the refresh token is an easy mistake and otherwise produces a
        // baffling 429 rather than a clear failure.
        CredentialFormat.Validate("sk-ant-ort01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"u8)
            .Should().Be(CredentialFormatProblem.RefreshTokenNotAccessToken);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("sk-proj-something")]
    [InlineData("Bearer sk-ant-oat01-AAAA")]
    public void Rejects_anything_without_the_access_token_prefix(string value)
    {
        CredentialFormat.Validate(Encoding.UTF8.GetBytes(value))
            .Should().Be(CredentialFormatProblem.WrongPrefix);
    }

    [Fact]
    public void Rejects_a_truncated_token()
    {
        CredentialFormat.Validate("sk-ant-oat01-AAAA"u8).Should().Be(CredentialFormatProblem.TooShort);
    }

    [Theory]
    [InlineData("sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAA AAAAAAAAAAAA")]
    [InlineData("sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAA\nAAAAAAAAAAAA")]
    [InlineData("sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAA\"AAAAAAAAAAAA")]
    public void Rejects_stray_whitespace_and_punctuation_from_a_bad_paste(string value)
    {
        CredentialFormat.Validate(Encoding.UTF8.GetBytes(value))
            .Should().Be(CredentialFormatProblem.IllegalCharacters);
    }

    [Fact]
    public void Rejects_something_absurdly_long()
    {
        string huge = "sk-ant-oat01-" + new string('A', CredentialFormat.MaximumLength);

        CredentialFormat.Validate(Encoding.UTF8.GetBytes(huge))
            .Should().Be(CredentialFormatProblem.TooShort, "the length check covers both ends");
    }

    [Theory]
    [InlineData(CredentialFormatProblem.None)]
    [InlineData(CredentialFormatProblem.Empty)]
    [InlineData(CredentialFormatProblem.WrongPrefix)]
    [InlineData(CredentialFormatProblem.TooShort)]
    [InlineData(CredentialFormatProblem.IllegalCharacters)]
    [InlineData(CredentialFormatProblem.RefreshTokenNotAccessToken)]
    public void Every_problem_has_a_message_that_never_echoes_the_value(CredentialFormatProblem problem)
    {
        string message = TestLocalizer.English(CredentialFormat.DescribeKey(problem));

        message.Should().NotBeNullOrWhiteSpace();
        Redactor.LooksRedacted(message).Should().BeTrue();
    }

    [Fact]
    public void ToBytes_trims_surrounding_whitespace_from_a_paste()
    {
        byte[] bytes = CredentialFormat.ToBytes($"  {ValidToken}\r\n");

        CredentialFormat.Validate(bytes).Should().Be(CredentialFormatProblem.None);
    }
}

public class ClaudeCodeFileTokenSourceTests : IDisposable
{
    private const string ValidToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-tokensource", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private string WriteCredentials(string json)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, ".credentials.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static string CredentialsJson(string token, DateTimeOffset expiresAt) => $$"""
        { "claudeAiOauth": {
            "accessToken": "{{token}}",
            "refreshToken": "sk-ant-ort01-BBBBBBBBBBBBBBBBBBBBBBBB",
            "expiresAt": {{expiresAt.ToUnixTimeMilliseconds()}},
            "subscriptionType": "max" } }
        """;

    [Fact]
    public async Task Reads_a_valid_unexpired_token()
    {
        string path = WriteCredentials(CredentialsJson(ValidToken, Fixture.FixedNow.AddHours(1)));
        var source = new ClaudeCodeFileTokenSource(path, new FakeTimeProvider(Fixture.FixedNow));

        byte[]? token = await source.GetAccessTokenAsync(Ct);

        token.Should().NotBeNull();
        Encoding.UTF8.GetString(token!).Should().Be(ValidToken);
    }

    [Fact]
    public async Task Returns_null_for_an_expired_token_so_the_UI_can_say_open_Claude_Code()
    {
        // Otherwise the user sees a 429, which looks like rate limiting rather
        // than an expired login. See docs/data-source.md.
        string path = WriteCredentials(CredentialsJson(ValidToken, Fixture.FixedNow.AddMinutes(-1)));
        var source = new ClaudeCodeFileTokenSource(path, new FakeTimeProvider(Fixture.FixedNow));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_there_is_no_credentials_file()
    {
        var source = new ClaudeCodeFileTokenSource(
            Path.Combine(_directory, "absent.json"), new FakeTimeProvider(Fixture.FixedNow));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
        source.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Returns_null_for_a_half_written_file_rather_than_throwing()
    {
        // Claude Code rewrites this file during its own refresh; we can catch it
        // mid-write. Next poll will succeed.
        string path = WriteCredentials("{ \"claudeAiOauth\": { \"accessTok");
        var source = new ClaudeCodeFileTokenSource(path, new FakeTimeProvider(Fixture.FixedNow));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_the_oauth_block_is_missing()
    {
        string path = WriteCredentials("""{ "somethingElse": true }""");
        var source = new ClaudeCodeFileTokenSource(path, new FakeTimeProvider(Fixture.FixedNow));

        (await source.GetAccessTokenAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Reads_a_token_with_no_recorded_expiry_and_lets_the_endpoint_judge()
    {
        string path = WriteCredentials($$"""{ "claudeAiOauth": { "accessToken": "{{ValidToken}}" } }""");
        var source = new ClaudeCodeFileTokenSource(path, new FakeTimeProvider(Fixture.FixedNow));

        (await source.GetAccessTokenAsync(Ct)).Should().NotBeNull();
    }

    [Fact]
    public async Task Never_returns_the_refresh_token()
    {
        string path = WriteCredentials(CredentialsJson(ValidToken, Fixture.FixedNow.AddHours(1)));
        var source = new ClaudeCodeFileTokenSource(path, new FakeTimeProvider(Fixture.FixedNow));

        byte[]? token = await source.GetAccessTokenAsync(Ct);

        Encoding.UTF8.GetString(token!).Should().NotContain("sk-ant-ort");
    }

    [Fact]
    public void Honours_the_CLAUDE_CONFIG_DIR_override()
    {
        string? original = Environment.GetEnvironmentVariable(
            ClaudeCodeFileTokenSource.ConfigDirEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ClaudeCodeFileTokenSource.ConfigDirEnvironmentVariable, _directory);

            ClaudeCodeFileTokenSource.DefaultCredentialsPath()
                .Should().Be(Path.Combine(_directory, ".credentials.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                ClaudeCodeFileTokenSource.ConfigDirEnvironmentVariable, original);
        }
    }

    [Fact]
    public void Describes_itself_for_the_Config_window()
    {
        TestLocalizer.English(new ClaudeCodeFileTokenSource().DescriptionKey)
            .Should().Be("Claude Code login");
    }
}

public class CredentialServiceTests
{
    private const string ValidToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CredentialService Build(
        InMemorySecretStore? store = null,
        IUsageProvider? provider = null,
        IAccessTokenSource? claudeCode = null)
        => new(
            store ?? new InMemorySecretStore(),
            claudeCode ?? new StubTokenSource(ValidToken),
            _ => provider ?? new FakeUsageProvider(
                FakeUsageScenario.Healthy, new FakeTimeProvider(Fixture.FixedNow)));

    [Fact]
    public async Task Stores_a_well_formed_token()
    {
        var store = new InMemorySecretStore();
        CredentialService service = Build(store);

        CredentialFormatProblem problem = await service.StoreAsync(Encoding.UTF8.GetBytes(ValidToken), Ct);

        problem.Should().Be(CredentialFormatProblem.None);
        (await service.HasStoredCredentialAsync(Ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Refuses_to_store_a_malformed_token()
    {
        var store = new InMemorySecretStore();
        CredentialService service = Build(store);

        CredentialFormatProblem problem = await service.StoreAsync("garbage"u8.ToArray(), Ct);

        problem.Should().Be(CredentialFormatProblem.WrongPrefix);
        (await service.HasStoredCredentialAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Zeroes_the_callers_buffer_whether_or_not_the_token_was_valid()
    {
        CredentialService service = Build();
        byte[] good = Encoding.UTF8.GetBytes(ValidToken);
        byte[] bad = "garbage"u8.ToArray();

        await service.StoreAsync(good, Ct);
        await service.StoreAsync(bad, Ct);

        good.Should().AllSatisfy(b => b.Should().Be(0));
        bad.Should().AllSatisfy(b => b.Should().Be(0), "a rejected token must be wiped too");
    }

    [Fact]
    public async Task Reports_no_stored_credential_before_anything_is_stored()
    {
        (await Build().HasStoredCredentialAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleting_forgets_the_credential()
    {
        var store = new InMemorySecretStore();
        CredentialService service = Build(store);
        await service.StoreAsync(Encoding.UTF8.GetBytes(ValidToken), Ct);

        await service.DeleteAsync(Ct);

        (await service.HasStoredCredentialAsync(Ct)).Should().BeFalse();
    }

    [Fact]
    public void Resolves_the_Claude_Code_source_by_default()
    {
        var claudeCode = new StubTokenSource(ValidToken);

        Build(claudeCode: claudeCode)
            .ResolveTokenSource(CredentialSource.ClaudeCodeLogin)
            .Should().BeSameAs(claudeCode);
    }

    [Fact]
    public void Resolves_the_stored_source_for_manual_tokens()
    {
        Build().ResolveTokenSource(CredentialSource.ManualToken)
            .Should().BeOfType<StoredTokenSource>();
    }

    [Fact]
    public async Task A_successful_test_reports_the_current_session_usage()
    {
        CredentialTestResult result = await Build().TestAsync(CredentialSource.ClaudeCodeLogin, Ct);

        result.IsSuccess.Should().BeTrue();
        TestLocalizer.English().Format(result.MessageKey, result.SessionPercent)
            .Should().Contain("29");
    }

    [Theory]
    [InlineData(FakeUsageScenario.Unauthorized, CredentialTestOutcome.Rejected)]
    [InlineData(FakeUsageScenario.AlwaysFails, CredentialTestOutcome.Unreachable)]
    public async Task A_failing_test_reports_why(FakeUsageScenario scenario, CredentialTestOutcome expected)
    {
        var provider = new FakeUsageProvider(scenario, new FakeTimeProvider(Fixture.FixedNow));

        CredentialTestResult result = await Build(provider: provider)
            .TestAsync(CredentialSource.ClaudeCodeLogin, Ct);

        result.Outcome.Should().Be(expected);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_rate_limited_test_is_inconclusive_and_never_reported_as_success()
    {
        // An unauthenticated request also returns 429 (measured 2026-09-04), so
        // treating a 429 as a pass would tell the user their broken token works.
        var provider = new FakeUsageProvider(FakeUsageScenario.RateLimited, new FakeTimeProvider(Fixture.FixedNow));

        CredentialTestResult result = await Build(provider: provider)
            .TestAsync(CredentialSource.ClaudeCodeLogin, Ct);

        result.Outcome.Should().Be(CredentialTestOutcome.Inconclusive);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_credential_is_reported_distinctly_from_a_rejected_one()
    {
        var provider = new ThrowingProvider(
            new UsageFetchException(UsageFetchFailure.NoCredential, "none"));

        CredentialTestResult result = await Build(provider: provider)
            .TestAsync(CredentialSource.ClaudeCodeLogin, Ct);

        result.Outcome.Should().Be(CredentialTestOutcome.NoCredential);
    }

    [Fact]
    public async Task An_unreadable_response_does_not_look_like_a_credential_problem()
    {
        var provider = new ThrowingProvider(new UsageParseException("bad json"));

        CredentialTestResult result = await Build(provider: provider)
            .TestAsync(CredentialSource.ClaudeCodeLogin, Ct);

        result.Outcome.Should().Be(CredentialTestOutcome.Unreachable);
    }

    [Fact]
    public async Task No_test_message_ever_contains_credential_shaped_text()
    {
        foreach (FakeUsageScenario scenario in Enum.GetValues<FakeUsageScenario>())
        {
            var provider = new FakeUsageProvider(scenario, new FakeTimeProvider(Fixture.FixedNow));
            CredentialTestResult result = await Build(provider: provider)
                .TestAsync(CredentialSource.ClaudeCodeLogin, Ct);

            string message = TestLocalizer.English().Format(result.MessageKey, result.SessionPercent);
            Redactor.LooksRedacted(message).Should().BeTrue();
            message.Should().NotContain("sk-ant-");
        }
    }

    [Fact]
    public void Surfaces_the_store_description_and_hardening_flag_for_the_Config_window()
    {
        var store = new InMemorySecretStore { Hardened = false };
        CredentialService service = Build(store);

        service.StoreDescriptionKey.Should().Be(store.DescriptionKey);
        service.IsStoreHardened.Should().BeFalse("the Config window must warn for a soft store");
    }

    [Fact]
    public void Rejects_null_constructor_arguments()
    {
        var store = new InMemorySecretStore();
        var source = new StubTokenSource(ValidToken);

        FluentActions.Invoking(() => new CredentialService(null!, source, _ => null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CredentialService(store, null!, _ => null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new CredentialService(store, source, null!))
            .Should().Throw<ArgumentNullException>();
    }

    private sealed class ThrowingProvider(Exception exception) : IUsageProvider
    {
        public string NameKey => "throwing";

        public Task<UsageSnapshot> FetchAsync(CancellationToken ct)
            => Task.FromException<UsageSnapshot>(exception);
    }

    private sealed class StubTokenSource(string? token) : IAccessTokenSource
    {
        public string DescriptionKey => "stub";

        public Task<byte[]?> GetAccessTokenAsync(CancellationToken ct)
            => Task.FromResult(token is null ? null : Encoding.UTF8.GetBytes(token));
    }
}

public class StoredTokenSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_what_the_store_holds()
    {
        var store = new InMemorySecretStore();
        await store.StoreAsync(StoredTokenSource.DefaultKey, "sk-ant-oat01-AAAA"u8.ToArray(), Ct);

        byte[]? token = await new StoredTokenSource(store).GetAccessTokenAsync(Ct);

        token.Should().NotBeNull();
    }

    [Fact]
    public async Task Returns_null_when_nothing_is_stored()
    {
        (await new StoredTokenSource(new InMemorySecretStore()).GetAccessTokenAsync(Ct))
            .Should().BeNull();
    }

    [Fact]
    public void Describes_the_underlying_store_for_the_Config_window()
    {
        TestLocalizer.English(new StoredTokenSource(new InMemorySecretStore()).DescriptionKey)
            .Should().Contain("Manually entered");
    }

    [Fact]
    public void Rejects_a_null_store_or_blank_key()
    {
        FluentActions.Invoking(() => new StoredTokenSource(null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new StoredTokenSource(new InMemorySecretStore(), " "))
            .Should().Throw<ArgumentException>();
    }
}

/// <summary>
/// A store for Core tests. Deliberately lives in the test project - shipping an
/// in-memory secret store would invite someone to wire it up for real.
/// </summary>
internal sealed class InMemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, byte[]> _values = [];

    public string DescriptionKey => "In-memory (tests only)";

    public bool Hardened { get; init; } = true;

    public bool IsHardened => Hardened;

    public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _values[key] = secret.ToArray();
        return Task.CompletedTask;
    }

    public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Task.FromResult(_values.TryGetValue(key, out byte[]? value) ? value.ToArray() : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_values.Remove(key, out byte[]? removed))
        {
            CryptographicOperations.ZeroMemory(removed);
        }

        return Task.CompletedTask;
    }
}
