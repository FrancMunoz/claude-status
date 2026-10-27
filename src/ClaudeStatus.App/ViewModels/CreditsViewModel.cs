using ClaudeStatus.Localization;
using ClaudeStatus.Usage;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClaudeStatus.App.ViewModels;

/// <summary>
/// The usage-credits row: what has been spent past the plan, and whether that is
/// happening now. Shared by the details popup, the hover card and the widget.
/// </summary>
/// <remarks>
/// The decision - standby, in use, cap reached - is <see cref="CreditStatus"/>'s,
/// in Core, so every indicator draws the same conclusion from a reading. This only
/// words it.
/// </remarks>
public partial class CreditsViewModel : ObservableObject
{
    private readonly ILocalizer _l;
    private CreditStatus? _status;

    /// <summary>
    /// Whether the row is shown: usage is past the limits, paid for or blocked.
    /// Off or on standby, nothing mentions credits.
    /// </summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>
    /// Whether usage is past the plan's limits: being paid for, or blocked by the cap.
    /// The widget shows its credits column only then; on standby it is not news.
    /// </summary>
    [ObservableProperty]
    private bool _isPastLimits;

    /// <summary>Whether the monthly cap is spent, which is the state worth the alert colour.</summary>
    [ObservableProperty]
    private bool _isExceeded;

    /// <summary>How much of the cap is spent, 0-100, for the bar. Zero without a cap.</summary>
    [ObservableProperty]
    private double _percent;

    /// <summary>The amount spent, in the reader's number format: <c>5,62 EUR</c>.</summary>
    [ObservableProperty]
    private string _amountText = string.Empty;

    /// <summary>The widget's amount: the number alone, <see cref="CurrencySign"/> set after it.</summary>
    [ObservableProperty]
    private string _amountNumberText = string.Empty;

    /// <summary>
    /// The currency after <see cref="AmountNumberText"/> on the widget: <c>€</c>,
    /// or the code for a currency with no one-glyph sign. Set like the per-cent
    /// sign beside the other columns' numbers.
    /// </summary>
    [ObservableProperty]
    private string _currencySign = string.Empty;

    /// <summary>On credits, on standby, or cap reached.</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    /// <summary>Spent against the cap, and what is left of it.</summary>
    [ObservableProperty]
    private string _detailText = string.Empty;

    public CreditsViewModel(ILocalizer localizer)
    {
        _l = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _l.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>The row's name, in the current language.</summary>
    public string Label => _l["Credits_Label"];

    /// <summary>The widget column's label, beside 5h and 7d.</summary>
    public string ShortLabel => _l["Widget_Credits"];

    /// <summary>Shows the credit picture from a reading; null hides the row.</summary>
    public void Update(UsageSnapshot? snapshot) => Update(CreditStatus.From(snapshot));

    /// <summary>Shows a credit picture already worked out; null hides the row.</summary>
    public void Update(CreditStatus? status)
    {
        _status = status;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ShortLabel));

        // Only past the limits. On standby "credits are on" is account settings,
        // not usage, and the row was one more line to read past for nothing.
        IsShown = _status is { IsPastLimits: true };
        if (_status is null)
        {
            IsPastLimits = false;
            IsExceeded = false;
            Percent = 0;
            AmountText = AmountNumberText = CurrencySign = StateText = DetailText = string.Empty;
            return;
        }

        IsPastLimits = _status.IsPastLimits;
        IsExceeded = _status.State == CreditState.CapReached;
        Percent = _status.Percent ?? 0d;

        AmountText = _status.Used?.Format(_l.Culture) ?? _l["Common_Unknown"];
        AmountNumberText = _status.Used?.FormatNumber(_l.Culture) ?? _l["Common_Unknown"];
        CurrencySign = _status.Used is not null
            ? CreditStatus.Symbol(_status.Used.Currency ?? _status.Limit?.Currency)
            : string.Empty;

        StateText = _l[StateKey(_status.State)];
        DetailText = Describe(_l, _status);
    }

    /// <summary>The resource key that names a credit state. Shared with the tray tooltip.</summary>
    internal static string StateKey(CreditState state) => state switch
    {
        CreditState.InUse => "Credits_State_InUse",
        CreditState.CapReached => "Credits_State_CapReached",
        _ => "Credits_State_Standby",
    };

    /// <summary>
    /// "5,62 EUR of 100,00 EUR · 94,38 EUR left", or as much of it as was reported.
    /// Shared with the tray tooltip.
    /// </summary>
    internal static string Describe(ILocalizer localizer, CreditStatus status)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(status);

        string used = status.Used?.Format(localizer.Culture) ?? localizer["Common_Unknown"];
        if (status.Limit is not { } limit)
        {
            return localizer.Format("Credits_Spent_NoCap", used);
        }

        string spent = localizer.Format("Credits_Spent", used, limit.Format(localizer.Culture));
        return status.Remaining is { } left
            ? localizer.Format("Credits_SpentAndLeft", spent, left.Format(localizer.Culture))
            : spent;
    }
}
