using System.Runtime.Versioning;
using System.Security.Cryptography;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.Linux;

/// <summary>
/// The fallback for Linux systems with no Secret Service: AES-256-GCM under a
/// key file with <c>0600</c> permissions.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the weakest store we ship, and the Config window must say so.</b>
/// <see cref="IsHardened"/> is false. It protects against a copied config
/// directory reaching another machine or user, but not against another process
/// running as the same user - that process can read the key file too. See
/// <c>docs/security.md</c> threat T11.
/// </para>
/// <para>
/// AES-GCM from the platform, a random 256-bit key from
/// <see cref="RandomNumberGenerator"/>, and a fresh random 96-bit nonce per
/// encryption. No custom crypto, no derived keys, no hardcoded salts.
/// </para>
/// <para>
/// Layout on disk: <c>nonce(12) ‖ tag(16) ‖ ciphertext</c>.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class AesGcmFileSecretStore : ISecretStore
{
    /// <summary>Name of the key file inside the config directory.</summary>
    public const string KeyFileName = "secret.key";

    private const int KeyLength = 32;   // AES-256
    private const int NonceLength = 12; // AesGcm.NonceByteSizes maximum
    private const int TagLength = 16;   // AesGcm.TagByteSizes maximum
    private const string SecretFileExtension = ".aesgcm";

    private readonly string _directory;
    private readonly Lock _keyLock = new();
    private byte[]? _key;

    public AesGcmFileSecretStore(string configDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configDirectory);
        _directory = configDirectory;
    }

    /// <inheritdoc />
    public string DescriptionKey => "Store_LinuxEncryptedFile";

    /// <inheritdoc />
    /// <remarks>Always false. This is the point of the flag.</remarks>
    public bool IsHardened => false;

    /// <inheritdoc />
    public async Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Directory.CreateDirectory(_directory);
        byte[] encryptionKey = GetOrCreateKey();

        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] tag = new byte[TagLength];
        byte[] ciphertext = new byte[secret.Length];

        using (var aes = new AesGcm(encryptionKey, TagLength))
        {
            aes.Encrypt(nonce, secret.Span, ciphertext, tag);
        }

        byte[] payload = new byte[NonceLength + TagLength + ciphertext.Length];
        nonce.CopyTo(payload.AsSpan(0, NonceLength));
        tag.CopyTo(payload.AsSpan(NonceLength, TagLength));
        ciphertext.CopyTo(payload.AsSpan(NonceLength + TagLength));

        string path = PathFor(key);
        string tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, payload, ct).ConfigureAwait(false);
        RestrictToOwner(tempPath);
        File.Move(tempPath, path, overwrite: true);

        CryptographicOperations.ZeroMemory(ciphertext);
    }

    /// <inheritdoc />
    public async Task<byte[]?> RetrieveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        string path = PathFor(key);
        if (!File.Exists(path) || !File.Exists(KeyPath))
        {
            return null;
        }

        byte[] payload;
        try
        {
            payload = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (payload.Length < NonceLength + TagLength)
        {
            return null;
        }

        byte[] plaintext = new byte[payload.Length - NonceLength - TagLength];
        try
        {
            using var aes = new AesGcm(GetOrCreateKey(), TagLength);
            aes.Decrypt(
                payload.AsSpan(0, NonceLength),
                payload.AsSpan(NonceLength + TagLength),
                payload.AsSpan(NonceLength, TagLength),
                plaintext);

            return plaintext;
        }
        catch (CryptographicException)
        {
            // Wrong key or tampered ciphertext - the copied-config case. GCM
            // authenticates, so this also catches modification, not just a bad key.
            CryptographicOperations.ZeroMemory(plaintext);
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

    private string KeyPath => Path.Combine(_directory, KeyFileName);

    /// <summary>Loads the encryption key, generating it on first use.</summary>
    private byte[] GetOrCreateKey()
    {
        lock (_keyLock)
        {
            if (_key is not null)
            {
                return _key;
            }

            if (File.Exists(KeyPath))
            {
                byte[] existing = File.ReadAllBytes(KeyPath);
                if (existing.Length == KeyLength)
                {
                    _key = existing;
                    return _key;
                }

                File.Delete(KeyPath);
            }

            Directory.CreateDirectory(_directory);
            byte[] fresh = RandomNumberGenerator.GetBytes(KeyLength);
            try
            {
                using (var stream = new FileStream(KeyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(fresh);
                }

                RestrictToOwner(KeyPath);
            }
            catch (IOException)
            {
                // Another process created it first. Adopt its key.
                fresh = File.ReadAllBytes(KeyPath);
            }

            _key = fresh;
            return _key;
        }
    }

    /// <summary>Sets <c>0600</c> so only the owning user can read the file.</summary>
    private static void RestrictToOwner(string path)
    {
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            // A filesystem that cannot express Unix modes (a mounted share, say).
            // The store is already flagged as not hardened, so this changes nothing
            // the user has not already been warned about.
        }
    }

    private string PathFor(string key)
    {
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexStringLower(hash.AsSpan(0, 16)) + SecretFileExtension);
    }
}
