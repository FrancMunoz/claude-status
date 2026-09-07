using System.Runtime.Versioning;
using System.Security.Cryptography;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.Windows;

/// <summary>
/// Protects secrets with Windows DPAPI, scoped to the current user, with
/// per-install entropy.
/// </summary>
/// <remarks>
/// <para>
/// DPAPI's <see cref="DataProtectionScope.CurrentUser"/> already binds the
/// ciphertext to the user account and the machine. The extra 32 bytes of
/// entropy, generated once per install and kept beside the config, mean that
/// even another application running as the same user cannot decrypt our blob
/// without also reading that file.
/// </para>
/// <para>
/// The entropy is <b>random</b>, never derived from a machine name, user name,
/// or anything else guessable - that would be encoding, not encryption
/// (<c>docs/security.md</c> §4).
/// </para>
/// <para>
/// Copying the config directory to another machine or user therefore yields
/// nothing: the entropy travels but DPAPI still refuses, and
/// <see cref="RetrieveAsync"/> returns null rather than throwing. An inert
/// credential is the designed outcome for threat T6.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>Name of the per-install entropy file inside the config directory.</summary>
    public const string EntropyFileName = "entropy.bin";

    /// <summary>Entropy length in bytes.</summary>
    public const int EntropyLength = 32;

    private const string SecretFileExtension = ".dpapi";

    private readonly string _directory;
    private readonly Lock _entropyLock = new();
    private byte[]? _entropy;

    public DpapiSecretStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = configDirectory;
    }

    /// <inheritdoc />
    public string DescriptionKey => "Store_WindowsDpapi";

    /// <inheritdoc />
    public bool IsHardened => true;

    /// <inheritdoc />
    public async Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Directory.CreateDirectory(_directory);
        byte[] entropy = GetOrCreateEntropy();

        byte[] protectedBytes;
        try
        {
            protectedBytes = ProtectedData.Protect(secret.ToArray(), entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            // Never include the exception's data, only its type via the inner exception.
            throw new SecretStoreException("Windows refused to protect the credential.", ex);
        }

        string path = PathFor(key);
        string tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, protectedBytes, ct).ConfigureAwait(false);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <inheritdoc />
    public async Task<byte[]?> RetrieveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = PathFor(key);
        if (!File.Exists(path) || !EntropyFileExists)
        {
            return null;
        }

        byte[] protectedBytes;
        try
        {
            protectedBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return ProtectedData.Unprotect(protectedBytes, GetOrCreateEntropy(), DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Wrong user, wrong machine, or a tampered blob. All the same answer:
            // there is no usable credential here. This is the copied-config case
            // working exactly as intended.
            return null;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = PathFor(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private bool EntropyFileExists => File.Exists(Path.Combine(_directory, EntropyFileName));

    /// <summary>
    /// Loads the per-install entropy, generating it on first use.
    /// </summary>
    /// <remarks>
    /// Written with <see cref="FileMode.CreateNew"/> so two processes racing on
    /// first run cannot end up with different entropy and silently invalidate
    /// each other's stored secret.
    /// </remarks>
    private byte[] GetOrCreateEntropy()
    {
        lock (_entropyLock)
        {
            if (_entropy is not null)
            {
                return _entropy;
            }

            string path = Path.Combine(_directory, EntropyFileName);
            if (File.Exists(path))
            {
                byte[] existing = File.ReadAllBytes(path);
                if (existing.Length == EntropyLength)
                {
                    _entropy = existing;
                    return _entropy;
                }

                // A truncated entropy file means every stored secret is already
                // unrecoverable. Start again rather than run with a weak value.
                File.Delete(path);
            }

            Directory.CreateDirectory(_directory);
            byte[] fresh = RandomNumberGenerator.GetBytes(EntropyLength);
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(fresh);
            }
            catch (IOException)
            {
                // Another process won the race. Use whatever it wrote.
                fresh = File.ReadAllBytes(path);
            }

            _entropy = fresh;
            return _entropy;
        }
    }

    /// <summary>Maps a key onto a file name, keeping arbitrary keys off the filesystem.</summary>
    private string PathFor(string key)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash.AsSpan(0, 16)) + SecretFileExtension);
    }
}
