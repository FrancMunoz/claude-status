using System.Globalization;

namespace ClaudeStatus.Theming;

/// <summary>
/// An opaque colour, as a theme file spells it.
/// </summary>
/// <remarks>
/// Core must not reference Avalonia (<c>docs/manual.md</c> §6), so themes are defined
/// in terms of this rather than <c>Avalonia.Media.Color</c>. The app converts at
/// the boundary. Alpha is deliberately absent: the only translucency in the app
/// is the OSD background opacity, which is a separate setting the user controls,
/// not something a theme author should be able to bake into a text colour.
/// </remarks>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Parses <c>#RRGGBB</c> or <c>#RGB</c>, with or without the hash.</summary>
    public static bool TryParse(string? hex, out Rgb value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        ReadOnlySpan<char> digits = hex.AsSpan().Trim();
        if (digits.Length > 0 && digits[0] == '#')
        {
            digits = digits[1..];
        }

        // #RGB is expanded the CSS way: #f0a means #ff00aa, not #0f0a0a.
        if (digits.Length == 3)
        {
            return TryNibble(digits[0], out byte r)
                && TryNibble(digits[1], out byte g)
                && TryNibble(digits[2], out byte b)
                && Assign((byte)((r * 16) + r), (byte)((g * 16) + g), (byte)((b * 16) + b), out value);
        }

        if (digits.Length != 6)
        {
            return false;
        }

        return TryByte(digits[..2], out byte red)
            && TryByte(digits[2..4], out byte green)
            && TryByte(digits[4..], out byte blue)
            && Assign(red, green, blue, out value);

        static bool Assign(byte r, byte g, byte b, out Rgb result)
        {
            result = new Rgb(r, g, b);
            return true;
        }

        static bool TryNibble(char c, out byte value)
        {
            bool ok = byte.TryParse(
                [c], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            return ok;
        }

        static bool TryByte(ReadOnlySpan<char> pair, out byte value)
            => byte.TryParse(pair, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Parses, or returns <paramref name="fallback"/> for anything unreadable.</summary>
    /// <remarks>
    /// Theme files are user-supplied. A typo in one colour must cost that colour,
    /// not the whole theme and certainly not the launch.
    /// </remarks>
    public static Rgb ParseOr(string? hex, Rgb fallback)
        => TryParse(hex, out Rgb value) ? value : fallback;

    /// <summary><c>#RRGGBB</c>.</summary>
    public string ToHex()
        => string.Create(
            CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}");

    /// <summary>
    /// Perceived brightness, 0 (black) to 1 (white), per WCAG.
    /// </summary>
    /// <remarks>
    /// Used to decide whether a theme is light or dark when its file does not say,
    /// and to check that derived greys stay readable. The sRGB gamma step matters:
    /// a naive average calls mid-blue and mid-yellow equally bright, and they are
    /// nowhere near.
    /// </remarks>
    public double RelativeLuminance
    {
        get
        {
            return (0.2126 * Channel(R)) + (0.7152 * Channel(G)) + (0.0722 * Channel(B));

            static double Channel(byte raw)
            {
                double value = raw / 255d;
                return value <= 0.03928d
                    ? value / 12.92d
                    : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
            }
        }
    }

    /// <summary>True when text on this colour should be light.</summary>
    public bool IsDark => RelativeLuminance < 0.5d;

    /// <summary>
    /// Mixes toward another colour. <paramref name="amount"/> 0 keeps this one, 1 gives the other.
    /// </summary>
    /// <remarks>
    /// This is how the muted caption colour is produced: the theme's text colour
    /// blended toward its background. Deriving it rather than asking for a sixth
    /// colour means a theme author cannot pick a grey that is invisible on their
    /// own background, which is the mistake this would otherwise invite.
    /// </remarks>
    public Rgb Blend(Rgb other, double amount)
    {
        double t = Math.Clamp(amount, 0d, 1d);
        return new Rgb(
            Mix(R, other.R, t),
            Mix(G, other.G, t),
            Mix(B, other.B, t));

        static byte Mix(byte from, byte to, double t)
            => (byte)Math.Clamp(Math.Round(from + ((to - from) * t)), 0d, 255d);
    }

    /// <summary>
    /// Contrast ratio against another colour, 1 (identical) to 21 (black on white).
    /// </summary>
    /// <remarks>
    /// WCAG asks for 4.5 for body text and 3 for large text. Used by the tests to
    /// prove every built-in theme is legible rather than merely pretty.
    /// </remarks>
    public double ContrastWith(Rgb other)
    {
        double a = RelativeLuminance;
        double b = other.RelativeLuminance;
        (double lighter, double darker) = a > b ? (a, b) : (b, a);
        return (lighter + 0.05d) / (darker + 0.05d);
    }
}
