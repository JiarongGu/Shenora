import {
  codecsFromInitSegment,
  nextSegment,
  parseManifest,
  pickMediaSource,
  segmentMimeType,
  type MediaSourceGlobals,
  type SegmentManifest,
} from './segmentStream.js';

/**
 * The imperative half of the segment route (D71 piece 4b): open a `SourceBuffer`, feed it, and stop when
 * the platform says stop.
 *
 * 🔴 **Three implementations disagree in ways the spec permits, so none of this is guessable** — the
 * measurements are in `docs/design/media.md`:
 *
 * - **Attachment is not portable.** iOS takes `srcObject` (a `ManagedMediaSource` is a valid
 *   `MediaSourceHandle`); Chromium refuses it and wants an object URL. Feature-detected, not branched on
 *   the shell: which one works is a property of the MediaSource, not of the OS.
 * - **The codecs are read from the init segment, never assumed.** The track set is a fact about the
 *   DEVICE, not the source: the same file yields a two-track init on iOS and a video-only one on Android,
 *   which cannot decode its AC-3 soundtrack. A mismatch kills the FIRST append and plays nothing.
 * - **The streaming gate is real on iOS and absent elsewhere.** Fetching past `endstreaming` is the
 *   misuse `ManagedMediaSource` exists to detect. A plain `MediaSource` has neither the event nor a
 *   `streaming` property, and its absence means "always streaming" — never "never asked".
 *
 * The dependencies below are injectable, so a fake source and a fake fetch drive every branch here.
 */
export interface SegmentBinderOptions {
  /** The playlist URL. Segment URIs are resolved relative to it. */
  manifest: string;

  /** The element to play into. Only the members this binder touches are required. */
  element: HTMLMediaElement;

  /** Where to look for a MediaSource. Defaults to `globalThis`. */
  globals?: MediaSourceGlobals;

  /** Defaults to `globalThis.fetch`. */
  fetch?: (url: string) => Promise<{ ok: boolean; status: number; arrayBuffer(): Promise<ArrayBuffer>; text(): Promise<string> }>;

  /** Defaults to `URL.createObjectURL`. Only used when `srcObject` is refused. */
  createObjectURL?: (source: object) => string;

  /** Defaults to `URL.revokeObjectURL`. The pair to {@link createObjectURL}. */
  revokeObjectURL?: (url: string) => void;

  /** Stop fetching once this many seconds are buffered ahead. Defaults to 30. */
  targetAheadSeconds?: number;

  /** Diagnostics. Every decision that could stall playback reports through here. */
  onDiagnostic?: (line: string) => void;
}

/** A live binding. Dispose it when the element goes away — it detaches every listener it added. */
export interface SegmentBinding {
  /** Indices appended so far. */
  readonly appended: ReadonlySet<number>;
  /** False while a managed source has said stop. Always true where the platform has no such signal. */
  readonly streaming: boolean;
  /** Which attachment this implementation accepted — the difference between the two shells. */
  readonly attachedBy: 'srcObject' | 'objectURL';
  /** What the SourceBuffer was opened with, read from the init segment. */
  readonly codecs: string;
  /** Detach listeners and release the object URL, if one was minted. */
  dispose(): void;
}

/**
 * How long to wait for the attached MediaSource to reach `open`. Attachment is local and immediate, so
 * this is a deadline for "something is wrong": the element can refuse or tear down an attachment
 * without raising any event this side can name, and the await would never return.
 */
const ATTACH_TIMEOUT_MS = 10_000;

/**
 * Thrown for every reason a stream cannot start, so a caller has one thing to catch.
 *
 * ⚠ **Not literally every reason** — a `TypeError` from the manifest fetch and a `RangeError` from a
 * truncated init segment propagate as themselves. Catch broadly if you need to be exhaustive.
 */
export class SegmentBinderError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'SegmentBinderError';
  }
}

/** Join a segment URI to the manifest's own location. */
function resolve(manifestUrl: string, uri: string): string {
  const slash = manifestUrl.lastIndexOf('/');
  return slash < 0 ? uri : manifestUrl.slice(0, slash + 1) + uri;
}

/** Seconds already buffered ahead of `currentTime`, across whichever range holds it. */
function bufferedAhead(element: HTMLMediaElement): number {
  const ranges = element.buffered;
  for (let i = 0; i < ranges.length; i++) {
    if (element.currentTime >= ranges.start(i) - 0.1 && element.currentTime <= ranges.end(i)) {
      return ranges.end(i) - element.currentTime;
    }
  }
  return 0;
}

/**
 * Open a MediaSource for `options.manifest` and keep it fed.
 *
 * Resolves once the init segment has been appended — the point after which the element can play — and
 * goes on fetching in the background until every segment is in or {@link SegmentBinding.dispose} is called.
 *
 * There is no `useSegmentStream` hook: this needs no React, so call it from an effect.
 */
export async function bindSegmentStream(options: SegmentBinderOptions): Promise<SegmentBinding> {
  const {
    manifest: manifestUrl,
    element,
    globals = globalThis as MediaSourceGlobals,
    targetAheadSeconds = 30,
    onDiagnostic,
  } = options;

  const doFetch = options.fetch ?? ((url: string) => fetch(url) as never);
  const say = (line: string) => onDiagnostic?.(line);

  /**
   * A FAILURE, as opposed to a trace line: reported to `console.error` when the app supplied no
   * `onDiagnostic`, so a segment answering 500 or an `appendBuffer` `QuotaExceededError` cannot stall
   * playback with nothing anywhere to explain it. A caller that took `onDiagnostic` owns its reporting
   * and is not double-logged.
   */
  const fail = (line: string) => (onDiagnostic ? onDiagnostic(line) : console.error(`[shenora] ${line}`));

  const kind = pickMediaSource(globals);
  if (kind === 'none') throw new SegmentBinderError('this browser has no MediaSource of either kind');

  const Source = (kind === 'managed' ? globals.ManagedMediaSource : globals.MediaSource) as
    new () => MediaSource & { streaming?: boolean };

  // ── the manifest, and the init segment it names ──────────────────────────────────────────────────
  const playlist = await doFetch(manifestUrl);
  if (!playlist.ok) throw new SegmentBinderError(`the manifest answered ${playlist.status}`);
  const parsed: SegmentManifest = parseManifest(await playlist.text());
  if (!parsed.initUri) {
    // A fragment repeats no decoder configuration, so appending one without this decodes nothing.
    throw new SegmentBinderError('the playlist declares no #EXT-X-MAP, which is not playable');
  }
  if (parsed.segments.length === 0) throw new SegmentBinderError('the playlist declares no segments');

  const initResponse = await doFetch(resolve(manifestUrl, parsed.initUri));
  if (!initResponse.ok) throw new SegmentBinderError(`the init segment answered ${initResponse.status}`);
  const init = new Uint8Array(await initResponse.arrayBuffer());

  // 🔴 The TRACK SET, from the bytes rather than from a constant — see the module remarks.
  const codecs = codecsFromInitSegment(init);
  if (!codecs) throw new SegmentBinderError('no track could be read from the init segment');
  const mime = segmentMimeType(codecs);
  say(`segments: opening ${mime} (${kind})`);

  // ── attach ──────────────────────────────────────────────────────────────────────────────────────
  const source = new Source();
  const revoke = options.revokeObjectURL ?? ((u: string) => URL.revokeObjectURL(u));
  let attachedBy: 'srcObject' | 'objectURL' = 'srcObject';
  let objectUrl: string | undefined;
  try {
    element.srcObject = source as never;
  } catch {
    attachedBy = 'objectURL';
    const mint = options.createObjectURL ?? ((s: object) => URL.createObjectURL(s as never));
    objectUrl = mint(source);
    element.src = objectUrl;
  }

  // ⚠ The object URL is minted BEFORE anything below can fail, so EVERY failure between the mint and
  // the returned binding must revoke it itself — the caller holds no binding to dispose yet, and the
  // document keeps the MediaSource alive for its lifetime. Three places: the open wait,
  // addSourceBuffer, and the init append.
  const revokeAndRethrow = (e: unknown): never => {
    if (objectUrl) revoke(objectUrl);
    throw e;
  };

  // 🔴 `error` IS NOT A MediaSource EVENT — the spec fires `sourceopen`, `sourceended` and
  // `sourceclose`. An attachment that CLOSES rather than opening (the element detached before load, an
  // attachment refused) leaves this await pending FOREVER on any other listener: no error, no
  // diagnostic, no way to reach dispose(). The deadline covers whatever is neither.
  await new Promise<void>((res, rej) => {
    if (source.readyState === 'open') return res();

    const settle = (finish: () => void) => () => {
      clearTimeout(timer);
      source.removeEventListener('sourceopen', onOpen);
      source.removeEventListener('sourceclose', onClose);
      finish();
    };
    const onOpen = settle(() => res());
    const onClose = settle(() => rej(new SegmentBinderError('the MediaSource closed before it opened')));
    const timer = setTimeout(
      settle(() => rej(new SegmentBinderError(
        `the MediaSource did not open within ${ATTACH_TIMEOUT_MS} ms (attached by ${attachedBy})`))),
      ATTACH_TIMEOUT_MS);

    source.addEventListener('sourceopen', onOpen);
    source.addEventListener('sourceclose', onClose);
  }).catch(revokeAndRethrow);

  let buffer: SourceBuffer;
  try {
    // Throws synchronously for a codecs string this implementation refuses.
    buffer = source.addSourceBuffer(mime);
  } catch (e) {
    revokeAndRethrow(e);
  }

  // ── state ───────────────────────────────────────────────────────────────────────────────────────
  const appended = new Set<number>();
  // Absent signals mean ALWAYS streaming. A managed source flips this on its own events.
  let streaming = true;
  let disposed = false;
  let pumping = false;

  /**
   * Append one buffer and settle when the source buffer says so.
   *
   * 🔴 **Both listeners come off on EITHER outcome.** `{ once: true }` removes only the listener that
   * FIRES, and the success path fires `updateend` — so with it the `error` listener stays attached once
   * per appended segment, each retaining a settled `rej` closure that `dispose()` cannot shed, and one
   * later real `error` invokes every one of them.
   */
  const append = (bytes: Uint8Array) => new Promise<void>((res, rej) => {
    const done = (settle: () => void) => () => {
      buffer.removeEventListener('updateend', onDone);
      buffer.removeEventListener('error', onFail);
      settle();
    };
    const onDone = done(() => res());
    const onFail = done(() => rej(new SegmentBinderError('appendBuffer failed')));

    buffer.addEventListener('updateend', onDone);
    buffer.addEventListener('error', onFail);
    try {
      buffer.appendBuffer(bytes as never);
    } catch (e) {
      // A synchronous throw settles nothing through the events, so shed them here too.
      buffer.removeEventListener('updateend', onDone);
      buffer.removeEventListener('error', onFail);
      rej(new SegmentBinderError(`appendBuffer threw: ${(e as Error).message}`));
    }
  });

  await append(init).catch(revokeAndRethrow);
  say('segments: init appended');

  /**
   * Fetch and append whatever {@link nextSegment} asks for, one at a time.
   *
   * ⚠ Re-entrancy is guarded rather than queued: element events fire far faster than an append
   * completes, and two concurrent `appendBuffer` calls on one SourceBuffer throw `InvalidStateError`,
   * which surfaces as a stall with no obvious cause.
   */
  const pump = async () => {
    if (pumping || disposed) return;
    pumping = true;
    try {
      for (;;) {
        if (disposed) return;
        const index = nextSegment(
          { currentTime: element.currentTime, bufferedAhead: bufferedAhead(element), appended, streaming },
          { segments: parsed.segments, targetAheadSeconds },
        );
        if (index === null) break;

        const entry = parsed.segments[index]!;
        const response = await doFetch(resolve(manifestUrl, entry.uri));
        if (disposed) return;
        if (!response.ok) {
          // 503 is the host saying "still producing" — a WAIT, not a failure. The route answers it
          // rather than 404ing a source that is merely not ready yet.
          fail(`segments: ${entry.uri} answered ${response.status}`);
          break;
        }

        await append(new Uint8Array(await response.arrayBuffer()));
        appended.add(index);
        if (disposed) return;
      }

      // Every segment in: say so, or the element never learns it has reached the end.
      if (appended.size === parsed.segments.length && source.readyState === 'open') {
        source.endOfStream();
        say('segments: endOfStream');
      }
    } catch (e) {
      fail(`segments: ${(e as Error).message}`);
    } finally {
      pumping = false;
    }
  };

  // ── the events that should make us reconsider ───────────────────────────────────────────────────
  const onStart = () => { streaming = true; say('segments: startstreaming'); void pump(); };
  const onEnd = () => { streaming = false; say('segments: endstreaming'); };
  const wake = () => { void pump(); };

  source.addEventListener('startstreaming', onStart);
  source.addEventListener('endstreaming', onEnd);
  element.addEventListener('timeupdate', wake);
  element.addEventListener('seeking', wake);
  element.addEventListener('waiting', wake);

  void pump();

  return {
    get appended() { return appended; },
    get streaming() { return streaming; },
    attachedBy,
    codecs,
    dispose() {
      if (disposed) return;
      disposed = true;
      source.removeEventListener('startstreaming', onStart);
      source.removeEventListener('endstreaming', onEnd);
      element.removeEventListener('timeupdate', wake);
      element.removeEventListener('seeking', wake);
      element.removeEventListener('waiting', wake);
      if (objectUrl) revoke(objectUrl);
    },
  };
}
