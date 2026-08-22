# temp cleanup (delete after use)
Remove-Item (Join-Path $env:LOCALAPPDATA 'AguiGroupChat') -Recurse -Force -ErrorAction SilentlyContinue
Write-Host 'cleaned'
