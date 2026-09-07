using System.Text.Json.Serialization;
using ClaudeStatus.Usage;

namespace ClaudeStatus.Config;

/// <summary>
/// The source-generated serializer metadata for everything this app persists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every type written to or read from disk must be listed here.</b> Reflection
/// based <c>JsonSerializer.Serialize&lt;T&gt;</c> works in a normal build and then
/// silently does nothing once the app is trimmed or AOT compiled - measured
/// 2026-09-04, where a trimmed build ran perfectly, logged normally, and quietly
/// never wrote its snapshot cache. Source generation is what makes persistence
/// survive <c>PublishTrimmed</c> and <c>PublishAot</c>.
/// </para>
/// <para>
/// Options live on the attribute rather than in a <c>JsonSerializerOptions</c>
/// instance, because the generator has to see them at compile time. In
/// particular <c>UseStringEnumConverter</c> replaces
/// <c>new JsonStringEnumConverter()</c>, whose non-generic form needs runtime
/// code generation and is flagged by IL3050.
/// </para>
/// <para>
/// Settings and the snapshot cache share one context and therefore one set of
/// options. Both are small, hand-inspectable files, so indenting both is a
/// feature: the cache is a couple of hundred bytes either way.
/// </para>
/// <para>
/// <b>Nothing here ever holds a secret.</b> See <c>docs/security.md</c> §4.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CachedSnapshot))]
internal sealed partial class ClaudeStatusJsonContext : JsonSerializerContext;
