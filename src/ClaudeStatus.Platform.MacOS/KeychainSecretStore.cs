using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using ClaudeStatus.Security;

namespace ClaudeStatus.Platform.MacOS;

/// <summary>How a read of another application's Keychain item turned out.</summary>
/// <remarks>
/// A bare null cannot carry this. "The item is not there" and "you were told no"
/// look the same to a caller holding null, and they call for opposite responses:
/// the first is worth retrying on the next poll because Claude Code may log in at
/// any moment, and the second must not be, because retrying a refusal is what
/// turns one permission prompt into one per poll.
/// </remarks>
public enum KeychainReadOutcome
{
    /// <summary>The item was read.</summary>
    Found = 0,

    /// <summary>No such item. Nobody was asked anything.</summary>
    NotFound = 1,

    /// <summary>The user was asked and said no, or the Keychain is locked.</summary>
    Denied = 2,

    /// <summary>Security.framework failed for some other reason.</summary>
    Unavailable = 3,
}

/// <summary>The outcome of a Keychain read, and the bytes when there are any.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Secret">
/// The item's data, set only when <paramref name="Outcome"/> is
/// <see cref="KeychainReadOutcome.Found"/>. The caller owns it and must zero it.
/// </param>
public readonly record struct KeychainReadResult(KeychainReadOutcome Outcome, byte[]? Secret);

/// <summary>
/// Stores secrets in the macOS login Keychain via <c>Security.framework</c>.
/// </summary>
/// <remarks>
/// <para>
/// Uses a generic password item keyed by service and account, the same shape
/// Claude Code itself uses. The Keychain binds the item to the user's login
/// keychain, so a copied config directory carries nothing (threat T6) - the
/// secret was never in the config directory to begin with.
/// </para>
/// <para>
/// P/Invoke rather than a NuGet wrapper: the surface needed is four functions,
/// and a dependency here would be a dependency sitting directly on the
/// credential path.
/// </para>
/// <para>
/// <b>Not verifiable on the development machine.</b> Written to the documented
/// contract and exercised by the shared <c>SecretStoreContractTests</c>, which
/// run on the macOS CI leg.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public sealed class KeychainSecretStore : ISecretStore
{
    private const string SecurityFramework =
        "/System/Library/Frameworks/Security.framework/Security";

    /// <summary>The service name our Keychain items are filed under.</summary>
    public const string ServiceName = "ClaudeStatus";

    // Security.framework result codes we care about.
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecAuthFailed = -25293;
    private const int ErrSecUserCanceled = -128;

    /// <inheritdoc />
    public string DescriptionKey => "Store_MacKeychain";

    /// <inheritdoc />
    public bool IsHardened => true;

    /// <inheritdoc />
    public Task StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        byte[] service = Encoding.UTF8.GetBytes(ServiceName);
        byte[] account = Encoding.UTF8.GetBytes(key);
        byte[] value = secret.ToArray();

        try
        {
            // SecItemAdd refuses duplicates, so replace by deleting first. The
            // window between the two is harmless: the worst case is the user
            // re-enters a token.
            _ = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out uint _, out IntPtr existing, out IntPtr existingItem);

            if (existing != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, existing);
            }

            int status = existingItem != IntPtr.Zero
                ? SecKeychainItemModifyAttributesAndData(existingItem, IntPtr.Zero, (uint)value.Length, value)
                : SecKeychainAddGenericPassword(
                    IntPtr.Zero,
                    (uint)service.Length, service,
                    (uint)account.Length, account,
                    (uint)value.Length, value,
                    out IntPtr _);

            if (status is not ErrSecSuccess and not ErrSecDuplicateItem)
            {
                throw new SecretStoreException(DescribeStatus(status, "store"));
            }

            return Task.CompletedTask;
        }
        catch (DllNotFoundException ex)
        {
            throw new SecretStoreException("The macOS Security framework is unavailable.", ex);
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new SecretStoreException("The macOS Keychain API is unavailable.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    /// <inheritdoc />
    public Task<byte[]?> RetrieveAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        byte[] service = Encoding.UTF8.GetBytes(ServiceName);
        byte[] account = Encoding.UTF8.GetBytes(key);

        IntPtr data = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out uint length, out data, out IntPtr _);

            if (status == ErrSecItemNotFound)
            {
                return Task.FromResult<byte[]?>(null);
            }

            if (status != ErrSecSuccess)
            {
                // A denied Keychain prompt is "no credential right now", not a crash.
                return Task.FromResult<byte[]?>(null);
            }

            byte[] result = new byte[length];
            Marshal.Copy(data, result, 0, (int)length);
            return Task.FromResult<byte[]?>(result);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult<byte[]?>(null);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        byte[] service = Encoding.UTF8.GetBytes(ServiceName);
        byte[] account = Encoding.UTF8.GetBytes(key);

        IntPtr data = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out uint _, out data, out IntPtr item);

            if (status == ErrSecItemNotFound || item == IntPtr.Zero)
            {
                return Task.CompletedTask;
            }

            int deleteStatus = SecKeychainItemDelete(item);
            if (deleteStatus is not ErrSecSuccess and not ErrSecItemNotFound)
            {
                throw new SecretStoreException(DescribeStatus(deleteStatus, "delete"));
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new SecretStoreException("The macOS Keychain API is unavailable.", ex);
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
        }
    }

    /// <summary>
    /// Reads a generic-password item belonging to <b>another</b> application,
    /// matching on service name alone.
    /// </summary>
    /// <remarks>
    /// Used to read Claude Code's own credential item, whose account name is the
    /// user's login and therefore not something we can predict. Passing a zero
    /// length account makes Security.framework match any account under that
    /// service. The first access prompts the user; a refusal surfaces as null.
    /// </remarks>
    public static Task<KeychainReadResult> RetrieveRawAsync(string service, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        ct.ThrowIfCancellationRequested();

        byte[] serviceBytes = Encoding.UTF8.GetBytes(service);
        IntPtr data = IntPtr.Zero;
        try
        {
            int status = SecKeychainFindGenericPassword(
                IntPtr.Zero,
                (uint)serviceBytes.Length, serviceBytes,
                0, null,
                out uint length, out data, out IntPtr _);

            if (status != ErrSecSuccess || data == IntPtr.Zero)
            {
                return Task.FromResult(new KeychainReadResult(Classify(status), null));
            }

            byte[] result = new byte[length];
            Marshal.Copy(data, result, 0, (int)length);
            return Task.FromResult(new KeychainReadResult(KeychainReadOutcome.Found, result));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult(new KeychainReadResult(KeychainReadOutcome.Unavailable, null));
        }
        finally
        {
            if (data != IntPtr.Zero)
            {
                _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
            }
        }
    }

    /// <summary>Sorts a failing OSStatus into an outcome a caller can act on.</summary>
    /// <remarks>
    /// <c>errSecUserCanceled</c> is the prompt's Deny button. <c>errSecAuthFailed</c>
    /// covers a locked keychain and a password the user could not supply - both are
    /// "asked and not granted", and both mean the same thing to a poll loop.
    /// </remarks>
    private static KeychainReadOutcome Classify(int status) => status switch
    {
        ErrSecItemNotFound => KeychainReadOutcome.NotFound,
        ErrSecUserCanceled or ErrSecAuthFailed => KeychainReadOutcome.Denied,
        _ => KeychainReadOutcome.Unavailable,
    };

    /// <summary>Turns an OSStatus into a message. Never includes the secret.</summary>
    private static string DescribeStatus(int status, string operation) => status switch
    {
        ErrSecAuthFailed => $"The Keychain refused the {operation}: authentication failed.",
        ErrSecUserCanceled => $"The Keychain {operation} was cancelled.",
        _ => $"The Keychain {operation} failed (OSStatus {status}).",
    };

    [DllImport(SecurityFramework, CharSet = CharSet.Ansi)]
    private static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[] accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework, CharSet = CharSet.Ansi)]
    private static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        byte[] serviceName,
        uint accountNameLength,
        byte[]? accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef,
        IntPtr attrList,
        uint length,
        byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);
}
