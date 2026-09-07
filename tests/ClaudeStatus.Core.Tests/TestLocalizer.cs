using System.Globalization;
using ClaudeStatus.Localization;

namespace ClaudeStatus.Core.Tests;

/// <summary>
/// A localizer over the real compiled resources, pinned to English.
/// </summary>
/// <remarks>
/// Core now returns resource keys rather than sentences, so the tests that used
/// to assert on wording resolve the key through the real resource set instead.
/// That keeps what those tests were actually protecting - that a message says
/// "unofficial", that it never echoes a token - while also failing if a key is
/// ever renamed in one place and not the other.
/// </remarks>
internal static class TestLocalizer
{
    public static Localizer English()
    {
        var localizer = new Localizer();
        localizer.SetCulture(CultureInfo.GetCultureInfo("en"));
        return localizer;
    }

    /// <summary>The English text for a key.</summary>
    public static string English(string key) => English()[key];
}
