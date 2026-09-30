using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Shenora.Chromium;
using Shenora.Chromium.Host;
using Shenora.Core.Ipc;
using Shenora.Modules.FileDialog;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The Chromium shell's file dialogs, the parts decided without a dialog on screen: CEF's filter form, the default
/// extension, the start folder, remembering a pick, and that the shell registers both the dialogs and the page's
/// route to them. The dialogs themselves were driven end to end in the shell.
/// </summary>
public class ChromiumFileDialogsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("shenora-dialogs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Store(string? remembered) : IFileDialogPathStore
    {
        public readonly Dictionary<string, string> Saved = new();
        public Task<string?> GetPathAsync(string key) => Task.FromResult(remembered);
        public Task SavePathAsync(string key, string path) { Saved[key] = path; return Task.CompletedTask; }
    }

    private static ChromiumFileDialogs Dialogs(IFileDialogPathStore? store) => new(null!, null!, store, null);

    [Fact]
    public void Filters_become_CEFs_description_and_extension_list()
    {
        Assert.Empty(ChromiumFileDialogs.AcceptFilters(null));
        Assert.Equal(["Images|.png;.jpg", "Text|.txt"], ChromiumFileDialogs.AcceptFilters(
        [
            new FileDialogFilter { Name = "Images", Extensions = ["png", ".jpg"] },
            new FileDialogFilter { Name = "Text", Extensions = ["txt"] },
        ]));
    }

    [Theory]
    [InlineData(@"C:\out\report", "txt", @"C:\out\report.txt")]
    [InlineData(@"C:\out\report", ".txt", @"C:\out\report.txt")]
    [InlineData(@"C:\out\report.csv", "txt", @"C:\out\report.csv")]   // the user's own extension wins
    [InlineData(@"C:\out\report", null, @"C:\out\report")]
    public void The_default_extension_is_added_only_when_the_user_typed_none(string picked, string? extension, string expected) =>
        Assert.Equal(expected, ChromiumFileDialogs.WithDefaultExtension(picked, extension));

    [Fact]
    public async Task A_dialog_asked_for_while_one_is_open_waits_for_it()
    {
        // CEF answered such a second dialog as cancelled itself, and then never answered the FIRST (measured on Windows).
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Dialogs test" });
        var host = new ChromiumHostOptions();
        builder.UseChromium(host);
        using var app = builder.Build();
        var posted = new ConcurrentQueue<Action>();
        var ui = new CefUiDispatcher(work => { posted.Enqueue(work); return true; }, () => false);
        ui.MarkReady();
        var windows = new ChromiumWindows(host, ui, app.Services.GetRequiredService<IMessageDispatcher>(), null, null, new ChromiumUrlLauncher());
        var dialogs = new ChromiumFileDialogs(windows, ui, null, null);

        var first = dialogs.OpenFileAsync();
        var second = dialogs.SaveFileAsync();

        Assert.Single(posted);
        Assert.False(second.IsCompleted);
        // The first ends (no window is open to own it), and then the second reaches CEF.
        Assert.True(posted.TryDequeue(out var shown));
        shown();
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (posted.IsEmpty && DateTime.UtcNow < until) await Task.Delay(10);
        Assert.Single(posted);
    }

    [Fact]
    public async Task The_start_folder_is_the_remembered_one_then_the_default_then_Documents()
    {
        var options = new OpenFileOptions { RememberPathKey = "k", DefaultPath = _dir };

        Assert.Equal(_dir, await Dialogs(new Store(_dir)).ResolveInitialPathAsync(options));
        // A remembered folder that no longer exists falls through to the default path.
        Assert.Equal(_dir, await Dialogs(new Store(Path.Combine(_dir, "gone"))).ResolveInitialPathAsync(options));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            await Dialogs(null).ResolveInitialPathAsync(new OpenFileOptions()));
    }

    [Fact]
    public async Task A_pick_remembers_its_folder_under_the_key_and_only_a_real_folder()
    {
        var store = new Store(null);
        var dialogs = Dialogs(store);

        await dialogs.RememberAsync(new OpenFileOptions { RememberPathKey = "k" }, _dir);
        await dialogs.RememberAsync(new OpenFileOptions { RememberPathKey = "gone" }, Path.Combine(_dir, "gone"));
        await dialogs.RememberAsync(new OpenFileOptions(), _dir);   // no key, no memory

        Assert.Equal(new Dictionary<string, string> { ["k"] = _dir }, store.Saved);
    }

    [Fact]
    public void The_shell_registers_the_dialogs_and_the_pages_route_to_them()
    {
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions { ApplicationName = "Dialogs test" });
        builder.UseChromium(new ChromiumHostOptions());
        using var app = builder.Build();

        Assert.IsType<ChromiumFileDialogs>(app.Services.GetRequiredService<IFileDialogs>());
        Assert.Contains(app.Services.GetServices<IIpcModule>(), m => m.ModuleName == FileDialogModule.Module);
    }
}
