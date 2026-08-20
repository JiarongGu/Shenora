using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shenora.Engine.Update;

/// <summary>
/// One tracked file in an <see cref="UpdateManifest"/>: where it lives, how big it is, and what it
/// hashes to (<c>docs/DECISIONS.md</c> D57).
/// </summary>
public sealed class ManifestFile
{
    /// <summary>
    /// Path RELATIVE to the install root, with forward slashes (<c>libs/app.dll</c>). ⚠ Comparisons
    /// normalize separators and ignore case; a mismatch does not fail loudly, it silently redownloads a
    /// file forever or misses a removal.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>Size in bytes. Used for the changeset's download total, never for equality.</summary>
    public required long Size { get; init; }

    /// <summary>SHA-256 as hex, compared case-insensitively (generators disagree about casing).</summary>
    public required string Sha256 { get; init; }
}

/// <summary>
/// The list of auto-updatable files a release ships, written beside the payload and installed with
/// it. Diffing the INSTALLED copy against a RELEASE copy produces a changeset
/// (<see cref="ManifestDiff.Compute"/>), so only changed files are downloaded.
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
    /// Read a manifest. Throws <see cref="JsonException"/> on malformed input, which <b>includes a
    /// file path that could resolve outside the install root</b>
    /// (<see cref="ManifestDiff.IsSafeRelativePath"/>).
    /// <para>
    /// ⚠ Refused HERE as well as at diff time: a poisoned BASELINE must take
    /// <see cref="UpdateStage.ApplyAsync"/>'s "no usable installed manifest" branch, not throw past that
    /// guard and leave an app permanently unable to update.
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
/// What changed between the installed manifest and a release one: the changeset an updater downloads
/// and an applier lands.
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
    /// Installed but not in the release — delete. <b>Tracked paths only, never a directory sweep:</b>
    /// user data lives in the same tree.
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
    /// legitimately means "every installed file is removed", so one that parsed to nothing produces a
    /// changeset deleting the whole install — as a SUCCESSFUL outcome. Validate before calling.
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

        // Ordered so two runs over the same inputs match: dictionary enumeration order is not a contract.
        added.Sort(ByPath);
        updated.Sort(ByPath);
        removed.Sort(StringComparer.Ordinal);
        return new ManifestDiff(added, updated, removed);
    }

    private static int ByPath(ManifestFile a, ManifestFile b) =>
        string.Compare(Normalize(a.Path), Normalize(b.Path), StringComparison.Ordinal);

    /// <summary>
    /// Forward slashes, lower-cased — <c>libs\app.dll</c> and <c>libs/app.dll</c> are one entry.
    /// <c>internal</c> so <see cref="UpdateStage"/>'s intrusion check and path resolution use this rule
    /// rather than a second copy of it.
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
    /// first argument when the second is rooted, and C++'s <c>std::filesystem::operator/</c> does the
    /// identical thing — <c>Shenora.Launcher</c>'s <c>parse_manifest</c> carries the same rejection.</item>
    /// <item>A <c>..</c> segment, which walks out of the root the ordinary way.</item>
    /// </list>
    /// <para>
    /// ⚠ <b>Refused at the MANIFEST, not at each call site</b>, because both later gates pass: hash
    /// verification checks CONTENT and never the PATH, and the stage's intrusion check walks the staged
    /// directory, which a file written outside it is not in.
    /// </para>
    /// </summary>
    internal static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // `IsPathRooted` covers `C:\x`, `C:x` and `\x` on Windows and `/x` everywhere.
        if (System.IO.Path.IsPathRooted(path)) return false;

        // Both separators: a manifest written on one platform is applied on another.
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
                // The WHOLE diff is refused, never the one row: nothing is written and nothing deleted.
                throw new ArgumentException(
                    $"The {name} manifest lists '{file.Path}', which is not a contained relative path. " +
                    "A manifest path must be relative to the install root and must not contain a '..' " +
                    "segment — anything else can resolve outside the tree being updated.", name);
            }

            if (!byPath.TryAdd(Normalize(file.Path), file))
            {
                // Throws rather than last-wins, which silently makes a changeset depend on list order.
                throw new ArgumentException(
                    $"The {name} manifest lists '{file.Path}' more than once (paths are compared " +
                    "case-insensitively with separators normalized).", name);
            }
        }
        return byPath;
    }
}
