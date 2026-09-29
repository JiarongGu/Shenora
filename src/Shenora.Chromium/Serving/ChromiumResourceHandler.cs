using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Shenora.Chromium.Interop;
using Shenora.Core.WebView;

namespace Shenora.Chromium.Serving;

/// <summary>
/// Delivers one <see cref="WebViewResourceResponse"/>, the kit's pipeline's answer, to CEF.
/// <para>
/// ASYNCHRONOUS both ways: the response is a task, and every read goes to the body with <c>ReadAsync</c>,
/// so a slow body (a computed media route) does not hold CEF's IO thread while it produces bytes. What does run
/// there: the pipeline's synchronous start, and a skip into a body that cannot seek. The body is disposed at its
/// end, on cancel, and when CEF frees the handler, whichever comes first, because nothing else ever will. A cancel
/// also cancels the pipeline's token, so a route still working learns nobody is reading.
/// </para>
/// <para>
/// A failed response becomes a 500 with a constant body: every response is readable by page script, and
/// an exception's message routinely names a local path.
/// </para>
/// </summary>
internal sealed unsafe class ChromiumResourceHandler : CefObject<_cef_resource_handler_t>
{
    private const int ErrFailed = -2;   // net::ERR_FAILED, what CEF takes as a failed read or skip
    private const int ChunkSize = 64 * 1024;

    private readonly Task<WebViewResourceResponse> _pending;
    private readonly CancellationTokenSource? _cancel;
    private readonly ILogger? _log;
    private WebViewResourceResponse? _response;
    private Stream? _body;
    private byte[]? _chunk;
    private int _closed;   // cancelled, or freed: a body that arrives later is disposed on arrival

    public ChromiumResourceHandler(Task<WebViewResourceResponse> response, ILogger? log = null)
    {
        _pending = response ?? throw new ArgumentNullException(nameof(response));
        _log = log;
        Struct->open = &Open;
        Struct->get_response_headers = &GetResponseHeaders;
        Struct->read = &Read;
        Struct->skip = &Skip;
        Struct->cancel = &Cancel;
    }

    /// <summary>A handler for a response that is already known.</summary>
    public ChromiumResourceHandler(WebViewResourceResponse response, ILogger? log = null) : this(Task.FromResult(response), log) { }

    /// <summary>A handler for a response <paramref name="produce"/> starts now, given a token this handler cancels
    /// when CEF does.</summary>
    public ChromiumResourceHandler(Func<CancellationToken, Task<WebViewResourceResponse>> produce, ILogger? log = null)
        : this(Start(produce, out var cancel), log) => _cancel = cancel;

    private static Task<WebViewResourceResponse> Start(Func<CancellationToken, Task<WebViewResourceResponse>> produce, out CancellationTokenSource cancel)
    {
        cancel = new CancellationTokenSource();
        try { return produce(cancel.Token); }
        catch (Exception ex) { return Task.FromException<WebViewResourceResponse>(ex); }
    }

    private static readonly byte[] FailureBody = "The response could not be produced."u8.ToArray();

    /// <summary>Take the pipeline's outcome: its response, or a constant 500 when it failed.</summary>
    private void Settle()
    {
        if (_pending.IsCompletedSuccessfully)
        {
            _response = _pending.Result;
        }
        else
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] A resource response failed; answering 500",
                LogLevel.Warning, _pending.Exception?.GetBaseException());
            _response = new WebViewResourceResponse
            {
                StatusCode = 500,
                ReasonPhrase = "Internal Server Error",
                Content = new MemoryStream(FailureBody, writable: false),
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/plain" },
            };
        }
        _body = _response.Content;
        // Cancelled while the pipeline was still working: this body has no reader and nothing else will close it.
        if (Volatile.Read(ref _closed) != 0) CloseBody();
    }

    private void CloseBody()
    {
        var body = Interlocked.Exchange(ref _body, null);
        if (body is null) return;
        try { body.Dispose(); }
        catch (Exception ex) { AppCallback.Log(_log, () => "[Shenora.Chromium] Disposing a response body threw", LogLevel.Warning, ex); }
    }

    private void Close()
    {
        Volatile.Write(ref _closed, 1);
        try { _cancel?.Cancel(); }
        catch (Exception ex) { AppCallback.Log(_log, () => "[Shenora.Chromium] Cancelling a resource response threw", LogLevel.Warning, ex); }
        CloseBody();
    }

    private protected override void OnFreed()
    {
        Close();
        _cancel?.Dispose();
    }

    // An exception must never unwind into CEF's frames: it ends the process. What these touch is app data.
    [UnmanagedCallersOnly]
    private static int Open(_cef_resource_handler_t* self, _cef_request_t* request, int* handleRequest, _cef_callback_t* callback)
    {
        try { return OpenCore(self, request, handleRequest, callback); }
        catch (Exception ex)
        {
            AppCallback.Log(From<ChromiumResourceHandler>(self)._log, () => "[Shenora.Chromium] Opening a response failed", LogLevel.Warning, ex);
            *handleRequest = 1;
            return 0;   // cancels the request
        }
    }

    private static int OpenCore(_cef_resource_handler_t* self, _cef_request_t* request, int* handleRequest, _cef_callback_t* callback)
    {
        using var ownedRequest = new CefRef<_cef_request_t>(request);
        var me = From<ChromiumResourceHandler>(self);
        if (me._pending.IsCompleted)
        {
            using var unused = new CefRef<_cef_callback_t>(callback);
            me.Settle();
            *handleRequest = 1;
            return 1;
        }

        // Decide later: the callback's reference is KEPT until it has been used, then released.
        *handleRequest = 0;
        var held = new CefRef<_cef_callback_t>(callback);
        me._pending.ContinueWith(_ =>
        {
            me.Settle();
            if (Volatile.Read(ref me._closed) == 0) held.Ptr->cont(held.Ptr);
            held.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        return 1;
    }

    [UnmanagedCallersOnly]
    private static void GetResponseHeaders(_cef_resource_handler_t* self, _cef_response_t* response, long* length, _cef_string_utf16_t* redirect)
    {
        using var ownedResponse = new CefRef<_cef_response_t>(response);
        var me = From<ChromiumResourceHandler>(self);
        try
        {
            var r = me._response!;
            response->set_status(response, r.StatusCode);
            var phrase = r.ReasonPhrase ?? "";
            fixed (char* p = phrase)
            {
                var s = CefStrings.View(p, phrase.Length);
                response->set_status_text(response, &s);
            }
            foreach (var (name, value) in r.Headers ?? new Dictionary<string, string>())
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)) SetContentType(response, value);
                else SetHeader(response, name, value);
            }
            *length = me._body is { CanSeek: true } body ? body.Length - body.Position : -1;
        }
        catch (Exception ex)
        {
            // A response the app built badly (a null header value, a body that cannot report its length): a 500 with
            // nothing to read, never the process.
            AppCallback.Log(me._log, () => "[Shenora.Chromium] A response's headers could not be read; answering 500", LogLevel.Warning, ex);
            me.CloseBody();
            response->set_status(response, 500);
            *length = 0;
        }
    }

    /// <summary>CEF keeps the MIME type and the charset apart: <c>text/html; charset=utf-8</c> is both.</summary>
    private static void SetContentType(_cef_response_t* response, string value)
    {
        var semicolon = value.IndexOf(';');
        var mime = (semicolon < 0 ? value : value[..semicolon]).Trim();
        fixed (char* m = mime)
        {
            var s = CefStrings.View(m, mime.Length);
            response->set_mime_type(response, &s);
        }
        const string key = "charset=";
        var at = value.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return;
        var charset = value[(at + key.Length)..].Split(';')[0].Trim().Trim('"');
        fixed (char* c = charset)
        {
            var s = CefStrings.View(c, charset.Length);
            response->set_charset(response, &s);
        }
    }

    private static void SetHeader(_cef_response_t* response, string name, string value)
    {
        fixed (char* n = name)
        fixed (char* v = value)
        {
            var sn = CefStrings.View(n, name.Length);
            var sv = CefStrings.View(v, value.Length);
            response->set_header_by_name(response, &sn, &sv, 1);
        }
    }

    [UnmanagedCallersOnly]
    private static int Read(_cef_resource_handler_t* self, void* output, int wanted, int* read, _cef_resource_read_callback_t* callback)
    {
        var me = From<ChromiumResourceHandler>(self);
        var held = new CefRef<_cef_resource_read_callback_t>(callback);
        var body = me._body;
        if (body is null || wanted <= 0)
        {
            held.Dispose();
            *read = 0;
            return 0;   // complete
        }

        me._chunk ??= new byte[ChunkSize];
        var buffer = me._chunk.AsMemory(0, Math.Min(wanted, ChunkSize));
        ValueTask<int> pending;
        try { pending = body.ReadAsync(buffer); }
        catch (Exception ex) { return me.Failed(ex, read, held); }

        if (pending.IsCompleted)
        {
            int n;
            try { n = pending.GetAwaiter().GetResult(); }
            catch (Exception ex) { return me.Failed(ex, read, held); }
            held.Dispose();
            if (n == 0) { me.CloseBody(); *read = 0; return 0; }
            Marshal.Copy(me._chunk, 0, (nint)output, n);
            *read = n;
            return 1;
        }

        // Later: CEF keeps `output` valid until the callback runs.
        var target = (nint)output;
        me.CompleteRead(pending, target, held);
        *read = 0;
        return 1;
    }

    /// <summary>A continuation, not <c>await</c>: an unsafe context may not await, and this whole type is one.</summary>
    private void CompleteRead(ValueTask<int> pending, nint target, CefRef<_cef_resource_read_callback_t> held) =>
        pending.AsTask().ContinueWith(t => FinishRead(t, target, held), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);

    private void FinishRead(Task<int> read, nint target, CefRef<_cef_resource_read_callback_t> held)
    {
        // Cancelled: CEF's buffer is no longer ours to write into, and nobody waits for the answer.
        if (Volatile.Read(ref _closed) != 0)
        {
            CloseBody();
            held.Dispose();
            return;
        }
        int result;
        if (read.IsCompletedSuccessfully)
        {
            result = read.Result;
            if (result > 0) Marshal.Copy(_chunk!, 0, target, result);
            else CloseBody();
        }
        else
        {
            AppCallback.Log(_log, () => "[Shenora.Chromium] Reading a response body failed", LogLevel.Warning, read.Exception?.GetBaseException());
            CloseBody();
            result = ErrFailed;
        }
        if (Volatile.Read(ref _closed) == 0) held.Ptr->cont(held.Ptr, result);
        held.Dispose();
    }

    private int Failed(Exception ex, int* read, CefRef<_cef_resource_read_callback_t> held)
    {
        AppCallback.Log(_log, () => "[Shenora.Chromium] Reading a response body failed", LogLevel.Warning, ex);
        CloseBody();
        held.Dispose();
        *read = ErrFailed;
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int Skip(_cef_resource_handler_t* self, long count, long* skipped, _cef_resource_skip_callback_t* callback)
    {
        using var ownedCallback = new CefRef<_cef_resource_skip_callback_t>(callback);
        var me = From<ChromiumResourceHandler>(self);
        try
        {
            var body = me._body ?? throw new ObjectDisposedException("body");
            if (body.CanSeek)
            {
                var moved = Math.Min(count, body.Length - body.Position);
                body.Seek(moved, SeekOrigin.Current);
                *skipped = moved;
                return 1;
            }
            var discard = new byte[Math.Min(count, ChunkSize)];
            long total = 0;
            while (total < count)
            {
                var n = body.Read(discard, 0, (int)Math.Min(discard.Length, count - total));
                if (n == 0) break;
                total += n;
            }
            *skipped = total;
            return 1;
        }
        catch (Exception ex)
        {
            AppCallback.Log(me._log, () => "[Shenora.Chromium] Skipping into a response body failed", LogLevel.Warning, ex);
            *skipped = ErrFailed;
            return 0;
        }
    }

    [UnmanagedCallersOnly]
    private static void Cancel(_cef_resource_handler_t* self) => From<ChromiumResourceHandler>(self).Close();
}
