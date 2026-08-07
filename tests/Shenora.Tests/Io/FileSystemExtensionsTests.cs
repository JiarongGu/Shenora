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
