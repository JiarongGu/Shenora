namespace Shenora.Tests.TestSupport;

/// <summary>
/// A disposable temporary directory — the ONE owner of the create/delete pair that had been written
/// seven times across the suite (P5.5 H7).
/// <para>
/// Cleanup is BEST-EFFORT on purpose, and that is the reason to share it rather than a matter of
/// taste: four of the copies called a bare <c>Directory.Delete(dir, recursive: true)</c> inside
/// <c>finally</c>, so a file still held open (a provider stream, a virus scanner, an
/// <c>IOException</c> on a loaded box) threw FROM the finally and REPLACED the test's real failure
/// with an unrelated IO error. A leaked temp directory is a smaller problem than a misreported test.
/// </para>
/// </summary>
internal sealed class TempDir : IDisposable
{
    private TempDir(string root) => Root = root;

    /// <summary>The directory's absolute path.</summary>
    public string Root { get; }

    /// <summary>
    /// Create a uniquely-named temp directory, plus any <paramref name="subdirectories"/> inside it.
    /// </summary>
    public static TempDir Create(params string[] subdirectories)
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-tests-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        foreach (var sub in subdirectories) Directory.CreateDirectory(Path.Combine(root, sub));
        return new TempDir(root);
    }

    /// <summary>Path of <paramref name="parts"/> resolved under <see cref="Root"/>.</summary>
    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    /// <summary>Write <paramref name="content"/> to <paramref name="relativePath"/>, creating parents.</summary>
    public string WriteFile(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* best-effort — see the type doc: never mask the test's own failure */ }
    }
}
