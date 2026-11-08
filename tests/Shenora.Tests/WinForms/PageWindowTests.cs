using Shenora.Windows;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.WinForms;

/// <summary>The window of the page that sent a request (<see cref="PageWindow"/>), and the dialogs that follow it.</summary>
public class PageWindowTests
{
    private static Form Shown(string name)
    {
        var form = new Form { Text = name, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        form.Show();
        return form;
    }

    [Fact]
    public void A_page_in_a_form_names_that_form()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            var page = new Panel();
            var nested = new Control();
            page.Controls.Add(nested);
            form.Controls.Add(page);

            using (PageSender.Enter(nested)) Assert.Same(form, PageWindow.Current);
        });
    }

    [Fact]
    public void Outside_a_page_there_is_no_window()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            Assert.Null(PageWindow.Current);

            // A programmatic send inside a page's work is nobody's.
            using (PageSender.Enter(form)) using (PageSender.Enter(null)) Assert.Null(PageWindow.Current);
        });
    }

    [Fact]
    public void A_page_outside_any_form_has_no_window()
    {
        Sta.Run(() =>
        {
            var loose = new Control();
            using (PageSender.Enter(loose)) Assert.Null(PageWindow.Current);
        });
    }

    [Fact]
    public void A_page_that_is_itself_a_window_names_itself()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            using (PageSender.Enter(form)) Assert.Same(form, PageWindow.Current);
        });
    }

    [Fact]
    public void The_window_follows_the_requests_continuations()
    {
        Sta.Run(() =>
        {
            using var form = new Form();
            Form? seen = null;
            using (PageSender.Enter(form))
            {
                Task.Run(() => seen = PageWindow.Current).GetAwaiter().GetResult();
            }

            Assert.Same(form, seen);
        });
    }

    [Fact]
    public void A_dialog_is_owned_by_the_asking_window()
    {
        Sta.Run(() =>
        {
            using var main = Shown("main");
            using var asking = Shown("asking");
            var interaction = new FormInteraction();
            interaction.SetMainForm(main);
            var dialogs = new FileDialogs(new FileDialogsOptions { Interaction = interaction });

            using (PageSender.Enter(asking))
            {
                var owner = dialogs.ResolveOwner();
                Assert.Same(asking, owner.Window);
                Assert.Equal(asking.Handle, owner.Handle);
            }
        });
    }

    [Fact]
    public void A_dialog_from_no_page_is_owned_by_the_main_window()
    {
        Sta.Run(() =>
        {
            using var main = Shown("main");
            var interaction = new FormInteraction();
            interaction.SetMainForm(main);
            var dialogs = new FileDialogs(new FileDialogsOptions { Interaction = interaction });

            var owner = dialogs.ResolveOwner();

            Assert.Same(main, owner.Window);
            Assert.Equal(main.Handle, owner.Handle);
        });
    }

    [Fact]
    public void A_dialog_falls_back_to_the_main_window_when_the_asking_one_is_gone_or_hidden()
    {
        Sta.Run(() =>
        {
            using var main = Shown("main");
            var interaction = new FormInteraction();
            interaction.SetMainForm(main);
            var dialogs = new FileDialogs(new FileDialogsOptions { Interaction = interaction });

            var hidden = Shown("hidden");
            hidden.Hide();
            using (PageSender.Enter(hidden)) Assert.Same(main, dialogs.ResolveOwner().Window);

            var closed = Shown("closed");
            closed.Dispose();
            using (PageSender.Enter(closed)) Assert.Same(main, dialogs.ResolveOwner().Window);

            hidden.Dispose();
        });
    }

    [Fact]
    public void Blocking_a_dialogs_owner_is_nested_and_leaves_the_others_alone()
    {
        Sta.Run(() =>
        {
            using var main = Shown("main");
            using var asking = Shown("asking");

            WindowBlock.Block(asking);
            WindowBlock.Block(asking);
            Assert.False(asking.Enabled);
            Assert.True(main.Enabled);

            WindowBlock.Unblock(asking);
            Assert.False(asking.Enabled);

            WindowBlock.Unblock(asking);
            Assert.True(asking.Enabled);
        });
    }

    [Fact]
    public void A_page_in_a_secondary_window_names_that_window_on_its_thread()
    {
        using var windows = new SecondaryWindows();
        Form? form = null;
        windows.Open("w-owner", new SecondaryWindowOptions
        {
            CreateForm = () => form = new Form { ShowInTaskbar = false, WindowState = FormWindowState.Minimized },
        });
        SpinWait.SpinUntil(() => form is { IsHandleCreated: true }, TimeSpan.FromSeconds(10));
        Assert.NotNull(form);

        Form? seen = null;
        using (PageSender.Enter(form)) seen = PageWindow.Current;

        Assert.Same(form, seen);
        windows.Close("w-owner");
        SpinWait.SpinUntil(() => !windows.HasWindow("w-owner"), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_dialog_with_no_window_at_all_is_unowned()
    {
        var owner = new FileDialogs().ResolveOwner();

        Assert.Null(owner.Window);
        Assert.Equal(IntPtr.Zero, owner.Handle);
    }
}
