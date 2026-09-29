using System.Runtime.InteropServices;
using System.Text;
using Shenora.Chromium.Interop;
using Shenora.Chromium.Serving;
using Shenora.Core.WebView;

namespace Shenora.Tests.Chromium;

/// <summary>
/// The CEF resource handler that delivers the kit's pipeline responses. CEF drives it only through its
/// struct's function pointers, so these do the same, against fake CEF structs (a response, the callbacks)
/// built the way the shell builds its own. No CEF binary is loaded.
/// </summary>
public unsafe class ChromiumResourceHandlerTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public void A_known_response_is_handled_at_once_and_served_whole()
    {
        var body = new TrackingStream("<p>hi</p>"u8.ToArray());
        using var run = new Run(new WebViewResourceResponse
        {
            Content = body,
            Headers = Headers(("Content-Type", "text/html; charset=utf-8"), ("X-Test", "yes")),
        });

        Assert.Equal((1, 1), run.Open());
        var response = run.Headers(out var length);

        Assert.Equal(200, response.Status);
        Assert.Equal("text/html", response.Mime);
        Assert.Equal("utf-8", response.Charset);
        Assert.Equal("yes", response.HeaderValues["X-Test"]);
        Assert.Equal(9, length);
        Assert.Equal("<p>hi</p>", Encoding.UTF8.GetString(run.ReadAll()));
        Assert.True(body.Disposed, "the body must close itself at its end: nothing else will");
    }

    [Fact]
    public void A_pending_response_defers_then_continues_exactly_once()
    {
        var pending = new TaskCompletionSource<WebViewResourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new Run(pending.Task);

        Assert.Equal((0, 1), run.Open());   // decide later
        Assert.Equal(0, run.Callback.Continued);

        pending.SetResult(WebViewResourceResponse.Bytes([1, 2, 3], "application/octet-stream"));
        Assert.True(SpinWait.SpinUntil(() => run.Callback.Continued == 1, Wait));
        Assert.Equal(200, run.Headers(out _).Status);
        Assert.Equal(1, run.Callback.Continued);
    }

    [Fact]
    public void A_failed_response_is_a_constant_500_that_names_nothing()
    {
        using var run = new Run(Task.FromException<WebViewResourceResponse>(new IOException(@"C:\secret\path.bin is locked")));

        run.Open();
        Assert.Equal(500, run.Headers(out _).Status);
        var text = Encoding.UTF8.GetString(run.ReadAll());
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cancelling_disposes_the_body()
    {
        var body = new TrackingStream(new byte[100]);
        using var run = new Run(WebViewResourceResponse.Ok(body, "application/octet-stream"));
        run.Open();

        run.Cancel();

        Assert.True(body.Disposed);
    }

    [Fact]
    public void A_body_that_arrives_after_a_cancel_is_disposed_on_arrival_and_never_continued()
    {
        var pending = new TaskCompletionSource<WebViewResourceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new Run(pending.Task);
        run.Open();
        run.Cancel();

        var body = new TrackingStream(new byte[10]);
        pending.SetResult(WebViewResourceResponse.Ok(body, "text/plain"));

        Assert.True(SpinWait.SpinUntil(() => body.Disposed, Wait), "a body with no reader must still be closed");
        Assert.Equal(0, run.Callback.Continued);
    }

    [Fact]
    public void A_slow_body_is_read_later_through_the_read_callback()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var run = new Run(WebViewResourceResponse.Ok(new SlowStream(gate.Task, "late"u8.ToArray()), "text/plain"));
        run.Open();
        run.Headers(out _);

        var buffer = new byte[16];
        fixed (byte* p = buffer)
        {
            var (ok, read) = run.Read(p, buffer.Length);
            Assert.Equal((1, 0), (ok, read));   // "later": the data is not there yet
            gate.SetResult();
            Assert.True(SpinWait.SpinUntil(() => run.ReadCallback.Result is not null, Wait));
        }
        Assert.Equal(4, run.ReadCallback.Result);
        Assert.Equal("late", Encoding.UTF8.GetString(buffer, 0, 4));
    }

    // A route still working when the page abandons the request learns it from its token.
    [Fact]
    public void Cancelling_cancels_the_token_the_response_was_started_with()
    {
        var token = CancellationToken.None;
        var pending = new TaskCompletionSource<WebViewResourceResponse>();
        using var run = new Run(ct => { token = ct; return pending.Task; });
        run.Open();
        Assert.False(token.IsCancellationRequested);

        run.Cancel();

        Assert.True(token.IsCancellationRequested);
    }

    // What the app built is app data: a null header value must not unwind into CEF, which ends the process.
    [Fact]
    public void A_response_whose_headers_cannot_be_read_is_a_500_not_a_crash()
    {
        using var run = new Run(new WebViewResourceResponse
        {
            Content = new MemoryStream("body"u8.ToArray()),
            Headers = new Dictionary<string, string> { ["X-Bad"] = null! },
        });
        run.Open();

        var response = run.Headers(out var length);

        Assert.Equal(500, response.Status);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Freeing_the_handler_disposes_a_body_nobody_read()
    {
        var body = new TrackingStream(new byte[100]);
        var run = new Run(WebViewResourceResponse.Ok(body, "application/octet-stream"));
        run.Open();

        run.Dispose();   // CEF's reference and the creator's both go

        Assert.True(body.Disposed);
    }

    private static IReadOnlyDictionary<string, string> Headers(params (string Name, string Value)[] headers) =>
        headers.ToDictionary(h => h.Name, h => h.Value, StringComparer.OrdinalIgnoreCase);

    /// <summary>One request's worth of calls into a handler, the way CEF makes them.</summary>
    private sealed class Run : IDisposable
    {
        private readonly ChromiumResourceHandler _handler;
        private readonly _cef_resource_handler_t* _struct;
        private bool _disposed;

        public FakeCallback Callback { get; } = new();
        public FakeReadCallback ReadCallback { get; } = new();

        public Run(WebViewResourceResponse response) : this(Task.FromResult(response)) { }

        public Run(Task<WebViewResourceResponse> response)
        {
            _handler = new ChromiumResourceHandler(response);
            _struct = _handler.ForCef();   // the reference CEF holds while the request lives
        }

        public Run(Func<CancellationToken, Task<WebViewResourceResponse>> produce)
        {
            _handler = new ChromiumResourceHandler(produce);
            _struct = _handler.ForCef();
        }

        public (int Handle, int Result) Open()
        {
            int handle;
            var result = _struct->open(_struct, null, &handle, Callback.ForCef());
            return (handle, result);
        }

        public FakeResponse Headers(out long length)
        {
            var response = new FakeResponse();
            long l;
            _struct->get_response_headers(_struct, response.ForCef(), &l, null);
            length = l;
            return response;
        }

        public (int Ok, int Read) Read(byte* buffer, int wanted)
        {
            int read;
            var ok = _struct->read(_struct, buffer, wanted, &read, ReadCallback.ForCef());
            return (ok, read);
        }

        public byte[] ReadAll()
        {
            var all = new List<byte>();
            var buffer = new byte[4];
            fixed (byte* p = buffer)
            {
                while (true)
                {
                    var (ok, read) = Read(p, buffer.Length);
                    if (ok == 0) break;
                    all.AddRange(buffer.AsSpan(0, read).ToArray());
                }
            }
            return all.ToArray();
        }

        public void Cancel() => _struct->cancel(_struct);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ((_cef_base_ref_counted_t*)_struct)->release((_cef_base_ref_counted_t*)_struct);
            _handler.Release();
        }
    }

    /// <summary>What CEF's response object was told.</summary>
    private sealed class FakeResponse : CefObject<_cef_response_t>
    {
        public int Status;
        public string? StatusText, Mime, Charset;
        public readonly Dictionary<string, string> HeaderValues = new(StringComparer.OrdinalIgnoreCase);

        public FakeResponse()
        {
            Struct->set_status = &SetStatus;
            Struct->set_status_text = &SetStatusText;
            Struct->set_mime_type = &SetMime;
            Struct->set_charset = &SetCharset;
            Struct->set_header_by_name = &SetHeader;
        }

        [UnmanagedCallersOnly] private static void SetStatus(_cef_response_t* self, int status) => From<FakeResponse>(self).Status = status;
        [UnmanagedCallersOnly] private static void SetStatusText(_cef_response_t* self, _cef_string_utf16_t* s) => From<FakeResponse>(self).StatusText = CefStrings.Read(s);
        [UnmanagedCallersOnly] private static void SetMime(_cef_response_t* self, _cef_string_utf16_t* s) => From<FakeResponse>(self).Mime = CefStrings.Read(s);
        [UnmanagedCallersOnly] private static void SetCharset(_cef_response_t* self, _cef_string_utf16_t* s) => From<FakeResponse>(self).Charset = CefStrings.Read(s);

        [UnmanagedCallersOnly]
        private static void SetHeader(_cef_response_t* self, _cef_string_utf16_t* name, _cef_string_utf16_t* value, int overwrite) =>
            From<FakeResponse>(self).HeaderValues[CefStrings.Read(name)] = CefStrings.Read(value);
    }

    private sealed class FakeCallback : CefObject<_cef_callback_t>
    {
        private int _continued;
        public int Continued => Volatile.Read(ref _continued);

        public FakeCallback() => Struct->cont = &Cont;

        [UnmanagedCallersOnly] private static void Cont(_cef_callback_t* self) => Interlocked.Increment(ref From<FakeCallback>(self)._continued);
    }

    private sealed class FakeReadCallback : CefObject<_cef_resource_read_callback_t>
    {
        private int _result = int.MinValue;
        public int? Result => Volatile.Read(ref _result) is var r && r == int.MinValue ? null : r;

        public FakeReadCallback() => Struct->cont = &Cont;

        [UnmanagedCallersOnly] private static void Cont(_cef_resource_read_callback_t* self, int bytes) => Volatile.Write(ref From<FakeReadCallback>(self)._result, bytes);
    }

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    /// <summary>A body whose data is not there until <paramref name="gate"/> opens: not seekable, like a pipe.</summary>
    private sealed class SlowStream(Task gate, byte[] data) : Stream
    {
        private bool _done;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("async only");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // A continuation, not `await`: this type sits inside an unsafe one, where await is not allowed.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(gate.ContinueWith(_ =>
            {
                if (_done) return 0;
                _done = true;
                data.CopyTo(buffer);
                return data.Length;
            }, TaskScheduler.Default));
    }
}
