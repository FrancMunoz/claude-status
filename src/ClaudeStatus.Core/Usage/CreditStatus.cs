using System.Globalization;

namespace ClaudeStatus.Usage;

/// <summary>Where the account stands with usage credits.</summary>
public enum CreditState
{
    /// <summary>Credits are on, and no limit is spent: the plan is still paying.</summary>
    Standby,

    /// <summary>A window is spent and credits are on, so usage is being paid for.</summary>
    InUse,

    /// <summary>The monthly credit cap is used up: nothing is covering usage past the limits.</summary>
    CapReached,
}

/// <summary>
/// The credit picture every indicator shows: whether usage is being paid for,
/// how much has been spent, and how much of the cap is left.
/// </summary>
/// <remarks>
/// <para>
/// <b>The endpoint has no "on credits" flag.</b> <c>spend.enabled</c> and
/// <c>extra_usage.is_enabled</c> only say credits are switched on, which is just
/// as true on a Monday morning with every limit untouched. What was observed on a
/// live account (2026-10-23) is that spend rises while a limit sits at 100 %, so
/// "in use" is inferred: credits on, and a window spent by the same rule the
/// indicators use for their cross (<see cref="IndicatorText.IsExhausted"/>).
/// </para>
/// <para>
/// The Fable window counts. Spending it does not stop other models, but anything
/// run on Fable past that point is paid for, and the person who reads this is the
/// one who knows whether they are doing that.
/// </para>
/// <para>
/// <b>Nor is there an "available" figure.</b> <c>spend.balance</c> was null on the
/// account inspected, so <see cref="Remaining"/> is the cap minus what is spent -
/// what is left this month as long as the cap is what binds. No reset date for the
/// cap is reported either.
/// </para>
/// <para>
/// Money comes from <c>spend</c>, which carries it typed, and falls back to
/// <c>extra_usage</c>, whose figures are in minor units with
/// <c>decimal_places</c> beside them.
/// </para>
/// </remarks>
/// <param name="State">Whether credits are being drawn on.</param>
/// <param name="Used">Spent this month, when reported.</param>
/// <param name="Limit">The monthly cap, when one is set.</param>
public sealed record CreditStatus(CreditState State, MoneyAmount? Used, MoneyAmount? Limit)
{
    /// <summary>
    /// The cap minus what is spent, never below zero. Null without both figures, or
    /// when they are in different currencies or precisions and cannot be subtracted.
    /// </summary>
    public MoneyAmount? Remaining
        => Used is { } used && Limit is { } limit
            && used.Exponent == limit.Exponent
            && string.Equals(used.Currency, limit.Currency, StringComparison.OrdinalIgnoreCase)
                ? limit with { AmountMinor = Math.Max(limit.AmountMinor - used.AmountMinor, 0) }
                : null;

    /// <summary>How much of the cap is spent, 0-100, or null without a cap.</summary>
    /// <remarks>
    /// Worked out from the amounts rather than taken from <c>spend.percent</c>,
    /// which the endpoint rounds to a whole number: 5.62 of 100 arrives as 6.
    /// </remarks>
    public double? Percent
        => Used is { } used && Limit is { AmountMinor: > 0 } limit && used.Exponent == limit.Exponent
            ? Math.Clamp(used.AmountMinor * 100d / limit.AmountMinor, 0d, 100d)
            : null;

    /// <summary>Whether usage is past the plan's limits right now, paid for or blocked.</summary>
    public bool IsPastLimits => State != CreditState.Standby;

    /// <summary>
    /// Reads the credit picture from a snapshot, or null when credits are off or
    /// the source did not report them - in which case no indicator mentions them.
    /// </summary>
    public static CreditStatus? From(UsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        SpendInfo? spend = snapshot.Spend;
        ExtraUsageInfo? extra = snapshot.ExtraUsage;
        bool enabled = spend?.Enabled == true || extra?.IsEnabled == true;
        if (!enabled)
        {
            return null;
        }

        MoneyAmount? used = spend?.Used ?? extra?.ToMoney(extra.UsedCredits);
        MoneyAmount? limit = spend?.Limit ?? extra?.ToMoney(extra.MonthlyLimit);

        bool capReached = extra?.SpendLimitReached == true
            || (used is { } u && limit is { AmountMinor: > 0 } l
                && u.Exponent == l.Exponent && u.AmountMinor >= l.AmountMinor);

        CreditState state = capReached
            ? CreditState.CapReached
            : AnySpent(snapshot) ? CreditState.InUse : CreditState.Standby;

        return new CreditStatus(state, used, limit);
    }

    private static bool AnySpent(UsageSnapshot snapshot)
        => Spent(snapshot.Session) || Spent(snapshot.Week) || Spent(snapshot.WeekFable);

    private static bool Spent(UsageWindow? window)
        => window is not null && IndicatorText.IsExhausted(window.Percent);

    /// <summary>
    /// The currency as one character where it has a common one - <c>€</c>,
    /// <c>$</c>, <c>£</c>, <c>¥</c> - and as its code otherwise.
    /// </summary>
    /// <remarks>
    /// A short list rather than <see cref="RegionInfo"/>: the symbol a region uses
    /// for its own currency is not what the rest of the world writes (in Canada,
    /// CAD is "$"), and an indicator has room for exactly one glyph.
    /// </remarks>
    public static string Symbol(string? currency) => currency?.ToUpperInvariant() switch
    {
        "EUR" => "€",
        "USD" => "$",
        "GBP" => "£",
        "JPY" => "¥",
        null or "" => string.Empty,
        _ => currency.ToUpperInvariant(),
    };

    /// <summary>Whether <see cref="Symbol"/> is a single glyph an icon can draw.</summary>
    public static bool HasGlyph(string? currency) => Symbol(currency).Length == 1;

    /// <summary>
    /// An amount as an indicator writes it: <c>€5.62</c>. Invariant digits, like
    /// every token in <see cref="IndicatorText"/>.
    /// </summary>
    public static string Compact(MoneyAmount? amount)
    {
        if (amount is null)
        {
            return "--";
        }

        string symbol = Symbol(amount.Currency);
        string number = amount.FormatNumber(CultureInfo.InvariantCulture);
        return symbol.Length == 1 ? symbol + number : (symbol.Length > 0 ? $"{number} {symbol}" : number);
    }
}
