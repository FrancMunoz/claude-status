namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Every literal here is a synthetic, obviously-fake value. Never paste a real
/// token into a test - see <c>docs/security.md</c> threat T4.
/// </summary>
public class RedactorTests
{
    private const string FakeAccessToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string FakeRefreshToken = "sk-ant-ort01-BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private const string FakeJwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.QUJDREVGR0hJSktMTU5PUA";

    [Theory]
    [InlineData(FakeAccessToken)]
    [InlineData(FakeRefreshToken)]
    [InlineData(FakeJwt)]
    public void Removes_credential_shaped_values(string secret)
    {
        string redacted = Redactor.Redact($"the value was {secret} here");

        redacted.Should().NotContain(secret);
        redacted.Should().Contain(Redactor.Placeholder);
    }

    [Fact]
    public void Redacts_a_bearer_header_but_keeps_the_scheme()
    {
        string redacted = Redactor.Redact($"Authorization: Bearer {FakeAccessToken}");

        redacted.Should().NotContain(FakeAccessToken);
        redacted.Should().Contain("Bearer");
        redacted.Should().Contain(Redactor.Placeholder);
    }

    [Fact]
    public void Redacts_a_secret_json_field_but_keeps_the_field_name()
    {
        string redacted = Redactor.Redact($$"""{"accessToken":"{{FakeAccessToken}}","subscriptionType":"max"}""");

        redacted.Should().NotContain(FakeAccessToken);
        redacted.Should().Contain("accessToken");
        redacted.Should().Contain("max", "non-secret fields must survive or the log is useless");
    }

    [Fact]
    public void Redacts_the_whole_credentials_file_shape()
    {
        string json = $$"""
            { "claudeAiOauth": {
                "accessToken": "{{FakeAccessToken}}",
                "refreshToken": "{{FakeRefreshToken}}",
                "expiresAt": 1770412938485,
                "subscriptionType": "max" } }
            """;

        string redacted = Redactor.Redact(json);

        redacted.Should().NotContain(FakeAccessToken).And.NotContain(FakeRefreshToken);
        redacted.Should().NotContain("sk-ant-");
    }

    [Fact]
    public void Redacts_account_identifier_headers()
    {
        // Not credentials, but they identify the user's org. Threat T10.
        string redacted = Redactor.Redact("anthropic-organization-id: 66460b5b-bd95-4ac4-abd8-cf12968a8d85");

        redacted.Should().NotContain("66460b5b");
        redacted.Should().Contain("anthropic-organization-id");
    }

    [Theory]
    [InlineData("Usage fetch failed (RateLimited); attempt 3.")]
    [InlineData("Session 29%, week 58%, Fable 88%.")]
    [InlineData("C:/Users/someone/AppData/Roaming/claudestatus/settings.json")]
    public void Leaves_ordinary_messages_alone(string message)
    {
        Redactor.Redact(message).Should().Be(message);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Handles_null_and_empty(string? input, string expected)
    {
        Redactor.Redact(input).Should().Be(expected);
    }

    [Fact]
    public void Redacts_every_occurrence_not_just_the_first()
    {
        string redacted = Redactor.Redact($"{FakeAccessToken} and again {FakeAccessToken}");

        redacted.Should().NotContain("sk-ant-");
    }

    [Fact]
    public void LooksRedacted_reports_whether_anything_slipped_through()
    {
        Redactor.LooksRedacted($"token {FakeAccessToken}").Should().BeFalse();
        Redactor.LooksRedacted(Redactor.Redact($"token {FakeAccessToken}")).Should().BeTrue();
    }
}
