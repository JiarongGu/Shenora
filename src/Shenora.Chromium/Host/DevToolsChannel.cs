using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Shenora.Chromium.Interop;

namespace Shenora.Chromium.Host;

/// <summary>
/// One browser's DevTools protocol, in process: CEF hands a message to the browser's own agent
/// (<c>send_dev_tools_message</c>) and its answers and events to an observer, with no debugging port and no socket.
/// Everything here runs on CEF's UI thread, where CEF calls the observer too.
/// </summary>
internal sealed unsafe class DevToolsChannel : IDisposable
{
    private readonly Observer _observer;
    private _cef_browser_host_t* _host;          // a reference of our own, released on dispose
    private _cef_registration_t* _registration;  // releasing it removes the observer
    private int _nextId;
    private readonly Dictionary<int, TaskCompletionSource<string>> _pending = [];
    private readonly Dictionary<string, List<Action<string>>> _events = new(StringComparer.Ordinal);

    /// <param name="host">The browser's host; the channel adds a reference of its own.</param>
    public DevToolsChannel(_cef_browser_host_t* host)
    {
        ((_cef_base_ref_counted_t*)host)->add_ref((_cef_base_ref_counted_t*)host);
        _host = host;
        _observer = new Observer(this);
        _registration = host->add_dev_tools_message_observer(host, _observer.ForCef());
    }

    /// <summary>
    /// Call a protocol method and complete with its result as JSON; fault with the protocol's own error when it refuses.
    /// UI thread. <paramref name="parametersJson"/> must be a JSON object.
    /// </summary>
    public Task<string> CallAsync(string method, string parametersJson)
    {
        if (_host == null) return Task.FromException<string>(new ObjectDisposedException(nameof(DevToolsChannel)));
        var id = ++_nextId;
        var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        var message = Encoding.UTF8.GetBytes(
            $"{{\"id\":{id},\"method\":{JsonSerializer.Serialize(method)},\"params\":{(string.IsNullOrWhiteSpace(parametersJson) ? "{}" : parametersJson)}}}");
        // Called from inside CEF's notification of this observer (an event handler answering the event: a screencast
        // frame's ack, a paused request's continue), the send is posted behind it: sent from in there, the reply notifies
        // the observers again while they are still being notified, which Chromium reports as a failed check (measured).
        if (_notifying > 0)
        {
            if (!CefTask.Post(cef_thread_id_t.TID_UI, () => Send(id, method, message)))
                Refused(id, method);
        }
        else Send(id, method, message);
        return answer.Task;
    }

    // Set while CEF is notifying the observer.
    private int _notifying;

    private void Send(int id, string method, byte[] message)
    {
        if (_host == null)
        {
            Refused(id, method);
            return;
        }
        int sent;
        fixed (byte* m = message) sent = _host->send_dev_tools_message(_host, m, (nuint)message.Length);
        if (sent == 0) Refused(id, method);
    }

    private void Refused(int id, string method)
    {
        if (_pending.Remove(id, out var answer))
            answer.TrySetException(new InvalidOperationException($"The browser refused the DevTools call {method}."));
    }

    /// <summary>Receive an event's parameters as JSON until the returned handle is disposed. UI thread.</summary>
    public IDisposable Subscribe(string eventName, Action<string> onEvent)
    {
        if (!_events.TryGetValue(eventName, out var handlers)) _events[eventName] = handlers = [];
        handlers.Add(onEvent);
        return new Subscription(this, eventName, onEvent);
    }

    /// <summary>Stop observing: every call still waiting fails, and CEF lets go of the observer. UI thread.</summary>
    public void Dispose()
    {
        if (_host == null) return;
        if (_registration != null) ((_cef_base_ref_counted_t*)_registration)->release((_cef_base_ref_counted_t*)_registration);
        _registration = null;
        ((_cef_base_ref_counted_t*)_host)->release((_cef_base_ref_counted_t*)_host);
        _host = null;
        foreach (var waiting in _pending.Values) waiting.TrySetException(new ObjectDisposedException(nameof(DevToolsChannel)));
        _pending.Clear();
        _events.Clear();
        _observer.Release();
    }

    private void Result(int id, bool success, string json)
    {
        if (!_pending.Remove(id, out var answer)) return;
        if (success) answer.TrySetResult(json);
        else answer.TrySetException(new InvalidOperationException($"DevTools answered an error: {json}"));
    }

    private void Event(string method, string json)
    {
        if (!_events.TryGetValue(method, out var handlers)) return;
        // A copy: a handler may unsubscribe itself.
        foreach (var handler in handlers.ToArray()) AppCallback.Run(() => handler(json));
    }

    private sealed class Subscription(DevToolsChannel channel, string eventName, Action<string> onEvent) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (channel._events.TryGetValue(eventName, out var handlers)) handlers.Remove(onEvent);
        }
    }

    private sealed class Observer : CefObject<_cef_dev_tools_message_observer_t>
    {
        // Weak in effect: the channel releases this observer as it goes, and CEF's reference then lets it be freed.
        private DevToolsChannel? _channel;

        public Observer(DevToolsChannel channel)
        {
            _channel = channel;
            Struct->on_dev_tools_method_result = &MethodResult;
            Struct->on_dev_tools_event = &DevToolsEvent;
        }

        private protected override void OnFreed() => _channel = null;

        [UnmanagedCallersOnly]
        private static void MethodResult(_cef_dev_tools_message_observer_t* self, _cef_browser_t* browser, int messageId, int success,
            void* result, nuint resultSize)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var channel = From<Observer>(self)._channel;
            if (channel is null) return;
            var json = result == null ? "{}" : Encoding.UTF8.GetString((byte*)result, (int)resultSize);
            channel._notifying++;
            try { AppCallback.Run(() => channel.Result(messageId, success == 1, json)); }
            finally { channel._notifying--; }
        }

        [UnmanagedCallersOnly]
        private static void DevToolsEvent(_cef_dev_tools_message_observer_t* self, _cef_browser_t* browser, _cef_string_utf16_t* method,
            void* parameters, nuint parametersSize)
        {
            using var b = new CefRef<_cef_browser_t>(browser);
            var channel = From<Observer>(self)._channel;
            if (channel is null) return;
            var name = CefStrings.Read(method);
            var json = parameters == null ? "{}" : Encoding.UTF8.GetString((byte*)parameters, (int)parametersSize);
            channel._notifying++;
            try { AppCallback.Run(() => channel.Event(name, json)); }
            finally { channel._notifying--; }
        }
    }
}
