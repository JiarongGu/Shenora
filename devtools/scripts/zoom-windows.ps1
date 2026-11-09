# Drives the Chromium sample's zoom probe (ZoomProbe) with REAL input on Windows: Ctrl and the wheel over its page and over the frame of
# another origin, and Ctrl with plus, minus and 0, sent with SendInput so Chromium hears them as it hears a person's. The probe's page
# and frame report what they hear and the zoom they are at, to the log named here.
#
#   powershell -File zoom-windows.ps1 -Exe <the sample's exe> -Log <log file> [-Allow 1] [-Mode zoom|scroll] [-Posted]
#
# -Posted sends no input to the desktop: only the wheel, as WM_MOUSEWHEEL with MK_CONTROL POSTED to the page's render widget (the way
# win-input posts a click), which needs no foreground and moves no cursor. Chromium takes a wheel's Ctrl from the key state, which a
# posted message does not change, so whether the page hears Ctrl at all is what the run shows; the keys cannot be posted with Ctrl.
#
# SendInput acts on the desktop's input, not on a window, so this takes the sample's window to the foreground first and checks, before
# EVERY input, that the window is still the foreground one: if it is not (a person has clicked elsewhere, a notification took the focus)
# nothing more is sent, and it says so. The cursor is moved over the window for the wheel and put back where it was. It runs for about
# 30 s and closes the sample.
param(
  [Parameter(Mandatory = $true)][string]$Exe,
  [Parameter(Mandatory = $true)][string]$Log,
  [string]$Allow = "0",
  [string]$Mode = "zoom",
  [switch]$Posted
)

Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ShenoraInput {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION u; }
  [DllImport("user32")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
  [DllImport("user32")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32")] public static extern bool GetCursorPos(out POINT p);
  [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  [DllImport("user32")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
  [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr h);
  [DllImport("user32")] public static extern bool SetProcessDPIAware();
  [DllImport("user32")] public static extern uint MapVirtualKey(uint code, uint type);
  [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32")] public static extern bool AttachThreadInput(uint from, uint to, bool attach);
  [DllImport("user32")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("kernel32")] public static extern uint GetCurrentThreadId();

  public static bool Foreground(IntPtr h) {
    ShowWindow(h, 9);
    uint pid; uint target = GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    uint me = GetCurrentThreadId();
    AttachThreadInput(me, target, true);
    SetForegroundWindow(h);
    AttachThreadInput(me, target, false);
    return GetForegroundWindow() == h;
  }
  static INPUT Key(ushort vk, bool up) {
    var i = new INPUT(); i.type = 1;
    i.u.ki.vk = vk; i.u.ki.scan = (ushort)MapVirtualKey(vk, 0);
    i.u.ki.flags = (up ? 2u : 0u);   // KEYEVENTF_KEYUP
    return i;
  }
  public static void Press(ushort[] modifiers, ushort vk) {
    var all = new System.Collections.Generic.List<INPUT>();
    foreach (var m in modifiers) all.Add(Key(m, false));
    all.Add(Key(vk, false)); all.Add(Key(vk, true));
    for (int k = modifiers.Length - 1; k >= 0; k--) all.Add(Key(modifiers[k], true));
    SendInput((uint)all.Count, all.ToArray(), Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Wheel(ushort[] modifiers, int delta) {
    foreach (var m in modifiers) SendInput(1, new[] { Key(m, false) }, Marshal.SizeOf(typeof(INPUT)));
    var w = new INPUT(); w.type = 0; w.u.mi.flags = 0x0800; w.u.mi.mouseData = unchecked((uint)delta);
    SendInput(1, new[] { w }, Marshal.SizeOf(typeof(INPUT)));
    for (int k = modifiers.Length - 1; k >= 0; k--) SendInput(1, new[] { Key(modifiers[k], true) }, Marshal.SizeOf(typeof(INPUT)));
  }
  public delegate bool EnumChild(IntPtr h, IntPtr p);
  [DllImport("user32")] static extern bool EnumChildWindows(IntPtr parent, EnumChild cb, IntPtr p);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder text, int max);
  public static IntPtr RenderWidget(IntPtr top) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(top, (h, p) => { var c = new StringBuilder(256); GetClassNameW(h, c, 256); if (c.ToString() == "Chrome_RenderWidgetHostHWND") { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  public static void PostWheel(IntPtr widget, int screenX, int screenY, int delta, bool ctrl) {
    long w = ((long)(short)delta << 16) | (ctrl ? 0x0008u : 0u);
    long l = ((long)(ushort)screenY << 16) | (ushort)screenX;
    PostMessage(widget, 0x020A, (IntPtr)w, (IntPtr)l);
  }
  public static void Click() {
    var d = new INPUT(); d.type = 0; d.u.mi.flags = 0x0002;
    var u = new INPUT(); u.type = 0; u.u.mi.flags = 0x0004;
    SendInput(2, new[] { d, u }, Marshal.SizeOf(typeof(INPUT)));
  }
}
"@

[ShenoraInput]::SetProcessDPIAware() | Out-Null
$ctrl = [uint16]0x11; $shift = [uint16]0x10
Remove-Item -ErrorAction SilentlyContinue $Log
$data = Join-Path ([IO.Path]::GetTempPath()) ("shenora-zoom-" + [guid]::NewGuid().ToString("N"))
$info = New-Object Diagnostics.ProcessStartInfo $Exe
$info.UseShellExecute = $false
$info.EnvironmentVariables["SHENORA_SAMPLE_ZOOM_PROBE"] = $Mode
$info.EnvironmentVariables["SHENORA_SAMPLE_ZOOM_LOG"] = $Log
$info.EnvironmentVariables["SHENORA_SAMPLE_ZOOM_ALLOW"] = $Allow
$info.Arguments = "--app-root `"$data`""
$app = [Diagnostics.Process]::Start($info)
$cursor = New-Object ShenoraInput+POINT
[ShenoraInput]::GetCursorPos([ref]$cursor) | Out-Null
$script:lost = $false
try {
  # Both documents loaded, and the window up.
  $deadline = (Get-Date).AddSeconds(120)
  while ((Get-Date) -lt $deadline) {
    if ($app.HasExited) { throw "the sample ended (exit $($app.ExitCode)) before its page loaded" }
    if ((Test-Path $Log) -and (Select-String -Path $Log -Pattern "loaded main" -Quiet) -and (Select-String -Path $Log -Pattern "loaded frame" -Quiet)) { break }
    Start-Sleep -Milliseconds 300
  }
  $app.Refresh()
  $hwnd = $app.MainWindowHandle
  if ($hwnd -eq [IntPtr]::Zero) { throw "the sample has no main window" }
  if ($Posted) {
    $widget = [ShenoraInput]::RenderWidget($hwnd)
    if ($widget -eq [IntPtr]::Zero) { throw "no Chrome_RenderWidgetHostHWND under the sample's window" }
    $scale = [ShenoraInput]::GetDpiForWindow($hwnd) / 96.0
    $origin = New-Object ShenoraInput+POINT
    [ShenoraInput]::ClientToScreen($hwnd, [ref]$origin) | Out-Null
    function PostedAt($x, $y, $delta, $n, $ctrl) { for ($i = 0; $i -lt $n; $i++) { [ShenoraInput]::PostWheel($widget, [int]($origin.X + $x * $scale), [int]($origin.Y + $y * $scale), $delta, $ctrl); Start-Sleep -Milliseconds 120 } }
    function PostedMark($text) { Add-Content -Path $Log -Value ("{0} ZOOM: MARK {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $text); Start-Sleep -Milliseconds 800 }
    Start-Sleep -Milliseconds 1500
    PostedMark "posted ctrl+wheel-up over page"; PostedAt 700 380 120 3 $true
    PostedMark "posted ctrl+wheel-down over page"; PostedAt 700 380 -120 5 $true
    PostedMark "posted ctrl+wheel-up over frame"; PostedAt 240 160 120 3 $true
    PostedMark "posted ctrl+wheel-down over frame"; PostedAt 240 160 -120 3 $true
    PostedMark "posted plain wheel over page"; PostedAt 700 380 -120 2 $false
    PostedMark "end"
    Start-Sleep -Milliseconds 1200
    return
  }
  if (-not [ShenoraInput]::Foreground($hwnd)) { throw "the sample's window would not take the foreground; no input was sent" }
  Start-Sleep -Milliseconds 1500
  $scale = [ShenoraInput]::GetDpiForWindow($hwnd) / 96.0
  $origin = New-Object ShenoraInput+POINT
  [ShenoraInput]::ClientToScreen($hwnd, [ref]$origin) | Out-Null
  function At($x, $y) { [ShenoraInput]::SetCursorPos([int]($origin.X + $x * $scale), [int]($origin.Y + $y * $scale)) | Out-Null }
  function Guard() {
    if ([ShenoraInput]::GetForegroundWindow() -ne $hwnd) { $script:lost = $true; throw "the sample's window lost the foreground; stopping before any more input" }
  }
  function Mark($text) { Guard; Add-Content -Path $Log -Value ("{0} ZOOM: MARK {1}" -f (Get-Date -Format "HH:mm:ss.fff"), $text); Start-Sleep -Milliseconds 800 }
  function WheelN($mods, $delta, $n) { for ($i = 0; $i -lt $n; $i++) { Guard; [ShenoraInput]::Wheel($mods, $delta); Start-Sleep -Milliseconds 90 } }
  function Keys($mods, $vk, $name) { Mark $name; Guard; [ShenoraInput]::Press($mods, $vk) }
  $page = @(700, 500); $frame = @(240, 160)
  if ($Mode -eq "scroll") {
    At $page[0] $page[1]; Mark "scrolling"
    for ($i = 0; $i -lt 760; $i++) { Guard; [ShenoraInput]::Wheel(@(), -120); Start-Sleep -Milliseconds 15; if ($i % 100 -eq 99 -and (Select-String -Path $Log -Pattern "done" -Quiet)) { break } }
  } else {
    At $page[0] $page[1]; Guard; [ShenoraInput]::Click(); Start-Sleep -Milliseconds 500
    Mark "ctrl+wheel-up over page"; WheelN @($ctrl) 120 3
    Mark "ctrl+wheel-down over page"; WheelN @($ctrl) -120 5
    Keys @($ctrl, $shift) 0xBB "ctrl+shift+plus"
    Keys @($ctrl) 0xBB "ctrl+equal"
    Keys @($ctrl) 0xBD "ctrl+minus"
    Keys @($ctrl) 0x30 "ctrl+0"
    Keys @($ctrl) 0x6B "ctrl+numpad-add"
    Keys @($ctrl) 0x6D "ctrl+numpad-subtract"
    Keys @($ctrl) 0x60 "ctrl+numpad-0"
    At $frame[0] $frame[1]; Start-Sleep -Milliseconds 300
    Mark "ctrl+wheel-up over frame"; WheelN @($ctrl) 120 3
    Mark "ctrl+wheel-down over frame"; WheelN @($ctrl) -120 3
    Guard; [ShenoraInput]::Click(); Start-Sleep -Milliseconds 400
    Keys @($ctrl) 0xBD "ctrl+minus with the frame focused"
    Keys @($ctrl, $shift) 0xBB "ctrl+shift+plus with the frame focused"
    Keys @($ctrl) 0x30 "ctrl+0 with the frame focused"
    Mark "plain wheel over frame"; WheelN @() -120 2
    At $page[0] $page[1]; Start-Sleep -Milliseconds 300
    Mark "plain wheel over page"; WheelN @() -120 2
    Keys @() 0xBD "plain minus"
    Mark "end"
  }
  Start-Sleep -Milliseconds 1200
}
finally {
  [ShenoraInput]::SetCursorPos($cursor.X, $cursor.Y) | Out-Null
  if (-not $app.HasExited) {
    [ShenoraInput]::PostMessage($app.MainWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null   # WM_CLOSE
    if (-not $app.WaitForExit(15000)) { Start-Process taskkill -ArgumentList "/PID", $app.Id, "/T", "/F" -WindowStyle Hidden -Wait }
  }
  Start-Sleep -Milliseconds 1500
  Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $data
}
if ($script:lost) { exit 3 }
