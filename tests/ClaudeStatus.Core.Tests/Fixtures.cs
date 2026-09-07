namespace ClaudeStatus.Core.Tests;

/// <summary>Loads the recorded responses in <c>Fixtures/</c>.</summary>
internal static class Fixture
{
    public const string Normal = "usage-normal.json";
    public const string NoScopedWindow = "usage-no-scoped-window.json";
    public const string LegacyFlatOnly = "usage-legacy-flat-only.json";
    public const string UnknownShapes = "usage-unknown-shapes.json";
    public const string RateLimited = "error-rate-limited.json";

    /// <summary>An arbitrary but fixed clock reading, so assertions never depend on "now".</summary>
    public static readonly DateTimeOffset FixedNow = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    public static string Read(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.Exists(path)
            ? File.ReadAllText(path)
            : throw new FileNotFoundException($"Fixture '{name}' was not copied to the output directory.", path);
    }

    public static UsageSnapshot Parse(string name) => UsageResponseParser.Parse(Read(name), FixedNow);
}
