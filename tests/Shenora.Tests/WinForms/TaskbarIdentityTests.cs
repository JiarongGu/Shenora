using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The identity over a real (invisible) form: what is written is read back through the same property store the taskbar reads,
/// which is the one thing a unit test can see of it. Whether the taskbar then groups and pins by it is the shell's.
/// </summary>
public class TaskbarIdentityTests
{
    private static string? Read(Form form, uint key) => TaskbarIdentity.Read(form.Handle, key);

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    public void An_id_windows_would_ignore_is_refused_when_it_is_worn(string id)
    {
        var identity = new TaskbarIdentity { AppId = id };
        using var form = new Form();
        Assert.Throws<ArgumentException>(() => identity.Wear(form));
    }

    [Fact]
    public void An_id_over_a_hundred_and_twenty_eight_characters_is_refused()
    {
        var identity = new TaskbarIdentity { AppId = new string('a', 129) };
        using var form = new Form();
        Assert.Throws<ArgumentException>(() => identity.Wear(form));
        new TaskbarIdentity { AppId = new string('a', 128) }.Validate();
    }

    // Windows reads the command only beside a name for the pin: a command alone is silently dropped, which is the defect to refuse.
    [Fact]
    public void A_relaunch_command_without_a_display_name_is_refused()
    {
        var identity = new TaskbarIdentity { AppId = "Shenora.Test", RelaunchCommand = "C:\\app.exe" };
        using var form = new Form();
        var error = Assert.Throws<ArgumentException>(() => identity.Wear(form));
        Assert.Contains("RelaunchDisplayName", error.Message);
    }

    [Fact]
    public void An_identity_is_written_when_the_handle_is_made_and_read_back_from_the_property_store() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        new TaskbarIdentity
        {
            AppId = "Shenora.Test.Identity",
            RelaunchCommand = "\"C:\\Apps\\app.exe\" --restore",
            RelaunchDisplayName = "Test app",
            RelaunchIcon = "C:\\Apps\\app.exe,0",
        }.Wear(form);

        _ = form.Handle;

        Assert.Equal("Shenora.Test.Identity", Read(form, TaskbarIdentity.IdKey));
        Assert.Equal("\"C:\\Apps\\app.exe\" --restore", Read(form, TaskbarIdentity.RelaunchCommandKey));
        Assert.Equal("Test app", Read(form, TaskbarIdentity.RelaunchDisplayNameKey));
        Assert.Equal("C:\\Apps\\app.exe,0", Read(form, TaskbarIdentity.RelaunchIconKey));
    });

    [Fact]
    public void A_form_that_already_has_a_handle_wears_it_at_once_and_a_value_left_unset_is_not_written() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        _ = form.Handle;

        new TaskbarIdentity { AppId = "Shenora.Test.Late" }.Wear(form);

        Assert.Equal("Shenora.Test.Late", Read(form, TaskbarIdentity.IdKey));
        Assert.Null(Read(form, TaskbarIdentity.RelaunchCommandKey));
    });

    // A property belongs to one handle: a window made again has nothing of the old one.
    [Fact]
    public void A_recreated_handle_wears_the_identity_again() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        new TaskbarIdentity { AppId = "Shenora.Test.Again" }.Wear(form);
        _ = form.Handle;
        var first = form.Handle;

        form.ShowInTaskbar = true;
        _ = form.Handle;

        Assert.NotEqual(first, form.Handle);
        Assert.Equal("Shenora.Test.Again", Read(form, TaskbarIdentity.IdKey));
    });

    [Fact]
    public void Clearing_empties_what_was_written() => Sta.Run(() =>
    {
        using var form = new Form { ShowInTaskbar = false };
        new TaskbarIdentity { AppId = "Shenora.Test.Clear" }.Wear(form);
        _ = form.Handle;
        Assert.NotNull(Read(form, TaskbarIdentity.IdKey));

        TaskbarIdentity.Write(form.Handle, [(TaskbarIdentity.IdKey, null)]);

        Assert.Null(Read(form, TaskbarIdentity.IdKey));
    });
}
