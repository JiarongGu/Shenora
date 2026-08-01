using Shenora.Core;

namespace Shenora.Tests.Work;

/// <summary>
/// <see cref="PathClaims"/> — the bridge from filesystem paths to scheduler claims, and the
/// containment guard. Containment is on the review guide's list of latent-defect classes ("path/
/// containment checks on anything that maps a request to a file"), so its traps are tested by name
/// rather than by example.
/// </summary>
public class PathClaimsTests
{
    private static string Root => OperatingSystem.IsWindows() ? @"C:\data" : "/data";

    private static string Under(params string[] parts) =>
        Path.Combine(new[] { Root }.Concat(parts).ToArray());

    [Fact]
    public void Two_spellings_of_one_path_produce_the_same_claim()
    {
        // The soundness precondition: if these differed, two mutations of one directory would run
        // concurrently because the scheduler saw unrelated keys.
        var direct = PathClaims.Exclusive(Under("mods", "x"));
        var indirect = PathClaims.Exclusive(Under("mods", "sub", "..", "x"));

        Assert.Equal(direct.Key, indirect.Key);
    }

    [Fact]
    public void An_ancestor_conflicts_with_a_descendant()
    {
        var scope = PathClaims.Scope;
        var parent = scope.Normalize(PathClaims.Exclusive(Under("mods")).Key);
        var child = scope.Normalize(PathClaims.Exclusive(Under("mods", "x", "file.txt")).Key);

        Assert.True(scope.Conflicts(parent, child));
        Assert.True(scope.Conflicts(child, parent));   // symmetry
    }

    [Fact]
    public void A_sibling_sharing_a_name_prefix_does_NOT_conflict()
    {
        var scope = PathClaims.Scope;
        var a = scope.Normalize(PathClaims.Exclusive(Under("mods")).Key);
        var b = scope.Normalize(PathClaims.Exclusive(Under("mods-backup")).Key);

        Assert.False(scope.Conflicts(a, b));
    }

    [Fact]
    public void Containment_accepts_the_root_and_its_descendants()
    {
        Assert.True(PathClaims.IsContained(Root, Root));
        Assert.True(PathClaims.IsContained(Root, Under("a")));
        Assert.True(PathClaims.IsContained(Root, Under("a", "b", "c.txt")));
    }

    [Fact]
    public void Containment_rejects_a_traversal_escape()
    {
        // Trap 1: `..` walks out. Resolved before comparison, so the escape is visible.
        Assert.False(PathClaims.IsContained(Root, Under("..", "elsewhere", "secret.txt")));
        Assert.False(PathClaims.IsContained(Root, Under("a", "..", "..", "secret.txt")));
    }

    [Fact]
    public void Containment_rejects_a_sibling_that_merely_shares_a_prefix()
    {
        // Trap 2: a naive StartsWith puts "C:\data-old" inside "C:\data".
        var sibling = Root + "-old";
        Assert.False(PathClaims.IsContained(Root, sibling));
        Assert.False(PathClaims.IsContained(Root, Path.Combine(sibling, "file.txt")));
    }

    [Fact]
    public void Containment_ignores_a_trailing_separator_on_either_side()
    {
        Assert.True(PathClaims.IsContained(Root + Path.DirectorySeparatorChar, Under("a")));
        Assert.True(PathClaims.IsContained(Root, Under("a") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void A_shared_claim_reads_and_an_exclusive_claim_writes()
    {
        Assert.Equal(ClaimMode.Shared, PathClaims.Shared(Under("a")).Mode);
        Assert.Equal(ClaimMode.Exclusive, PathClaims.Exclusive(Under("a")).Mode);
        Assert.Equal(PathClaims.ScopeName, PathClaims.Shared(Under("a")).Scope);
    }

    [Fact]
    public async Task Registered_on_a_scheduler_it_serializes_overlapping_paths()
    {
        // The end-to-end shape an adopting app writes: this is the family's file-operation planner.
        await using var scheduler = new MissionScheduler(new MissionSchedulerOptions
        {
            DefaultLaneCapacity = 4,
            Scopes = [PathClaims.Scope],
        });

        var gate = new object();
        var active = 0;
        var peak = 0;

        async Task Touch(CancellationToken ct)
        {
            lock (gate) { active++; peak = Math.Max(peak, active); }
            await Task.Delay(50, ct);
            lock (gate) active--;
        }

        var directory = scheduler.SubmitAsync(new MissionRequest
        {
            Run = c => Touch(c.Cancellation),
            Claims = [PathClaims.Exclusive(Under("mods"))],
        });
        var fileInside = scheduler.SubmitAsync(new MissionRequest
        {
            Run = c => Touch(c.Cancellation),
            Claims = [PathClaims.Exclusive(Under("mods", "x", "f.txt"))],
        });

        await Task.WhenAll(directory, fileInside);

        Assert.Equal(1, peak);
    }
}
