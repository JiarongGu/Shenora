# Prints, one JSON line per change, the top-level windows of the processes of one name as they appear, move, show and go:
# the time (Unix ms, the caller's own clock), the window's handle and class, whether it is visible, and its rectangle. A
# process is named once, when first seen, with the time it was created ({"process":<pid>,"started":<Unix ms>}).
#
#   powershell -File watch-windows.ps1 -ProcessName <name without .exe> -Seconds <n>
#
# It prints {"ready":true} once it is compiled and looking, so the caller starts the app after that and misses none of
# its first moments. Read-only: it enumerates and reads, never acts on a window.
param(
  [Parameter(Mandatory = $true)][string]$ProcessName,
  [int]$Seconds = 10
)

Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ShenoraWatch {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
  [DllImport("user32")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
  [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
  [DllImport("user32")] static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hwnd, StringBuilder text, int max);
  [DllImport("user32")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
  public static List<string> Of(uint pid) {
    var found = new List<string>();
    EnumWindows((hwnd, p) => {
      uint owner; GetWindowThreadProcessId(hwnd, out owner);
      if (owner != pid) return true;
      var cls = new StringBuilder(256); GetClassNameW(hwnd, cls, 256);
      RECT r; GetWindowRect(hwnd, out r);
      found.Add(hwnd.ToInt64() + "|" + cls + "|" + (IsWindowVisible(hwnd) ? 1 : 0) + "|" + r.Left + "," + r.Top + "," + (r.Right - r.Left) + "," + (r.Bottom - r.Top));
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
"@

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$seen = @{}
$known = @{}
Write-Output '{"ready":true}'
while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
  $now = @{}
  foreach ($process in @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)) {
    if (-not $known.ContainsKey($process.Id)) {
      $known[$process.Id] = $true
      Write-Output ('{{"process":{0},"started":{1}}}' -f $process.Id, ([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds())
    }
    foreach ($line in [ShenoraWatch]::Of([uint32]$process.Id)) {
      $parts = $line.Split('|')
      $now[$parts[0]] = $true
      $state = $parts[2] + '|' + $parts[3]
      if ($seen[$parts[0]] -ne $state) {
        $seen[$parts[0]] = $state
        Write-Output ('{{"t":{0},"hwnd":"{1}","class":"{2}","visible":{3},"rect":"{4}"}}' -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $parts[0], $parts[1], $parts[2], $parts[3])
      }
    }
  }
  foreach ($gone in @($seen.Keys)) {
    if (-not $now.ContainsKey($gone)) {
      $seen.Remove($gone)
      Write-Output ('{{"t":{0},"hwnd":"{1}","gone":true}}' -f [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(), $gone)
    }
  }
  Start-Sleep -Milliseconds 2
}
