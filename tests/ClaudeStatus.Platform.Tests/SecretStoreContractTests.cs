using System.Security.Cryptography;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.Tests;

/// <summary>
/// The contract every <see cref="ISecretStore"/> must satisfy, regardless of OS.
/// </summary>
/// <remarks>
/// <para>
/// Each platform derives from this and supplies its store. Derived fixtures skip
/// themselves when the mechanism is not present, so the suite is green on every
/// leg of the CI matrix and actually meaningful on the leg that matters.
/// </para>
/// <para>
/// Never put a real token in these tests. Every value here is synthetic.
/// </para>
/// </remarks>
public abstract class SecretStoreContractTests : IAsyncLifetime
{
    /// <summary>A synthetic, obviously-fake access token.</summary>
    protected static readonly byte[] SampleSecret =
        "sk-ant-oat01-CONTRACTTESTVALUE0000000000000000000000"u8.ToArray();

    private readonly List<string> _keysToClean = [];

    /// <summary>The store under test, or null when the mechanism is unavailable here.</summary>
    protected abstract ISecretStore? CreateStore();

    /// <summary>Why the store is unavailable, for the skip message.</summary>
    protected abstract string UnavailableReason { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Removes anything a test left behind, even when it failed.</summary>
    /// <remarks>
    /// This is xUnit's <see cref="IAsyncLifetime"/> teardown, not the
    /// <see cref="IAsyncDisposable"/> pattern, so there is no finalizer to suppress.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design", "CA1816:Dispose methods should call SuppressFinalize",
        Justification = "xUnit IAsyncLifetime teardown, not a disposal pattern; no finalizer exists.")]
    public async ValueTask DisposeAsync()
    {
        ISecretStore? store = CreateStore();
        if (store is null)
        {
            return;
        }

        foreach (string key in _keysToClean)
        {
            try
            {
                await store.DeleteAsync(key, CancellationToken.None);
            }
            catch (SecretStoreException)
            {
                // Best effort.
            }
        }
    }

    /// <summary>Gets the store, or skips the test when the OS mechanism is absent.</summary>
    private ISecretStore Require()
    {
        ISecretStore? store = CreateStore();
        if (store is null)
        {
            Assert.Skip(UnavailableReason);
        }

        return store!;
    }

    /// <summary>A key unique to this test run, registered for cleanup.</summary>
    private string NewKey()
    {
        string key = $"claudestatus.test.{Guid.NewGuid():N}";
        _keysToClean.Add(key);
        return key;
    }

    [Fact]
    public async Task Retrieving_a_key_that_was_never_stored_returns_null()
    {
        ISecretStore store = Require();

        (await store.RetrieveAsync(NewKey(), Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Stores_then_retrieves_the_exact_bytes()
    {
        ISecretStore store = Require();
        string key = NewKey();

        await store.StoreAsync(key, SampleSecret, Ct);
        byte[]? retrieved = await store.RetrieveAsync(key, Ct);

        retrieved.Should().NotBeNull();
        retrieved.Should().Equal(SampleSecret);
    }

    [Fact]
    public async Task Storing_twice_replaces_rather_than_duplicates()
    {
        ISecretStore store = Require();
        string key = NewKey();
        byte[] second = "sk-ant-oat01-SECONDVALUE00000000000000000000000000000"u8.ToArray();

        await store.StoreAsync(key, SampleSecret, Ct);
        await store.StoreAsync(key, second, Ct);

        (await store.RetrieveAsync(key, Ct)).Should().Equal(second);
    }

    [Fact]
    public async Task Deleting_makes_the_secret_unretrievable()
    {
        ISecretStore store = Require();
        string key = NewKey();

        await store.StoreAsync(key, SampleSecret, Ct);
        await store.DeleteAsync(key, Ct);

        (await store.RetrieveAsync(key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_something_that_does_not_exist_is_not_an_error()
    {
        ISecretStore store = Require();

        Func<Task> act = () => store.DeleteAsync(NewKey(), Ct);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Keys_do_not_collide()
    {
        ISecretStore store = Require();
        string first = NewKey();
        string second = NewKey();
        byte[] otherValue = "sk-ant-oat01-OTHERVALUE000000000000000000000000000000"u8.ToArray();

        await store.StoreAsync(first, SampleSecret, Ct);
        await store.StoreAsync(second, otherValue, Ct);

        (await store.RetrieveAsync(first, Ct)).Should().Equal(SampleSecret);
        (await store.RetrieveAsync(second, Ct)).Should().Equal(otherValue);
    }

    [Fact]
    public async Task The_returned_buffer_is_the_callers_to_zero()
    {
        // The contract says the caller owns and wipes it. Prove that zeroing the
        // returned array does not corrupt what is stored.
        ISecretStore store = Require();
        string key = NewKey();
        await store.StoreAsync(key, SampleSecret, Ct);

        byte[] first = (await store.RetrieveAsync(key, Ct))!;
        CryptographicOperations.ZeroMemory(first);

        (await store.RetrieveAsync(key, Ct)).Should().Equal(SampleSecret);
    }

    [Fact]
    public async Task A_stored_secret_survives_a_new_store_instance()
    {
        // The app is restarted far more often than it is used; a store that only
        // works in-process would be useless.
        ISecretStore store = Require();
        string key = NewKey();
        await store.StoreAsync(key, SampleSecret, Ct);

        ISecretStore fresh = CreateStore()!;

        (await fresh.RetrieveAsync(key, Ct)).Should().Equal(SampleSecret);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejects_a_blank_key(string key)
    {
        ISecretStore store = Require();

        await FluentActions.Awaiting(() => store.StoreAsync(key, SampleSecret, Ct))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => store.RetrieveAsync(key, Ct))
            .Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => store.DeleteAsync(key, Ct))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Declares_a_description_for_the_Config_window()
    {
        ISecretStore store = Require();

        store.DescriptionKey.Should().NotBeNullOrWhiteSpace();
    }
}
