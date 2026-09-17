using System.Text.Json.Serialization;

namespace ClaudeStatus.Sessions;

/// <summary>
/// Source-generated serializer metadata for the session spool and the session list.
/// </summary>
/// <remarks>
/// <para>
/// Same reason as <c>ClaudeStatusJsonContext</c>: a trimmed build disables
/// reflection-based <c>JsonSerializer</c>, and the first release build with
/// sessions threw at startup reading <c>sessions.json</c> (2026-09-16) - tests
/// never saw it, because they are not trimmed.
/// </para>
/// <para>
/// A context of its own rather than a line in <c>ClaudeStatusJsonContext</c>,
/// because the options differ and the files already on disk depend on them: no
/// naming policy (the properties were written PascalCase) and not indented (the
/// spool is written by a hook process on every turn).
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = false)]
[JsonSerializable(typeof(SessionStore.StoredSession[]))]
[JsonSerializable(typeof(SessionSpool.SpooledEvent))]
internal sealed partial class SessionsJsonContext : JsonSerializerContext;
