# AG-UI GroupChat Desktop - local embedding model download helper
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1
#   powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1 -Url "https://<mirror>/xxx.gguf"
# Default model: nomic-embed-text-v1.5.Q8_0.gguf (~130MB, 768 dims, matches default appsettings)
param(
  [string]$Url = 'https://huggingface.co/nomic-ai/nomic-embed-text-v1.5-GGUF/resolve/main/nomic-embed-text-v1.5.Q8_0.gguf',
  [string]$OutDir = 'src/AguiGroupChat.Desktop/models'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = if ([System.IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
New-Item -ItemType Directory -Force -Path $out | Out-Null
$target = Join-Path $out 'embedding.gguf'
$part = $target + '.part'
if (Test-Path $target) { Write-Host "Model already exists: $target"; exit 0 }
Write-Host "Downloading: $Url"
Write-Host "Saving to:   $target"
try {
  $ProgressPreference = 'SilentlyContinue'
  Invoke-WebRequest -Uri $Url -OutFile $part -UseBasicParsing
  Move-Item $part $target -Force
  Write-Host 'Done. Restart the desktop app to enable semantic memory.'
} catch {
  Remove-Item $part -Force -ErrorAction SilentlyContinue
  throw "Download failed: $($_.Exception.Message)"
}
