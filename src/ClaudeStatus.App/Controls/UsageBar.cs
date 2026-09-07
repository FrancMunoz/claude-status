using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ClaudeStatus.App.Controls;

/// <summary>
/// A thin, fully rounded usage bar.
/// </summary>
/// <remarks>
/// <para>
/// Drawn rather than templated. Avalonia's <see cref="ProgressBar"/> computes its
/// indicator's width in code against named template parts, so restyling it into
/// something this minimal means reproducing a contract that is not part of its
/// public API - and getting that subtly wrong shows up as a bar that never moves.
/// Forty lines of <see cref="Render"/> owe nothing to that contract.
/// </para>
/// <para>
/// It also buys two details a templated <see cref="ProgressBar"/> cannot easily
/// give: caps that stay perfectly semicircular at any height, and a floor on the
/// fill width so that 1 % is a visible dot rather than a hairline that reads as
/// zero.
/// </para>
/// </remarks>
public sealed class UsageBar : Control
{
    /// <summary>How full the bar is, 0-100.</summary>
    public static readonly StyledProperty<double> PercentProperty =
        AvaloniaProperty.Register<UsageBar, double>(nameof(Percent));

    /// <summary>The filled portion.</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(Fill));

    /// <summary>The groove behind it.</summary>
    public static readonly StyledProperty<IBrush?> TrackProperty =
        AvaloniaProperty.Register<UsageBar, IBrush?>(nameof(Track));

    static UsageBar()
    {
        AffectsRender<UsageBar>(PercentProperty, FillProperty, TrackProperty);
        AffectsMeasure<UsageBar>(HeightProperty);
    }

    /// <inheritdoc cref="PercentProperty" />
    public double Percent
    {
        get => GetValue(PercentProperty);
        set => SetValue(PercentProperty, value);
    }

    /// <inheritdoc cref="FillProperty" />
    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <inheritdoc cref="TrackProperty" />
    public IBrush? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Rect bounds = new(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        double radius = bounds.Height / 2;

        if (Track is { } track)
        {
            context.DrawRectangle(track, null, new RoundedRect(bounds, radius));
        }

        if (Fill is not { } fill)
        {
            return;
        }

        double fraction = Math.Clamp(double.IsFinite(Percent) ? Percent : 0d, 0d, 100d) / 100d;
        if (fraction <= 0d)
        {
            return;
        }

        // Never narrower than it is tall. Below that the rounded caps overlap and
        // the fill degenerates into a lens shape thinner than the track, so a real
        // reading of 1 % ends up looking like no reading at all.
        double width = Math.Max(bounds.Width * fraction, bounds.Height);

        context.DrawRectangle(
            fill, null, new RoundedRect(bounds.WithWidth(Math.Min(width, bounds.Width)), radius));
    }
}
