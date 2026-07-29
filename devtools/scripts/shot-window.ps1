# Capture the target app window (occlusion-safe: PrintWindow + PW_RENDERFULLCONTENT so the
# WebView2 composition is included) into a PNG for the visual-verification loop. The process name
# comes from devtools/project.config.mjs via `dev.mjs shot`.
param(
    [string]$OutFile = "$PSScriptRoot\..\screenshots\app-window.png",
    [Parameter(Mandatory = $true)][string]$ProcessName
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32Shot {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
'@ -ReferencedAssemblies System.Runtime.InteropServices
Add-Type -AssemblyName System.Drawing

[Win32Shot]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) | Out-Null # PER_MONITOR_AWARE_V2

$p = Get-Process $ProcessName -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
if (-not $p) { Write-Error "No '$ProcessName' process with a visible window (tray-hidden windows can't be captured)."; exit 1 }

$r = New-Object Win32Shot+RECT
[Win32Shot]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
[Win32Shot]::PrintWindow($p.MainWindowHandle, $hdc, 2) | Out-Null  # 2 = PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$bmp.Save((Resolve-Path -Path (Split-Path -Parent $OutFile)).Path + '\' + (Split-Path -Leaf $OutFile))
$g.Dispose(); $bmp.Dispose()
Write-Output "captured ${w}x${h} -> $OutFile"
