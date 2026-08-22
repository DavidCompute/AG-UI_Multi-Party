# Desktop close-exit test: start app, wait for local service, send WM_CLOSE to main window,
# assert the process exits within the timeout (no hang, no orphan process).
# Usage: powershell -ExecutionPolicy Bypass -File tools/desktop-close-test.ps1 -ExePath artifacts\close-test\AguiGroupChat.Desktop.exe
param([string]$ExePath)
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Close {
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
  public const uint WM_CLOSE = 0x0010;
}
"@

$p = Start-Process -FilePath $ExePath -WorkingDirectory (Split-Path $ExePath) -PassThru

# Wait for local service (port 5200; stop docker web / anything on 5200 before running)
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
  if ($p.HasExited) { throw "Process exited early (code $($p.ExitCode))" }
  try {
    $r = Invoke-WebRequest -Uri "http://127.0.0.1:5200/" -TimeoutSec 2 -UseBasicParsing
    if ($r.StatusCode -eq 200) { $ready = $true; break }
  } catch {}
  Start-Sleep -Milliseconds 500
}
if (-not $ready) { if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }; throw "Local service not ready within 45s" }
Write-Host "Local service ready, waiting for window..."
Start-Sleep -Seconds 3

$p.Refresh()
$hwnd = $p.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) { Stop-Process -Id $p.Id -Force; throw "Main window handle not found" }
Write-Host "Main window handle: $hwnd, sending WM_CLOSE..."

[Win32Close]::PostMessage($hwnd, [Win32Close]::WM_CLOSE, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null

if (-not $p.WaitForExit(20000)) {
  Stop-Process -Id $p.Id -Force
  throw "FAIL: process did not exit within 20s after window close (hang)"
}
Write-Host "PASS: process exited normally after window close (code $($p.ExitCode))"
