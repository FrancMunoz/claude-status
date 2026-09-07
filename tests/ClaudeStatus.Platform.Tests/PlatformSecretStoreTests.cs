using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClaudeStatus.Platform.Linux;
using ClaudeStatus.Platform.MacOS;
using ClaudeStatus.Platform.Windows;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.Tests;

/// <summary>A scratch config directory that cleans itself up.</summary>
public abstract class TempDirectoryFixture : IDisposable
{
    private bool _disposed;

    protected string Directory { get; } = Path.Combine(
        Path.GetTempPath(), "claudestatus-secretstore", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed || !disposing)
        {
            return;
        }

        _disposed = true;
        if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}

/// <summary>DPAPI. Runs for real on Windows, skips elsewhere.</summary>
public sealed class DpapiSecretStoreTests : SecretStoreContractTests
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-dpapi", Guid.NewGuid().ToString("N"));

    protected override string UnavailableReason => "DPAPI is only available on Windows.";

    protected override ISecretStore? CreateStore()
        => OperatingSystem.IsWindows() ? new DpapiSecretStore(_directory) : null;

    [Fact]
    public async Task Writes_a_per_install_entropy_file_of_the_documented_length()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(UnavailableReason);
        }

        ISecretStore store = CreateStore()!;
        await store.StoreAsync("entropy-probe", SampleSecret, Ct);

        string entropyPath = Path.Combine(_directory, DpapiSecretStore.EntropyFileName);
        File.Exists(entropyPath).Should().BeTrue();
        new FileInfo(entropyPath).Length.Should().Be(DpapiSecretStore.EntropyLength);
    }

    [Fact]
    public async Task The_entropy_is_random_per_install_not_derived_from_anything_guessable()
    {
        // docs/security.md §4: deriving a key from a machine name or user name is
        // encoding, not encryption. Two installs must not share entropy.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(UnavailableReason);
        }

        string otherDirectory = Path.Combine(
            Path.GetTempPath(), "claudestatus-dpapi", Guid.NewGuid().ToString("N"));
        try
        {
            await new DpapiSecretStore(_directory).StoreAsync("a", SampleSecret, Ct);
            await new DpapiSecretStore(otherDirectory).StoreAsync("a", SampleSecret, Ct);

            byte[] first = await File.ReadAllBytesAsync(
                Path.Combine(_directory, DpapiSecretStore.EntropyFileName), Ct);
            byte[] second = await File.ReadAllBytesAsync(
                Path.Combine(otherDirectory, DpapiSecretStore.EntropyFileName), Ct);

            first.Should().NotEqual(second);
        }
        finally
        {
            if (Directory.Exists(otherDirectory))
            {
                Directory.Delete(otherDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task A_secret_copied_to_a_config_directory_with_different_entropy_is_inert()
    {
        // Threat T6: a copied config folder must be useless. Simulated by moving
        // the ciphertext next to a different entropy file - the closest we can get
        // to "another machine" on one box.
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(UnavailableReason);
        }

        const string key = "copied-config";
        await new DpapiSecretStore(_directory).StoreAsync(key, SampleSecret, Ct);

        string attackerDirectory = Path.Combine(
            Path.GetTempPath(), "claudestatus-dpapi", Guid.NewGuid().ToString("N"));
        try
        {
            var attacker = new DpapiSecretStore(attackerDirectory);
            await attacker.StoreAsync("unrelated", SampleSecret, Ct); // forces its own entropy

            // Copy only the ciphertext across, leaving the attacker's entropy in place.
            foreach (string file in Directory.GetFiles(_directory, "*.dpapi"))
            {
                File.Copy(file, Path.Combine(attackerDirectory, Path.GetFileName(file)), overwrite: true);
            }

            (await attacker.RetrieveAsync(key, Ct))
                .Should().BeNull("a config copied without its entropy must decrypt to nothing");
        }
        finally
        {
            if (Directory.Exists(attackerDirectory))
            {
                Directory.Delete(attackerDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Reports_itself_as_hardened()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip(UnavailableReason);
        }

        CreateStore()!.IsHardened.Should().BeTrue();
        await Task.CompletedTask;
    }
}

/// <summary>The Linux AES-GCM fallback. Runs for real on Linux, skips elsewhere.</summary>
public sealed class AesGcmFileSecretStoreTests : SecretStoreContractTests
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "claudestatus-aesgcm", Guid.NewGuid().ToString("N"));

    protected override string UnavailableReason => "The AES-GCM file store is the Linux fallback.";

    protected override ISecretStore? CreateStore()
        => OperatingSystem.IsLinux() ? new AesGcmFileSecretStore(_directory) : null;

    [Fact]
    public async Task Reports_itself_as_NOT_hardened_so_the_Config_window_warns()
    {
        // Threat T11. If this ever returns true the warning banner disappears and
        // the user is silently told a weaker store is a strong one.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip(UnavailableReason);
        }

        CreateStore()!.IsHardened.Should().BeFalse();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_key_file_is_readable_only_by_its_owner()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip(UnavailableReason);
        }

        await CreateStore()!.StoreAsync("mode-probe", SampleSecret, Ct);

        UnixFileMode mode = File.GetUnixFileMode(
            Path.Combine(_directory, AesGcmFileSecretStore.KeyFileName));

        mode.Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public async Task A_tampered_ciphertext_is_rejected_rather_than_returned()
    {
        // AES-GCM authenticates, so a flipped bit must fail the tag check.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip(UnavailableReason);
        }

        const string key = "tamper-probe";
        ISecretStore store = CreateStore()!;
        await store.StoreAsync(key, SampleSecret, Ct);

        string file = Directory.GetFiles(_directory, "*.aesgcm").Single();
        byte[] payload = await File.ReadAllBytesAsync(file, Ct);
        payload[^1] ^= 0xFF;
        await File.WriteAllBytesAsync(file, payload, Ct);

        (await store.RetrieveAsync(key, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task A_secret_copied_without_its_key_file_is_inert()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip(UnavailableReason);
        }

        const string key = "copied-config";
        await CreateStore()!.StoreAsync(key, SampleSecret, Ct);

        string attackerDirectory = Path.Combine(
            Path.GetTempPath(), "claudestatus-aesgcm", Guid.NewGuid().ToString("N"));
        try
        {
            var attacker = new AesGcmFileSecretStore(attackerDirectory);
            await attacker.StoreAsync("unrelated", SampleSecret, Ct);

            foreach (string file in Directory.GetFiles(_directory, "*.aesgcm"))
            {
                File.Copy(file, Path.Combine(attackerDirectory, Path.GetFileName(file)), overwrite: true);
            }

            (await attacker.RetrieveAsync(key, Ct)).Should().BeNull();
        }
        finally
        {
            if (Directory.Exists(attackerDirectory))
            {
                Directory.Delete(attackerDirectory, recursive: true);
            }
        }
    }
}

/// <summary>libsecret. Runs only where a keyring actually answers.</summary>
public sealed class LibsecretSecretStoreTests : SecretStoreContractTests
{
    protected override string UnavailableReason =>
        "No Secret Service (libsecret) is available on this machine.";

    protected override ISecretStore? CreateStore()
        => OperatingSystem.IsLinux() && LibsecretSecretStore.IsAvailable()
            ? new LibsecretSecretStore()
            : null;

    [Fact]
    public void Probing_availability_never_throws_on_any_platform()
    {
        // The composition root calls this on Linux to decide between libsecret and
        // the fallback, so it must answer rather than explode when the .so is absent.
        Func<bool> act = LibsecretSecretStore.IsAvailable;

        act.Should().NotThrow();
    }
}

/// <summary>macOS Keychain. Runs only on macOS.</summary>
public sealed class KeychainSecretStoreTests : SecretStoreContractTests
{
    protected override string UnavailableReason => "The Keychain is only available on macOS.";

    protected override ISecretStore? CreateStore()
        => OperatingSystem.IsMacOS() ? new KeychainSecretStore() : null;

    [Fact]
    public async Task Reports_itself_as_hardened()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(UnavailableReason);
        }

        CreateStore()!.IsHardened.Should().BeTrue();
        await Task.CompletedTask;
    }
}

/// <summary>Checks that hold on every platform, without touching an OS store.</summary>
public class SecretStoreConstructionTests
{
    [Fact]
    public void Every_store_rejects_a_blank_config_directory()
    {
        FluentActions.Invoking(() => new DpapiSecretStore("  ")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new AesGcmFileSecretStore("  ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void The_platform_stores_agree_on_which_are_hardened()
    {
        // A regression here silently changes whether the user sees a security warning.
        new DpapiSecretStore(Path.GetTempPath()).IsHardened.Should().BeTrue();
        new KeychainSecretStore().IsHardened.Should().BeTrue();
        new LibsecretSecretStore().IsHardened.Should().BeTrue();
        new AesGcmFileSecretStore(Path.GetTempPath()).IsHardened.Should().BeFalse();
    }

    [Fact]
    public void Exactly_one_store_matches_the_running_platform()
    {
        // Guards the Phase 4 composition root against being wired up on an OS
        // whose store cannot work.
        int matches = (OperatingSystem.IsWindows() ? 1 : 0)
            + (OperatingSystem.IsMacOS() ? 1 : 0)
            + (OperatingSystem.IsLinux() ? 1 : 0);

        matches.Should().Be(1);
        RuntimeInformation.OSDescription.Should().NotBeNullOrWhiteSpace();
    }
}
