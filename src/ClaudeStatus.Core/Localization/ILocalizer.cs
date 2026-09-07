using System.ComponentModel;
using System.Globalization;

namespace ClaudeStatus.Localization;

/// <summary>
/// Resolves a resource key to text in the language currently in use.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Core rather than the app so that every layer speaks in <b>keys</b>
/// instead of English prose. Nothing below the UI should decide how a sentence
/// reads: platform code reports <c>"Store_WindowsDpapi"</c>, and only the view
/// turns that into "Windows DPAPI…" or "DPAPI de Windows…".
/// </para>
/// <para>
/// Implements <see cref="INotifyPropertyChanged"/> so a language change
/// refreshes every binding without reopening a window. Implementations raise the
/// change for the indexer, which Avalonia treats as "all keys may have changed".
/// </para>
/// </remarks>
public interface ILocalizer : INotifyPropertyChanged
{
    /// <summary>The language currently in use. Also the culture used for formatting.</summary>
    CultureInfo Culture { get; }

    /// <summary>
    /// The text for a key.
    /// </summary>
    /// <remarks>
    /// Never throws and never returns null. An unknown key comes back as the key
    /// itself, which is ugly on screen and therefore easy to spot - far better
    /// than a blank label that nobody notices until a user reports it.
    /// </remarks>
    string this[string key] { get; }

    /// <summary>Looks up a key and fills in its placeholders, using <see cref="Culture"/>.</summary>
    string Format(string key, params object?[] arguments);
}
