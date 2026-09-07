using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using ClaudeStatus.App.Composition;
using ClaudeStatus.App.Tray;
using ClaudeStatus.Platform;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// Pixel-level checks on the rendered icon.
/// </summary>
/// <remarks>
/// These exist because two real bugs got past tests that only asserted a bitmap
/// came back non-null: the glyph was invisible on a light taskbar, and the ring
/// collapsed to a dot at 100 %. "Something was returned" is not a rendering test.
/// </remarks>
[Collection(HeadlessTests.Name)]
public class TrayIconContrastTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static UsageSnapshot At(double percent) => new(
        UsageWindow.Create(percent, Now),
        UsageWindow.Create(percent, Now),
        UsageWindow.Create(percent, Now),
        new Dictionary<string, UsageWindow>(),
        Now,
        false);

    /// <summary>Reads the bitmap back and reports what was actually drawn.</summary>
    private static (int Painted, double MeanLuminance) Inspect(RenderTargetBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        int count = size.Width * size.Height;
        byte[] buffer = new byte[count * 4];

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(size),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        int painted = 0;
        double luminance = 0;
        for (int i = 0; i < count; i++)
        {
            byte b = buffer[i * 4];
            byte g = buffer[(i * 4) + 1];
            byte r = buffer[(i * 4) + 2];
            byte a = buffer[(i * 4) + 3];

            if (a > 32)
            {
                painted++;
                luminance += (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
            }
        }

        return (painted, painted == 0 ? 0 : luminance / painted);
    }

    [Fact]
    public void The_glyph_is_light_on_a_dark_tray_and_dark_on_a_light_one()
    {
        // The original bug: near-white ink unconditionally, invisible on the
        // Windows light theme, which is the default on a fresh install.
        fixture.Should().NotBeNull();

        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap onDark = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);
            using RenderTargetBitmap onLight = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Light);

            double dark = Inspect(onDark).MeanLuminance;
            double light = Inspect(onLight).MeanLuminance;

            dark.Should().BeGreaterThan(light + 60, "the two must be clearly different inks");
            dark.Should().BeGreaterThan(120, "ink on a dark tray must be light");
            light.Should().BeLessThan(110, "ink on a light tray must be dark");
        });
    }

    [Fact]
    public void An_unknown_background_gets_a_halo_so_the_glyph_cannot_vanish()
    {
        // Linux, where there is no portable way to ask the panel its colour.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap plain = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);
            using RenderTargetBitmap haloed = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Unknown);

            Inspect(haloed).Painted.Should().BeGreaterThan(
                Inspect(plain).Painted,
                "the outline must add pixels around the glyph");
        });
    }

    [Fact]
    public void The_ring_at_one_hundred_percent_is_a_full_circle_not_a_dot()
    {
        // ArcTo from a point back to itself is degenerate and renders as a speck,
        // so the ring collapsed exactly when it mattered most.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap quarter = TrayIconRenderer.Render(
                At(25), IndicatorMode.Ring, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);
            using RenderTargetBitmap full = TrayIconRenderer.Render(
                At(100), IndicatorMode.Ring, ThresholdState.Exceeded,
                IndicatorAlert.None, TrayBackground.Dark);

            Inspect(full).Painted.Should().BeGreaterThan(
                Inspect(quarter).Painted,
                "a full ring must paint more than a quarter one, not collapse to a dot");
        });
    }

    /// <summary>
    /// Total alpha across the whole bitmap - how much ink is on the icon.
    /// </summary>
    /// <remarks>
    /// The right measure for a fade, and the mean luminance used elsewhere is the
    /// wrong one: fading scales alpha and leaves the hue alone, so a dimmed red
    /// glyph has exactly the same red in it, just less of it.
    /// </remarks>
    private static long InkMass(RenderTargetBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        long total = 0;
        for (int i = 3; i < buffer.Length; i += 4)
        {
            total += buffer[i];
        }

        return total;
    }

    private static UsageSnapshot Stale(double percent) => At(percent) with { IsStale = true };

    [Theory]
    [InlineData(TrayBackground.Dark)]
    [InlineData(TrayBackground.Light)]
    [InlineData(TrayBackground.Unknown)]
    public void A_stale_reading_is_drawn_faded(TrayBackground background)
    {
        // Everywhere else says "stale" in words. The icon is the thing people
        // actually glance at, and a three-hour-old 61 % used to look exactly like
        // a live one - which is the reading someone acts on without checking.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap fresh = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, background);
            using RenderTargetBitmap stale = TrayIconRenderer.Render(
                Stale(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, background);

            InkMass(stale).Should().BeLessThan(
                InkMass(fresh), $"a stale reading must look different on a {background} tray");
        });
    }

    [Fact]
    public void A_stale_reading_is_still_clearly_legible()
    {
        // Faded, not erased. The number is old, not unavailable, and it is still
        // the best information there is.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap fresh = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);
            using RenderTargetBitmap stale = TrayIconRenderer.Render(
                Stale(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);

            InkMass(stale).Should().BeGreaterThan(
                (long)(InkMass(fresh) * 0.35), "a fade that faint reads as a fault");
        });
    }

    [Fact]
    public void A_stale_over_threshold_reading_keeps_its_alert_colour()
    {
        // Recolouring to grey would throw away the one thing worth keeping: a
        // stale 95 % is still the best evidence that the user is near the limit.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap stale = TrayIconRenderer.Render(
                Stale(95), IndicatorMode.SessionPercent, ThresholdState.Exceeded,
                IndicatorAlert.None, TrayBackground.Dark);
            using RenderTargetBitmap normal = TrayIconRenderer.Render(
                Stale(61), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);

            Inspect(stale).MeanLuminance.Should().NotBe(
                Inspect(normal).MeanLuminance, "the alert ink must survive the fade");
        });
    }

    [Fact]
    public void The_ring_fades_when_stale_too()
    {
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap fresh = TrayIconRenderer.Render(
                At(61), IndicatorMode.Ring, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);
            using RenderTargetBitmap stale = TrayIconRenderer.Render(
                Stale(61), IndicatorMode.Ring, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);

            InkMass(stale).Should().BeLessThan(InkMass(fresh));
        });
    }

    [Fact]
    public void A_missing_credential_is_never_faded_even_on_a_stale_reading()
    {
        // The "!" is actionable and must not be softened. Only readings fade.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap fresh = TrayIconRenderer.Render(
                At(61), IndicatorMode.SessionPercent, ThresholdState.Unknown,
                IndicatorAlert.NeedsCredential, TrayBackground.Dark);
            using RenderTargetBitmap stale = TrayIconRenderer.Render(
                Stale(61), IndicatorMode.SessionPercent, ThresholdState.Unknown,
                IndicatorAlert.NeedsCredential, TrayBackground.Dark);

            InkMass(stale).Should().Be(InkMass(fresh));
        });
    }

    private static UsageSnapshot Mixed(double session, double week, double fable) => new(
        UsageWindow.Create(session, Now),
        UsageWindow.Create(week, Now),
        UsageWindow.Create(fable, Now),
        new Dictionary<string, UsageWindow>(),
        Now,
        false);

    [Theory]
    [InlineData(7d)]
    [InlineData(61d)]
    [InlineData(100d)]
    public void The_number_fills_the_space_it_is_given(double percent)
    {
        // The complaint that produced this: the icon looked undersized beside the
        // system's own. It was being fitted to FormattedText.Height - the whole
        // typographic line, ascender to descender - while digits occupy only the
        // cap height in the middle of it, so about a third of the icon was
        // invisible padding. Fitting to the ink means the digits fill the box.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap bitmap = TrayIconRenderer.Render(
                Mixed(percent, 43, 88), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);

            (int left, int right, int top, int bottom) = InkBounds(bitmap)!.Value;

            double glyphBox = TrayIconRenderer.GlyphBoxHeight;
            int glyphHeight = bottom - top;

            glyphHeight.Should().BeGreaterThan(
                (int)(glyphBox * 0.5),
                $"'{percent}' should fill much of the {glyphBox} px it is given");

            // Deliberately no assertion that the ink clears the left and right
            // edges. It used to, and that was a margin - the very thing removed so
            // the number could grow. Digits running to the edge is now correct.
            (right - left).Should().BeGreaterThan(
                (int)(bitmap.PixelSize.Width * 0.5), "and most of the width it is given");
        });
    }

    [Theory]
    [InlineData(29d)]
    [InlineData(61d)]
    [InlineData(88d)]
    public void A_two_digit_reading_fills_the_height_it_is_given(double percent)
    {
        // The uniform fit takes the smaller of the width and height ratios, and for
        // two digits the tighter axis is width. That left two-digit numbers well
        // short of the height they had, with a band of empty pixels above and
        // below. The y axis is now scaled separately to spend it.
        //
        // Asserted at 0.9 rather than 1.0 to leave the fit its rounding slack.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap bitmap = TrayIconRenderer.Render(
                Mixed(percent, 43, 51), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);

            (int _, int _, int top, int bottom) = InkBounds(bitmap)!.Value;

            double glyphBox = TrayIconRenderer.GlyphBoxHeight;
            int glyphHeight = bottom - top;

            glyphHeight.Should().BeGreaterThan(
                (int)(glyphBox * 0.9),
                $"'{percent}' must use nearly all of the {glyphBox} px it is given");
        });
    }

    /// <summary>The bounding box of the painted pixels, or null when nothing was drawn.</summary>
    private static (int Left, int Right, int Top, int Bottom)? InkBounds(RenderTargetBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1;
        for (int y = 0; y < size.Height; y++)
        {
            for (int x = 0; x < size.Width; x++)
            {
                if (buffer[(((y * size.Width) + x) * 4) + 3] <= 32)
                {
                    continue;
                }

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        return right < 0 ? null : (left, right, top, bottom);
    }

    [Fact]
    public void A_three_digit_reading_is_still_drawn_as_large_as_it_can_be()
    {
        // The fit shrinks "100" only as far as it must. If it ever collapses to a
        // token size the icon stops being readable at 16 px, which no assertion
        // about "something was painted" would catch.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap full = TrayIconRenderer.Render(
                At(100), IndicatorMode.SessionPercent, ThresholdState.Normal,
                IndicatorAlert.None, TrayBackground.Dark);

            (int left, int right, int top, int bottom) = InkBounds(full)!.Value;
            int box = full.PixelSize.Width;

            (right - left).Should().BeGreaterThan(
                (int)(box * 0.75), "three digits should use most of the width");
            (bottom - top).Should().BeGreaterThan(
                (int)(box * 0.3), "and stay tall enough to read when downscaled");
        });
    }

    [Theory]
    [InlineData(TrayBackground.Dark)]
    [InlineData(TrayBackground.Light)]
    [InlineData(TrayBackground.Unknown)]
    public void Every_state_paints_something_on_every_background(TrayBackground background)
    {
        // The floor: an icon that paints nothing is an invisible icon.
        HeadlessAppFixture.Invoke(() =>
        {
            (string Name, UsageSnapshot? Snap, IndicatorMode Mode, ThresholdState State, IndicatorAlert Alert)[] cases =
            [
                ("normal", At(61), IndicatorMode.SessionPercent, ThresholdState.Normal, IndicatorAlert.None),
                ("exceeded", At(95), IndicatorMode.SessionPercent, ThresholdState.Exceeded, IndicatorAlert.None),
                ("no data", null, IndicatorMode.SessionPercent, ThresholdState.Unknown, IndicatorAlert.None),
                ("ring", At(61), IndicatorMode.Ring, ThresholdState.Normal, IndicatorAlert.None),
                ("full ring", At(100), IndicatorMode.Ring, ThresholdState.Exceeded, IndicatorAlert.None),
                ("alert", null, IndicatorMode.SessionPercent, ThresholdState.Unknown, IndicatorAlert.NeedsCredential),
            ];

            foreach ((string name, UsageSnapshot? snap, IndicatorMode mode, ThresholdState state, IndicatorAlert alert)
                in cases)
            {
                using RenderTargetBitmap bitmap = TrayIconRenderer.Render(snap, mode, state, alert, background);
                Inspect(bitmap).Painted.Should().BeGreaterThan(
                    60, $"'{name}' on a {background} tray must be visible");
            }
        });
    }
}

/// <summary>The tray theme providers.</summary>
public class TrayThemeProviderTests
{
    [Fact]
    public void A_static_provider_reports_what_it_was_given()
    {
        new StaticTrayThemeProvider(TrayBackground.Light).Current.Should().Be(TrayBackground.Light);
        new StaticTrayThemeProvider(TrayBackground.Unknown).Current.Should().Be(TrayBackground.Unknown);
    }

    [Fact]
    public void The_composition_root_picks_a_provider_that_never_throws()
    {
        ITrayThemeProvider provider = PlatformServices.CreateTrayThemeProvider();

        provider.Should().NotBeNull();
        Func<TrayBackground> act = () => provider.Current;
        act.Should().NotThrow("it is read on every render and must never take the app down");
    }

    [Fact]
    public void On_Windows_the_taskbar_theme_is_actually_readable()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows only.");
        }

        PlatformServices.CreateTrayThemeProvider().Current
            .Should().BeOneOf(TrayBackground.Light, TrayBackground.Dark);
    }
}

/// <summary>
/// The two states that get a shape instead of a number.
/// </summary>
/// <remarks>
/// Both must be told apart from each other and from the credential warning by
/// <b>silhouette</b>, not colour: macOS renders a template icon as a monochrome
/// mask, so a red cross and a grey circle are the same picture there.
/// </remarks>
[Collection(HeadlessTests.Name)]
public class TrayIconSymbolTests(HeadlessAppFixture fixture)
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static UsageSnapshot At(double session) => new(
        UsageWindow.Create(session, Now),
        UsageWindow.Create(43, Now),
        UsageWindow.Create(88, Now),
        new Dictionary<string, UsageWindow>(),
        Now,
        false);

    private static RenderTargetBitmap Render(
        UsageSnapshot? snapshot, IndicatorAlert alert = IndicatorAlert.None) =>
        TrayIconRenderer.Render(
            snapshot,
            IndicatorMode.SessionPercent,
            snapshot is null ? ThresholdState.Unknown : ThresholdState.Exceeded,
            alert,
            TrayBackground.Dark);

    /// <summary>A coarse 8x8 signature of where the ink is, for comparing shapes.</summary>
    private static string Signature(RenderTargetBitmap bitmap)
    {
        PixelSize size = bitmap.PixelSize;
        byte[] buffer = new byte[size.Width * size.Height * 4];

        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, size.Width * 4);
        }
        finally
        {
            handle.Free();
        }

        var cells = new char[64];
        for (int cell = 0; cell < 64; cell++)
        {
            int cx = (cell % 8) * (size.Width / 8);
            int cy = (cell / 8) * (size.Height / 8);
            int painted = 0;

            for (int y = cy; y < cy + (size.Height / 8); y++)
            {
                for (int x = cx; x < cx + (size.Width / 8); x++)
                {
                    if (buffer[(((y * size.Width) + x) * 4) + 3] > 32)
                    {
                        painted++;
                    }
                }
            }

            cells[cell] = painted > (size.Width / 8) * (size.Height / 8) / 4 ? '#' : '.';
        }

        return new string(cells);
    }

    [Fact]
    public void A_window_with_nothing_left_shows_a_shape_rather_than_the_number()
    {
        // "100" and "99" differ by one glyph and mean quite different things.
        fixture.Should().NotBeNull();

        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap nearly = Render(At(99));
            using RenderTargetBitmap exhausted = Render(At(100));

            Signature(exhausted).Should().NotBe(
                Signature(nearly), "the exhausted state must not just be another number");
        });
    }

    [Theory]
    [InlineData(99.94d, false)]
    [InlineData(99.95d, true)]
    [InlineData(100d, true)]
    [InlineData(150d, true)]
    public void Exhaustion_uses_the_same_rounding_as_the_displayed_number(
        double percent, bool expected)
    {
        // Otherwise the icon could show a cross while the tooltip still said 99 %.
        TrayIconRenderer.IsExhausted(percent).Should().Be(expected);
        if (expected)
        {
            TrayIconRenderer.FormatPercent(percent).Should().Be("100");
        }
    }

    [Fact]
    public void Never_having_connected_shows_a_shape_rather_than_a_dash()
    {
        // A dash is indistinguishable from "still loading", which is what this
        // looked like for as long as the endpoint stayed unreachable.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap waiting = Render(null);
            using RenderTargetBitmap offline = Render(null, IndicatorAlert.Unreachable);

            Signature(offline).Should().NotBe(
                Signature(waiting), "never-connected must not look like not-yet-connected");
        });
    }

    [Fact]
    public void A_reading_that_exists_is_never_replaced_by_the_offline_shape()
    {
        // Unreachable with a cached reading keeps showing the reading, faded. The
        // shape is only for having nothing at all.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap offline = Render(null, IndicatorAlert.Unreachable);
            using RenderTargetBitmap cached = Render(
                At(61) with { IsStale = true }, IndicatorAlert.Unreachable);

            Signature(cached).Should().NotBe(Signature(offline));
        });
    }

    [Fact]
    public void All_three_symbols_are_different_silhouettes()
    {
        // Colour cannot carry this: macOS draws a template icon as a mask.
        HeadlessAppFixture.Invoke(() =>
        {
            using RenderTargetBitmap exhausted = Render(At(100));
            using RenderTargetBitmap offline = Render(null, IndicatorAlert.Unreachable);
            using RenderTargetBitmap credential = Render(null, IndicatorAlert.NeedsCredential);

            string[] shapes =
            [
                Signature(exhausted), Signature(offline), Signature(credential),
            ];

            shapes.Should().OnlyHaveUniqueItems("each state has to be recognisable on its own");
        });
    }
}
