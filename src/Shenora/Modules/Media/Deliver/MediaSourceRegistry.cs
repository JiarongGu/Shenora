using System.Collections.Concurrent;

namespace Shenora.Modules.Media;

/// <summary>
/// A remote source an app has AUTHORISED, and the name diagnostics may call it by.
/// </summary>
/// <remarks>
/// 🔴 <b><see cref="Url"/> is treated as a secret and <see cref="Label"/> is not</b> — a remote media URL
/// routinely carries credentials (a presigned S3 link, a CDN token, a session query string), and every
/// diagnostic here prints the source it is working on. ⚠ <see cref="ToString"/> is overridden for the same
/// reason: a record's generated version prints every property, so the url would reach any log line,
/// exception message or debugger watch that formats the object.
/// </remarks>
public sealed record RemoteMediaSource
{
    /// <summary>
    /// What this source IS — its identity and, for an app that keys off it, its address. ⚠ Treated as a
    /// SECRET: never logged, never put on the wire, never returned to the page. It is not what the engine
    /// reads either — that is <see cref="Open"/>, so the url stays inside the app's own closure.
    /// </summary>
    public required Uri Url { get; init; }

    /// <summary>
    /// How to READ the bytes: open a fresh SEEKABLE stream over the source, however it is fetched — the kit
    /// ships no transport, and Matroska is read by offset (<see cref="MediaByteSource.Open"/>).
    /// <para>
    /// ⚠ <b>For a ranged source, do not write this by hand</b> — take
    /// <see cref="MediaByteSource.ForRanges"/>'s <c>Open</c>. It supplies the buffering, without which the
    /// EBML parser costs one round trip PER BYTE; only the range fetch itself is yours.
    /// </para>
    /// <para>
    /// 🔴 <b>Null means this source can be described but never PRODUCED from, and the route refuses it at
    /// the MANIFEST</b> — a playlist is derived from the duration, which is suppliable, so serving one for
    /// bytes nobody can read hands the page a complete playlist whose every entry <c>503</c>s for ever.
    /// </para>
    /// </summary>
    public Func<CancellationToken, Stream>? Open { get; init; }

    /// <summary>
    /// What diagnostics call this source — a title, a track id, anything meaningful and not sensitive.
    /// ⚠ It is PRINTED, so do not build it from the url.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// A stable identity for caching, when the url itself is not one. Defaults to the url.
    /// <para>
    /// 🔴 <b>Set this whenever the url ROTATES.</b> Segments are cached under a key derived from it, so a
    /// presigned url that changes every hour re-segments the same film from scratch each time while the old
    /// copies sit in the cache until the sweep reaches them.
    /// </para>
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// How long it plays, when the caller already knows. Null asks the engine instead, which costs a launch
    /// reading a network header before the first manifest can be answered.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Whether it has a video track, when the caller already knows. Null asks the engine.</summary>
    public bool? HasPicture { get; init; }

    /// <summary>🔴 Redacted — see the type's remarks. The url is a credential carrier.</summary>
    public override string ToString() => $"{nameof(RemoteMediaSource)} {{ Label = {Label}, Url = *** }}";
}

/// <summary>
/// Remote sources the app has authorised, each addressable only by an opaque handle it issued.
/// </summary>
/// <remarks>
/// 🔴 <b>THE INVERSION IS THE SECURITY PROPERTY.</b> A handle that was never issued cannot be guessed, so
/// the page cannot express a source this registry did not authorise — strictly stronger than
/// <see cref="MediaConversionOptions.AllowRemoteSource"/>, whose predicate over a page-supplied url can be
/// WRONG, and wrong means the host fetches an address the page could not reach itself. ⚠ Registering is
/// the app's decision and this type does not second-guess it; the boundary is that the PAGE cannot add to it.
/// </remarks>
public sealed class MediaSourceRegistry
{
    private readonly ConcurrentDictionary<string, RemoteMediaSource> _sources = new(StringComparer.Ordinal);

    /// <summary>Authorise a source and get the handle a page may name it by.</summary>
    /// <returns>
    /// An opaque handle. ⚠ 128 bits of it, because this IS the capability — anything shorter or derived
    /// from the source (a hash, a slug, a counter) can be guessed or enumerated.
    /// </returns>
    public string Register(RemoteMediaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(source.Url);
        ArgumentException.ThrowIfNullOrWhiteSpace(source.Label);
        if (!source.Url.IsAbsoluteUri)
            throw new ArgumentException("A remote source needs an absolute url.", nameof(source));

        var handle = Guid.NewGuid().ToString("n");
        _sources[handle] = source;
        return handle;
    }

    /// <summary>
    /// Withdraw a handle. Anything already playing keeps its open source; nothing NEW can be started
    /// through it.
    /// </summary>
    /// <returns>True when the handle existed.</returns>
    public bool Release(string handle) =>
        !string.IsNullOrEmpty(handle) && _sources.TryRemove(handle, out _);

    /// <summary>Withdraw every handle — a sign-out, or a session ending.</summary>
    public void ReleaseAll() => _sources.Clear();

    /// <summary>How many handles are live. For diagnostics and tests.</summary>
    public int Count => _sources.Count;

    /// <summary>
    /// The source behind a handle, or null. ⚠ Internal because public it would let app code turn a
    /// page-supplied string back into a credential-bearing url — the leak this design exists to prevent.
    /// </summary>
    internal RemoteMediaSource? Resolve(string handle) =>
        !string.IsNullOrEmpty(handle) && _sources.TryGetValue(handle, out var source) ? source : null;
}
