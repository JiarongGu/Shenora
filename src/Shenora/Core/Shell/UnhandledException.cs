namespace Shenora.Core.Shell;

/// <summary>Where an unhandled exception surfaced.</summary>
public enum UnhandledExceptionSource
{
    /// <summary>The shell's UI thread — recoverable: a WinForms UI-thread exception (<c>Application.ThreadException</c>),
    /// or work posted to the Chromium shell's UI thread, an <c>async void</c> continuation there included.</summary>
    UiThread,

    /// <summary>Any other thread (<c>AppDomain.UnhandledException</c>) — the process is usually dying.</summary>
    AppDomain,

    /// <summary>A faulted Task nobody observed (<c>TaskScheduler.UnobservedTaskException</c>).</summary>
    UnobservedTask,
}

/// <summary>An unhandled exception, as a desktop shell delivers it to the app: <c>WinFormsBootstrapOptions</c>' and
/// <c>ChromiumHostOptions</c>' <c>OnUnhandledException</c> (D88).</summary>
/// <param name="Exception">What was thrown.</param>
/// <param name="Source">Where it surfaced.</param>
/// <param name="IsTerminating">The runtime is ending the process because of it.</param>
public sealed record UnhandledExceptionReport(Exception Exception, UnhandledExceptionSource Source, bool IsTerminating);
