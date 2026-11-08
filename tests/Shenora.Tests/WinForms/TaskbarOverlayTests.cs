using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The overlay's behaviour over a real (invisible) form on an STA thread, with the shell call replaced by a recorder: a unit
/// test has no taskbar button to put an icon on, and must not touch the one of the machine that runs it. The drawing is read
/// back from the icon it makes.
/// </summary>
public class TaskbarOverlayTests
{
    private sealed record Call(IntPtr Window, IntPtr Icon, string? Description);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    private static (Form Form, TaskbarOverlay Overlay, List<Call> Calls) Wired()
    {
        var form = new Form { ShowInTaskbar = false };
        var calls = new List<Call>();
        var overlay = new TaskbarOverlay(form) { Surface = (window, icon, description) => { calls.Add(new Call(window, icon, description)); return 0; } };
        return (form, overlay, calls);
    }

    [Theory]
    [InlineData(-3, null)]
    [InlineData(0, null)]
    [InlineData(1, "1")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(5000, "99+")]
    public void A_count_is_drawn_as_its_number_and_capped_at_ninety_nine(int count, string? label)
    {
        Assert.Equal(label, TaskbarOverlay.Label(count));
    }

    [Theory]
    [InlineData(96, 16)]
    [InlineData(48, 16)]
    [InlineData(144, 24)]
    [InlineData(192, 32)]
    public void The_icon_is_sixteen_pixels_at_96_dpi_and_scales_with_it_never_below(int dpi, int side)
    {
        Assert.Equal(side, TaskbarOverlay.Side(dpi));
    }

    [Fact]
    public void The_icon_is_a_circle_of_the_fill_with_the_text_in_the_ink_and_nothing_outside_it() => Sta.Run(() =>
    {
        var fill = Color.FromArgb(0xC4, 0x2B, 0x1C);
        var ink = Color.FromArgb(0x10, 0xF0, 0x20);
        var handle = TaskbarOverlay.Draw("8", 32, fill, ink);
        try
        {
            using var icon = Icon.FromHandle(handle);
            using var bitmap = icon.ToBitmap();

            Assert.Equal(32, bitmap.Width);
            // The corner is outside the circle: transparent, so the button shows through.
            Assert.Equal(0, bitmap.GetPixel(0, 0).A);
            // The ring's edge, left of the digit, is the fill.
            var edge = bitmap.GetPixel(3, 16);
            Assert.Equal(255, edge.A);
            Assert.Equal(fill.R, edge.R);
            Assert.Equal(fill.G, edge.G);

            // The ink is on the canvas, and the digit sits about the middle: its pixels' centre of mass is, not the font's line box.
            long x = 0, y = 0, n = 0;
            for (var py = 0; py < 32; py++)
                for (var px = 0; px < 32; px++)
                {
                    var c = bitmap.GetPixel(px, py);
                    if (c.A == 255 && Math.Abs(c.G - ink.G) < 40 && Math.Abs(c.R - ink.R) < 40 && Math.Abs(c.B - ink.B) < 40) { x += px; y += py; n++; }
                }

            Assert.True(n > 20, "the text was not drawn in the ink");
            Assert.InRange((double)x / n, 12, 20);
            Assert.InRange((double)y / n, 12, 20);
        }
        finally
        {
            DestroyIcon(handle);
        }
    });

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [Fact]
    public void An_overlay_shown_before_the_window_has_a_handle_lands_when_the_handle_is_made() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        using (overlay)
        {
            overlay.Show(3, "3 waiting");
            Assert.Empty(calls);

            _ = form.Handle;

            var call = Assert.Single(calls);
            Assert.Equal(form.Handle, call.Window);
            Assert.NotEqual(IntPtr.Zero, call.Icon);
            Assert.Equal("3 waiting", call.Description);
        }
    });

    [Fact]
    public void The_description_defaults_to_the_text_and_a_clear_sends_no_icon_and_no_words() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        using (overlay)
        {
            _ = form.Handle;
            overlay.Show("!");
            overlay.Clear();

            Assert.Equal(2, calls.Count);
            Assert.Equal("!", calls[0].Description);
            Assert.Equal(IntPtr.Zero, calls[1].Icon);
            Assert.Null(calls[1].Description);
        }
    });

    [Fact]
    public void A_count_of_none_clears_the_overlay() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        using (overlay)
        {
            _ = form.Handle;
            overlay.Show(2);
            overlay.Show(0);

            Assert.NotEqual(IntPtr.Zero, calls[0].Icon);
            Assert.Equal(IntPtr.Zero, calls[^1].Icon);
        }
    });

    // Explorer makes the button again when it restarts, and says so with a message sent to the window: the overlay it had is gone.
    [Fact]
    public void The_overlay_is_put_back_when_the_shell_says_the_button_was_made() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        using (overlay)
        {
            _ = form.Handle;
            overlay.Show(7);
            Assert.Single(calls);

            SendMessage(form.Handle, TaskbarOverlay.ButtonCreatedMessage, IntPtr.Zero, IntPtr.Zero);

            Assert.Equal(2, calls.Count);
            Assert.NotEqual(IntPtr.Zero, calls[1].Icon);
        }
    });

    [Fact]
    public void The_overlay_follows_a_recreated_handle() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        using (overlay)
        {
            _ = form.Handle;
            overlay.Show(4);
            var first = form.Handle;

            // A style change that WinForms answers by making the window again: the new window has no overlay.
            form.ShowInTaskbar = true;
            _ = form.Handle;

            Assert.NotEqual(first, form.Handle);
            Assert.Equal(form.Handle, calls[^1].Window);
            Assert.NotEqual(IntPtr.Zero, calls[^1].Icon);

            // And the new window is listened on too.
            var before = calls.Count;
            SendMessage(form.Handle, TaskbarOverlay.ButtonCreatedMessage, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(before + 1, calls.Count);
        }
    });

    [Fact]
    public void A_colour_change_redraws_a_shown_overlay_and_a_hidden_one_stays_hidden() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        using (overlay)
        {
            _ = form.Handle;
            overlay.Fill = Color.Blue;
            Assert.Empty(calls);

            overlay.Show(1);
            overlay.Ink = Color.Black;
            Assert.Equal(2, calls.Count);
        }
    });

    [Fact]
    public void Disposing_takes_the_icon_off_the_button_and_stops_listening() => Sta.Run(() =>
    {
        var (form, overlay, calls) = Wired();
        using (form)
        {
            _ = form.Handle;
            overlay.Show(1);
            overlay.Dispose();

            Assert.Equal(IntPtr.Zero, calls[^1].Icon);
            var before = calls.Count;
            SendMessage(form.Handle, TaskbarOverlay.ButtonCreatedMessage, IntPtr.Zero, IntPtr.Zero);
            overlay.Show(9);
            Assert.Equal(before, calls.Count);
        }
    });

    [Fact]
    public void A_failing_shell_call_leaves_the_window_working_and_frees_the_icon() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        using var overlay = new TaskbarOverlay(form)
        {
            Surface = (_, _, _) => throw new COMException("no taskbar", unchecked((int)0x80004005)),
        };
        _ = form.Handle;

        overlay.Show(1);
        overlay.Clear();

        Assert.False(form.IsDisposed);
    });

    // The one real call: whether this machine has a taskbar button yet or not, asking is never an error for the window.
    [Fact]
    public void The_real_shell_is_asked_without_throwing() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        using var overlay = new TaskbarOverlay(form);
        _ = form.Handle;

        overlay.Show(1, "one");
        overlay.Clear();
    });
}
