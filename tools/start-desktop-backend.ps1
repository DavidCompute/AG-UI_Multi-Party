$ErrorActionPreference = "Stop"
$dir = "$env:LOCALAPPDATA\AguiGroupChat"
$out = "$env:LOCALAPPDATA\Temp\agui_v_out.log"
$err = "$env:LOCALAPPDATA\Temp\agui_v_err.log"
$exe = Join-Path $dir "AguiGroupChat.Desktop.exe"
$p = Start-Process -FilePath $exe -ArgumentList "--backend" -WorkingDirectory $dir -RedirectStandardOutput $out -RedirectStandardError $err -WindowStyle Hidden -PassThru
Write-Output ("STARTED_PID=" + $p.Id)
