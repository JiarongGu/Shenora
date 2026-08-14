using System.Text.Json;
using System.Text.Json.Serialization;
using Shenora.Engine.Files;

using Shenora;

namespace Shenora.Modules.Update;

/// <summary>
/// One tracked file in an <see cref="UpdateManifest"/>: where it lives, how big it is, and what it
/// hashes to. The triple two sibling apps arrived at independently
/// (<c>docs/DECISIONS.md</c> D57), which is why it is this and not more.
/// </summary>
public sealed class ManifestFile
{
    /// <summary>
    /// Path RELATIVE to the install root, with forward slashes (<c>libs/app.dll</c>). Comparisons
    /// normalize separators and ignore case, so a manifest written on one platform still diffs
    /// against one written on another — a mismatch there does not fail loudly, it silently
    /// redownloads a file forever or misses a removal.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Size in bytes. Used for the changeset's download total, never for equality.</summary>
    public required long Size { get; init; }

    /// <summary>
    /// SHA-256 as hex. Case-insensitive on comparison because generators disagree about casing, and
    /// a diff that treats <c>ABC…</c> and <c>abc…</c> as different would report every file changed.
    /// </summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// The list of auto-updatable files a release ships, written beside the payload and installed with
/// it. Diffing the INSTALLED copy against a RELEASE copy is what produces a changeset
/// (<see cref="ManifestDiff.Compute"/>), so only changed files are downloaded.
/// <para>
/// The kit ships the contract and the diff, not a downloader and not a release source — where
/// manifests come from is the app's (`generic-library.md`: ship the mechanism, never the product).
/// </para>
/// </summary>
public sealed class UpdateManifest
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>The version this manifest describes (the app's own string; the kit does not parse it).</summary>
    public required string Version { get; init; }

    /// <summary>When it was generated. Diagnostic only — never used to decide staleness.</summary>
    public DateTimeOffset? GeneratedAt { get; init; }

    /// <summary>The tracked files. A path appearing twice is malformed and throws at diff time.</summary>
    public required IReadOnlyList<ManifestFile> Files { get; init; }

    /// <summary>
    /// Read a manifest. Throws <see cref="JsonException"/> on malformed input — which <b>includes a
    /// file path that could resolve outside the install root</b> (see
    /// <see cref="ManifestDiff.IsSafeRelativePath"/>): such a path is not a stylistic problem, it is a
    /// manifest that cannot be applied safely, so it is malformed in the only sense this type cares about.
    /// <para>
    /// ⚠ Refused HERE as well as at diff time, and the difference matters: a poisoned BASELINE (the
    /// installed <c>manifest.json</c>, written by whatever applied the last update) must take
    /// <see cref="UpdateStage.ApplyAsync"/>'s existing <i>"no usable installed manifest — applying without
    /// removals"</i> branch rather than aborting the update. Failing at parse puts it there for free;
    /// failing only at <see cref="ManifestDiff.Compute"/> would throw past that guard and leave an app
    /// permanently unable to update. <c>Shenora.Launcher</c>'s <c>parse_manifest</c> refuses at the same
    /// point, for the same reason.
    /// </para>
    /// </summary>
    public static UpdateManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json)
            ?? throw new JsonException("The manifest parsed to null.");

        foreach (var file in manifest.Files)
        {
            if (!ManifestDiff.IsSafeRelativePath(file.Path))
            {
                throw new JsonException(
                    $"The manifest lists '{file.Path}', which is not a contained relative path. A manifest " +
                    "path must be relative to the install root and must not contain a '..' segment.");
            }
        }

        return manifest;
    }

    /// <summary>Write a manifest, in the shape <see cref="Parse"/> reads.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// What changed between the installed manifest and a release one: the changeset an updater
/// downloads and an applier lands. A pure function over two lists — the single most testable piece
/// of the update story, and the one both sibling apps hand-rolled TWICE (once in C#, once again in
/// their native applier) because the two phases are in different languages.
/// </summary>
public sealed class ManifestDiff
{
    private ManifestDiff(IReadOnlyList<ManifestFile> added, IReadOnlyList<ManifestFile> updated,
                         IReadOnlyList<string> removed)
    {
        Added = added;
        Updated = updated;
        Removed = removed;
    }

    /// <summary>In the release, not installed — download and write.</summary>
    public IReadOnlyList<ManifestFile> Added { get; }

    /// <summary>In both, with a different hash — download and replace.</summary>
    public IReadOnlyList<ManifestFile> Updated { get; }

    /// <summary>
    /// Installed but not in the release — delete. **Tracked paths only, never a directory sweep:**
    /// user data lives in the same tree, and a manifest is the only thing that knows which files the
    /// app owns.
    /// </summary>
    public IReadOnlyList<string> Removed { get; }

    /// <summary>Bytes to fetch: the sizes of <see cref="Added"/> + <see cref="Updated"/>.</summary>
    public long DownloadBytes => Added.Sum(f => f.Size) + Updated.Sum(f => f.Size);

    /// <summary>Nothing to do — already up to date.</summary>
    public bool IsEmpty => Added.Count == 0 && Updated.Count == 0 && Removed.Count == 0;

    /// <summary>
    /// Compare an installed manifest with a release one.
    /// <para>
    /// ⚠ <b>A release manifest that failed to load must never reach this method.</b> An EMPTY release
    /// legitimately means "every installed file is removed", so handing in a manifest that parsed to
    /// nothing produces a changeset that deletes the whole install — and it would do so as the
    /// SUCCESSFUL outcome of a copy. One sibling carries exactly that guard in its applier and the
    /// other does not, which is why it is stated here rather than assumed. Validate the release
    /// manifest before calling, not after.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">Either manifest lists the same path twice.</exception>
    public static ManifestDiff Compute(UpdateManifest installed, UpdateManifest release)
    {
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(release);

        var installedByPath = Index(installed, nameof(installed));
        var releaseByPath = Index(release, nameof(release));

        var added = new List<ManifestFile>();
        var updated = new List<ManifestFile>();
        foreach (var (path, file) in releaseByPath)
        {
            if (!installedByPath.TryGetValue(path, out var current)) added.Add(file);
            else if (!string.Equals(current.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) updated.Add(file);
        }

        var removed = installedByPath.Keys.Where(path => !releaseByPath.ContainsKey(path)).ToList();

        // Ordered so a changeset is reviewable and two runs over the same inputs are identical — a
        // dictionary's enumeration order is not a contract, and this is shown to users.
        added.Sort(ByPath);
        updated.Sort(ByPath);
        removed.Sort(StringComparer.Ordinal);
        return new ManifestDiff(added, updated, removed);
    }

    private static int ByPath(ManifestFile a, ManifestFile b) =>
        string.Compare(Normalize(a.Path), Normalize(b.Path), StringComparison.Ordinal);

    /// <summary>
    /// Forward slashes, lower-cased. Manifests are written by whatever packaged the release and read
    /// by whatever is applying it, so `libs\app.dll` and `libs/app.dll` must be the same entry —
    /// otherwise a file is "added" on every single check and never converges.
    /// <para>
    /// <c>internal</c> rather than private because <see cref="UpdateStage"/>'s intrusion check compares
    /// disk paths against manifest paths and MUST use the same rule. A second copy of it would be a
    /// rule that can drift, and these comparison rules are sabotage-verified in one place.
    /// </para>
    /// </summary>
    internal static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    /// <summary>
    /// Whether <paramref name="path"/> can only ever resolve INSIDE the tree it is combined with.
    /// <para>
    /// 🔴 <b>The manifest is the only input in this kit that arrives from a REMOTE server, and it drives
    /// both <c>File.Create</c> and <c>File.Delete</c>.</b> Two shapes escape a root, and neither fails
    /// loudly:
    /// </para>
    /// <list type="number">
    /// <item>A ROOTED path. <see cref="System.IO.Path.Combine(string, string)"/> SILENTLY DISCARDS its
    /// first argument when the second is rooted — the quirk
    /// <see cref="UpdateStage.ResolveBaselinePath"/> already names as <i>"the exact behaviour this repo
    /// already had to fix a security bug over"</i> — and C++'s <c>std::filesystem::operator/</c> does the
    /// identical thing, which is why <c>Shenora.Launcher</c>'s <c>parse_manifest</c> carries the same
    /// rejection.</item>
    /// <item>A <c>..</c> segment, which walks out of the root the ordinary way.</item>
    /// </list>
    /// <para>
    /// ⚠ <b>Refused at the MANIFEST, not at each call site.</b> Hash verification checks a file's CONTENT
    /// and never its PATH, and the stage's intrusion check walks the staged directory — so a file written
    /// outside it is not in the walk, and is then looked for at the same escaped location and found. Both
    /// gates pass. The path is the only thing that can catch this, and it has one owner so a fourth
    /// consumer cannot forget it.
    /// </para>
    /// <para>
    /// <c>internal</c> for the same reason as <see cref="Normalize"/>: <see cref="UpdateStage"/> resolves
    /// manifest paths against a root and MUST refuse on the same rule, and a second copy is a rule that
    /// can drift.
    /// </para>
    /// </summary>
    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Platform-correct on purpose: `IsPathRooted` knows about `C:\x`, `C:x` and `\x` on Windows and
        // about `/x` everywhere. The applier runs on the machine that will use the path, so its answer
        // is the one that matters.
        if (System.IO.Path.IsPathRooted(path)) return false;

        // Both separators, because a manifest written on one platform is applied on another — the same
        // reason `Normalize` exists.
        foreach (var segment in path.Split('/', '\\'))
        {
            if (segment == "..") return false;
        }

        return true;
    }

    private static Dictionary<string, ManifestFile> Index(UpdateManifest manifest, string name)
    {
        var byPath = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
            if (!IsSafeRelativePath(file.Path))
            {
                // Loud and total, not a skipped entry: a manifest carrying an escaping path is not a
                // manifest with one bad row, it is one whose author is not who the applier thinks it is.
                // Refusing the whole diff means nothing is written and nothing is deleted.
                throw new ArgumentException(
                    $"The {name} manifest lists '{file.Path}', which is not a contained relative path. " +
                    "A manifest path must be relative to the install root and must not contain a '..' " +
                    "segment — anything else can resolve outside the tree being updated.", name);
            }

            if (!byPath.TryAdd(Normalize(file.Path), file))
            {
                // Loud, because the alternative is silent: last-wins would make the changeset depend
                // on list order, and a duplicate path in a manifest means whatever generated it is
                // broken in a way that will not fix itself.
                throw new ArgumentException(
                    $"The {name} manifest lists '{file.Path}' more than once (paths are compared " +
                    "case-insensitively with separators normalized).", name);
            }
        }
        return byPath;
    }
}
