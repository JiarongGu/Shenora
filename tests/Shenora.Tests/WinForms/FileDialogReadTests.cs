using Shenora.Core;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.WinForms;

/// <summary>
/// <see cref="IFileDialogs.OpenReadAsync"/>'s DEFAULT implementation — the one both shells use today.
/// <para>
/// It exists so portable app logic never calls <c>File.OpenRead</c> on a picked handle itself: the
/// contract says <see cref="FileDialogResult.FilePath"/> is "a path or URI the HOST can resolve",
/// and only the host knows which. That it is a real path on Windows AND on Android (MAUI's picker
/// copies the document into app cache) is a fact about today's two shells, not a property of the
/// contract — a shell handing back a genuine content URI overrides this and app logic never notices.
/// </para>
/// </summary>
public class FileDialogReadTests
{
    /// <summary>A dialogs implementation that only picks — exactly what a shell provides.</summary>
    private sealed class PathOnlyDialogs(string? picked) : IFileDialogs
    {
        public Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null) =>
            Task.FromResult(picked is null ? FileDialogResult.Cancelled() : FileDialogResult.Selected(picked));

        public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null) =>
            throw new NotSupportedException();

        public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Reads_the_content_behind_a_picked_handle()
    {
        using var dir = TempDir.Create();
        var path = dir.WriteFile("picked.txt", "the bytes");
        IFileDialogs dialogs = new PathOnlyDialogs(path);

        var picked = await dialogs.OpenFileAsync();
        await using var content = await dialogs.OpenReadAsync(picked.FilePath!);

        Assert.NotNull(content);
        Assert.Equal("the bytes", await new StreamReader(content!).ReadToEndAsync());
    }

    [Fact]
    public async Task A_handle_that_no_longer_resolves_is_null_rather_than_a_throw()
    {
        // Both real cases: a file deleted between choosing and reading, and — on mobile — a cache
        // copy that was evicted. Neither is a programming error, so neither should look like one.
        using var dir = TempDir.Create();
        IFileDialogs dialogs = new PathOnlyDialogs(null);

        Assert.Null(await dialogs.OpenReadAsync(dir.Combine("never-existed.txt")));
    }

    [Fact]
    public async Task An_empty_handle_IS_a_caller_bug()
    {
        IFileDialogs dialogs = new PathOnlyDialogs(null);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => dialogs.OpenReadAsync("  "));
    }

    [Fact]
    public async Task A_shell_can_OVERRIDE_the_default_for_a_handle_that_is_not_a_path()
    {
        // The point of putting this on the interface at all. A shell whose picker returns a content
        // URI resolves it itself, and portable logic calling OpenReadAsync is unaffected — which is
        // the "universal interface, device-dependent implementation" split.
        IFileDialogs dialogs = new ContentUriDialogs();

        await using var content = await dialogs.OpenReadAsync("content://docs/42");

        Assert.Equal("resolved by the host", await new StreamReader(content!).ReadToEndAsync());
    }

    private sealed class ContentUriDialogs : IFileDialogs
    {
        public Task<FileDialogResult> OpenFileAsync(OpenFileOptions? options = null) =>
            Task.FromResult(FileDialogResult.Selected("content://docs/42"));

        public Task<FileDialogResult> OpenFolderAsync(OpenFolderOptions? options = null) =>
            throw new NotSupportedException();

        public Task<FileDialogResult> SaveFileAsync(SaveFileOptions? options = null) =>
            throw new NotSupportedException();

        public Task<Stream?> OpenReadAsync(string handle, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("resolved by the host")));
    }
}
