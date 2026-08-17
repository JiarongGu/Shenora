using Shenora.Engine.Files;
using Shenora.Core.Shell;

namespace Shenora.Tests.Io;

/// <summary>
/// What an update reports when a cross-process lease is REFUSED.
///
/// <para>
/// 🔴 The point of <see cref="FileUpdateResult.Holders"/> is to turn "the process cannot access the file"
/// into something an app can act on or show a user. That only works if the queue asks about the path that
/// actually refused: naming the first path in the set instead puts the wrong filename in the error and
/// sends the lock inspector after a file nobody is holding, so the holders come back empty and the
/// diagnostic reports nothing while looking like it worked.
/// </para>
/// </summary>
public class FileUpdateLeaseReportingTests
{
    /// <summary>Grants every lease except one named path.</summary>
    private sealed class SelectiveLocker(string refuses) : IPathLocker
    {
        public Task<IPathLease?> TryAcquireAsync(string path, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IPathLease?>(
                string.Equals(path, PathClaims.Canonical(refuses), StringComparison.Ordinal)
                    ? null
                    : new Lease(path));

        private sealed class Lease(string path) : IPathLease
        {
            public string Path => path;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Records which path it was asked about, and answers for exactly one.</summary>
    private sealed class RecordingInspector(string holds) : IFileLockInspector
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyList<FileLockHolder> WhoHolds(string path)
        {
            Asked.Add(path);
            return string.Equals(path, PathClaims.Canonical(holds), StringComparison.Ordinal)
                ? [new FileLockHolder(4242, "other-app")]
                : [];
        }
    }

    /// <summary>
    /// The contested path is deliberately NOT the first one the queue leases: paths are leased in sorted
    /// order, so "b.dat" is reached second and a first-path report would name "a.dat".
    /// </summary>
    [Fact]
    public async Task A_refused_lease_names_the_path_that_refused_and_inspects_THAT_path()
    {
        var root = Path.Combine(Path.GetTempPath(), "shenora-lease-report");
        var first = Path.Combine(root, "a.dat");
        var contested = Path.Combine(root, "b.dat");

        var inspector = new RecordingInspector(contested);
        var queue = new FileUpdateQueue(new FileUpdateQueueOptions
        {
            Locker = new SelectiveLocker(contested),
            LockInspector = inspector,
            LeaseTimeout = TimeSpan.Zero,
        });

        var result = await queue.ApplyAsync(new FileUpdate
        {
            Changes =
            [
                new FileChange.Delete(first),
                new FileChange.Delete(contested),
            ],
        });

        Assert.NotNull(result.Error);
        Assert.Contains("b.dat", result.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a.dat", result.Error.Message, StringComparison.Ordinal);

        // The inspector must have been asked about the contested path — and therefore have something to say.
        Assert.Equal([PathClaims.Canonical(contested)], inspector.Asked);
        Assert.Equal(4242, Assert.Single(result.Holders).ProcessId);
    }
}
