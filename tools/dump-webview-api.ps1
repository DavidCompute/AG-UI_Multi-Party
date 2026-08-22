$ErrorActionPreference = 'Continue'
$dll = "C:\Users\david\.nuget\packages\avalonia.controls.webview\12.1.0\lib\net10.0\Avalonia.Controls.WebView.dll"
$lines = @()
$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$types = $null
try { $types = $asm.GetTypes() } catch { $types = $_.Exception.Types | Where-Object { $_ } }
foreach ($t in ($types | Where-Object { $_.Name -match 'NativeWebView|WebViewAdapter|WebView2' } | Select-Object -First 10)) {
    $lines += "=== " + $t.FullName + " ==="
    foreach ($e in $t.GetEvents()) { $lines += "  EV " + $e.EventHandlerType.Name + " " + $e.Name }
    foreach ($m in ($t.GetMethods() | Where-Object { $_.IsPublic -and -not $_.IsSpecialName } | Select-Object -First 15)) {
        $lines += "  M " + $m.ReturnType.Name + " " + $m.Name + "(" + ((($m.GetParameters()) | ForEach-Object { $_.ParameterType.Name }) -join ",") + ")"
    }
    foreach ($p in $t.GetProperties()) { $lines += "  P " + $p.PropertyType.Name + " " + $p.Name }
}
$lines | Out-File -FilePath 'C:\Users\david\src\AG-UI_Multi-Party\tools\webview-api.txt' -Encoding utf8
