import {
  getBridge,
  isShenoraAvailable,
  useDropZone,
  useShenoraEvent,
  useShenoraQuery,
  useWindowMaximized,
  WindowCommands,
  type ShellInfo,
} from '@shenora/react';
import { useEffect, useMemo, useRef, useState } from 'react';

/**
 * The Chromium sample's page (samples/Shenora.Sample.Chromium): the same page on macOS, Linux and Windows. Almost
 * everything it does is the app's PORTABLE logic, `SAMPLE_LOGIC` (samples/Shenora.Sample.Logic), which the Windows
 * and MAUI samples run unchanged; the rest is the kit's own routes (window commands, drop zones).
 */
const LOGIC = 'SAMPLE_LOGIC';

// A dialog waits on a person, so no timeout: the host answers when it closes.
const untilClosed = { timeoutMs: Infinity };

const ok: React.CSSProperties = { color: '#7fd18c', fontWeight: 600 };
const quiet: React.CSSProperties = { color: '#9a9a9a' };
const row: React.CSSProperties = { margin: '0.5rem 0' };

function TitleBar({ commands }: { commands: WindowCommands }) {
  const maximized = useWindowMaximized(commands);
  return (
    <header
      className="drag"
      style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', height: '2.2rem',
        paddingLeft: '0.8rem', background: '#252525', color: '#9a9a9a', fontSize: '0.85rem', userSelect: 'none' }}
    >
      <span>神阙 Shenora · Chromium shell</span>
      <div className="no-drag">
        <button style={{ border: 'none', background: 'none' }} onClick={() => void commands.minimize()}>─</button>
        <button style={{ border: 'none', background: 'none' }} onClick={() => void commands.toggleMaximize()}>
          {maximized ? '❐' : '☐'}
        </button>
        <button style={{ border: 'none', background: 'none' }} onClick={() => void commands.close()}>✕</button>
      </div>
    </header>
  );
}

export function App() {
  const hosted = isShenoraAvailable();
  const commands = useMemo(() => new WindowCommands(), []);

  // The ready handshake answers with what this app composed (Program.cs, ShellInfo): render from it, never from
  // the OS.
  const [shell, setShell] = useState<ShellInfo>();
  useEffect(() => {
    if (!hosted) return;
    getBridge().notifyReady().then(setShell, (error: unknown) => console.error('[sample] handshake failed', error));
  }, [hosted]);
  const can = (capability: string) => shell?.capabilities.includes(capability) ?? false;

  // The splash is held (Program.cs, HoldUntilClosed) until the page has painted its first screen with what the
  // handshake answered: the frame after the next is on screen, so the splash's skeleton turns into the page itself.
  useEffect(() => {
    if (!hosted || !shell) return;
    requestAnimationFrame(() => requestAnimationFrame(() => {
      commands.closeSplash().catch((error: unknown) => console.error('[sample] closeSplash failed', error));
    }));
  }, [hosted, shell, commands]);

  // A typed round trip into the portable logic.
  const echo = useShenoraQuery<{ echoed: string; length: number }>(LOGIC, 'ECHO', { payload: { text: 'shenora' }, enabled: hosted });

  // Native dialogs, through the app's own logic around them.
  const [file, setFile] = useState<string>();
  const pick = () =>
    getBridge()
      .invoke<{ success: boolean; filePath?: string }>(LOGIC, 'PICK_FILE', untilClosed)
      .then((r) => setFile(r.success ? `picked ${r.filePath}` : 'cancelled'), (e: Error) => setFile(e.message));
  const save = () =>
    getBridge()
      .invoke<{ success: boolean; filePath?: string }>(LOGIC, 'SAVE_TEXT', { payload: { text: 'hello from the Chromium shell' }, ...untilClosed })
      .then((r) => setFile(r.success ? `saved ${r.filePath ?? ''}` : 'cancelled'), (e: Error) => setFile(e.message));

  // The clipboard.
  const [clip, setClip] = useState<string>();
  const copy = () => getBridge().invoke(LOGIC, 'COPY_TEXT', { payload: { text: `copied at ${new Date().toLocaleTimeString()}` } }).then(() => setClip('copied'));
  const paste = () => getBridge().invoke<{ text?: string }>(LOGIC, 'READ_CLIPBOARD').then((r) => setClip(r.text ?? '(no text)'));

  // Files dropped from the OS arrive as their real paths.
  const dropRef = useRef<HTMLDivElement>(null);
  const [dropped, setDropped] = useState<string[]>();
  useDropZone({ targetRef: dropRef, onDrop: setDropped, zoneId: 'chromium-drop', dropClassName: 'drop-hover', enabled: can('dropZones') });

  // Work the host queues, reported as events while it runs.
  const [missions, setMissions] = useState<Record<string, string>>({});
  useShenoraEvent<{ missionId: string; kind: string; state: string }>(LOGIC, 'MISSION_UPDATED', (m) =>
    setMissions((all) => ({ ...all, [m.missionId]: `${m.kind}: ${m.state}` })));

  // A native event: the tray's own item (Program.cs).
  const [hello, setHello] = useState<string>();
  useShenoraEvent<{ at: string }>('SAMPLE_CHROMIUM', 'TRAY_HELLO', (e) => setHello(`the tray said hello at ${e.at}`));

  // The app launched again while running: this window came forward, and that launch's arguments arrive here.
  const [launched, setLaunched] = useState<string>();
  useShenoraEvent<{ arguments: string[] }>('SAMPLE_CHROMIUM', 'LAUNCHED_AGAIN', (e) =>
    setLaunched(`launched again with ${e.arguments.length ? e.arguments.join(' · ') : 'no arguments'}`));

  return (
    <>
      <TitleBar commands={commands} />
      <main style={{ maxWidth: '44rem', margin: '1.5rem auto', padding: '0 1rem' }}>
        <h1 style={{ fontWeight: 300, letterSpacing: '0.1em' }}>神阙 Shenora</h1>
        <p style={row}>
          shell: {shell ? <span style={ok}>{shell.name} · {shell.capabilities.join(', ')}</span> : <span style={quiet}>{hosted ? 'handshaking…' : 'no host (plain browser)'}</span>}
        </p>
        <p style={row}>
          ipc: {echo.data ? <span style={ok}>ECHO("shenora") → {echo.data.echoed} ({echo.data.length})</span> : <span style={quiet}>{echo.error?.message ?? '…'}</span>}
        </p>
        <p style={row}>
          <button disabled={!can('filePicker')} onClick={pick}>pick a file</button>{' '}
          <button disabled={!can('savePicker')} onClick={save}>save a file</button>{' '}
          <span style={file ? ok : quiet}>{file ?? 'dialogs: idle'}</span>
        </p>
        <p style={row}>
          <button disabled={!hosted} onClick={copy}>copy</button>{' '}
          <button disabled={!hosted} onClick={paste}>paste</button>{' '}
          <span style={clip ? ok : quiet}>{clip ?? 'clipboard: idle'}</span>
        </p>
        <div ref={dropRef} style={{ margin: '0.75rem 0', padding: '0.8rem 1rem', border: '1px dashed #555', borderRadius: 8, ...quiet }}>
          {dropped?.length ? <span style={ok}>dropped: {dropped.join(', ')}</span> : 'drop files here (their real paths come back)'}
        </div>
        <p style={row}>
          <button disabled={!hosted} onClick={() => getBridge().post(LOGIC, 'SCHEDULE_DEMO')}>queue 4 missions</button>{' '}
          <span style={Object.keys(missions).length ? ok : quiet}>
            {Object.values(missions).join(' · ') || 'missions: none yet'}
          </span>
        </p>
        <p style={row}>
          <span style={hello ? ok : quiet}>{hello ?? (can('tray') ? 'tray: try its "Say hello to the page"' : 'tray: none')}</span>
        </p>
        <p style={row}>
          <span style={launched ? ok : quiet}>{launched ?? 'single instance: launch the app again'}</span>
        </p>
        <p style={row}>
          <button disabled={!hosted} onClick={() => getBridge().invoke(LOGIC, 'OPEN_URL', { payload: { url: 'https://github.com/JiarongGu/Shenora' } })}>
            open the repo in your browser
          </button>
        </p>
      </main>
    </>
  );
}
