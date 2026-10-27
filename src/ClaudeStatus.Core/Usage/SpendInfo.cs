using System.Globalization;

namespace ClaudeStatus.Usage;

/// <summary>
/// An amount of money as the endpoint reports it: minor units plus an exponent.
/// </summary>
/// <param name="AmountMinor">The amount in the currency's smallest unit, e.g. cents.</param>
/// <param name="Currency">ISO 4217 code, e.g. <c>USD</c>.</param>
/// <param name="Exponent">How many decimal places the currency has. 2 for USD, 0 for JPY.</param>
/// <remarks>
/// Kept in minor units rather than converted on parse, because the exponent is
/// what says where the decimal point goes and currencies disagree about it.
/// Converting early would quietly turn ¥100 into ¥1.00.
/// </remarks>
public sealed record MoneyAmount(long AmountMinor, string? Currency, int Exponent)
{
    /// <summary>The amount as a decimal, e.g. 1234 minor units with exponent 2 is 12.34.</summary>
    /// <remarks>
    /// <see cref="decimal"/> rather than <see cref="double"/>: this is money, and a
    /// binary fraction cannot represent 0.10 exactly.
    /// </remarks>
    public decimal Value
    {
        get
        {
            // A hostile or garbled exponent must not produce an OverflowException
            // inside a UI binding. Anything outside this range is not a currency.
            int exponent = Math.Clamp(Exponent, 0, 6);
            decimal divisor = 1m;
            for (int index = 0; index < exponent; index++)
            {
                divisor *= 10m;
            }

            return AmountMinor / divisor;
        }
    }

    /// <summary>Formats the amount for display in a given culture.</summary>
    /// <remarks>
    /// Deliberately not <c>"C"</c>: that would format a USD amount with the
    /// viewer's own currency symbol, so $12.34 read on a Spanish machine would
    /// claim to be €12,34. The number follows the culture and the code is printed
    /// as-is instead.
    /// </remarks>
    public string Format(CultureInfo culture)
    {
        string number = FormatNumber(culture);
        return string.IsNullOrEmpty(Currency) ? number : $"{number} {Currency}";
    }

    /// <summary>The number alone, to as many places as the currency has.</summary>
    public string FormatNumber(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        return Value.ToString(
            "N" + Math.Clamp(Exponent, 0, 6).ToString(CultureInfo.InvariantCulture), culture);
    }
}

/// <summary>
/// The <c>spend</c> block: pay-as-you-go money spent beyond the plan's limits.
/// </summary>
/// <remarks>
/// On a plan with credits switched off - which is the default, and what the
/// recorded capture shows - <see cref="Enabled"/> is false and everything else is
/// zero or null. The report window says so plainly rather than showing a row of
/// dashes that look like a failure.
/// </remarks>
/// <param name="Enabled">Whether spending beyond the plan is switched on.</param>
/// <param name="Used">Money spent so far.</param>
/// <param name="Limit">The cap, when one is set.</param>
/// <param name="Percent">How much of the cap is used, 0-100.</param>
/// <param name="Severity">The source's own label. Raw, never an enum.</param>
/// <param name="DisabledReason">Why spending is off, when the source says.</param>
public sealed record SpendInfo(
    bool Enabled,
    MoneyAmount? Used,
    MoneyAmount? Limit,
    double? Percent,
    string? Severity,
    string? DisabledReason);

/// <summary>
/// The <c>extra_usage</c> block: prepaid credits that cover you past the plan limits.
/// </summary>
/// <param name="IsEnabled">Whether extra usage is active on this account.</param>
/// <param name="UserDisabled">Whether the user themselves turned it off.</param>
/// <param name="SpendLimitReached">Whether the monthly cap has been hit.</param>
/// <param name="CreditsEverEnabled">Whether it was ever on - distinguishes "never used" from "turned off".</param>
/// <param name="MonthlyLimit">The monthly cap, in credits.</param>
/// <param name="UsedCredits">Credits consumed this month.</param>
/// <param name="Utilization">How much of the cap is used, 0-100.</param>
/// <param name="Currency">Currency of the credit figures, when given.</param>
/// <param name="DisabledReason">Why it is off, when the source says.</param>
public sealed record ExtraUsageInfo(
    bool IsEnabled,
    bool UserDisabled,
    bool SpendLimitReached,
    bool CreditsEverEnabled,
    double? MonthlyLimit,
    double? UsedCredits,
    double? Utilization,
    string? Currency,
    string? DisabledReason)
{
    /// <summary>
    /// Where the decimal point goes in <see cref="MonthlyLimit"/> and
    /// <see cref="UsedCredits"/>, which are minor units: 562 with 2 is 5.62.
    /// </summary>
    public int? DecimalPlaces { get; init; }

    /// <summary>
    /// Turns one of this block's figures into money. They are minor units:
    /// <c>used_credits: 562.0</c> beside <c>decimal_places: 2</c> was 5.62 EUR,
    /// matching <c>spend.used</c> in the same response (2026-10-23). Without
    /// <c>decimal_places</c>, 2 is assumed, which is what every currency the
    /// account can be billed in has used so far.
    /// </summary>
    public MoneyAmount? ToMoney(double? minor)
        => minor is { } value && double.IsFinite(value)
            ? new MoneyAmount(
                (long)Math.Round(Math.Clamp(value, 0d, long.MaxValue / 2d)),
                Currency,
                Math.Clamp(DecimalPlaces ?? 2, 0, 6))
            : null;
}
