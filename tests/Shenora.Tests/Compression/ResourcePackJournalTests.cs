using Shenora.Engine.Compression;

namespace Shenora.Tests.Compression;

/// <summary>
/// <see cref="ResourcePackJournal"/> — the boot decision and the confirm/rollback ordering.
/// <para>
/// 🔴 <b>Every case here is one an app gets WRONG SILENTLY.</b> The type exists because the correct
/// ordering has no visible failure mode: a pack retried for ever, a store release that never reaches the
/// UI, a promotion that happened without the page ever rendering — none of them throws, logs, or looks
/// different from working. So these assert the ORDER of effects, not just their end state.
/// </para>
/// </summary>
public class ResourcePackJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "shenora-packjournal-" + Guid.NewGuid().ToString("N")[..8]);

    public ResourcePackJournalTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Plain integer versions, so "newer" is unambiguous in the assertions below.</summary>
    private static int Numeric(string left, string right) =>
        int.Parse(left, System.Globalization.CultureInfo.InvariantCulture)
            .CompareTo(int.Parse(right, System.Globalization.CultureInfo.InvariantCulture));

    private string Path_ => System.IO.Path.Combine(_dir, "packs.json");

    /// <summary>A journal over the SAME file — a new instance is what the next app start really has.</summary>
    private ResourcePackJournal Journal(int maxAttempts = 1, Comparison<string>? order = null) =>
        new(new ResourcePackJournalOptions
        {
            Path = Path_,
            Order = order ?? Numeric,
            MaxAttempts = maxAttempts,
        });

    [Fact]
    public void With_nothing_staged_the_packaged_pack_is_served()
    {
        var choice = Journal().Open("100");

        Assert.Equal("100", choice.Version);
        Assert.Equal(ResourcePackKind.Packaged, choice.Kind);
        Assert.Null(choice.RolledBackFrom);
    }

    [Fact]
    public void A_staged_pack_is_served_once_and_only_becomes_trusted_when_it_CONFIRMS()
    {
        Journal().Stage("200");

        var first = Journal().Open("100");
        Assert.Equal("200", first.Version);
        Assert.Equal(ResourcePackKind.Pending, first.Kind);
        Assert.Equal(1, first.Attempt);

        // The page came up and said so.
        Journal().Confirm();

        // ⚠ ACTIVE, not pending — a promoted pack must not keep spending attempts, or it is rolled back
        // while working perfectly.
        var later = Journal().Open("100");
        Assert.Equal("200", later.Version);
        Assert.Equal(ResourcePackKind.Active, later.Kind);
        Assert.Equal(0, later.Attempt);
    }

    [Fact]
    public void A_pack_that_never_confirms_is_rolled_back_rather_than_retried_for_ever()
    {
        // 🔴 THE CASE THE ATTEMPT COUNT EXISTS FOR: a pack that kills the app before any of its code runs.
        // Nothing calls Confirm, so every start is a fresh journal over the same file.
        Journal().Stage("200");

        var served = Journal().Open("100");
        Assert.Equal("200", served.Version);

        var recovered = Journal().Open("100");
        Assert.Equal("100", recovered.Version);
        Assert.Equal(ResourcePackKind.Packaged, recovered.Kind);
        Assert.Equal("200", recovered.RolledBackFrom);

        // ...and it stays gone. A rollback that un-does itself on the next start is the same loop.
        var after = Journal().Open("100");
        Assert.Equal("100", after.Version);
        Assert.Null(after.RolledBackFrom);
    }

    [Fact]
    public void The_attempt_is_PERSISTED_BEFORE_the_pack_is_served()
    {
        // 🔴 THE ORDERING, ASSERTED DIRECTLY. Written the other way — serve, then count — a pack that
        // faults before the app can record anything leaves the count at zero and is retried for ever.
        // Reading the file back mid-flight is the only way to tell the two apart: the end state is
        // identical for a run that survived.
        Journal().Stage("200");

        var choice = Journal().Open("100");
        Assert.Equal(ResourcePackKind.Pending, choice.Kind);

        var onDisk = File.ReadAllText(Path_);
        Assert.Contains("\"Attempts\":1", onDisk, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxAttempts_is_honoured_before_the_rollback()
    {
        Journal(maxAttempts: 3).Stage("200");

        for (var i = 1; i <= 3; i++)
        {
            var attempt = Journal(maxAttempts: 3).Open("100");
            Assert.Equal("200", attempt.Version);
            Assert.Equal(i, attempt.Attempt);
        }

        var rolledBack = Journal(maxAttempts: 3).Open("100");
        Assert.Equal("100", rolledBack.Version);
        Assert.Equal("200", rolledBack.RolledBackFrom);
    }

    [Fact]
    public void A_NEWER_PACKAGED_pack_outranks_a_confirmed_staged_one()
    {
        // 🔴 THE DEFECT THIS TYPE EXISTS FOR. Without this, a device that has ever fetched a pack can
        // never be reached by a store release again: the staged one wins for ever and the app runs an old
        // page under a new binary.
        Journal().Stage("200");
        Journal().Open("100");
        Journal().Confirm();

        // The app ships again, with a client newer than anything on disk.
        var choice = Journal().Open("300");

        Assert.Equal("300", choice.Version);
        Assert.Equal(ResourcePackKind.Packaged, choice.Kind);
    }

    [Fact]
    public void A_superseded_staged_pack_is_not_re_chosen_on_the_next_start()
    {
        // The record has to be CLEARED, not merely out-voted: left in place it is re-compared every start,
        // and one wrong comparison puts it back in force.
        Journal().Stage("200");
        Journal().Open("100");
        Journal().Confirm();
        Journal().Open("300");

        // Back to an older packaged version — which cannot happen by upgrade, but CAN by a user installing
        // an older build. The staged pack must still be gone rather than resurrected.
        var next = Journal().Open("150");
        Assert.Equal("150", next.Version);
        Assert.Equal(ResourcePackKind.Packaged, next.Kind);
    }

    [Fact]
    public void An_OLDER_packaged_pack_leaves_the_staged_one_in_force()
    {
        // The other direction, and it is the ordinary case: the whole point of staging is that a fetched
        // pack outranks the one shipped with the app while it is genuinely newer.
        Journal().Stage("200");
        Journal().Open("100");
        Journal().Confirm();

        var choice = Journal().Open("100");
        Assert.Equal("200", choice.Version);
        Assert.Equal(ResourcePackKind.Active, choice.Kind);
    }

    [Fact]
    public void A_comparator_that_THROWS_falls_back_to_the_packaged_pack()
    {
        // ⚠ FAIL TOWARD THE PACK THAT ALWAYS EXISTS. A comparator that throws on some version string must
        // not leave a staged pack in force — that is the state an app cannot get out of.
        Journal().Stage("200");
        Journal().Open("100");
        Journal().Confirm();

        var journal = Journal(order: (_, _) => throw new FormatException("unparseable"));
        var choice = journal.Open("100");

        Assert.Equal("100", choice.Version);
        Assert.Equal(ResourcePackKind.Packaged, choice.Kind);
    }

    [Fact]
    public void An_UNREADABLE_record_starts_from_the_packaged_pack_rather_than_throwing()
    {
        File.WriteAllText(Path_, "{ this is not json");

        var choice = Journal().Open("100");
        Assert.Equal("100", choice.Version);
        Assert.Equal(ResourcePackKind.Packaged, choice.Kind);
    }

    [Fact]
    public void Confirm_without_anything_pending_does_nothing()
    {
        // It must not promote the PACKAGED version into the record — that would pin the version the app
        // shipped with as "staged", and the next real store release would then be compared against itself.
        Journal().Confirm();

        var choice = Journal().Open("100");
        Assert.Equal("100", choice.Version);
        Assert.Equal(ResourcePackKind.Packaged, choice.Kind);
    }

    [Fact]
    public void Reset_returns_to_the_packaged_pack()
    {
        Journal().Stage("200");
        Journal().Open("100");
        Journal().Confirm();

        Journal().Reset();

        var choice = Journal().Open("100");
        Assert.Equal("100", choice.Version);
        Assert.Equal(ResourcePackKind.Packaged, choice.Kind);
    }

    [Fact]
    public void Staging_a_second_pack_replaces_a_pending_one_and_resets_its_attempts()
    {
        Journal(maxAttempts: 2).Stage("200");
        Journal(maxAttempts: 2).Open("100");        // 200 has now been tried once

        Journal(maxAttempts: 2).Stage("300");

        var choice = Journal(maxAttempts: 2).Open("100");
        Assert.Equal("300", choice.Version);
        // ⚠ 1, not 2: the new pack gets its own budget. Inheriting the old count would roll back a pack
        // that had never been served.
        Assert.Equal(1, choice.Attempt);
    }
}
