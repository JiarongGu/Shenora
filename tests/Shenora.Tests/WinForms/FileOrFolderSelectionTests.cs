using Shenora.Windows;

namespace Shenora.Tests.WinForms;

/// <summary>
/// The one decision inside <c>OpenFolderAsync(AllowFileSelection: true)</c>: whether what came back is a
/// real path or the placeholder standing in for "the folder I am looking at".
///
/// <para>
/// Windows has no "file OR folder" dialog mode — the Common Item Dialog does folders
/// (<c>FOS_PICKFOLDERS</c>) or files, never both — so the kit types a fake name into an
/// <c>OpenFileDialog</c> and reads it back. That makes the READ-BACK the fragile part, and until
/// 2026-08-05 it was reachable only by opening a real dialog, which is why the defect below shipped.
/// </para>
/// </summary>
public class FileOrFolderSelectionTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shenora-fof-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_placeholder_means_the_folder_it_sits_in()
    {
        // The whole reason the placeholder exists: the user navigated somewhere and pressed Open without
        // choosing a file, so the fake name comes back appended to the directory they were browsing.
        var dir = NewTempDir();
        try
        {
            var resolved = FileDialogs.ResolveFileOrFolderSelection(
                Path.Combine(dir, FileDialogs.FolderPlaceholder));

            Assert.Equal(dir, resolved);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_REAL_file_named_like_the_placeholder_wins_over_it()
    {
        // THE DEFECT (2026-08-05). The old code tested the NAME first, including
        // GetFileNameWithoutExtension, so picking this file returned its directory instead — a wrong
        // ANSWER, silently, not a refusal.
        var dir = NewTempDir();
        var file = Path.Combine(dir, FileDialogs.FolderPlaceholder + ".txt");
        try
        {
            File.WriteAllText(file, "real");

            Assert.Equal(file, FileDialogs.ResolveFileOrFolderSelection(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_REAL_extensionless_file_named_EXACTLY_the_placeholder_also_wins()
    {
        // The other half of the same bug: `GetFileName(selected) == placeholder` matched this one, and it
        // is the more exact collision of the two.
        var dir = NewTempDir();
        var file = Path.Combine(dir, FileDialogs.FolderPlaceholder);
        try
        {
            File.WriteAllText(file, "real");

            Assert.Equal(file, FileDialogs.ResolveFileOrFolderSelection(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void An_ordinary_picked_file_is_returned_untouched()
    {
        // The path that must stay QUIET — the common case, and the one a fix like this can break.
        var dir = NewTempDir();
        var file = Path.Combine(dir, "notes.txt");
        try
        {
            File.WriteAllText(file, "hello");

            Assert.Equal(file, FileDialogs.ResolveFileOrFolderSelection(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_name_that_exists_as_NEITHER_is_returned_as_is_for_the_caller_to_reject()
    {
        // The user typed something that is not there. This function does not decide that — the caller's
        // exists-check does, and turns it into Cancelled. Pinned so the responsibility does not migrate
        // here later and start returning a directory for a typo.
        var dir = NewTempDir();
        var missing = Path.Combine(dir, "does-not-exist.txt");
        try
        {
            Assert.Equal(missing, FileDialogs.ResolveFileOrFolderSelection(missing));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
