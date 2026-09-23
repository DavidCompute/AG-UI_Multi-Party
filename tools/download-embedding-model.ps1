# AG-UI GroupChat Desktop - local embedding model download helper
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1
#   powershell -ExecutionPolicy Bypass -File tools/download-embedding-model.ps1 -Url "https://<mirror>/xxx.gguf"
#
# 默认模型：bge-m3-Q8_0.gguf（约 605MB，**1024 维**）——必须与 desktop 的
# Agents:Memory:EmbeddingDimensions=1024 一致。
# ⚠️ 旧默认是 nomic-embed-text-v1.5（130MB、768 维），与 appsettings 的 1024 不符：
#    直接用它会让向量维度与建表维度不匹配，RAG 静默失效。要换模型必须同步改 EmbeddingDimensions。
param(
  [string]$Url = 'https://www.modelscope.cn/models/gpustack/bge-m3-GGUF/resolve/master/bge-m3-Q8_0.gguf',
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
  # 尺寸护栏：bge-m3-Q8_0 约 605MB。小于 400MB 基本可以断定不是它（例如 nomic 只有 130MB），
  # 那种情况会与 EmbeddingDimensions=1024 不匹配、让 RAG 静默失效——宁可在这里失败。
  $len = (Get-Item $part).Length
  if ($len -lt 400MB) {
    Remove-Item $part -Force -ErrorAction SilentlyContinue
    throw "Downloaded file is only $([math]::Round($len/1MB,1)) MB - that is not bge-m3-Q8_0 (1024 dims, ~605MB). If you really want another model, also set Agents:Memory:EmbeddingDimensions accordingly."
  }
  Move-Item $part $target -Force
  Write-Host "Done ($([math]::Round($len/1MB,1)) MB). Restart the desktop app to enable semantic memory."
} catch {
  Remove-Item $part -Force -ErrorAction SilentlyContinue
  throw "Download failed: $($_.Exception.Message)"
}
