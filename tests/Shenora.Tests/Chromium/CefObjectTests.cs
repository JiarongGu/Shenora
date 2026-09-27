using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The memory layer under the Chromium shell. CEF drives it only through the struct's own function
/// pointers, so these call those pointers, not the managed methods behind them. No CEF binary is loaded.
/// </summary>
public unsafe class CefObjectTests
{
    private sealed class Task : CefObject<_cef_task_t>
    {
        public int Executed;
        public int Freed;

        public Task() => Struct->execute = &Execute;

        public _cef_task_t* Raw => Struct;

        public _cef_base_ref_counted_t* Base => (_cef_base_ref_counted_t*)Struct;

        [UnmanagedCallersOnly]
        private static void Execute(_cef_task_t* self) => From<Task>(self).Executed++;

        private protected override void OnFreed() => Freed++;
    }

    [Fact]
    public void Its_base_says_the_CEF_structs_own_size()
    {
        var task = new Task();

        // CEF decides which members exist from this, so a wrapper's size would be read as members.
        Assert.Equal((nuint)sizeof(_cef_task_t), task.Base->size);
        task.Release();
    }

    [Fact]
    public void A_callback_reaches_its_managed_object_through_self()
    {
        var task = new Task();

        task.Raw->execute(task.Raw);

        Assert.Equal(1, task.Executed);
        task.Release();
    }

    [Fact]
    public void CEFs_last_release_frees_the_object_once_and_reports_it()
    {
        var task = new Task();
        var b = task.Base;
        b->add_ref(b);

        task.Release();                 // the creator lets go; CEF still holds one
        Assert.Equal(0, task.Freed);

        Assert.Equal(1, b->release(b)); // 1 = "deleted", which is what CEF's contract asks for
        Assert.Equal(1, task.Freed);
    }

    [Fact]
    public void A_release_that_leaves_a_reference_reports_zero()
    {
        var task = new Task();
        var b = task.Base;
        b->add_ref(b);

        Assert.Equal(0, b->release(b));
        Assert.Equal(0, task.Freed);
        task.Release();
        Assert.Equal(1, task.Freed);
    }

    [Fact]
    public void Handing_it_to_CEF_adds_the_reference_CEF_takes()
    {
        var task = new Task();

        var given = task.ForCef();

        Assert.Equal(2, task.References);
        Assert.Equal(0, ((_cef_base_ref_counted_t*)given)->release((_cef_base_ref_counted_t*)given));
        task.Release();
        Assert.Equal(1, task.Freed);
    }

    [Fact]
    public void The_ref_queries_answer_from_the_live_count()
    {
        var task = new Task();
        var b = task.Base;

        Assert.Equal(1, b->has_one_ref(b));
        b->add_ref(b);
        Assert.Equal(0, b->has_one_ref(b));
        Assert.Equal(1, b->has_at_least_one_ref(b));

        b->release(b);
        task.Release();
    }

    [Fact]
    public void Releasing_past_zero_is_refused_loudly()
    {
        var task = new Task();
        task.Release();

        // Through the struct this would end the process (FailFast): an exception must never unwind into
        // CEF's frames, and a count below zero means the memory is already gone.
        Assert.Throws<InvalidOperationException>(task.Release);
        Assert.Equal(1, task.Freed);
    }

    [Fact]
    public void A_CefRef_releases_the_reference_it_owns()
    {
        var task = new Task();

        using (var held = new CefRef<_cef_task_t>(task.ForCef()))
        {
            Assert.False(held.IsNull);
            Assert.Equal(2, task.References);
        }

        Assert.Equal(1, task.References);
        task.Release();
        Assert.Equal(1, task.Freed);
    }

    [Fact]
    public void A_CefRef_passing_its_struct_to_CEF_keeps_its_own_reference()
    {
        var task = new Task();
        var held = new CefRef<_cef_task_t>(task.ForCef());

        var passed = held.ForCef();      // what CEF consumes when it is handed the struct
        Assert.Equal(3, task.References);
        ((_cef_base_ref_counted_t*)passed)->release((_cef_base_ref_counted_t*)passed);

        held.Dispose();
        task.Release();
        Assert.Equal(1, task.Freed);
    }
}
