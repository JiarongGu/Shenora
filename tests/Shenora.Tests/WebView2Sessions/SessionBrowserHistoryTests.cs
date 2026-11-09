using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using Shenora.Core.Sessions;
using Shenora.Tests.TestSupport;
using Shenora.Windows;

namespace Shenora.Tests.WebView2Sessions;

/// <summary>
/// The WebView2 session browser's history (<see cref="ISessionBrowserHistory"/>), against a REAL WebView2: a step
/// goes where the control's own history says, and <c>CanGoBack</c>/<c>CanGoForward</c> are the control's. The Chromium
/// shell's browser is covered by <c>ChromiumSessionBrowserHistoryTests</c> and the sample's panel probe.
/// </summary>
public class SessionBrowserHistoryTests
{
    [Fact]
    public void A_WebView2_browser_steps_back_and_forward_through_its_history()
    {
        using var profile = TempDir.Create();
        using var server = LocalPages.Start();
        OnForm(anchor => StepThroughAsync(anchor, profile.Root, server.Origin));
    }

    [Fact]
    public void A_closed_WebView2_browser_has_no_history_to_step_through_and_steps_nowhere()
    {
        using var profile = TempDir.Create();
        OnForm(async anchor =>
        {
            var host = new WebView2SessionHost(anchor);
            var browser = await host.CreateAsync(
                new SessionBrowserDefinition { Options = new SessionBrowserOptions { ProfileDirectory = profile.Root }, Scope = () => "closed" },
                CancellationToken.None);
            var history = Assert.IsAssignableFrom<ISessionBrowserHistory>(browser);
            browser.Close();

            // As the Chromium session browser's are once its browser has gone: nothing to ask, nothing thrown.
            Assert.False(history.CanGoBack);
            Assert.False(history.CanGoForward);
            history.GoBack();
            history.GoForward();
            history.Reload();
            history.Stop();
        });
    }

    // The body runs on a form's message loop, on an STA thread of its own; what it throws is thrown here.
    private static void OnForm(Func<Form, Task> body)
    {
        Exception? failure = null;
        Sta.Run(() =>
        {
            using var anchor = new Form { ShowInTaskbar = false };
            anchor.Shown += async (_, _) =>
            {
                try { await body(anchor); }
                catch (Exception ex) { failure = ex; }
                finally { anchor.Close(); }
            };
            Application.Run(anchor);
        });
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    // Pages the browser can load, answered from this machine: a data: URL reports no address as the page's source.
    private sealed class LocalPages : IDisposable
    {
        private readonly HttpListener _listener;

        private LocalPages(HttpListener listener, string origin)
        {
            _listener = listener;
            Origin = origin;
        }

        public string Origin { get; }

        public static LocalPages Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var origin = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(origin);
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); }
                    catch { return; }
                    var body = Encoding.UTF8.GetBytes($"<html><body>{context.Request.Url!.AbsolutePath}</body></html>");
                    context.Response.ContentType = "text/html";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
            return new LocalPages(listener, origin);
        }

        public void Dispose() => _listener.Close();
    }

    private static async Task StepThroughAsync(Form anchor, string profile, string origin)
    {
        string one = origin + "first", two = origin + "second";
        var host = new WebView2SessionHost(anchor);
        var browser = await host.CreateAsync(
            new SessionBrowserDefinition { Options = new SessionBrowserOptions { ProfileDirectory = profile }, Scope = () => "history" },
            CancellationToken.None);
        try
        {
            var history = Assert.IsAssignableFrom<ISessionBrowserHistory>(browser);
            Assert.False(history.CanGoBack);
            Assert.False(history.CanGoForward);
            history.GoBack();   // nowhere to go: nothing happens
            var asked = new List<string>();
            browser.CancelNavigation = uri => { asked.Add(uri); return false; };

            Assert.Equal(one, (await Completed(browser, () => browser.Navigate(one))).Uri);
            Assert.Equal(two, (await Completed(browser, () => browser.Navigate(two))).Uri);
            Assert.True(history.CanGoBack);
            Assert.False(history.CanGoForward);

            Assert.Equal(one, (await Completed(browser, history.GoBack)).Uri);
            Assert.False(history.CanGoBack);
            Assert.True(history.CanGoForward);

            Assert.Equal(two, (await Completed(browser, history.GoForward)).Uri);
            Assert.True(history.CanGoBack);
            Assert.False(history.CanGoForward);

            Assert.Equal(two, (await Completed(browser, history.Reload)).Uri);
            Assert.Equal([one, two, one, two, two], asked);
        }
        finally { browser.Close(); }
    }

    // The navigation `start` begins, as the browser reports it finished, within a bound.
    private static async Task<SessionNavigationResult> Completed(ISessionBrowser browser, Action start)
    {
        var done = new TaskCompletionSource<SessionNavigationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(SessionNavigationResult result) => done.TrySetResult(result);
        browser.NavigationCompleted += On;
        try
        {
            start();
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally { browser.NavigationCompleted -= On; }
    }
}
