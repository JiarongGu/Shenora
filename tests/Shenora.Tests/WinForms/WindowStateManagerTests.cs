using System.Drawing;
using Shenora.Tests.TestSupport;
using Shenora.WinForms;

namespace Shenora.Tests.WinForms;

public class WindowStateManagerTests
{
    private static readonly WindowStateOptions Options = new();

    // ---- ToPhysical ----

    [Fact]
    public void ToPhysical_is_identity_at_100_percent()
    {
        var (w, h, x, y, max) = WindowStateManager.ToPhysical(
            new WindowState(1000, 700, 30, 40, false), 1.0, Options);
        Assert.Equal((1000, 700), (w, h));
        Assert.Equal((30, 40), (x!.Value, y!.Value));
        Assert.False(max);
    }

    [Theory]
    [InlineData(1.5, 1500, 1050, 45, 60)]
    [InlineData(2.0, 2000, 1400, 60, 80)]
    public void ToPhysical_scales_size_and_position(double scale, int w, int h, int x, int y)
    {
        var result = WindowStateManager.ToPhysical(new WindowState(1000, 700, 30, 40, false), scale, Options);
        Assert.Equal((w, h, x, y), (result.Width, result.Height, result.X!.Value, result.Y!.Value));
    }

    [Fact]
    public void ToPhysical_falls_back_to_defaults_and_clamps_minimum_in_logical_space()
    {
        var defaults = WindowStateManager.ToPhysical(null, 2.0, Options);
        Assert.Equal((Options.DefaultWidth * 2, Options.DefaultHeight * 2), (defaults.Width, defaults.Height));
        Assert.Null(defaults.X);

        var clamped = WindowStateManager.ToPhysical(new WindowState(100, 100, null, null, false), 1.5, Options);
        Assert.Equal(((int)(Options.MinWidth * 1.5), (int)(Options.MinHeight * 1.5)), (clamped.Width, clamped.Height));
    }

    [Theory]
    [InlineData(30, null)]
    [InlineData(null, 40)]
    public void ToPhysical_position_is_both_or_neither(int? x, int? y)
    {
        var result = WindowStateManager.ToPhysical(new WindowState(1000, 700, x, y, false), 1.0, Options);
        Assert.Null(result.X);
        Assert.Null(result.Y);
    }

    [Fact]
    public void ToPhysical_survives_a_non_positive_scale()
    {
        var result = WindowStateManager.ToPhysical(new WindowState(1000, 700, 30, 40, false), 0, Options);
        Assert.Equal(1000, result.Width); // identity fallback, never zero/negative geometry
    }

    // ---- ToPhysical work-area clamp (finding: a size saved on a big display would restore too large) ----

    private static readonly Rectangle[] SmallWorkArea = [new(0, 0, 1366, 728)];

    [Fact]
    public void ToPhysical_with_workAreas_shrinks_a_saved_size_that_would_overflow_the_target()
    {
        // Saved 2400x1500 (a big external monitor) restoring onto a 1366x728 laptop panel.
        var result = WindowStateManager.ToPhysical(
            new WindowState(2400, 1500, 100, 100, false), 1.0, Options, SmallWorkArea);
        Assert.Equal((1366, 728), (result.Width, result.Height));
        // Position is not clamped — IsVisible + the caller's centre fallback own that concern.
        Assert.Equal((100, 100), (result.X!.Value, result.Y!.Value));
    }

    [Fact]
    public void ToPhysical_with_workAreas_is_a_no_op_when_the_saved_size_already_fits()
    {
        var result = WindowStateManager.ToPhysical(
            new WindowState(1200, 700, 30, 40, false), 1.0, Options, SmallWorkArea);
        Assert.Equal((1200, 700), (result.Width, result.Height));
    }

    [Fact]
    public void ToPhysical_with_workAreas_respects_the_MinWidth_MinHeight_floor()
    {
        // Even on a tiny target area, the physical size never drops below the DPI-scaled minimum
        // — a window smaller than its own minimum is useless.
        Rectangle[] tiny = [new(0, 0, 100, 100)];
        var result = WindowStateManager.ToPhysical(
            new WindowState(1600, 1000, 0, 0, false), 1.0, Options, tiny);
        Assert.Equal((Options.MinWidth, Options.MinHeight), (result.Width, result.Height));
    }

    [Fact]
    public void ToPhysical_with_workAreas_picks_the_target_containing_the_saved_position()
    {
        // Two monitors side by side. The saved position sits on the second one; the clamp must
        // use ITS work area, not the primary's — which is what "target monitor" means.
        Rectangle[] two = [new(0, 0, 3840, 2160), new(3840, 0, 1366, 728)];
        var result = WindowStateManager.ToPhysical(
            new WindowState(2000, 1000, 3900, 100, false), 1.0, Options, two);
        Assert.Equal((1366, 728), (result.Width, result.Height));
    }

    [Fact]
    public void ToPhysical_with_workAreas_does_not_clamp_when_MaxToWorkArea_is_off()
    {
        var offOptions = new WindowStateOptions { MaxToWorkArea = false };
        var result = WindowStateManager.ToPhysical(
            new WindowState(2400, 1500, 100, 100, false), 1.0, offOptions, SmallWorkArea);
        Assert.Equal((2400, 1500), (result.Width, result.Height));
    }

    [Fact]
    public void ToPhysical_with_workAreas_falls_back_to_the_first_area_when_no_position_is_saved()
    {
        // No saved position → target defaults to the first (primary) work area, so a saved size
        // that overflows the primary still shrinks even before the window has a position.
        var result = WindowStateManager.ToPhysical(
            new WindowState(2400, 1500, null, null, false), 1.0, Options, SmallWorkArea);
        Assert.Equal((1366, 728), (result.Width, result.Height));
    }

    // ---- ToLogical ----

    [Fact]
    public void ToLogical_roundtrips_with_ToPhysical()
    {
        var logical = WindowStateManager.ToLogical(new Rectangle(60, 80, 2000, 1400), false, 2.0);
        Assert.Equal(new WindowState(1000, 700, 30, 40, false), logical);

        var physical = WindowStateManager.ToPhysical(logical, 2.0, Options);
        Assert.Equal((2000, 1400, 60, 80), (physical.Width, physical.Height, physical.X!.Value, physical.Y!.Value));
    }

    // ---- IsVisible ----

    private static readonly Rectangle[] SingleScreen = [new(0, 0, 1920, 1080)];

    [Fact]
    public void IsVisible_true_when_fully_on_screen()
    {
        Assert.True(WindowStateManager.IsVisible(100, 100, 800, 600, SingleScreen, Options));
    }

    [Fact]
    public void IsVisible_false_when_fully_off_screen()
    {
        Assert.False(WindowStateManager.IsVisible(2000, 100, 800, 600, SingleScreen, Options));
    }

    [Fact]
    public void IsVisible_requires_a_grabbable_strip_not_just_any_overlap()
    {
        // Only 50 px of width overlaps (< MinVisibleWidth 120) — a sliver is not grabbable.
        Assert.False(WindowStateManager.IsVisible(1870, 100, 800, 600, SingleScreen, Options));
        // 200 px overlap is.
        Assert.True(WindowStateManager.IsVisible(1720, 100, 800, 600, SingleScreen, Options));
    }

    [Fact]
    public void IsVisible_checks_every_screen()
    {
        Rectangle[] two = [new(0, 0, 1920, 1080), new(1920, 0, 1920, 1080)];
        Assert.True(WindowStateManager.IsVisible(2200, 200, 800, 600, two, Options));
    }

    // ---- Store integration (pure part) ----

    // ── The app-maximized seam (P5.5 H2) ──────────────────────────────────────────────────────────
    // Frameless chrome maximizes by hand and keeps WindowState.Normal, so reading Form.WindowState /
    // Form.RestoreBounds persisted "not maximized" together with the WORK-AREA rect as the normal
    // size. Next launch: the window filled the work area believing it wasn't maximized, the border gap
    // came back, the chrome glyph was wrong, and clicking maximize captured the work-area rect as the
    // restore bounds — making RESTORE A PERMANENT NO-OP. Live in the reference composition.

    /// <summary>A window that manages its own maximize, like frameless <c>OptimizedForm</c>.</summary>
    private sealed class AppMaximizedForm : Form, IAppMaximizable
    {
        // DesignerSerializationVisibility.Hidden: WFO1000 treats a settable public property on a
        // Form-derived type as designer-serializable state (the same reason OptimizedForm marks its
        // WndProcHook). These are test inputs, not designer properties.
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsAppMaximized { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Rectangle AppRestoreBounds { get; set; }
    }

    [Fact]
    public void Save_prefers_the_windows_own_maximize_truth_over_WindowState()
    {
        var store = new FakeWindowStateStore();
        using var form = new AppMaximizedForm
        {
            StartPosition = FormStartPosition.Manual,
            // What a manually-maximized window looks like: filling the work area, WindowState.Normal.
            Bounds = new Rectangle(0, 0, 1920, 1040),
            IsAppMaximized = true,
            AppRestoreBounds = new Rectangle(120, 80, 900, 700),
        };
        Assert.Equal(FormWindowState.Normal, form.WindowState); // the property that used to be read

        new WindowStateManager(store, new WindowStateOptions()).Save(form);

        Assert.NotNull(store.Saved);
        Assert.True(store.Saved!.Maximized);            // …not false, as WindowState would have said
        // …and the persisted size is the WINDOWED geometry, not the work area — this is what made
        // restore a no-op on the next launch.
        var scale = DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi);
        Assert.Equal(DpiHelper.Scale(900, 1 / scale), store.Saved.Width);
        Assert.Equal(DpiHelper.Scale(700, 1 / scale), store.Saved.Height);
    }

    [Fact]
    public void Save_falls_back_to_WindowState_for_an_ordinary_form()
    {
        var store = new FakeWindowStateStore();
        using var form = new Form { StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(50, 60, 800, 600) };

        new WindowStateManager(store, new WindowStateOptions()).Save(form);

        Assert.NotNull(store.Saved);
        Assert.False(store.Saved!.Maximized);   // a framed window's WindowState IS the truth
    }

    [Fact]
    public void Apply_does_not_clobber_a_minimum_size_the_form_set_itself()
    {
        // The runner creates the form and THEN applies state, so an app that sets MinimumSize in its
        // constructor had it silently replaced — the reference composition's own 640x420 was dead code.
        // Explicit scale so the assertion is DPI-independent (the parameterless overload is now
        // deferred pre-handle; that path is exercised by Apply_parameterless_defers_to_HandleCreated).
        var store = new FakeWindowStateStore();
        using var form = new Form { MinimumSize = new Size(640, 420) };

        new WindowStateManager(store, new WindowStateOptions()).Apply(form, 1.0);

        Assert.Equal(new Size(640, 420), form.MinimumSize);
    }

    [Fact]
    public void Apply_still_sets_a_minimum_when_the_form_has_none()
    {
        var store = new FakeWindowStateStore();
        using var form = new Form();
        var options = new WindowStateOptions();

        new WindowStateManager(store, options).Apply(form, 1.0);

        Assert.Equal(new Size(options.MinWidth, options.MinHeight), form.MinimumSize);
    }

    [Fact]
    public void Apply_scale_overload_sizes_by_the_explicit_scale_not_SystemScale()
    {
        // The finding: an adopter calling Apply from OnHandleCreated with the form's own DeviceDpi
        // scale gets per-monitor accuracy that the default (primary-monitor SystemScale) does not.
        // Verifies the explicit scale is what's used, without depending on this machine's DPI —
        // a 300x200 save at scale 2.0 must land on 600x400, whatever the primary is.
        //
        // Small values on purpose: the ordinary defaults would push MinWidth/MinHeight above the
        // saved size (so the min clamp would preempt the scale multiply), AND once WinForms
        // creates a handle it caps Form.Size to the current monitor's height on this machine
        // (measured: 1800×1400 requested → 1800×1220 stored). Both traps go away when both
        // dimensions sit well inside any monitor's work area.
        var store = new FakeWindowStateStore { Stored = new WindowState(300, 200, null, null, false) };
        using var form = new Form();
        var options = new WindowStateOptions { MaxToWorkArea = false, MinWidth = 100, MinHeight = 100 };

        new WindowStateManager(store, options).Apply(form, 2.0);

        Assert.Equal(new Size(600, 400), form.Size);
        // Minimum size follows the same explicit scale — no primary-monitor bleed.
        Assert.Equal(new Size(options.MinWidth * 2, options.MinHeight * 2), form.MinimumSize);
    }

    [Fact]
    public void Apply_places_the_saved_position_even_when_maximized()
    {
        // The maximized flag must NOT discard placement: the pre-show position is what makes
        // WinForms maximize onto the SAVED monitor, and what restore-down returns to (Save
        // captures RestoreBounds for exactly this). Regression: a review found an added
        // !maximized condition silently re-centering every maximized launch.
        // Explicit scale so position is DPI-independent; the maximized flag now lands as the
        // deferred marker (see Apply_defers_maximize_to_Shown_for_a_plain_form for the
        // Shown-time consumption).
        var store = new FakeWindowStateStore { Stored = new WindowState(500, 400, 10, 10, Maximized: true) };
        using var form = new Form();
        new WindowStateManager(store).Apply(form, 1.0);

        Assert.Equal(FormStartPosition.Manual, form.StartPosition);
        Assert.Equal(new Point(10, 10), form.Location);
        Assert.Equal(WindowStateManager.RestoreMaximizedTag, form.Tag);
        Assert.Equal(FormWindowState.Normal, form.WindowState);
    }

    // ── Deferred DPI resolution + deferred maximize application (adopter, 2026-08-01) ────────────
    // Two findings from Stage 1 adoption on 0.1.1: the adopter should not have to know that
    // DeviceDpi is the right source and that OnHandleCreated is the only moment it is valid; and
    // for a plain Form, WindowState.Maximized set from Apply/OnHandleCreated goes back to Normal
    // by OnLoad, so the window opened restored-down however it was closed.

    [Fact]
    public void Apply_parameterless_defers_to_HandleCreated_when_the_handle_does_not_exist_yet()
    {
        // The 0.1.1 default resolved SystemScale (primary monitor) synchronously; the new default
        // waits for a handle and resolves ScaleFromDeviceDpi(form.DeviceDpi) — per-monitor accurate
        // by default. This test verifies deferral without asserting a specific scale (which
        // depends on the test machine).
        //
        // Small saved size + tiny MinWidth/MinHeight for the same reason
        // Apply_scale_overload_sizes_by_the_explicit_scale_not_SystemScale explains: once WinForms
        // creates a Form's handle it clamps Size to the current monitor's work area, so a saved
        // size the CI runner cannot fit (GitHub Actions runner: 1044x788) reads back CLAMPED and
        // the assertion misfires with a value nothing in the kit chose (v1 of this test regressed
        // on CI at 1200x800, 2026-08-01).
        Sta.Run(() =>
        {
            var store = new FakeWindowStateStore { Stored = new WindowState(400, 300, null, null, false) };
            using var form = new Form { MinimumSize = new Size(1, 1) };
            var before = form.Size;
            var options = new WindowStateOptions { MaxToWorkArea = false, MinWidth = 100, MinHeight = 100 };

            new WindowStateManager(store, options).Apply(form);

            Assert.False(form.IsHandleCreated);
            Assert.Equal(before, form.Size);   // nothing applied yet — deferred

            _ = form.Handle;                   // fire HandleCreated

            var scale = DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi);
            Assert.Equal(new Size(DpiHelper.Scale(400, scale), DpiHelper.Scale(300, scale)), form.Size);
        });
    }

    [Fact]
    public void Apply_parameterless_applies_synchronously_when_the_handle_already_exists()
    {
        // Same size/min sizing rationale as
        // Apply_parameterless_defers_to_HandleCreated_when_the_handle_does_not_exist_yet.
        Sta.Run(() =>
        {
            var store = new FakeWindowStateStore { Stored = new WindowState(400, 300, null, null, false) };
            using var form = new Form { MinimumSize = new Size(1, 1) };
            _ = form.Handle;                   // handle exists before Apply
            var options = new WindowStateOptions { MaxToWorkArea = false, MinWidth = 100, MinHeight = 100 };

            new WindowStateManager(store, options).Apply(form);

            var scale = DpiHelper.ScaleFromDeviceDpi(form.DeviceDpi);
            Assert.Equal(new Size(DpiHelper.Scale(400, scale), DpiHelper.Scale(300, scale)), form.Size);
        });
    }

    [Fact]
    public void Apply_parameterless_pre_positions_before_reading_DeviceDpi()
    {
        // Adopter-review 2026-08-01: on a mixed-DPI multi-monitor box, reading form.DeviceDpi at
        // HandleCreated returns the initial monitor's DPI (typically the primary, since Location
        // hasn't been set yet) — sizing against that DPI is wrong when the saved position is on
        // a secondary monitor with a different DPI. Belt-and-braces: the deferred path
        // pre-positions the handle to the saved location BEFORE reading DeviceDpi, so the move
        // brings the handle onto the target monitor first.
        //
        // Without a second monitor this test can't observe the DPI change directly. What it CAN
        // verify is that pre-positioning HAPPENS - form.Location matches the saved value the
        // moment the deferred apply completes, meaning any DPI change from the move already
        // fired synchronously before Apply(form, scale) read DeviceDpi.
        Sta.Run(() =>
        {
            // A saved position that is guaranteed to be on-screen on any test machine (0,0 is
            // inside the primary monitor's bounds on every configuration).
            var store = new FakeWindowStateStore { Stored = new WindowState(600, 400, 10, 10, false) };
            using var form = new Form { MinimumSize = new Size(1, 1) };

            new WindowStateManager(store, new WindowStateOptions { MaxToWorkArea = false }).Apply(form);
            _ = form.Handle;   // trigger deferred handler

            // Location applied via pre-position and again inside Apply — the value is idempotent.
            Assert.Equal(FormStartPosition.Manual, form.StartPosition);
            Assert.Equal(new Point(10, 10), form.Location);
        });
    }

    [Fact]
    public void Apply_parameterless_skips_pre_position_for_an_off_screen_saved_position()
    {
        // If the saved position is off-screen (a monitor was unplugged/rearranged), pre-position
        // is a no-op — moving the handle to an unreachable point would be pointless AND would
        // fire a spurious WM_DPICHANGED if the point happened to land on a phantom monitor.
        // Apply's own IsVisible + centre fallback then handles placement inside the primary.
        Sta.Run(() =>
        {
            // Off-screen: no test machine has a monitor covering (100000, 100000).
            var store = new FakeWindowStateStore { Stored = new WindowState(600, 400, 100000, 100000, false) };
            using var form = new Form { MinimumSize = new Size(1, 1) };

            new WindowStateManager(store, new WindowStateOptions { MaxToWorkArea = false }).Apply(form);
            _ = form.Handle;   // trigger deferred handler

            // Fell through to Apply's centre fallback (position not applied, StartPosition centred).
            Assert.Equal(FormStartPosition.CenterScreen, form.StartPosition);
        });
    }

    [Fact]
    public void Apply_defers_maximize_to_Shown_for_a_plain_form()
    {
        // The finding: `Apply` set `form.WindowState = FormWindowState.Maximized` synchronously,
        // and on a real Show the state reset to Normal by OnLoad. The fix reuses the marker path
        // that IAppMaximizable already got, via a one-shot Shown handler for plain forms.
        Sta.Run(() =>
        {
            var store = new FakeWindowStateStore { Stored = new WindowState(500, 400, 10, 10, Maximized: true) };
            using var form = new Form();
            new WindowStateManager(store).Apply(form, 1.0);

            Assert.Equal(WindowStateManager.RestoreMaximizedTag, form.Tag);
            Assert.Equal(FormWindowState.Normal, form.WindowState);

            // Actually run the show sequence: OnHandleCreated → OnLoad → OnShown. Our deferred
            // handler consumes the marker on Shown and applies WindowState.Maximized; the test's
            // Shown subscribes AFTER the deferred handler so it observes the applied state, then
            // closes the pump.
            FormWindowState afterShown = default;
            object? tagAfterShown = null;
            form.Shown += (_, _) =>
            {
                afterShown = form.WindowState;
                tagAfterShown = form.Tag;
                form.Close();
            };
            Application.Run(form);

            Assert.Equal(FormWindowState.Maximized, afterShown);
            Assert.Null(tagAfterShown);        // marker consumed
        });
    }

    [Fact]
    public void JsonFileWindowStateStore_roundtrips_and_is_best_effort()
    {
        using var temp = TempDir.Create();
        var dir = temp.Root;
        var store = new JsonFileWindowStateStore(Path.Combine(dir, "window.json"));
        Assert.Null(store.Load());

        store.Save(new WindowState(1000, 700, 30, 40, true));
        Assert.Equal(new WindowState(1000, 700, 30, 40, true), store.Load());

        File.WriteAllText(Path.Combine(dir, "window.json"), "{not json");
        Assert.Null(store.Load()); // corrupt file → null, never a throw
    }
}
