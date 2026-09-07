using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// Exercises the HTTP layer against a stub handler. No request ever leaves the
/// machine; the fixtures supply the bodies.
/// </summary>
public class ClaudeSubscriptionUsageProviderTests
{
    private const string FakeToken = "sk-ant-oat01-AAAAAAAAAAAAAAAAAAAAAAAA";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (ClaudeSubscriptionUsageProvider Provider, StubHandler Handler) Build(
        HttpResponseMessage response, string? token = FakeToken)
    {
        var handler = new StubHandler(response);
        var http = new HttpClient(handler);
        var clock = new FakeTimeProvider(Fixture.FixedNow);
        return (new ClaudeSubscriptionUsageProvider(http, new StubTokenSource(token), clock), handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Fetches_and_parses_a_successful_response()
    {
        (ClaudeSubscriptionUsageProvider provider, _) = Build(Json(HttpStatusCode.OK, Fixture.Read(Fixture.Normal)));

        UsageSnapshot snapshot = await provider.FetchAsync(Ct);

        snapshot.Session!.Percent.Should().Be(29d);
        snapshot.WeekFable!.Percent.Should().Be(88d);
        snapshot.FetchedAt.Should().Be(Fixture.FixedNow);
    }

    [Fact]
    public async Task Sends_the_bearer_token_and_the_required_beta_header()
    {
        (ClaudeSubscriptionUsageProvider provider, StubHandler handler) =
            Build(Json(HttpStatusCode.OK, Fixture.Read(Fixture.Normal)));

        await provider.FetchAsync(Ct);

        handler.LastRequest!.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be(FakeToken);
        handler.LastRequest.Headers.GetValues("anthropic-beta").Should().Contain("oauth-2025-04-20");
    }

    [Fact]
    public async Task Only_ever_talks_to_the_official_endpoint()
    {
        // docs/security.md §5: exactly one host, ever.
        (ClaudeSubscriptionUsageProvider provider, StubHandler handler) =
            Build(Json(HttpStatusCode.OK, Fixture.Read(Fixture.Normal)));

        await provider.FetchAsync(Ct);

        handler.LastRequest!.RequestUri.Should().Be(ClaudeSubscriptionUsageProvider.Endpoint);
        handler.LastRequest.RequestUri!.Host.Should().Be("api.anthropic.com");
        handler.LastRequest.RequestUri.Scheme.Should().Be("https");
    }

    [Fact]
    public async Task Reports_NoCredential_when_the_user_is_not_logged_in()
    {
        (ClaudeSubscriptionUsageProvider provider, StubHandler handler) =
            Build(Json(HttpStatusCode.OK, "{}"), token: null);

        Func<Task> act = () => provider.FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>())
            .Which.Failure.Should().Be(UsageFetchFailure.NoCredential);
        handler.LastRequest.Should().BeNull("no request should be made without a credential");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, UsageFetchFailure.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, UsageFetchFailure.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests, UsageFetchFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UsageFetchFailure.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, UsageFetchFailure.ServerError)]
    [InlineData(HttpStatusCode.BadRequest, UsageFetchFailure.Network)]
    public async Task Classifies_failure_status_codes(HttpStatusCode status, UsageFetchFailure expected)
    {
        (ClaudeSubscriptionUsageProvider provider, _) = Build(Json(status, "{}"));

        Func<Task> act = () => provider.FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>()).Which.Failure.Should().Be(expected);
    }

    [Fact]
    public async Task Reads_Retry_After_as_delta_seconds()
    {
        // The real endpoint returned Retry-After: 1731 on a 429.
        HttpResponseMessage response = Json(HttpStatusCode.TooManyRequests, Fixture.Read(Fixture.RateLimited));
        response.Headers.Add("Retry-After", "1731");
        (ClaudeSubscriptionUsageProvider provider, _) = Build(response);

        Func<Task> act = () => provider.FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>())
            .Which.RetryAfter.Should().Be(TimeSpan.FromSeconds(1731));
    }

    [Fact]
    public async Task Reads_Retry_After_given_as_an_http_date()
    {
        HttpResponseMessage response = Json(HttpStatusCode.TooManyRequests, "{}");
        response.Headers.Add("Retry-After", Fixture.FixedNow.AddMinutes(10).ToString("R"));
        (ClaudeSubscriptionUsageProvider provider, _) = Build(response);

        Func<Task> act = () => provider.FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>())
            .Which.RetryAfter.Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_429_without_a_Retry_After_still_classifies_as_rate_limited()
    {
        (ClaudeSubscriptionUsageProvider provider, _) =
            Build(Json(HttpStatusCode.TooManyRequests, Fixture.Read(Fixture.RateLimited)));

        Func<Task> act = () => provider.FetchAsync(Ct);

        UsageFetchException thrown = (await act.Should().ThrowAsync<UsageFetchException>()).Which;
        thrown.Failure.Should().Be(UsageFetchFailure.RateLimited);
        thrown.RetryAfter.Should().BeNull();
    }

    [Fact]
    public async Task A_failure_message_never_contains_the_token_or_the_body()
    {
        // A failing response can echo request headers. See docs/security.md T2.
        HttpResponseMessage response = Json(
            HttpStatusCode.BadGateway, $"upstream said: Authorization: Bearer {FakeToken}");
        (ClaudeSubscriptionUsageProvider provider, _) = Build(response);

        Func<Task> act = () => provider.FetchAsync(Ct);

        string message = (await act.Should().ThrowAsync<UsageFetchException>()).Which.Message;
        message.Should().NotContain("sk-ant-").And.NotContain(FakeToken);
    }

    [Fact]
    public async Task An_unparseable_success_body_surfaces_as_a_parse_failure()
    {
        (ClaudeSubscriptionUsageProvider provider, _) = Build(Json(HttpStatusCode.OK, "<html>not json</html>"));

        Func<Task> act = () => provider.FetchAsync(Ct);

        await act.Should().ThrowAsync<UsageParseException>();
    }

    [Fact]
    public async Task A_transport_failure_is_reported_as_a_network_failure()
    {
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException("dns went away")));
        var provider = new ClaudeSubscriptionUsageProvider(
            http, new StubTokenSource(FakeToken), new FakeTimeProvider(Fixture.FixedNow));

        Func<Task> act = () => provider.FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>())
            .Which.Failure.Should().Be(UsageFetchFailure.Network);
    }

    [Fact]
    public async Task A_client_timeout_is_reported_as_a_network_failure_not_a_cancellation()
    {
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("timed out")));
        var provider = new ClaudeSubscriptionUsageProvider(
            http, new StubTokenSource(FakeToken), new FakeTimeProvider(Fixture.FixedNow));

        Func<Task> act = () => provider.FetchAsync(Ct);

        (await act.Should().ThrowAsync<UsageFetchException>())
            .Which.Failure.Should().Be(UsageFetchFailure.Network);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_as_a_cancellation()
    {
        var http = new HttpClient(new ThrowingHandler(new TaskCanceledException("cancelled")));
        var provider = new ClaudeSubscriptionUsageProvider(
            http, new StubTokenSource(FakeToken), new FakeTimeProvider(Fixture.FixedNow));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => provider.FetchAsync(cts.Token);

        await act.Should().ThrowAsync<TaskCanceledException>();
    }

    [Fact]
    public void Rejects_null_constructor_arguments()
    {
        var http = new HttpClient(new StubHandler(Json(HttpStatusCode.OK, "{}")));

        FluentActions.Invoking(() => new ClaudeSubscriptionUsageProvider(null!, new StubTokenSource(FakeToken)))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new ClaudeSubscriptionUsageProvider(http, null!))
            .Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Has_a_name_for_the_Info_window_that_says_it_is_unofficial()
    {
        (ClaudeSubscriptionUsageProvider provider, _) = Build(Json(HttpStatusCode.OK, "{}"));

        TestLocalizer.English(provider.NameKey).Should().Contain("unofficial");
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class StubTokenSource(string? token) : IAccessTokenSource
    {
        public string DescriptionKey => "stub";

        public Task<byte[]?> GetAccessTokenAsync(CancellationToken ct)
            => Task.FromResult(token is null ? null : Encoding.UTF8.GetBytes(token));
    }
}
