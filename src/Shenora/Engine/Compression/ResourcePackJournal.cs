using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Shenora.Engine.Compression;

/// <summary>Where a <see cref="ResourcePackJournal"/> keeps its record, and how it decides.</summary>
public sealed class ResourcePackJournalOptions
{
    /// <summary>
    /// The file the record is written to. One per pack NAME — two packs sharing a path would overwrite
    /// each other's decision.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// Orders two versions: greater than zero when the FIRST is newer, as <see cref="Comparison{T}"/>.
    /// <para>
    /// 🔴 <b>REQUIRED, because the kit cannot supply it and must not guess.</b> A version is opaque here
    /// (<see cref="ResourcePack"/>) — semver, a build counter and a content hash order differently, and a
    /// content hash does not order at all. An app that cannot order its versions cannot answer "is the one
    /// I shipped newer than the one on disk?", which is the whole question.
    /// </para>
    /// </summary>
    public required Comparison<string> Order { get; init; }

    /// <summary>
    /// How many times a pending pack may be SERVED before it is rolled back. Default 1: served once, and
    /// if that run does not <see cref="ResourcePackJournal.Confirm"/>, it is discarded.
    /// <para>
    /// ⚠ Raising it means a pack that kills the app on start is served that many times before the app
    /// recovers on its own.
    /// </para>
    /// </summary>
    public int MaxAttempts { get; init; } = 1;
}

/// <summary>Where the version a <see cref="ResourcePackJournal"/> chose came from.</summary>
public enum ResourcePackKind
{
    /// <summary>The one built into the app. Always available, so this is the floor the others are measured against.</summary>
    Packaged,

    /// <summary>A staged version that has been confirmed by a previous run.</summary>
    Active,

    /// <summary>A staged version being tried for the first time. Unconfirmed — see <see cref="ResourcePackJournal.Confirm"/>.</summary>
    Pending,
}

/// <summary>
/// Which version to serve, and whether the last one worked.
/// </summary>
/// <param name="Version">The version to serve. Never null: with nothing staged it is the packaged one.</param>
/// <param name="Kind">Where it came from.</param>
/// <param name="Attempt">
/// How many times this PENDING version has now been served, this one included. Zero for anything already
/// trusted.
/// </param>
/// <param name="RolledBackFrom">
/// The version discarded on this call, when a pending pack ran out of attempts. Null otherwise — and worth
/// surfacing, because a rollback is otherwise completely silent to the user.
/// </param>
public sealed record ResourcePackResult(string Version, ResourcePackKind Kind, int Attempt,
                                        string? RolledBackFrom = null);

/// <summary>
/// The record of WHICH version of a <see cref="ResourcePack"/> is in force, and how the last attempt at a
/// new one went — so an app that fetches its own packs can serve a new one, find out whether it works, and
/// get back to a working one on its own.
///
/// <para>
/// 🔴 <b>IT DECIDES; IT DOES NOT DELIVER.</b> Where packs come from, what a version STRING means and how
/// two of them order all stay the app's (D42) — <see cref="ResourcePack.StageAsync"/> already takes bytes
/// the app fetched. This type answers only which LOCAL version is served next and whether the last one
/// confirmed itself. That boundary is why it is not a "store": it owns no bytes and downloads nothing.
/// </para>
///
/// <para>
/// 🔴 <b>THE DEFECT THIS EXISTS TO MAKE UNREPRESENTABLE.</b> An app that serves a fetched pack in
/// preference to its packaged one, without comparing versions, can never ship a fix through the app store
/// again: once any pack is on disk it outranks the client inside every later build, for ever, and the app
/// keeps running an old page under a new binary. It is silent, it is permanent, and it SELF-HEALS in
/// testing — while the app and its server ship together, a newer server simply replaces the stale pack, so
/// nothing looks wrong until the two can diverge. <see cref="Open"/> therefore <b>requires</b> the packaged
/// version as an argument: there is no overload that lets the comparison be skipped.
/// </para>
///
/// <para>
/// ⚠ <b>ONE ORDERING IS CORRECT AND EVERY OTHER FAILS SILENTLY</b>, which is the rest of why this is a
/// mechanism rather than advice. The attempt is counted and PERSISTED BEFORE the pending pack is served,
/// so a pack that crashes before running any of the app's code is still counted — written the other way,
/// the count stays at zero and the app retries the same broken pack for ever. Nothing is promoted without
/// a <see cref="Confirm"/> from the running page, and confirmation travels over the bridge, so a pack that
/// cannot talk to the host is one that cannot confirm.
/// </para>
///
/// <para>
/// ⚠ <b>It never deletes a directory.</b> Collection is <see cref="ResourcePack.PruneOthers"/>'s, on the
/// next start, for the reason that type gives: the version being replaced is usually still open.
/// </para>
/// </summary>
/// <remarks>
/// The record is a small JSON file, rewritten whole. ⚠ Not concurrency-safe across processes: the decision
/// belongs to app start, where there is one.
/// </remarks>
public sealed partial class ResourcePackJournal
{
    private readonly ResourcePackJournalOptions _options;
    private readonly ILogger? _log;

    /// <summary>
    /// The file <c>shenora copy</c> drops in a bundle it stages, carrying that bundle's version.
    /// <para>
    /// 🔴 <b>THE NAME AND THE SHAPE ARE AN AGREEMENT WITH THE CLI, not an implementation detail</b> —
    /// written in TypeScript at build time, read here in C# at boot, and a drift on either side is SILENT:
    /// the reader finds no stamp, the shell falls back to a hand-maintained constant, and the comparison
    /// <see cref="Open"/> forces becomes wrong while looking right. Pinned against the CLI's own source by
    /// <c>ResourcePackStampTests</c>, the same way the IPC wire is mirrored.
    /// </para>
    /// </summary>
    public const string StampFileName = ".shenora-pack.json";

    /// <summary>
    /// The version of the packaged bundle in <paramref name="bundleDirectory"/>, ready to hand to
    /// <see cref="Open"/> — or null when it carries no stamp, which an unstamped build, an older CLI and a
    /// hand-assembled bundle all look like.
    /// <para>
    /// ⚠ <b>Null is a real answer, not an error.</b> <c>shenora copy</c> deliberately writes NO stamp when
    /// the web app declares no version, because an invented one compares as a real one. And null must stay
    /// distinguishable from <c>""</c>: <see cref="Open"/> REFUSES a blank, so collapsing the two would turn
    /// "no version yet" into an exception at boot instead of a decision the app can still make.
    /// </para>
    /// </summary>
    public static string? PackagedVersionIn(string bundleDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        try
        {
            var path = System.IO.Path.Combine(bundleDirectory, StampFileName);
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                   && version.ValueKind is JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(version.GetString())
                ? version.GetString()
                : null;
        }
        catch (Exception)
        {
            // A stamp that cannot be read is the same as none: the app still starts, on the packaged bundle.
            return null;
        }
    }

    /// <param name="options">Where the record lives and how versions order.</param>
    /// <param name="log">Where a rollback is reported. Null keeps it silent.</param>
    public ResourcePackJournal(ResourcePackJournalOptions options, ILogger? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Path);
        ArgumentNullException.ThrowIfNull(options.Order);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        _log = log;
    }

    /// <summary>
    /// Record a freshly staged version as the one to try next. Call it AFTER
    /// <see cref="ResourcePack.StageAsync"/> has reported the pack ready — a version recorded here that is
    /// not on disk is served as a directory of nothing.
    /// </summary>
    /// <param name="version">The staged version.</param>
    public void Stage(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        var state = Read();
        state.Pending = version;
        state.Attempts = 0;
        Write(state);
    }

    /// <summary>
    /// Decide what to serve now, counting the attempt if it is a pending pack.
    /// <para>
    /// 🔴 <b>Call this ONCE per app start, before the webview navigates</b>, and serve what it returns. Its
    /// side effects are the point: it spends an attempt, and it performs the rollback when one runs out.
    /// </para>
    /// </summary>
    /// <param name="packagedVersion">
    /// The version of the pack built INTO this app build. 🔴 Required, and the app must bake it from the
    /// same source the pack itself was built from — a constant that drifts from the packaged bytes makes
    /// every comparison below wrong while looking right.
    /// <para>
    /// ⚠ For a web client staged by <c>shenora copy</c>, that number is already in the bundle: the CLI
    /// writes <c>.shenora-pack.json</c> (<c>{"version":"…"}</c>) carrying the web app's own declared
    /// version. Reading it beats a second constant, which is the drift this parameter warns about.
    /// </para>
    /// </param>
    public ResourcePackResult Open(string packagedVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagedVersion);
        var state = Read();
        string? rolledBackFrom = null;

        if (state.Pending is { } pending)
        {
            if (state.Attempts < _options.MaxAttempts)
            {
                // 🔴 PERSIST FIRST, SERVE SECOND. A pack that faults before any of the app's code runs must
                // still have cost an attempt, or this is an infinite retry of the same broken pack.
                state.Attempts++;
                Write(state);
                return new ResourcePackResult(pending, ResourcePackKind.Pending, state.Attempts);
            }

            // Out of attempts: it was served and never confirmed, so it does not get another chance.
            rolledBackFrom = pending;
            state.Pending = null;
            state.Attempts = 0;
            Write(state);
            _log?.LogWarning("Resource pack {Version} was served {Attempts} time(s) without confirming, so it "
                           + "has been rolled back. The app is running the previous pack.",
                             pending, _options.MaxAttempts);
        }

        // 🔴 THE COMPARISON THE WHOLE TYPE EXISTS FOR. A confirmed pack still loses to a packaged one that
        // is newer — which is what a store release IS — and the record is cleared so it cannot be re-chosen.
        if (state.Active is { } active)
        {
            if (Newer(packagedVersion, active))
            {
                state.Active = null;
                Write(state);
                _log?.LogInformation("The packaged resource pack {Packaged} is newer than the staged {Active}, "
                                   + "so the staged one has been dropped.", packagedVersion, active);
                return new ResourcePackResult(packagedVersion, ResourcePackKind.Packaged, 0, rolledBackFrom);
            }
            return new ResourcePackResult(active, ResourcePackKind.Active, 0, rolledBackFrom);
        }

        return new ResourcePackResult(packagedVersion, ResourcePackKind.Packaged, 0, rolledBackFrom);
    }

    /// <summary>
    /// The pack served by <see cref="Open"/> works — promote it, so later starts take it without spending
    /// an attempt. A no-op when nothing is pending.
    /// <para>
    /// 🔴 <b>Call this from the RUNNING PAGE, not from app start.</b> The whole guarantee is that a pack
    /// which cannot run cannot confirm; confirming on the host's own timer would promote a pack that never
    /// rendered.
    /// </para>
    /// </summary>
    public void Confirm()
    {
        var state = Read();
        if (state.Pending is not { } pending) return;
        state.Active = pending;
        state.Pending = null;
        state.Attempts = 0;
        Write(state);
    }

    /// <summary>
    /// Forget every staged version, so the next <see cref="Open"/> returns the packaged one. For an app
    /// offering its user a way out; the directories are <see cref="ResourcePack.PruneOthers"/>'s to collect.
    /// </summary>
    public void Reset() => Write(new Record());

    private bool Newer(string left, string right)
    {
        try
        {
            return _options.Order(left, right) > 0;
        }
        catch (Exception ex)
        {
            // ⚠ FAIL TOWARD THE PACKAGED PACK. A comparator that throws must not leave a staged pack in
            // force, because that is the state nothing can get out of.
            _log?.LogWarning(ex, "Comparing resource pack versions '{Left}' and '{Right}' threw, so the "
                               + "packaged pack is being preferred.", left, right);
            return true;
        }
    }

    private Record Read()
    {
        try
        {
            if (!File.Exists(_options.Path)) return new Record();
            return JsonSerializer.Deserialize(File.ReadAllText(_options.Path), RecordContext.Default.Record)
                   ?? new Record();
        }
        catch (Exception ex)
        {
            // An unreadable record is the same as none: the packaged pack is always serveable, so the app
            // starts rather than failing on its own bookkeeping.
            _log?.LogWarning(ex, "The resource pack record at {Path} could not be read; starting from the "
                               + "packaged pack.", _options.Path);
            return new Record();
        }
    }

    private void Write(Record state)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(_options.Path);
            if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
            File.WriteAllText(_options.Path, JsonSerializer.Serialize(state, RecordContext.Default.Record));
        }
        catch (Exception ex)
        {
            // 🔴 A record that cannot be written is a REAL failure and is said out loud: the attempt count
            // is what stops a broken pack looping, so losing it silently is the defect this type prevents.
            _log?.LogError(ex, "The resource pack record at {Path} could not be written. A pending pack may "
                             + "be retried more often than {MaxAttempts}.", _options.Path, _options.MaxAttempts);
        }
    }

    /// <summary>The persisted shape. Internal: the FILE is the contract, and it is this type's alone.</summary>
    internal sealed class Record
    {
        public string? Active { get; set; }
        public string? Pending { get; set; }
        public int Attempts { get; set; }
    }

    [JsonSerializable(typeof(Record))]
    [JsonSourceGenerationOptions(WriteIndented = false)]
    private sealed partial class RecordContext : JsonSerializerContext;
}
