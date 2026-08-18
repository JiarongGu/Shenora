using System.Collections.Concurrent;

namespace Shenora.Modules.Media;

/// <summary>
/// A remote source an app has AUTHORISED, and the name diagnostics may call it by.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b><see cref="Url"/> is treated as a secret and <see cref="Label"/> is not.</b> A remote media URL
/// routinely carries the caller's credentials — a presigned S3 link, a CDN token, a session query string —
/// and every existing diagnostic in this route prints the source it is working on. Splitting the two is
/// what lets the logs stay useful without becoming a credential dump, and it is why this type exists
/// rather than the route taking a bare <see cref="System.Uri"/>.
/// </para>
/// <para>
/// ⚠ <b><see cref="ToString"/> is overridden for the same reason.</b> A record's generated version prints
/// every property, so the url would reach any log line, exception message or debugger watch that formats
/// the object — none of which looks like a place a credential goes.
/// </para>
/// </remarks>
public sealed record RemoteMediaSource
{
    /// <summary>
    /// Where the engine reads from. ⚠ Treated as a SECRET: it is never logged, never put on the wire, and
    /// never returned to the page.
    /// </summary>
    public required Uri Url { get; init; }

    /// <summary>
    /// What diagnostics call this source — a title, a track id, anything meaningful and not sensitive.
    /// ⚠ It is PRINTED, so do not build it from the url.
    /// </summary>
    public required string Label { get; init; }

    /// <summary>
    /// A stable identity for caching, when the url itself is not one.
    /// <para>
    /// 🔴 <b>Set this whenever the url ROTATES.</b> Segments are cached under a key derived from this, and
    /// a presigned url that changes every hour is a different key every hour — so the same film is
    /// re-segmented from scratch each time while the old copies sit in the cache until the sweep reaches
    /// them. A local file avoids this by keying on identity+length+mtime, none of which is knowable here
    /// without fetching. Defaults to the url, which is correct only for a stable address.
    /// </para>
    /// </summary>
    public string? Identity { get; init; }

    /// <summary>
    /// How long it plays, when the caller already knows.
    /// <para>
    /// ⚠ Supplied rather than probed because probing a REMOTE source costs an engine launch that reads a
    /// network header before the first manifest can be answered — and the caller that registered the
    /// source usually has this from the same catalogue entry the url came from. Null falls back to asking
    /// the engine, which still works and is simply slower.
    /// </para>
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
/// <para>
/// 🔴 <b>THE INVERSION IS THE SECURITY PROPERTY.</b> The alternative — letting the page name a url and
/// asking the app to judge it — is what <see cref="MediaConversionOptions.AllowRemoteSource"/> does, and
/// it is strictly weaker: a policy that has to judge a page-supplied url can be WRONG, and being wrong
/// means the host fetches an address the page could not reach itself (an internal service, a metadata
/// endpoint, anything behind the machine). A handle that was never issued cannot be guessed, so there is
/// no judgement to get wrong. The page cannot express a source this registry did not authorise.
/// </para>
/// <para>
/// ⚠ <b>Registering is the app's decision and this type deliberately does not second-guess it.</b> It
/// holds what it was given. The boundary is that the PAGE cannot add to it.
/// </para>
/// <para>
/// Found by an adopter building it by hand: a track the webview refuses, that is not downloaded, had
/// exactly one answer — the server's transcode — spending CPU and a lossy step on a file the device's own
/// engine reads fine. Working around it meant forking this route to teach it a handle.
/// </para>
/// </remarks>
public sealed class MediaSourceRegistry
{
    private readonly ConcurrentDictionary<string, RemoteMediaSource> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Authorise a source and get the handle a page may name it by.
    /// </summary>
    /// <returns>
    /// An opaque handle. ⚠ 128 bits of it, because this IS the capability — anything shorter or derived
    /// from the source (a hash, a slug, a counter) can be guessed or enumerated, which hands back exactly
    /// the property the inversion bought.
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
    /// The source behind a handle, or null.
    /// <para>
    /// ⚠ Internal: resolving a handle is the ROUTE's job. Public, it would let app code turn a
    /// page-supplied string back into a credential-bearing url, which is the leak this design exists to
    /// prevent.
    /// </para>
    /// </summary>
    internal RemoteMediaSource? Resolve(string handle) =>
        !string.IsNullOrEmpty(handle) && _sources.TryGetValue(handle, out var source) ? source : null;
}
