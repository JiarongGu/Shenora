using Shenora.Core;
using Shenora.Windows;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.WinForms;

/// <summary>
/// Tests over the pure/seam parts — the live dialogs themselves need a human (or the e2e loop);
/// family precedent: real UI behavior is the sample's subject.
/// </summary>
public class FileDialogsTests
{
    private sealed class FakePathStore : IFileDialogPathStore
    {
        public Dictionary<string, string> Paths { get; } = new();

        public Task<string?> GetPathAsync(string key) =>
            Task.FromResult(Paths.TryGetValue(key, out var path) ? path : null);

        public Task SavePathAsync(string key, string path)
        {
            Paths[key] = path;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Filter_strings_follow_the_winforms_shape()
    {
        Assert.Equal("All Files (*.*)|*.*", FileDialogs.BuildFilterString(null));
        Assert.Equal("All Files (*.*)|*.*", FileDialogs.BuildFilterString([]));
        Assert.Equal(
            "Images|*.png;*.jpg|Archives|*.zip",
            FileDialogs.BuildFilterString(
            [
                new FileDialogFilter { Name = "Images", Extensions = ["png", "jpg"] },
                new FileDialogFilter { Name = "Archives", Extensions = ["zip"] },
            ]));
    }

    [Fact]
    public async Task Initial_path_prefers_the_remembered_directory()
    {
        using var temp = TempDir.Create();
        var existing = temp.Root;
        var store = new FakePathStore { Paths = { ["import"] = existing } };
        var dialogs = new FileDialogs(new FileDialogsOptions { PathStore = store });

        var resolved = await dialogs.ResolveInitialPathAsync(new FileDialogOptions { RememberPathKey = "import" });

        Assert.Equal(existing, resolved);
    }

    [Fact]
    public async Task Stale_remembered_paths_fall_through_to_the_default()
    {
        using var temp = TempDir.Create();
        var defaultDir = temp.Root;
        var store = new FakePathStore { Paths = { ["import"] = Path.Combine(defaultDir, "gone-subdir") } };
        var dialogs = new FileDialogs(new FileDialogsOptions { PathStore = store });

        var resolved = await dialogs.ResolveInitialPathAsync(new FileDialogOptions
        {
            RememberPathKey = "import",
            DefaultPath = defaultDir,
        });

        Assert.Equal(defaultDir, resolved);
    }

    [Fact]
    public async Task Initial_path_falls_back_to_documents()
    {
        var dialogs = new FileDialogs();

        var resolved = await dialogs.ResolveInitialPathAsync(null);

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), resolved);
    }

    [Fact]
    public async Task Remember_saves_only_valid_keyed_directories()
    {
        using var temp = TempDir.Create();
        var existing = temp.Root;
        var store = new FakePathStore();
        var dialogs = new FileDialogs(new FileDialogsOptions { PathStore = store });
        var keyed = new FileDialogOptions { RememberPathKey = "import" };

        await dialogs.RememberPathAsync(keyed, existing);
        Assert.Equal(existing, store.Paths["import"]);

        await dialogs.RememberPathAsync(keyed, Path.Combine(existing, "missing"));
        await dialogs.RememberPathAsync(new FileDialogOptions(), existing); // no key
        await dialogs.RememberPathAsync(keyed, null);
        Assert.Single(store.Paths); // nothing else landed
    }

    [Fact]
    public async Task A_failing_store_read_never_breaks_the_dialog_flow()
    {
        var dialogs = new FileDialogs(new FileDialogsOptions { PathStore = new ThrowingStore() });

        var resolved = await dialogs.ResolveInitialPathAsync(new FileDialogOptions { RememberPathKey = "x" });

        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), resolved);
    }

    private sealed class ThrowingStore : IFileDialogPathStore
    {
        public Task<string?> GetPathAsync(string key) => throw new IOException("settings file locked");

        public Task SavePathAsync(string key, string path) => throw new IOException("settings file locked");
    }
}
