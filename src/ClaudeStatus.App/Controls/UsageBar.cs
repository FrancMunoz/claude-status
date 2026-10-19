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

    /// <summary>
    /// Optional split of the fill, as relative weights, biggest first.
    /// </summary>
    /// <remarks>
    /// The fill keeps its width; the weights only cut it, so the bar still reads
    /// as the window's percentage first. Segment <c>i</c> is drawn at
    /// <see cref="SegmentShades"/>[i] of the fill's strength - the same ramp the
    /// iCUE widget uses - with a pixel of track between neighbours. Null, or a
    /// single weight, draws the plain fill.
    /// </remarks>
    public static readonly StyledProperty<IReadOnlyList<double>?> SegmentsProperty =
        AvaloniaProperty.Register<UsageBar, IReadOnlyList<double>?>(nameof(Segments));

    /// <summary>How strong each segment is, biggest share first. Also the legend's swatches.</summary>
    public static IReadOnlyList<double> SegmentShades { get; } = [1d, 0.62, 0.38, 0.22];

    static UsageBar()
    {
        AffectsRender<UsageBar>(PercentProperty, FillProperty, TrackProperty, SegmentsProperty);
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

    /// <inheritdoc cref="SegmentsProperty" />
    public IReadOnlyList<double>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
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

        RoundedRect shape = new(bounds.WithWidth(Math.Min(width, bounds.Width)), radius);
        if (Segments is { Count: > 1 } segments && segments.Sum(weight => Math.Max(weight, 0d)) is > 0d and var total)
        {
            DrawSegments(context, fill, shape, segments, total);
            return;
        }

        context.DrawRectangle(fill, null, shape);
    }

    /// <summary>
    /// Cuts the fill into segments inside its own rounded outline, so only the two
    /// outer ends are rounded and the cuts are square.
    /// </summary>
    private static void DrawSegments(
        DrawingContext context, IBrush fill, RoundedRect shape, IReadOnlyList<double> segments, double total)
    {
        const double Gap = 1d;
        Rect area = shape.Rect;
        int count = Math.Min(segments.Count, SegmentShades.Count);
        double x = area.X;

        using (context.PushClip(shape))
        {
            for (int index = 0; index < count; index++)
            {
                double share = area.Width * Math.Max(segments[index], 0d) / total;
                double drawn = index < count - 1 ? share - Gap : area.Right - x;
                if (drawn > 0d)
                {
                    using (context.PushOpacity(SegmentShades[index]))
                    {
                        context.DrawRectangle(fill, null, new Rect(x, area.Y, drawn, area.Height));
                    }
                }

                x += share;
            }
        }
    }
}
