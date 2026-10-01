using Shenora.Windows;

namespace Shenora.Tests.WinForms;

public class FormInteractionTests
{
    [Fact]
    public void Handle_is_zero_without_a_form_or_before_creation()
    {
        var interaction = new FormInteraction();
        Assert.Equal(IntPtr.Zero, interaction.GetMainFormHandle());

        using var form = new Form();
        interaction.SetMainForm(form);
        // No handle yet — answering Zero is the FIX: the source's Invoke dance would have
        // CREATED the handle on the calling (wrong) thread here.
        Assert.Equal(IntPtr.Zero, interaction.GetMainFormHandle());

        _ = form.Handle;
        Assert.NotEqual(IntPtr.Zero, interaction.GetMainFormHandle());
    }

    [Fact]
    public void Blocking_is_nested()
    {
        using var form = new Form();
        var interaction = new FormInteraction();
        interaction.SetMainForm(form);
        Assert.True(form.Enabled);

        interaction.BlockInteraction();
        interaction.BlockInteraction(); // second dialog on top
        Assert.False(form.Enabled);

        interaction.UnblockInteraction();
        Assert.False(form.Enabled); // still one block outstanding

        interaction.UnblockInteraction();
        Assert.True(form.Enabled);
    }

    [Fact]
    public void Unbalanced_unblocks_are_harmless()
    {
        using var form = new Form();
        var interaction = new FormInteraction();
        interaction.SetMainForm(form);

        interaction.UnblockInteraction(); // never below zero
        interaction.BlockInteraction();
        Assert.False(form.Enabled);
        interaction.UnblockInteraction();
        Assert.True(form.Enabled);
    }

    /// <summary>A block posted from another thread must not land AFTER an unblock made on the UI thread, which applies
    /// inline: the post applied the value it captured, so the form stayed disabled with nothing blocking it.</summary>
    [Fact]
    public void An_unblock_on_the_UI_thread_is_not_overtaken_by_a_block_posted_from_another()
    {
        Shenora.Tests.TestSupport.Sta.Run(() =>
        {
            using var form = new Form();
            _ = form.Handle;
            var interaction = new FormInteraction();
            interaction.SetMainForm(form);

            Task.Run(interaction.BlockInteraction).Wait();   // posted: not the UI thread
            interaction.UnblockInteraction();                 // inline: this is the UI thread
            for (var i = 0; i < 20; i++) Application.DoEvents();

            Assert.True(form.Enabled, "the form was left disabled with no block outstanding");
        });
    }

    [Fact]
    public void Blocking_without_a_form_is_a_no_op()
    {
        var interaction = new FormInteraction();
        interaction.BlockInteraction();
        interaction.UnblockInteraction();
    }
}
