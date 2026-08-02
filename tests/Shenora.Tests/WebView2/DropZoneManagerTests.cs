using Shenora.Core;
using Shenora.Ipc;
using Shenora.Tests.TestSupport;
using Shenora.Windows;
using WebView2Control = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Shenora.Tests.WebView2;

/// <summary>
/// Lifecycle + coordinate tests over real (invisible) controls, on STA threads — the overlays
/// set <c>AllowDrop = true</c>, whose OLE registration requires STA (the P4.2 lesson). Real
/// drag-drop, occlusion checks, and per-monitor DPI are the sample-e2e/manual subject.
/// </summary>
public class DropZoneManagerTests
{
    private static (Form Form, WebView2Control WebView, EventBus Bus, DropZoneManager Manager) CreateFixture()
    {
        var form = new Form();
        var webView = new WebView2Control { Dock = DockStyle.Fill };
        form.Controls.Add(webView);
        _ = form.Handle; // coordinate conversion + overlay parenting need handles
        var bus = new EventBus();
        var manager = new DropZoneManager(new DropZoneManagerOptions
        {
            WebView = webView,
            ParentForm = form,
            EventBus = bus,
        });
        return (form, webView, bus, manager);
    }

    [Fact]
    public void Register_creates_a_parented_overlay_and_update_moves_it() => Sta.Run(() =>
    {
        var (form, _, _, manager) = CreateFixture();
        using (form)
        using (manager)
        {
            manager.RegisterZone("z1", 10, 20, 100, 50);

            Assert.True(manager.HasZone("z1"));
            var overlay = manager.TryGetOverlay("z1")!;
            Assert.Contains(overlay, form.Controls.Cast<Control>());
            // DPI-unaware test process → DeviceDpi 96 → CSS == physical; the WebView fills the
            // form, so form coordinates equal the CSS rect here.
            Assert.Equal(new Rectangle(10, 20, 100, 50), overlay.Bounds);

            manager.UpdateZoneBounds("z1", 15, 25, 110, 60);
            Assert.Equal(new Rectangle(15, 25, 110, 60), overlay.Bounds);
            Assert.Equal(1, manager.ZoneCount); // update never duplicates
        }
    });

    [Fact]
    public void Unregister_disposes_and_clear_all_empties() => Sta.Run(() =>
    {
        var (form, _, _, manager) = CreateFixture();
        using (form)
        using (manager)
        {
            manager.RegisterZone("z1", 0, 0, 10, 10);
            manager.RegisterZone("z2", 20, 0, 10, 10);
            var first = manager.TryGetOverlay("z1")!;

            manager.UnregisterZone("z1");
            Assert.True(first.IsDisposed);
            Assert.False(manager.HasZone("z1"));

            manager.ClearAll();
            Assert.Equal(0, manager.ZoneCount);

            manager.UnregisterZone("never-existed"); // no-op, no throw
        }
    });

    [Fact]
    public void Reapply_recomputes_bounds_from_the_stored_css_rects() => Sta.Run(() =>
    {
        var (form, _, _, manager) = CreateFixture();
        using (form)
        using (manager)
        {
            manager.RegisterZone("z1", 10, 20, 100, 50);
            var overlay = manager.TryGetOverlay("z1")!;
            overlay.UpdateOverlayBounds(0, 0, 1, 1); // knock it out of place

            manager.ReapplyAllZoneBounds(); // what the DpiChanged handler runs

            Assert.Equal(new Rectangle(10, 20, 100, 50), overlay.Bounds);
        }
    });

    [Fact]
    public void Notifications_emit_on_the_bus_with_the_wire_shape() => Sta.Run(() =>
    {
        var (form, _, bus, manager) = CreateFixture();
        using (form)
        using (manager)
        {
            var received = new List<EventMessage>();
            bus.SubscribeToModule(DropZoneManager.Module, message =>
            {
                lock (received) received.Add(message);
                return Task.CompletedTask;
            });

            manager.NotifyDragEnter("z1");
            manager.NotifyDragLeave("z1");
            manager.NotifyFileDrop("z1", ["C:\\a.txt", "C:\\b.txt"], new Point(3, 4));

            Assert.Equal(["DRAG_ENTER", "DRAG_LEAVE", "FILE_DROP"], received.Select(m => m.Type));
            // The wire shape useDropZone reads (camelCase via IpcJson when the bridge forwards).
            var drop = IpcJson.SerializeToElement(received[2].Payload!);
            Assert.Equal("z1", drop.GetProperty("zoneId").GetString());
            Assert.Equal(2, drop.GetProperty("files").GetArrayLength());
            Assert.Equal(4, drop.GetProperty("position").GetProperty("y").GetInt32());
        }
    });

    [Fact]
    public void Pre_handle_calls_proceed_inline_without_recursing() => Sta.Run(() =>
    {
        // Regression: the pre-handle marshal used to re-invoke the CALLER as its own action —
        // unbounded recursion → uncatchable StackOverflow (reachable via a startup-failure
        // dispose). Pre-handle must proceed inline.
        var form = new Form();
        var webView = new WebView2Control { Dock = DockStyle.Fill };
        form.Controls.Add(webView); // deliberately NO form.Handle
        using (form)
        using (var manager = new DropZoneManager(new DropZoneManagerOptions
        {
            WebView = webView,
            ParentForm = form,
            EventBus = new EventBus(),
        }))
        {
            manager.RegisterZone("z1", 0, 0, 10, 10);
            manager.ClearAll();
            manager.UnregisterZone("z1");
        }
    });

    [Fact]
    public void Dispose_detaches_form_handlers_and_destroys_overlays() => Sta.Run(() =>
    {
        var (form, _, _, manager) = CreateFixture();
        using (form)
        {
            manager.RegisterZone("z1", 0, 0, 10, 10);
            var overlay = manager.TryGetOverlay("z1")!;

            manager.Dispose();

            Assert.True(overlay.IsDisposed);
            Assert.Equal(0, manager.ZoneCount);
        }
    });

    [Fact]
    public async Task Facade_routes_the_client_messages() => await Task.Run(() => Sta.Run(() =>
    {
        var (form, _, _, manager) = CreateFixture();
        using (form)
        using (manager)
        {
            var facade = new DropZoneFacade(manager);
            var dispatcher = new MessageDispatcher().UseErrorHandler().MapModule(facade);

            IpcResponse Send(string type, object payload) =>
                dispatcher.DispatchAsync(new IpcRequest
                {
                    Module = DropZoneManager.Module,
                    Type = type,
                    Payload = IpcJson.SerializeToElement(payload),
                }).GetAwaiter().GetResult();

            Assert.True(Send("REGISTER", new { zoneId = "z1", x = 1, y = 2, width = 30, height = 40 }).Success);
            Assert.True(manager.HasZone("z1"));

            Assert.True(Send("UPDATE", new { zoneId = "z1", x = 5, y = 6, width = 30, height = 40 }).Success);
            Assert.Equal(new Rectangle(5, 6, 30, 40), manager.TryGetOverlay("z1")!.Bounds);

            Assert.True(Send("SHOW", new { zoneId = "z1" }).Success);

            Assert.True(Send("UNREGISTER", new { zoneId = "z1" }).Success);
            Assert.False(manager.HasZone("z1"));

            var missing = Send("REGISTER", new { zoneId = "z2" }); // no bounds
            Assert.False(missing.Success);
            Assert.Equal(IpcErrorCodes.MissingPayloadValue, missing.Error!.Code);

            var unknown = Send("NOPE", new { });
            Assert.Equal(IpcErrorCodes.NoHandler, unknown.Error!.Code);
        }
    }));
}
