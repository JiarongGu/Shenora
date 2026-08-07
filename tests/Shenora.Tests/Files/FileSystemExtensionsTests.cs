using Microsoft.Extensions.DependencyInjection;
using Shenora.Core;
using Shenora.IO;

namespace Shenora.Tests.Io;

/// <summary>
/// <see cref="FileSystemExtensions.UseFileSystem"/> — the file engine's half of the "one call" treatment
/// the media player got. What matters is that the ZERO-ARGUMENT call produces a working queue, because
/// that is the whole claim.
/// </summary>
public class FileSystemExtensionsTests
{
    [Fact]
    public void One_call_produces_a_usable_queue()
    {
        using var root = new TempRoot();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Path },
        });

        builder.UseFileSystem();
        using var app = builder.Build();

        Assert.NotNull(app.Services.GetService<IFileUpdateQueue>());
    }

    /// <summary>
    /// The journal and the locks are defaulted because they are the app's OWN storage — choosing them
    /// changes nothing the app is exposed to. ⚠ Contrast <c>MediaPlayerOptions.AllowedRoots</c>, which the
    /// kit refuses to default because it IS a containment boundary.
    /// </summary>
    [Fact]
    public void Journal_and_locks_are_defaulted_under_the_app_data_directory()
    {
        using var root = new TempRoot();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Path },
        });

        builder.UseFileSystem();
        using var app = builder.Build();
        var options = app.Services.GetRequiredService<FileUpdateQueueOptions>();

        // 🔴 NOT YET — and this half is the point. The journal and locker are built when the QUEUE is
        // resolved, never at `Use…` time, because `Paths.DataArea` creates the directory it names: an
        // engine that provisions storage merely by being registered cannot be on by default (D64).
        Assert.Null(options.Journal);
        Assert.Null(options.Locker);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "journal")),
            "registration alone must not create the journal directory");

        // Asking for the engine is what provisions it.
        _ = app.Services.GetRequiredService<IFileUpdateQueue>();

        Assert.NotNull(options.Journal);
        Assert.NotNull(options.Locker);
    }

    /// <summary>An explicit value survives — defaults are applied AFTER `configure`, never over it.</summary>
    [Fact]
    public void An_explicit_setting_is_not_overwritten_by_a_default()
    {
        using var root = new TempRoot();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Path },
        });

        builder.UseFileSystem(x => x.LeaseTimeout = TimeSpan.FromMinutes(7));
        using var app = builder.Build();

        Assert.Equal(TimeSpan.FromMinutes(7), app.Services.GetRequiredService<FileUpdateQueueOptions>().LeaseTimeout);
    }

    /// <summary>
    /// 🔴 A shell-registered per-platform piece must actually be CONSULTED. <c>IFileLockInspector</c> is
    /// the file system's equivalent of media's <c>IMediaCapability</c>: the engine is portable, the answer
    /// is not — "who holds this file open?" is Restart Manager on Windows and something else elsewhere.
    /// <para>
    /// ⚠ This repo has paid twice for registering something nothing asks for — D59's audio conversion, and
    /// <c>RestartManagerLockInspector</c>, which shipped, was documented and was tested, yet no container
    /// ever built one. Both were INVISIBLE: an empty <c>WhoHolds</c> legitimately means "cannot tell", so
    /// the degraded answer was indistinguishable from the honest one.
    /// </para>
    /// </summary>
    [Fact]
    public void A_registered_lock_inspector_reaches_the_queue()
    {
        using var root = new TempRoot();
        var builder = ShenoraApplication.CreateBuilder(new ShenoraApplicationOptions
        {
            ApplicationName = "probe",
            Paths = new ShenoraPathsOptions { ExplicitRoot = root.Path },
        });

        var inspector = new StubInspector();
        builder.Services.AddSingleton<IFileLockInspector>(inspector);
        builder.UseFileSystem();
        using var app = builder.Build();

        _ = app.Services.GetRequiredService<IFileUpdateQueue>();   // building the queue is what wires it

        Assert.Same(inspector, app.Services.GetRequiredService<FileUpdateQueueOptions>().LockInspector);
    }

    private sealed class StubInspector : IFileLockInspector
    {
        public IReadOnlyList<FileLockHolder> WhoHolds(string path) => [];
    }

    private sealed class TempRoot : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"shenora-fs-{Guid.NewGuid():N}");

        public TempRoot() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
        }
    }
}
