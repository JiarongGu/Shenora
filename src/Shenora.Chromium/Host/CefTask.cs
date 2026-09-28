using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// Work CEF runs on one of its threads. CEF takes the one reference it is handed, the creator lets its own
/// go at once, and CEF's release after running frees it.
/// </summary>
internal sealed unsafe class CefTask : CefObject<_cef_task_t>
{
    private readonly Action _body;

    private CefTask(Action body)
    {
        _body = body;
        Struct->execute = &Execute;
    }

    /// <summary>True when CEF accepted the task, which it refuses once its thread is gone.</summary>
    public static bool Post(cef_thread_id_t thread, Action body)
    {
        var task = new CefTask(body);
        var posted = Cef.cef_post_task(thread, task.ForCef()) == 1;
        task.Release();
        return posted;
    }

    /// <inheritdoc cref="Post"/>
    public static bool PostDelayed(cef_thread_id_t thread, TimeSpan delay, Action body)
    {
        var task = new CefTask(body);
        var posted = Cef.cef_post_delayed_task(thread, task.ForCef(), (long)delay.TotalMilliseconds) == 1;
        task.Release();
        return posted;
    }

    // Guarded: an exception must never unwind into CEF's frames, and a posted body has no caller to catch it.
    [UnmanagedCallersOnly]
    private static void Execute(_cef_task_t* self) => AppCallback.Run(From<CefTask>(self)._body);
}
