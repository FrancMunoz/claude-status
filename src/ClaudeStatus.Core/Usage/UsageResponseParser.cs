using System.Globalization;
using System.Text.Json;

namespace ClaudeStatus.Usage;

/// <summary>
/// Turns the raw <c>GET /api/oauth/usage</c> body into a <see cref="UsageSnapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Separated from <see cref="ClaudeSubscriptionUsageProvider"/> so it can be
/// tested against recorded fixtures with no network involved.
/// </para>
/// <para>
/// The endpoint is undocumented and has already been observed adding, renaming
/// and nulling fields between releases (see <c>docs/data-source.md</c>). The
/// rules here are therefore deliberately paranoid:
/// </para>
/// <list type="bullet">
///   <item>Never enumerate top-level keys - new codenamed buckets appear without warning.</item>
///   <item>Never bind <c>severity</c> or <c>kind</c> to a closed enum.</item>
///   <item>Accept a percentage as either an integer or a float.</item>
///   <item>Accept <c>null</c> anywhere, including a missing <c>limits</c> array.</item>
///   <item>An unreadable field yields "unknown", never an exception and never a zero.</item>
/// </list>
/// </remarks>
public static class UsageResponseParser
{
    private const string KindSession = "session";
    private const string KindWeeklyAll = "weekly_all";
    private const string KindWeeklyScoped = "weekly_scoped";

    /// <summary>
    /// Parses a response body. Throws only when the payload is not JSON at all.
    /// </summary>
    /// <param name="json">The raw response body.</param>
    /// <param name="fetchedAt">The clock reading to stamp on the snapshot.</param>
    /// <exception cref="UsageParseException">The body was not valid JSON, or not a JSON object.</exception>
    public static UsageSnapshot Parse(string json, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            // Deliberately does not include the body: a malformed response could
            // be an error page echoing a header. See docs/security.md T2.
            throw new UsageParseException("The usage response was not valid JSON.", ex);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new UsageParseException(
                    $"The usage response was a JSON {root.ValueKind}, expected an object.");
            }

            UsageWindow? session = null;
            UsageWindow? week = null;
            UsageWindow? weekFable = null;
            Dictionary<string, UsageWindow> other = new(StringComparer.OrdinalIgnoreCase);

            // Preferred source: the structured limits[] array.
            if (root.TryGetProperty("limits", out JsonElement limits)
                && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement limit in limits.EnumerateArray())
                {
                    if (limit.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string? kind = ReadString(limit, "kind");
                    UsageWindow? window = ReadLimitWindow(limit);
                    if (kind is null || window is null)
                    {
                        continue;
                    }

                    switch (kind)
                    {
                        case KindSession:
                            session ??= window;
                            break;

                        case KindWeeklyAll:
                            week ??= window;
                            break;

                        case KindWeeklyScoped:
                            string model = ReadScopedModelName(limit) ?? "Unknown";
                            window = window with { ScopeModel = model };
                            if (string.Equals(model, UsageSnapshot.FableModelName, StringComparison.OrdinalIgnoreCase))
                            {
                                weekFable ??= window;
                            }
                            else
                            {
                                // Unknown scoped models are kept, not dropped, so a
                                // future model shows up in the details window instead
                                // of silently vanishing.
                                other.TryAdd(model, window);
                            }

                            break;

                        default:
                            // An unrecognised kind is expected, not exceptional.
                            break;
                    }
                }
            }

            // Fallback: the legacy flat keys, used only for what limits[] did not provide.
            session ??= ReadFlatWindow(root, "five_hour");
            week ??= ReadFlatWindow(root, "seven_day");

            return new UsageSnapshot(
                Session: session,
                Week: week,
                WeekFable: weekFable,
                OtherWindows: other,
                FetchedAt: fetchedAt,
                IsStale: false)
            {
                Spend = ReadSpend(root),
                ExtraUsage = ReadExtraUsage(root),
            };
        }
    }

    /// <summary>Reads a <c>limits[]</c> entry, which carries <c>percent</c>.</summary>
    private static UsageWindow? ReadLimitWindow(JsonElement limit)
    {
        double? percent = ReadNumber(limit, "percent");
        if (percent is null)
        {
            return null;
        }

        return UsageWindow.Create(percent.Value, ReadTimestamp(limit, "resets_at")) with
        {
            Severity = ReadString(limit, "severity"),
            Kind = ReadString(limit, "kind"),
            IsActive = ReadBool(limit, "is_active") ?? false,
            LimitDollars = ReadNumber(limit, "limit_dollars"),
            UsedDollars = ReadNumber(limit, "used_dollars"),
            RemainingDollars = ReadNumber(limit, "remaining_dollars"),
            LockedReason = ReadString(limit, "locked_reason"),
        };
    }

    /// <summary>Reads a legacy flat bucket, which carries <c>utilization</c>.</summary>
    /// <remarks>
    /// Any top-level bucket may be <c>null</c> or an object - <c>nimbus_quill</c>
    /// was observed as an object while its siblings were null.
    /// </remarks>
    private static UsageWindow? ReadFlatWindow(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement bucket) || bucket.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? utilization = ReadNumber(bucket, "utilization");
        if (utilization is null)
        {
            return null;
        }

        return UsageWindow.Create(utilization.Value, ReadTimestamp(bucket, "resets_at")) with
        {
            LimitDollars = ReadNumber(bucket, "limit_dollars"),
            UsedDollars = ReadNumber(bucket, "used_dollars"),
            RemainingDollars = ReadNumber(bucket, "remaining_dollars"),
            LockedReason = ReadString(bucket, "locked_reason"),
        };
    }

    /// <summary>Reads the <c>spend</c> block, which is absent on most plans.</summary>
    private static SpendInfo? ReadSpend(JsonElement root)
    {
        if (!root.TryGetProperty("spend", out JsonElement spend) || spend.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new SpendInfo(
            Enabled: ReadBool(spend, "enabled") ?? false,
            Used: ReadMoney(spend, "used"),
            Limit: ReadMoney(spend, "limit"),
            Percent: ReadNumber(spend, "percent"),
            Severity: ReadString(spend, "severity"),
            DisabledReason: ReadString(spend, "disabled_reason"));
    }

    /// <summary>Reads the <c>extra_usage</c> block: prepaid credits.</summary>
    private static ExtraUsageInfo? ReadExtraUsage(JsonElement root)
    {
        if (!root.TryGetProperty("extra_usage", out JsonElement extra)
            || extra.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new ExtraUsageInfo(
            IsEnabled: ReadBool(extra, "is_enabled") ?? false,
            UserDisabled: ReadBool(extra, "user_disabled") ?? false,
            SpendLimitReached: ReadBool(extra, "spend_limit_reached") ?? false,
            CreditsEverEnabled: ReadBool(extra, "credits_ever_enabled") ?? false,
            MonthlyLimit: ReadNumber(extra, "monthly_limit"),
            UsedCredits: ReadNumber(extra, "used_credits"),
            Utilization: ReadNumber(extra, "utilization"),
            Currency: ReadString(extra, "currency"),
            DisabledReason: ReadString(extra, "disabled_reason"));
    }

    /// <summary>Reads a <c>{ amount_minor, currency, exponent }</c> money object.</summary>
    private static MoneyAmount? ReadMoney(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement money) || money.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        double? amount = ReadNumber(money, "amount_minor");
        if (amount is null)
        {
            return null;
        }

        return new MoneyAmount(
            AmountMinor: (long)Math.Clamp(amount.Value, long.MinValue, long.MaxValue),
            Currency: ReadString(money, "currency"),

            // A missing exponent means the value is already in major units, which
            // is a safer assumption than dividing by an arbitrary power of ten.
            Exponent: (int)(ReadNumber(money, "exponent") ?? 0d));
    }

    /// <summary>Digs out <c>scope.model.display_name</c>, tolerating nulls at every level.</summary>
    private static string? ReadScopedModelName(JsonElement limit)
        => limit.TryGetProperty("scope", out JsonElement scope) && scope.ValueKind == JsonValueKind.Object
        && scope.TryGetProperty("model", out JsonElement model) && model.ValueKind == JsonValueKind.Object
            ? ReadString(model, "display_name")
            : null;

    /// <summary>
    /// Reads a boolean, tolerating the string and numeric spellings of one.
    /// </summary>
    /// <remarks>
    /// The endpoint has been seen returning numbers where a boolean was expected
    /// on other fields, so this accepts <c>true</c>, <c>"true"</c> and <c>1</c>
    /// rather than treating anything unfamiliar as false.
    /// </remarks>
    private static bool? ReadBool(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetDouble(out double number) => number != 0d,
            JsonValueKind.String when bool.TryParse(value.GetString(), out bool parsed) => parsed,
            _ => null,
        };
    }

    private static string? ReadString(JsonElement owner, string name)
        => owner.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Reads a number that may arrive as an int (<c>29</c>) or a float (<c>29.0</c>),
    /// and has also been seen as a numeric string.
    /// </summary>
    private static double? ReadNumber(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double number) => number,
            JsonValueKind.String when double.TryParse(
                value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null,
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out DateTimeOffset parsed)
            ? parsed
            : null;
    }
}
