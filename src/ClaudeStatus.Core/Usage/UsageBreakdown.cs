namespace ClaudeStatus.Usage;

/// <summary>
/// Where the weekly usage went, split by the surface it was spent on: the
/// endpoint's <c>seven_day_breakdown</c> block.
/// </summary>
/// <remarks>
/// <para>
/// First seen on 2026-10-17. The shares are of the usage, not of the limit: on
/// the recorded capture they add up to 100 while the weekly window stood at 85 %.
/// </para>
/// <para>
/// The rows are kept in the order the source sent them and are never matched
/// against a closed list. A surface added upstream arrives as a row we have no
/// translation for, and the report shows it under the source's own name.
/// </para>
/// </remarks>
/// <param name="AsOf">When the source computed the split. It lags the limits.</param>
/// <param name="WindowStartedAt">When the 7-day window the split covers began.</param>
/// <param name="Rows">One row per surface, in the source's order.</param>
public sealed record UsageBreakdown(
    DateTimeOffset? AsOf,
    DateTimeOffset? WindowStartedAt,
    IReadOnlyList<UsageBreakdownRow> Rows)
{
    /// <summary>
    /// Value equality, including <see cref="Rows"/>.
    /// </summary>
    /// <remarks>
    /// The generated equality would compare the list by reference, so every poll
    /// would look like a change. See <see cref="UsageSnapshot.Equals(UsageSnapshot?)"/>
    /// for why that matters.
    /// </remarks>
    public bool Equals(UsageBreakdown? other)
        => other is not null
        && AsOf == other.AsOf
        && WindowStartedAt == other.WindowStartedAt
        && Rows.SequenceEqual(other.Rows);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(AsOf);
        hash.Add(WindowStartedAt);
        foreach (UsageBreakdownRow row in Rows)
        {
            hash.Add(row);
        }

        return hash.ToHashCode();
    }
}

/// <summary>One surface's share of the weekly usage.</summary>
/// <param name="Key">The source's identifier, e.g. <c>claude_code</c>. Raw, never an enum.</param>
/// <param name="DisplayName">The source's English label, used only for a key we cannot translate.</param>
/// <param name="Percent">The share of the week's usage, 0-100.</param>
public sealed record UsageBreakdownRow(string Key, string? DisplayName, double Percent);
