# AG-UI GroupChat Desktop - WiX v4 MSI build script
# Usage: powershell -ExecutionPolicy Bypass -File tools/build-msi.ps1 [-Version 1.0.74]
# Deps: dotnet tool install -g wix --version "4.*"
param(
  [string]$Version = "1.0.74",
  [string]$PublishDir = "artifacts/win-x64",
  [string]$OutDir = "artifacts/wix"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root $PublishDir
$out = Join-Path $root $OutDir

# Walk publish dir and emit WiX v4 ComponentGroup (skip exe - handled by package.wxs,
# skip data\* - runtime-generated; skip empty dirs)
function GenerateFilesWxs {
  param([string]$PublishDir)

  $dirIds = @{}   # full path -> directory id
  $dirXml = New-Object System.Text.StringBuilder

  # 目录是否跳过：运行期数据 data/、WebView2 用户缓存目录、非 Windows 平台 runtimes
  function ShouldSkipDir([System.IO.DirectoryInfo]$d) {
    if ($d.Name -eq 'data') { return $true }
    if ($d.FullName -match 'AguiGroupChat\.Desktop\.exe\.WebView2') { return $true }
    if (IsForeignRuntime $d.FullName) { return $true }
    return $false
  }

  # 递归生成嵌套的 <Directory> 树（WiX 目录层级依赖 XML 嵌套；平铺会导致安装后目录结构被拍扁，
  # 原生库 / wwwroot 等子目录全部错位，deps.json 按 runtimes/win-x64/native/... 找不到 e_sqlite3 等）
  function EmitDir([System.IO.DirectoryInfo]$d, [int]$depth) {
    $rel = $d.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $id = 'dir_' + ($rel -replace '[\\/]', '_' -replace '[^A-Za-z0-9_]', '_')
    $dirIds[$d.FullName] = $id
    $pad = ('    ' * ($depth + 1))
    $children = @(Get-ChildItem -Path $d.FullName -Directory -ErrorAction SilentlyContinue | Where-Object { -not (ShouldSkipDir $_) })
    $null = $dirXml.AppendLine("$pad<Directory Id=`"$id`" Name=`"$($d.Name)`">")
    foreach ($c in $children) { $null = EmitDir $c ($depth + 1) }
    $null = $dirXml.AppendLine("$pad</Directory>")
    return $id
  }

  $topDirs = @(Get-ChildItem -Path $PublishDir -Directory -ErrorAction SilentlyContinue | Where-Object { -not (ShouldSkipDir $_) })
  foreach ($d in $topDirs) { $null = EmitDir $d 0 }

  $manifest = @()
  $manifest += '<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">'
  if ($dirIds.Count -gt 0) {
    $manifest += '  <Fragment>'
    $manifest += '    <DirectoryRef Id="INSTALLFOLDER">'
    $manifest += $dirXml.ToString()
    $manifest += '    </DirectoryRef>'
    $manifest += '  </Fragment>'
  }
  $manifest += '  <Fragment>'
  $manifest += '    <ComponentGroup Id="AppFiles" Directory="INSTALLFOLDER">'

  $files = Get-ChildItem -Path $PublishDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'AguiGroupChat.Desktop.exe' -and $_.FullName.Replace('\', '/') -notmatch '/(data|AguiGroupChat\.Desktop\.exe\.WebView2)/' -and -not (IsForeignRuntime $_.FullName) }
  foreach ($f in $files) {
    $rel = $f.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $relSafe = ($rel -replace '\\', '_' -replace '[^A-Za-z0-9_.]', '_')
    $cmpId = 'cmp_' + $relSafe
    # 确定性 GUID：由相对路径 MD5 生成（Guid="*" 按内容哈希，相同内容文件会冲突）
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($rel))
    $md5.Dispose()
    $guid = [guid]::new($hash).ToString()
    $attr = ''
    if ($f.Directory.FullName -ne $PublishDir) {
      $attr = ' Directory="' + $dirIds[$f.Directory.FullName] + '"'
    }
    $manifest += ('      <Component Id="{0}"{1} Guid="{2}">' -f $cmpId, $attr, $guid)
    $manifest += ('        <File Source="{0}" />' -f ($f.FullName -replace '\\', '\'))
    $manifest += '      </Component>'
  }

  $manifest += '    </ComponentGroup>'
  $manifest += '  </Fragment>'
  $manifest += '</Wix>'
  return ($manifest -join "`r`n")
}

# True if path is under runtimes/<rid>/ with a non-Windows rid (linux*/osx*/freebsd*/etc.)
function IsForeignRuntime([string]$path) {
  $idx = $path.IndexOf('\runtimes\')
  if ($idx -lt 0) { return $false }
  $rest = $path.Substring($idx + 10)
  $end = $rest.IndexOf('\')
  if ($end -lt 0) { return $false }
  $rid = $rest.Substring(0, $end)
  return -not ($rid -in @('win', 'win-x64', 'win-x86', 'win-arm64'))
}

# 0. tool check
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
  throw 'wix CLI not found. Run: dotnet tool install -g wix --version "4.*"'
}

# 0.5 记录脚本启动时间：用于校验本次 publish 是否真的重新生成了关键 DLL（防增量跳过导致 MSI 打包旧 DLL）
$scriptStart = Get-Date

# 0.6 强制全新编译：清理关键项目 Release 中间产物 + 发布目录旧 DLL/exe。
#      dotnet publish 默认增量，若它认为项目未变会复用旧的 bin/Release 产物 → artifacts 里留旧 DLL →
#      MSI 打包的就是旧后端（曾致「重装无效 / 仍 405」）。清掉后 publish 必重编。
Write-Host "[0/5] clean Release intermediates + stale publish DLLs (force fresh build)"
$cleanProjects = @(
  "src/AguiGroupChat.Agents",
  "src/AguiGroupChat.Hub",
  "src/AguiGroupChat.Web",
  "src/AguiGroupChat.Desktop.Core",
  "src/AguiGroupChat.Desktop"
)
foreach ($p in $cleanProjects) {
  foreach ($sub in @('bin\Release', 'obj\Release')) {
    $dir = Join-Path $root (Join-Path $p $sub)
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  }
}
# 清掉发布目录里可能残留的旧桌面程序集（AguiGroupChat.*.dll/.exe/.pdb），保证 artifacts 不残留旧版被 wix 复用
Get-ChildItem -Path $publish -File -ErrorAction SilentlyContinue |
  Where-Object { $_.Name -match '^AguiGroupChat\.(Agents|Hub|Web|Desktop|Sdk)' -or $_.Name -eq 'AguiGroupChat.Desktop.exe' } |
  ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }

# 1. publish (Release)
Write-Host "[1/5] dotnet publish -> $publish"
dotnet publish (Join-Path $root "src/AguiGroupChat.Desktop/AguiGroupChat.Desktop.csproj") -c Release -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 1.5 剔除 runtimes/ 下非 Windows 平台原生库（LLamaSharp 全平台资产约 100MB；桌面版仅运行于 Windows，
#     保留 win-x64 / win-x86 / win-arm64）。模型文件已在发布前排除（csproj 不捆绑 models/）
$runtimeRoot = Join-Path $publish 'runtimes'
if (Test-Path $runtimeRoot) {
  Get-ChildItem $runtimeRoot -Directory | Where-Object { $_.Name -notlike 'win*' } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force
  }
}

# 1.6 捆绑 VC++ 运行库（app-local，与 exe 同目录）：llama/ggml/e_sqlite3 均为 MSVC 编译，
#     目标机缺失 vcruntime140*.dll 时 LLamaSharp 原生库加载失败（部分电脑启动崩溃的主因）。
#     Windows DLL 搜索顺序包含 exe 所在目录，故放发布根即可被原生库依赖解析到
$redist = Join-Path $root 'tools\wix\redist'
if (Test-Path $redist) {
  Get-ChildItem $redist -File | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $publish $_.Name) -Force
  }
}

# 2. generate file manifest (files.wxs) by walking the publish dir
New-Item -ItemType Directory -Force -Path $out | Out-Null
Write-Host "[2/5] generate files.wxs"
# 清理 wix 中间产物，避免复用旧组件的 cab / wixobj
Get-ChildItem -Path $out -Filter '*.wixpdb' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$filesWxs = GenerateFilesWxs -PublishDir $publish
Set-Content -Path (Join-Path $out "files.wxs") -Value $filesWxs -Encoding UTF8
if (-not $filesWxs) { throw "generate files.wxs failed" }

# 3. compile MSI
Write-Host "[3/5] wix build -> AguiGroupChat-Desktop-$Version.msi"
$msi = Join-Path $out "AguiGroupChat-Desktop-$Version.msi"
# 品牌图标（package.wxs 的 $(var.AppIcon)）：tools/wix/agui-icon.ico（多尺寸，含 256，供 ARPPRODUCTICON / 快捷方式）
$appIcon = Join-Path $root "tools/wix/agui-icon.ico"
if (-not (Test-Path $appIcon)) { throw "Missing icon: $appIcon (expected from assets/, copy via tools/icon-gen)" }
# 输出 MSI 若已存在先删（避免 wix 复用旧 cab / 旧产物）
if (Test-Path $msi) { Remove-Item $msi -Force }
wix build (Join-Path $root "tools/wix/package.wxs") (Join-Path $out "files.wxs") -d "PublishDir=$publish" -d "Version=$Version" -d "AppIcon=$appIcon" -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# 4. verify backend DLL freshness (abort if publish reused stale DLL -> MSI would bundle old backend)
Write-Host "[4/5] verify backend DLL freshness"
$gate = @('AguiGroupChat.Web.dll', 'AguiGroupChat.Agents.dll', 'AguiGroupChat.Desktop.Core.dll', 'AguiGroupChat.Desktop.dll')
$stale = @()
foreach ($name in $gate) {
  $dll = Join-Path $publish $name
  if (-not (Test-Path $dll)) { $stale += ($name + '-MISSING'); continue }
  $dllTime = (Get-Item $dll).LastWriteTime
  if ($dllTime -lt $scriptStart.AddSeconds(-5)) { $stale += ($name + '-STALE') }
}
if ($stale.Count -gt 0) {
  throw ('STALE backend DLL detected in publish dir: ' + ($stale -join ', ') + '. Aborted - rerun to rebuild.')
}
Write-Host "  OK: backend DLLs are fresh from this publish"

# 5. result
$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host ("[5/5] Done: " + $msi + " (" + $size + " MB)")
Write-Host ('Install: msiexec /i "' + $msi + '"   Uninstall: Control Panel > Programs')
