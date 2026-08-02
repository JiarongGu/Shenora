using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shenora.Core;

/// <summary>
/// One tracked file in an <see cref="UpdateManifest"/>: where it lives, how big it is, and what it
/// hashes to. The triple two sibling apps arrived at independently
/// (<c>docs/2026-08-02-shenora-app-update-design.md</c> §0), which is why it is this and not more.
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

    /// <summary>Read a manifest. Throws <see cref="JsonException"/> on malformed input.</summary>
    public static UpdateManifest Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<UpdateManifest>(json, Json)
            ?? throw new JsonException("The manifest parsed to null.");
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
    /// other does not; the design doc lists it among the guards a port must not drop. Validate the
    /// release manifest before calling, not after.
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

    private static Dictionary<string, ManifestFile> Index(UpdateManifest manifest, string name)
    {
        var byPath = new Dictionary<string, ManifestFile>(StringComparer.Ordinal);
        foreach (var file in manifest.Files)
        {
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
