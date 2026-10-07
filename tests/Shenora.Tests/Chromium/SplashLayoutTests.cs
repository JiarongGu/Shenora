using System.Drawing;
using Shenora.Chromium;
using Shenora.Chromium.Host;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The splash's layout: an element tree, a window size and a scale in, the draw operations every platform's painter
/// runs out. A fixed-width "font" (7 px a character at 13 px, lines 1.25 high) makes every position computable by hand.
/// </summary>
public class SplashLayoutTests
{
    private sealed class Mono : ISplashTextMeasurer
    {
        public SizeF Measure(string text, SplashFont font, float maxWidth)
        {
            var width = text.Length * 7f * font.Size / 13f;
            var lines = maxWidth > 0 ? Math.Max(1, (int)Math.Ceiling(width / maxWidth)) : 1;
            return new SizeF(maxWidth > 0 ? Math.Min(width, maxWidth) : width, lines * font.Size * 1.25f);
        }
    }

    private static SplashFrame Build(SplashElement root, int width = 400, int height = 300, float scale = 1, double phase = 0, Color? background = null) =>
        SplashLayout.Build(root, background ?? Color.Black, new Size(width, height), scale, new Mono(), phase, _ => new Size(100, 50));

    [Fact]
    public void The_first_op_fills_the_window_with_the_background()
    {
        var frame = Build(new SplashStack());
        Assert.Equal(new SplashFill(new RectangleF(0, 0, 400, 300), Color.Black), frame.Ops[0]);
        Assert.Equal(new Size(400, 300), frame.Size);
    }

    [Fact]
    public void A_stack_centres_its_children_as_a_group_and_spaces_them()
    {
        var frame = Build(new SplashStack { Spacing = 10, Children = [new SplashText("abcd"), new SplashText("ab")] });
        var runs = frame.Ops.OfType<SplashTextRun>().ToList();
        // 16.25 + 10 + 16.25 = 42.5 high, so the group starts at (300 - 42.5) / 2.
        Assert.Equal(new RectangleF(186, 128.75f, 28, 16.25f), runs[0].Bounds);
        Assert.Equal(new RectangleF(193, 155f, 14, 16.25f), runs[1].Bounds);
    }

    [Fact]
    public void A_horizontal_stack_lays_its_children_across()
    {
        var frame = Build(new SplashStack { Orientation = SplashOrientation.Horizontal, Spacing = 6, Children = [new SplashText("ab"), new SplashText("abcd")] });
        var runs = frame.Ops.OfType<SplashTextRun>().ToList();
        // 14 + 6 + 28 = 48 wide, from (400 - 48) / 2.
        Assert.Equal(176, runs[0].Bounds.X);
        Assert.Equal(196, runs[1].Bounds.X);
        Assert.Equal(runs[0].Bounds.Y, runs[1].Bounds.Y);
    }

    [Fact]
    public void Scale_multiplies_every_device_independent_value()
    {
        var frame = Build(new SplashText("ab"), width: 800, height: 600, scale: 2);
        var run = frame.Ops.OfType<SplashTextRun>().Single();
        Assert.Equal(26f, run.Font.Size);
        // "ab" is 14 wide at scale 1, so 28 here.
        Assert.Equal(new RectangleF(386, 283.75f, 28, 32.5f), run.Bounds);
    }

    [Fact]
    public void A_hidden_element_takes_no_space()
    {
        var frame = Build(new SplashStack { Spacing = 10, Children = [new SplashText("ab") { Visible = false }, new SplashText("ab")] });
        Assert.Equal(141.875f, Assert.Single(frame.Ops.OfType<SplashTextRun>()).Bounds.Y);
    }

    [Fact]
    public void A_layer_places_each_child_by_its_own_alignment()
    {
        var frame = Build(new SplashLayer
        {
            Children =
            [
                new SplashText("ab"),
                new SplashText("ab") { HorizontalAlign = SplashAlign.End, VerticalAlign = SplashAlign.End, Margin = 16 },
                new SplashText("ab") { HorizontalAlign = SplashAlign.Start, VerticalAlign = SplashAlign.Start, Margin = new SplashInsets(5, 7, 0, 0) },
            ],
        });
        var runs = frame.Ops.OfType<SplashTextRun>().ToList();
        Assert.Equal(new PointF(193, 141.875f), runs[0].Bounds.Location);
        Assert.Equal(new PointF(400 - 16 - 14, 300 - 16 - 16.25f), runs[1].Bounds.Location);
        Assert.Equal(new PointF(5, 7), runs[2].Bounds.Location);
    }

    [Fact]
    public void A_stretched_child_fills_its_container_across()
    {
        var frame = Build(new SplashStack
        {
            Padding = 20,
            Children = [new SplashProgress { HorizontalAlign = SplashAlign.Stretch, Width = null }],
        });
        var track = frame.Ops.OfType<SplashFill>().Skip(1).First();
        Assert.Equal(new RectangleF(20, 148, 360, 4), track.Bounds);
    }

    [Fact]
    public void A_container_background_is_drawn_behind_its_children_and_sets_their_text_colour()
    {
        var frame = Build(new SplashStack { Background = Color.White, Padding = 8, Children = [new SplashText("ab")] });
        var fills = frame.Ops.OfType<SplashFill>().ToList();
        Assert.Equal(Color.White, fills[1].Color);
        Assert.Equal(new RectangleF(0, 0, 400, 300), fills[1].Bounds);   // the root stack fills the window
        Assert.Equal(SplashLayout.Foreground(Color.White), frame.Ops.OfType<SplashTextRun>().Single().Color);
    }

    [Fact]
    public void An_image_keeps_its_aspect_from_one_given_side_and_its_own_size_from_none()
    {
        Assert.Equal(new SizeF(50, 25), Build(new SplashImage("x.png") { Width = 50 }).Ops.OfType<SplashImageDraw>().Single().Bounds.Size);
        Assert.Equal(new SizeF(60, 30), Build(new SplashImage("x.png") { Height = 30 }).Ops.OfType<SplashImageDraw>().Single().Bounds.Size);
        Assert.Equal(new SizeF(200, 100), Build(new SplashImage("x.png"), scale: 2).Ops.OfType<SplashImageDraw>().Single().Bounds.Size);
    }

    [Fact]
    public void A_determinate_bar_fills_its_value_and_does_not_animate()
    {
        var frame = Build(new SplashProgress { Value = 0.25, Width = 200 });
        var fills = frame.Ops.OfType<SplashFill>().Skip(1).ToList();
        Assert.Equal(new RectangleF(100, 148, 200, 4), fills[0].Bounds);   // track
        Assert.Equal(new RectangleF(100, 148, 50, 4), fills[1].Bounds);    // value
        Assert.False(frame.Animated);
    }

    [Fact]
    public void A_value_outside_zero_to_one_is_clamped()
    {
        var fills = Build(new SplashProgress { Value = 1.7, Width = 200 }).Ops.OfType<SplashFill>().ToList();
        Assert.Equal(200, fills[^1].Bounds.Width);
    }

    [Fact]
    public void An_indeterminate_bar_slides_inside_its_track_with_the_phase_and_animates()
    {
        RectangleF Segment(double phase) => Build(new SplashProgress { Width = 200 }, phase: phase).Ops.OfType<SplashFill>().Last().Bounds;
        Assert.Equal(100, Segment(0).X);
        Assert.Equal(100 + 200 - 60, Segment(0.5).X);
        Assert.Equal(60, Segment(0.25).Width);
        Assert.True(Build(new SplashProgress()).Animated);
    }

    [Fact]
    public void Text_defaults_to_a_colour_readable_on_its_background()
    {
        Assert.Equal(Color.FromArgb(0xF0, 0xF0, 0xF0), SplashLayout.Foreground(Color.Black));
        Assert.Equal(Color.FromArgb(0x20, 0x20, 0x20), SplashLayout.Foreground(Color.White));
        Assert.Equal(Color.FromArgb(0xF0, 0xF0, 0xF0), Build(new SplashText("a")).Ops.OfType<SplashTextRun>().Single().Color);
        Assert.Equal(Color.Red, Build(new SplashText("a") { Color = Color.Red }).Ops.OfType<SplashTextRun>().Single().Color);
    }

    [Fact]
    public void Translucent_text_is_blended_over_its_background_into_an_opaque_colour()
    {
        var run = Build(new SplashText("a") { Color = Color.FromArgb(128, 255, 255, 255) }).Ops.OfType<SplashTextRun>().Single();
        Assert.Equal(Color.FromArgb(255, 128, 128, 128), run.Color);
    }

    [Fact]
    public void A_long_text_wraps_inside_the_window()
    {
        var run = Build(new SplashText(new string('x', 100)), width: 300).Ops.OfType<SplashTextRun>().Single();
        Assert.Equal(300, run.Bounds.Width);
        Assert.Equal(3 * 16.25f, run.Bounds.Height);
    }

    [Fact]
    public void Negative_sizes_and_spacing_count_as_nothing()
    {
        var frame = Build(new SplashStack { Spacing = -50, Children = [new SplashText("ab"), new SplashProgress { Width = -5 }] });
        Assert.All(frame.Ops.OfType<SplashFill>(), f => Assert.True(f.Bounds.Width >= 0 && f.Bounds.Height >= 0));
    }

    [Fact]
    public void The_png_reader_takes_the_size_from_the_header()
    {
        byte[] header = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 1, 0, 0, 0, 0, 0x80];
        Assert.Equal(new Size(256, 128), SplashPng.ReadSize(header));
        Assert.Null(SplashPng.ReadSize(new byte[] { 1, 2, 3 }));
        Assert.Null(SplashPng.ReadSize(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png")));
    }
}
