using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Sample.Chromium;
using Shenora.Windows;

namespace Shenora.Sample.ChromiumViewHost;

/// <summary>
/// A WinForms form holding one <see cref="ChromiumView"/>, for <c>SHENORA_SAMPLE_HOOKS_PROBE=1</c> or <c>2</c>: the app's
/// download, permission and renderer-failure hooks, set on <see cref="ChromiumEngineOptions"/>, run for real on a page that
/// is not one of the Chromium shell's windows. <see cref="HooksProbe"/> is the Chromium sample's, linked; see it for what it
/// checks and what the <c>HOOKS:</c> lines mean. Without the variable the window shows the kit's not-found page: this app
/// has no page of its own.
/// </summary>
internal static class Program
{
    private const string Title = "Shenora ChromiumView Sample";

    [STAThread]
    private static int Main(string[] args)
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            Args = args,
            ApplicationName = Title,
        });

        builder.UseChromiumEngine(new ChromiumEngineOptions
        {
            ResourceProvider = HooksProbe.Provider(null),
            OnDownloadStarting = HooksProbe.OnDownloadStarting,
            OnDownloadProgress = HooksProbe.OnDownloadProgress,
            OnPermissionRequested = HooksProbe.OnPermissionRequested,
            OnProcessFailed = HooksProbe.OnProcessFailed,
        });
        Form? form = null;
        builder.UseWindows(new WindowsHostOptions
        {
            MainForm = sp => form = new Form
            {
                Text = Title,
                ClientSize = new Size(1000, 700),
                Controls = { new ChromiumView(sp.GetRequiredService<ChromiumEngine>()) { Name = "probe", Path = HooksProbe.Page("404.html"), Dock = DockStyle.Fill } },
            },
        });
        builder.OnStarting(_ => HooksProbe.Start(Title, pageWindow: true, () => form?.BeginInvoke(new Action(form.Close))));

        using var app = builder.Build();
        app.Run();
        return 0;
    }
}
