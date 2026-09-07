using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.Linux;

/// <summary>
/// Stores secrets in the freedesktop Secret Service (GNOME Keyring, KWallet)
/// through <c>libsecret</c>.
/// </summary>
/// <remarks>
/// <para>
/// The preferred Linux store. The keyring is unlocked by the user's login, so a
/// copied config directory carries nothing - the secret never touches it
/// (threat T6).
/// </para>
/// <para>
/// <see cref="IsAvailable"/> probes for the library and a reachable service.
/// On a headless box, or a session with no keyring daemon, the composition root
/// falls back to <see cref="AesGcmFileSecretStore"/> and the Config window shows
/// the warning that <see cref="ISecretStore.IsHardened"/> demands.
/// </para>
/// <para>
/// <b>Not verifiable on the development machine.</b> Written to the libsecret
/// contract and exercised by the shared <c>SecretStoreContractTests</c> on the
/// Linux CI leg, which skip when no keyring is present.
/// </para>
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class LibsecretSecretStore : ISecretStore
{
    // libsecret ships an unversioned .so only with the -dev package, so try the
    // versioned sonames a normal desktop actually has.
    private const string Libsecret = "libsecret-1.so.0";

    /// <summary>Attribute name our items are tagged with.</summary>
    public const string AttributeName = "claudestatus-key";

    private const string SchemaName = "com.zeroworks.ClaudeStatus";

    /// <inheritdoc />
    public string DescriptionKey => "Store_LinuxSecretService";

    /// <inheritdoc />
    public bool IsHardened => true;

    /// <summary>
    /// True when libsecret is present and a keyring answers.
    /// </summary>
    /// <remarks>
    /// Probes with a lookup for a key that will not exist: "not found" proves the
    /// service is reachable, which is exactly what we need to know.
    /// </remarks>
    public static bool IsAvailable()
    {
        try
        {
            IntPtr schema = CreateSchema();
            if (schema == IntPtr.Zero)
            {
                return false;
            }

            IntPtr error = IntPtr.Zero;
            IntPtr value = secret_password_lookup_sync(
                schema, IntPtr.Zero, ref error, AttributeName, "__probe__", IntPtr.Zero);

            if (value != IntPtr.Zero)
            {
                secret_password_free(value);
            }

            bool failed = error != IntPtr.Zero;
            if (failed)
            {
                g_error_free(error);
            }

            secret_schema_unref(schema);
            return !failed;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        // libsecret's password API is null-terminated UTF-8. An access token is
        // base64url text, so this is lossless.
        byte[] nullTerminated = new byte[secret.Length + 1];
        try
        {
            secret.Span.CopyTo(nullTerminated);

            IntPtr schema = CreateSchema();
            if (schema == IntPtr.Zero)
            {
                throw new SecretStoreException("Could not create a libsecret schema.");
            }

            IntPtr error = IntPtr.Zero;
            try
            {
                bool stored = secret_password_store_sync(
                    schema,
                    null,                       // default collection
                    "ClaudeStatus access token",
                    nullTerminated,
                    IntPtr.Zero,
                    ref error,
                    AttributeName,
                    key,
                    IntPtr.Zero);

                if (!stored || error != IntPtr.Zero)
                {
                    throw new SecretStoreException("The system keyring refused to store the credential.");
                }

                return Task.CompletedTask;
            }
            finally
            {
                if (error != IntPtr.Zero)
                {
                    g_error_free(error);
                }

                secret_schema_unref(schema);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreException("libsecret is not available on this system.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nullTerminated);
        }
    }

    /// <inheritdoc />
    public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        try
        {
            IntPtr schema = CreateSchema();
            if (schema == IntPtr.Zero)
            {
                return Task.FromResult<byte[]?>(null);
            }

            IntPtr error = IntPtr.Zero;
            IntPtr value = IntPtr.Zero;
            try
            {
                value = secret_password_lookup_sync(
                    schema, IntPtr.Zero, ref error, AttributeName, key, IntPtr.Zero);

                if (error != IntPtr.Zero || value == IntPtr.Zero)
                {
                    return Task.FromResult<byte[]?>(null);
                }

                return Task.FromResult<byte[]?>(ReadNullTerminated(value));
            }
            finally
            {
                if (value != IntPtr.Zero)
                {
                    secret_password_free(value);
                }

                if (error != IntPtr.Zero)
                {
                    g_error_free(error);
                }

                secret_schema_unref(schema);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        try
        {
            IntPtr schema = CreateSchema();
            if (schema == IntPtr.Zero)
            {
                return Task.CompletedTask;
            }

            IntPtr error = IntPtr.Zero;
            try
            {
                // Returns false when there was nothing to remove, which is not an error.
                _ = secret_password_clear_sync(
                    schema, IntPtr.Zero, ref error, AttributeName, key, IntPtr.Zero);

                return Task.CompletedTask;
            }
            finally
            {
                if (error != IntPtr.Zero)
                {
                    g_error_free(error);
                }

                secret_schema_unref(schema);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreException("libsecret is not available on this system.", ex);
        }
    }

    /// <summary>Copies a null-terminated C string into a managed buffer.</summary>
    private static byte[] ReadNullTerminated(IntPtr pointer)
    {
        int length = 0;
        while (Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }

        byte[] result = new byte[length];
        Marshal.Copy(pointer, result, 0, length);
        return result;
    }

    /// <summary>Builds the schema describing our single string attribute.</summary>
    private static IntPtr CreateSchema()
        => secret_schema_new(
            SchemaName,
            SecretSchemaFlags.None,
            AttributeName,
            SecretSchemaAttributeType.String,
            IntPtr.Zero);

    private enum SecretSchemaFlags
    {
        None = 0,
    }

    private enum SecretSchemaAttributeType
    {
        String = 0,
    }

    [DllImport(
        Libsecret,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern IntPtr secret_schema_new(
        string name,
        SecretSchemaFlags flags,
        string attributeName,
        SecretSchemaAttributeType attributeType,
        IntPtr terminator);

    [DllImport(Libsecret)]
    private static extern void secret_schema_unref(IntPtr schema);

    [DllImport(
        Libsecret,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern bool secret_password_store_sync(
        IntPtr schema,
        string? collection,
        string label,
        byte[] password,
        IntPtr cancellable,
        ref IntPtr error,
        string attributeName,
        string attributeValue,
        IntPtr terminator);

    [DllImport(
        Libsecret,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern IntPtr secret_password_lookup_sync(
        IntPtr schema,
        IntPtr cancellable,
        ref IntPtr error,
        string attributeName,
        string attributeValue,
        IntPtr terminator);

    [DllImport(
        Libsecret,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern bool secret_password_clear_sync(
        IntPtr schema,
        IntPtr cancellable,
        ref IntPtr error,
        string attributeName,
        string attributeValue,
        IntPtr terminator);

    [DllImport(Libsecret)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport("libglib-2.0.so.0")]
    private static extern void g_error_free(IntPtr error);
}
