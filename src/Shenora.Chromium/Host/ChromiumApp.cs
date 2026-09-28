using System.Runtime.InteropServices;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>The <c>cef_app_t</c> the shell hands CEF, and the browser-process handler behind it.</summary>
internal sealed unsafe class ChromiumApp : CefObject<_cef_app_t>
{
    private readonly ProcessHandler _process;

    /// <param name="contextInitialized">Runs on CEF's UI thread once CEF's context exists.</param>
    public ChromiumApp(Action contextInitialized)
    {
        _process = new ProcessHandler(contextInitialized);
        Struct->get_browser_process_handler = &GetProcessHandler;
    }

    [UnmanagedCallersOnly]
    private static _cef_browser_process_handler_t* GetProcessHandler(_cef_app_t* self) => From<ChromiumApp>(self)._process.ForCef();

    private sealed class ProcessHandler : CefObject<_cef_browser_process_handler_t>
    {
        private readonly Action _contextInitialized;

        public ProcessHandler(Action contextInitialized)
        {
            _contextInitialized = contextInitialized;
            Struct->on_context_initialized = &ContextInitialized;
        }

        [UnmanagedCallersOnly]
        private static void ContextInitialized(_cef_browser_process_handler_t* self) => AppCallback.Run(From<ProcessHandler>(self)._contextInitialized);
    }
}
