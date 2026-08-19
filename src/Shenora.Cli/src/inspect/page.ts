// The page a device opens. One self-contained string: no imports, no build step, no framework — so it
// cannot break when the app's own dependencies do, which is the state it exists to diagnose.
import { INSPECT_DEFAULT_PORT } from './service.js';

export interface PageOptions {
  /** Shown in the heading, so two services on one LAN are told apart. */
  title?: string;
  /** The app's origin, for the reachability probe. Empty disables that panel. */
  appOrigin?: string;
}

export function inspectPage(options: PageOptions = {}): string {
  const title = options.title ?? 'Shenora device diagnostics';
  const appOrigin = options.appOrigin ?? '';
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)}</title>
<style>
  :root { color-scheme: light dark; }
  body { font: 15px/1.5 system-ui, -apple-system, sans-serif; margin: 0; padding: 16px 14px 48px; }
  h1 { font-size: 17px; margin: 0 0 4px; }
  h2 { font-size: 13px; text-transform: uppercase; letter-spacing: .06em; opacity: .6;
       margin: 22px 0 6px; }
  .sub { opacity: .6; font-size: 13px; margin: 0 0 8px; }
  table { border-collapse: collapse; width: 100%; font-size: 13px; }
  td { padding: 3px 8px 3px 0; vertical-align: top; border-bottom: 1px solid rgba(128,128,128,.18); }
  td:first-child { opacity: .7; white-space: nowrap; width: 40%; }
  .ok { color: #1a7f37; } .no { color: #b3261e; } .warn { color: #9a6700; }
  pre { background: rgba(128,128,128,.12); padding: 10px; border-radius: 8px; overflow: auto;
        font-size: 12px; max-height: 40vh; }
  button { font: inherit; padding: 7px 13px; border-radius: 8px; border: 1px solid rgba(128,128,128,.4);
           background: transparent; color: inherit; margin: 0 6px 6px 0; }
  .dot { display: inline-block; width: 8px; height: 8px; border-radius: 50%; margin-right: 6px;
         background: #b3261e; vertical-align: middle; }
  input { font: inherit; font-family: ui-monospace, monospace; padding: 7px 9px; border-radius: 8px;
          border: 1px solid rgba(128,128,128,.4); background: transparent; color: inherit;
          width: min(100%, 420px); margin: 0 6px 6px 0; }
  .steps { font-size: 13px; opacity: .8; padding-left: 20px; margin: 6px 0 0; }
  .steps li { margin: 2px 0; }
  code { font-family: ui-monospace, monospace; background: rgba(128,128,128,.15); padding: 1px 5px;
         border-radius: 4px; }
  .dot.on { background: #1a7f37; }
</style>
</head>
<body>
<h1>${escapeHtml(title)}</h1>
<p class="sub" id="who">…</p>

<h2>Remote control</h2>
<p class="sub"><span class="dot" id="dot"></span><span id="rstat">connecting…</span></p>
<pre id="rlog">waiting for the inspect service…</pre>

<!-- ── The OPERATOR half. Rendered only on loopback; see the script's note on why that is cosmetic. -->
<div id="operator" hidden>
  <h2>Operator — this machine</h2>
  <p class="sub">You are on loopback, so this half is yours. A phone opening the LAN URL sees none of it.</p>

  <ol class="steps" id="steps">
    <li>Open the LAN URL printed by <code>shenora inspect serve</code> on the device.</li>
    <li>It appears under <b>Devices</b> below within a second or two.</li>
    <li>Run an expression in its page, or a command on the Mac.</li>
  </ol>

  <h2>Devices</h2>
  <table id="devices"><tr><td colspan="2">none yet</td></tr></table>

  <h2>Run in the device's page</h2>
  <input id="expr" type="text" value="location.href" spellcheck="false">
  <button id="run-eval">Evaluate</button>

  <h2>Run on the Mac (ssh)</h2>
  <input id="cmd" type="text" value="xcodebuild -version" spellcheck="false">
  <button id="run-host">Run</button>

  <pre id="out">(nothing run yet)</pre>
</div>

<h2>This device</h2>
<table id="env"></table>

<h2>Report</h2>
<button id="copy">Copy report</button>
<pre id="report">…</pre>

<script>
(function () {
  'use strict';
  var APP_ORIGIN = ${JSON.stringify(appOrigin)};
  var report = {};
  var rlog = [];

  function text(id, s) { document.getElementById(id).textContent = s; }
  function rows(id, pairs) {
    document.getElementById(id).innerHTML = pairs.map(function (p) {
      return '<tr><td>' + esc(p[0]) + '</td><td class="' + (p[2] || '') + '">' + esc(p[1]) + '</td></tr>';
    }).join('');
  }
  function esc(s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c];
    });
  }
  function log(line) {
    rlog.push(new Date().toLocaleTimeString() + '  ' + line);
    if (rlog.length > 12) rlog.shift();
    text('rlog', rlog.join('\\n'));
  }

  // ── Which device is this? The name is how an operator addresses it, so it has to be stable and
  // human-readable. The socket address is added by the SERVER — a page cannot learn its own LAN address.
  var ua = navigator.userAgent;
  var device = (function () {
    if (/iPhone/.test(ua)) return 'iPhone';
    if (/iPad/.test(ua)) return 'iPad';
    if (/Android/.test(ua)) return 'Android';
    if (/Macintosh/.test(ua)) return 'Mac';
    if (/Windows/.test(ua)) return 'Windows';
    return 'device';
  })() + '-' + Math.random().toString(36).slice(2, 6);

  // Shell detection reports what is THERE rather than asserting which shell this is — a wrong claim
  // here sends the next hour in the wrong direction.
  var shell = [];
  if (window.chrome && window.chrome.webview) shell.push('WebView2');
  if (window.webkit && window.webkit.messageHandlers) shell.push('WKWebView');
  if (/\\bwv\\b/.test(ua)) shell.push('Android WebView');
  if (window.shenora) shell.push('shenora bridge');

  report.device = device;
  report.userAgent = ua;
  report.shell = shell.join(', ') || 'none detected (a plain browser?)';
  report.screen = window.innerWidth + '×' + window.innerHeight + ' @' + (window.devicePixelRatio || 1) + 'x';
  report.secureContext = !!window.isSecureContext;
  report.clipboardApi = !!(navigator.clipboard && navigator.clipboard.writeText);
  report.location = location.href;

  text('who', device + ' · ' + (shell.join(', ') || 'plain browser'));
  rows('env', [
    ['device', device],
    ['shell', report.shell],
    ['viewport', report.screen],
    ['secure context', report.secureContext ? 'yes' : 'no — plain http, so no clipboard/crypto APIs',
      report.secureContext ? 'ok' : 'warn'],
    ['user agent', ua],
  ]);
  text('report', JSON.stringify(report, null, 2));

  // ── Can this device reach the app's server? The response is opaque under no-cors, so the SIGNAL is
  // whether the promise resolves at all, and how fast. That is enough to separate "the network is fine,
  // the app is broken" from "this device cannot see the server", which is the split that matters.
  if (APP_ORIGIN) {
    var t0 = (performance && performance.now) ? performance.now() : Date.now();
    fetch(APP_ORIGIN, { mode: 'no-cors', cache: 'no-store' }).then(function () {
      var ms = Math.round(((performance && performance.now) ? performance.now() : Date.now()) - t0);
      report.appReachable = 'yes (' + ms + 'ms)';
    }).catch(function (e) {
      report.appReachable = 'NO — ' + (e && e.message ? e.message : 'unreachable');
    }).then(function () {
      text('report', JSON.stringify(report, null, 2));
    });
  }

  // ── Clipboard, with the fallback that is the NORMAL path here: a webview on plain http over the LAN
  // is not a secure context and has no navigator.clipboard at all.
  document.getElementById('copy').addEventListener('click', function () {
    var body = JSON.stringify(report, null, 2);
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(body).then(function () { log('report copied'); }, selectInstead);
    } else {
      selectInstead();
    }
    function selectInstead() {
      var pre = document.getElementById('report');
      var range = document.createRange();
      range.selectNodeContents(pre);
      var sel = window.getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
      log('no clipboard API here — the report is selected, copy it by hand');
    }
  });

  // ── The device's half of the channel: announce, then drain a queue.
  var since = null;
  var on = false;
  var ran = 0;

  function paint() {
    document.getElementById('dot').className = 'dot' + (on ? ' on' : '');
    text('rstat', (on ? 'connected' : 'not connected') + ' · ' + ran + ' action' + (ran === 1 ? '' : 's') + ' run');
  }

  function hello() {
    return fetch('/api/inspect/hello', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ device: device, report: report }),
    }).then(function (r) { return r.json(); }).then(function (d) {
      log('announced as ' + device);
      return d;
    });
  }

  function post(kind, ok, value) {
    return fetch('/api/inspect/results', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ device: device, kind: kind, ok: ok, value: value }),
    });
  }

  function runAction(a) {
    ran++;
    if (a.kind === 'reload') { post('reload', true, 'reloading').then(function () { location.reload(); }); return; }
    if (a.kind === 'report') { post('report', true, JSON.stringify(report)); log('sent report'); return; }
    // \`new Function\` rather than a bare eval, so the expression closes over \`report\` and nothing else.
    try {
      var value = (new Function('report', 'return (' + a.payload + ')'))(report);
      Promise.resolve(value).then(function (v) {
        var body;
        // A DOM node or a function has no JSON form; String() is a worse answer than nothing only if
        // it is silent about being one.
        try { body = JSON.stringify(v); if (body === undefined) body = String(v); }
        catch (e) { body = String(v); }
        post('eval', true, body);
        log('ran: ' + a.payload.slice(0, 60));
      }, function (e) {
        post('eval', false, String(e && e.message ? e.message : e));
        log('threw: ' + a.payload.slice(0, 40));
      });
    } catch (e) {
      post('eval', false, String(e && e.message ? e.message : e));
      log('failed to compile: ' + a.payload.slice(0, 40));
    }
  }

  function poll() {
    fetch('/api/inspect/actions?device=' + encodeURIComponent(device) + '&since=' + (since === null ? 0 : since),
      { cache: 'no-store' })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        // 🔴 The reconnect EDGE. A page whose service restarted under it keeps polling happily with the
        // operator's copy of its report empty forever — which reads as "the device reported nothing"
        // when the truth is "it never got to report". Re-announcing on every false→true transition is
        // the fix, which makes the line that clears \`on\` below load-bearing rather than bookkeeping.
        if (!on) { on = true; hello().catch(function () {}); }
        // ⚠ A first poll starts at the CURRENT head, not zero — otherwise a page opened an hour in
        // replays every action ever queued, which on an eval queue is actively harmful.
        if (since === null) { since = d.latest; paint(); return; }
        since = d.latest;
        (d.actions || []).forEach(runAction);
        paint();
      })
      .catch(function () { on = false; paint(); })
      .then(function () { setTimeout(poll, 1200); });
  }

  // ── The OPERATOR half, on this machine only.
  //
  // 🔴 Hiding it off-loopback is COSMETIC and must be read that way. The SERVER is the boundary: every
  // route below answers 404 to a non-loopback peer whatever the page does, and a phone could forge these
  // requests trivially. This only stops an operator panel appearing on a phone that cannot use it, which
  // would read as a broken tool rather than as a closed door.
  var isOperator = location.hostname === '127.0.0.1' || location.hostname === 'localhost'
    || location.hostname === '::1';

  if (isOperator) {
    document.getElementById('operator').hidden = false;

    var show = function (label, body) {
      text('out', label + '\\n\\n' + body);
    };

    var refreshDevices = function () {
      fetch('/api/inspect/devices', { cache: 'no-store' })
        .then(function (r) { return r.ok ? r.json() : { devices: [] }; })
        .then(function (d) {
          var listed = (d.devices || []).map(function (x) {
            return [x.name + '  ·  ' + x.address, x.polls + ' polls'];
          });
          if (listed.length === 0) listed = [['none yet', 'open the LAN URL on the device']];
          rows('devices', listed);
        })
        .catch(function () {});
    };

    // ⚠ Reads the results cursor BEFORE queueing, exactly as the CLI does: a device polling on a 1.2 s
    // loop can answer before a later cursor read would have started watching, and the reply is then
    // invisible. Same bug, same fix, stated in both places because they are separate implementations.
    document.getElementById('run-eval').addEventListener('click', function () {
      var expr = document.getElementById('expr').value;
      show('evaluating on the device…', expr);
      fetch('/api/inspect/results').then(function (r) { return r.json(); }).then(function (before) {
        var cursor = before.latest || 0;
        return fetch('/api/inspect/actions', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ kind: 'eval', payload: expr }),
        }).then(function () { return waitForResult(cursor, 15000); });
      }).then(function (hit) {
        show(hit ? (hit.device + (hit.ok ? '' : '  (threw)')) : 'no device answered within 15s',
          hit ? hit.value : 'Is one listed under Devices? A closed page cannot answer.');
      }).catch(function (e) { show('failed', String(e && e.message ? e.message : e)); });
    });

    var waitForResult = function (cursor, budgetMs) {
      var deadline = Date.now() + budgetMs;
      var attempt = function () {
        return fetch('/api/inspect/results?since=' + cursor, { cache: 'no-store' })
          .then(function (r) { return r.json(); })
          .then(function (d) {
            var hit = (d.results || []).filter(function (x) { return x.kind === 'eval'; })[0];
            if (hit) return hit;
            if (Date.now() > deadline) return null;
            return new Promise(function (r) { setTimeout(r, 400); }).then(attempt);
          });
      };
      return attempt();
    };

    // 🔴 The ssh route. This is the operator control the service always carried and the page never used
    // — the capability existed, nothing consulted it, and an unused seam is indistinguishable from a
    // broken one from the outside.
    document.getElementById('run-host').addEventListener('click', function () {
      var command = document.getElementById('cmd').value;
      show('running on the Mac…', command);
      fetch('/api/inspect/host', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ command: command }),
      }).then(function (r) {
        // ⚠ No backticks in this file's JS: the whole page is a template literal, so one would end it.
        if (r.status === 409) return { error: 'no Mac configured — see: shenora ios doctor --host' };
        return r.json();
      }).then(function (d) {
        if (d.error) { show('cannot run', d.error); return; }
        show((d.host || 'the Mac') + (d.ok ? '' : '  (exit ' + d.status + ')'), d.out || '(no output)');
      }).catch(function (e) { show('failed', String(e && e.message ? e.message : e)); });
    });

    refreshDevices();
    setInterval(refreshDevices, 2000);
  }

  paint();
  hello().catch(function () {});
  // Again shortly, to catch the async reachability probe landing after the first announce.
  setTimeout(function () { hello().catch(function () {}); }, 2500);
  poll();
})();
</script>
</body>
</html>
`;
}

function escapeHtml(s: string): string {
  return s.replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c] ?? c));
}

export { INSPECT_DEFAULT_PORT };
