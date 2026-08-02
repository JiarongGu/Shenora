using Shenora.Core;
using Shenora.Tests.TestSupport;

namespace Shenora.Tests.WinForms;

/// <summary>
/// <see cref="IFileDialogs.SaveAsync"/>'s DEFAULT implementation — the portable save, and the
/// counterpart to <see cref="IFileDialogs.OpenReadAsync"/>: open became universal by letting the host
/// do the reading, save becomes universal by letting the host do the writing.
/// <para>
/// The tests that matter are the FAILURE ones. A save picker is usually pointed at a long operation
/// (an encode, an export, a report), so the interesting question is never "does a good write land" but
/// "what happens to the file the user already had when the write does NOT finish" — and the answer has
/// to be "nothing", or the picker destroys data every time an operation is interrupted.
/// </para>
/// </summary>
public class FileDialogSaveTests
{
    /// <summary>A shell that only PICKS — exactly the half a desktop shell provides.</summary>
    private sealed class PathOnlyDialogs(string? destination) : IFileDialogs
    {
        public int SaveFileCalls { get; private set; }

        public Task<FileDialogResult> OpenFileAsync(FileDialogOptions? options = null) =>
            throw new NotSupportedException();

        public Task<FileDialogResult> OpenFolderAsync(FileDialogOptions? options = null) =>
            throw new NotSupportedException();

        public Task<FileDialogResult> SaveFileAsync(FileDialogOptions? options = null)
        {
            SaveFileCalls++;
            return Task.FromResult(destination is null
                ? FileDialogResult.Cancelled()
                : FileDialogResult.Selected(destination));
        }
    }

    private static Task WriteText(Stream stream, string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return stream.WriteAsync(bytes, 0, bytes.Length);
    }

    [Fact]
    public async Task A_completed_write_lands_at_the_picked_destination()
    {
        using var dir = TempDir.Create();
        var path = dir.Combine("export.txt");
        IFileDialogs dialogs = new PathOnlyDialogs(path);

        var result = await dialogs.SaveAsync(null, (stream, _) => WriteText(stream, "exported"));

        Assert.True(result.Success);
        Assert.Equal(path, result.FilePath);
        Assert.Equal("exported", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_THROWING_write_leaves_the_user_s_previous_file_EXACTLY_as_it_was()
    {
        // The guarantee the whole shape exists for. Half-way through a long export the operation
        // fails — and the file the user had before must not have been touched. A naive
        // File.Create-then-write destroys it at the moment it opens the handle, before a single byte
        // of the new content is known to be good.
        using var dir = TempDir.Create();
        var path = dir.WriteFile("report.txt", "LAST GOOD REPORT");
        IFileDialogs dialogs = new PathOnlyDialogs(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dialogs.SaveAsync(null, async (stream, _) =>
            {
                await WriteText(stream, "partial garba");
                throw new InvalidOperationException("the encoder gave up");
            }));

        Assert.Equal("LAST GOOD REPORT", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_CANCELLED_write_leaves_the_user_s_previous_file_EXACTLY_as_it_was()
    {
        using var dir = TempDir.Create();
        var path = dir.WriteFile("report.txt", "LAST GOOD REPORT");
        IFileDialogs dialogs = new PathOnlyDialogs(path);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dialogs.SaveAsync(null, async (stream, token) =>
            {
                await WriteText(stream, "partial");
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            }, cts.Token));

        Assert.Equal("LAST GOOD REPORT", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_failed_write_leaves_NO_temp_file_behind_either()
    {
        // Discarding the temp is the other half of "the previous file survives": a save the user
        // retries must not accumulate debris beside their document, and a stray sibling is the kind of
        // thing that shows up in their folder rather than in a test.
        using var dir = TempDir.Create();
        var path = dir.WriteFile("report.txt", "LAST GOOD REPORT");
        IFileDialogs dialogs = new PathOnlyDialogs(path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dialogs.SaveAsync(null, (_, _) => throw new InvalidOperationException("nope")));

        Assert.Equal(["report.txt"], Directory.GetFiles(dir.Root).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task A_cancelled_PICK_never_runs_the_write_at_all()
    {
        var dialogs = new PathOnlyDialogs(null);
        var wrote = false;

        var result = await ((IFileDialogs)dialogs).SaveAsync(null, (_, _) =>
        {
            wrote = true;
            return Task.CompletedTask;
        });

        Assert.False(result.Success);
        Assert.Null(result.FilePath);
        Assert.False(wrote, "the content must not be produced when there is nowhere to put it");
        Assert.Equal(1, dialogs.SaveFileCalls);
    }

    [Fact]
    public async Task Cancelling_BEFORE_the_write_starts_does_not_touch_the_destination()
    {
        // The token is checked AFTER the pick on purpose: by then the user has chosen a destination,
        // and writing to it anyway would modify a file the caller has already given up on. The check
        // means an already-cancelled save costs the pick and nothing else.
        using var dir = TempDir.Create();
        var path = dir.WriteFile("report.txt", "LAST GOOD REPORT");
        IFileDialogs dialogs = new PathOnlyDialogs(path);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var wrote = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dialogs.SaveAsync(null, (_, _) => { wrote = true; return Task.CompletedTask; }, cts.Token));

        Assert.False(wrote);
        Assert.Equal("LAST GOOD REPORT", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task A_null_write_callback_IS_a_caller_bug_and_costs_no_dialog()
    {
        // Refused before the picker opens: showing a dialog and only then discovering the caller has
        // no content to write would put a modal in front of the user for nothing.
        var dialogs = new PathOnlyDialogs("ignored");

        await Assert.ThrowsAsync<ArgumentNullException>(() => ((IFileDialogs)dialogs).SaveAsync(null, null!));
        Assert.Equal(0, dialogs.SaveFileCalls);
    }

    [Fact]
    public async Task A_shell_with_no_addressable_destination_OVERRIDES_the_default()
    {
        // The reason this lives on the interface. A mobile shell has no path to hand back — the user
        // grants one document and the app writes into it while the grant is live — so it substitutes
        // its own implementation and reports success with NO path. Portable logic is unaffected, which
        // is the "universal interface, device-dependent implementation" split.
        var dialogs = new GrantOnlyDialogs();

        var result = await ((IFileDialogs)dialogs).SaveAsync(null, (stream, _) => WriteText(stream, "handed over"));

        Assert.True(result.Success);
        Assert.Null(result.FilePath);
        Assert.Equal("handed over", dialogs.Written);
        Assert.Equal(0, dialogs.SaveFileCalls);
    }

    /// <summary>A shell that can write into a one-time grant but cannot name a destination.</summary>
    private sealed class GrantOnlyDialogs : IFileDialogs
    {
        public string? Written { get; private set; }
        public int SaveFileCalls { get; private set; }

        public Task<FileDialogResult> OpenFileAsync(FileDialogOptions? options = null) =>
            throw new NotSupportedException();

        public Task<FileDialogResult> OpenFolderAsync(FileDialogOptions? options = null) =>
            throw new NotSupportedException();

        public Task<FileDialogResult> SaveFileAsync(FileDialogOptions? options = null)
        {
            SaveFileCalls++;
            throw new NotSupportedException("no addressable destination on this shell");
        }

        public async Task<FileDialogResult> SaveAsync(FileDialogOptions? options,
                                                     Func<Stream, CancellationToken, Task> write,
                                                     CancellationToken cancellationToken = default)
        {
            using var sink = new MemoryStream();
            await write(sink, cancellationToken);
            Written = System.Text.Encoding.UTF8.GetString(sink.ToArray());
            return new FileDialogResult { Success = true };
        }
    }
}
