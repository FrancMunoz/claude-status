using System.Text.Json;
using ClaudeStatus.App.Composition;
using ClaudeStatus.Sessions;

namespace ClaudeStatus.App;

/// <summary>
/// What this executable does when Claude Code runs it as a hook.
/// </summary>
/// <remarks>
/// <para>
/// The same binary, started with <see cref="ClaudeCodeHooks.Marker"/>, at the end
/// of somebody's turn. It reads the payload Claude Code pipes in, drops one file
/// in the spool and exits - no window, no Avalonia, no tray icon. A hook that
/// started a UI would put a second copy of the app on screen every time anyone
/// pressed Enter.
/// </para>
/// <para>
/// It must run before anything touches Avalonia, for the same reason the Velopack
/// hooks in <c>Program.cs</c> do: by the time a lifetime exists it is too late to
/// decide not to have one.
/// </para>
/// <para>
/// <b>It never fails loudly.</b> Whatever happens here happens at the end of
/// every turn, in a process the user did not start and cannot see. An error
/// message would be printed into their session; a non-zero exit would be reported
/// as a failing hook. A missed notification is worth neither.
/// </para>
/// </remarks>
internal static class HookEntryPoint
{
    /// <summary>
    /// Handles the hook invocation, if this is one.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="configDirectory">Where the spool lives.</param>
    /// <returns>True when this process was a hook and is now done.</returns>
    public static bool TryHandle(string[] args, string configDirectory)
    {
        if (args is null || Array.IndexOf(args, ClaudeCodeHooks.Marker) < 0)
        {
            return false;
        }

        try
        {
            // The event name is the argument after the marker. Everything else on
            // the command line is ignored: it is our own hook, and anything we did
            // not put there is not ours to interpret.
            int marker = Array.IndexOf(args, ClaudeCodeHooks.Marker);
            string? hookEvent = marker + 1 < args.Length ? args[marker + 1] : null;

            if (ClaudeCodeHooks.KindFor(hookEvent) is null)
            {
                return true;
            }

            (string? id, string? folder, string? notificationType) = Parse(ReadPayload());

            // Decided again with the payload in hand: a Notification only counts when
            // it is the idle prompt, and the type is inside the payload.
            if (ClaudeCodeHooks.KindFor(hookEvent, notificationType) is not { } kind)
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                // Here or nowhere: this process's ancestors are the session's
                // terminal, and they stop being reachable the moment it exits.
                // After the payload is read, because finding the console attaches
                // to it for an instant. Not for an ending session - there is
                // nothing left to focus, and SessionEnd runs synchronously while
                // Claude Code waits.
                SessionOrigin? origin = kind == SessionEventKind.Ended
                    ? null
                    : PlatformServices.CreateTerminalFocus().Capture();

                new SessionSpool(configDirectory).Write(
                    new SessionEvent(kind, id, folder ?? string.Empty, DateTimeOffset.UtcNow, origin));
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or OutOfMemoryException)
        {
            // See the note above: silence is the only correct behaviour here.
        }

        return true;
    }

    /// <summary>
    /// Reads the hook payload from standard input.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>Console.In</c>.</b> This executable is a <c>WinExe</c> - it has
    /// no console, because a tray app that opened one would flash a black window
    /// at every launch. In a process with no console <c>Console.In</c> is a null
    /// reader: it returns an empty string immediately rather than failing, so the
    /// payload silently arrived empty, no session id was found, and the hook
    /// wrote nothing at all while appearing to succeed.
    /// <see cref="Console.OpenStandardInput"/> returns the handle the parent
    /// actually gave us, which is the pipe Claude Code is writing to.
    /// </remarks>
    private static string ReadPayload()
    {
        using Stream input = Console.OpenStandardInput();
        if (input == Stream.Null)
        {
            return string.Empty;
        }

        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Pulls the session id, the folder and the notification type out of Claude Code's payload.
    /// </summary>
    /// <remarks>
    /// Read field by field rather than deserialized into a type, and every field
    /// optional. This is another application's wire format arriving on stdin: it
    /// will grow keys we have never seen, and the day it does must not be the day
    /// notifications stop. Nothing else in the payload is read - notably not
    /// <c>transcript_path</c>, which we are handed and have no business opening.
    /// </remarks>
    private static (string? Id, string? Folder, string? NotificationType) Parse(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return (null, null, null);
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (null, null, null);
        }

        return (
            Text(document.RootElement, "session_id"),
            Text(document.RootElement, "cwd"),
            Text(document.RootElement, "notification_type"));
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
