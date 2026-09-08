# AG-UI GroupChat NativeBridge - self-contained Windows MSI builder (WiX v4)
# Usage: powershell -ExecutionPolicy Bypass -File tools/publish-nativebridge-msi.ps1 [-Version 1.0.121]
# Produces: artifacts/nativebridge-wix/AguiGroupChat-NativeBridge-<Version>-win-x64.msi
#   - win-x64 self-contained publish (target machine needs NO .NET runtime)
#   - perUser MSI (no admin): installs to %LocalAppData%\AguiGroupChat\NativeBridge,
#     registers HKCU autostart (run-hidden.vbs), starts the bridge right after install.
#   - The package is GENERIC (no server/token baked in): the platform web page configures
#     the running bridge on login ("login-to-connect / logout-to-disconnect").
#   - Upload the .msi via the web UI (资料弹窗 -> 本机桥 -> 管理员上传) for users to download.
param(
  [string]$Version = "1.0.121",
  [string]$OutDir = "artifacts/nativebridge-wix",
  [string]$PublishRel = "artifacts/nativebridge-pkg"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
  throw 'wix CLI not found. Run: dotnet tool install -g wix --version "4.*"'
}

$out = Join-Path $root $OutDir
$publish = Join-Path $root $PublishRel
$appIcon = Join-Path $root "tools/wix/agui-icon.ico"
if (-not (Test-Path $appIcon)) { throw "Missing icon: $appIcon" }

# -------- 0. clean Release intermediates (force fresh build) --------
Write-Host "[0/6] clean Release intermediates + stale publish dir (force fresh build)"
foreach ($p in @("src/AguiGroupChat.NativeBridge", "src/AguiGroupChat.SkillHosting")) {
  foreach ($sub in @('bin\Release', 'obj\Release')) {
    $dir = Join-Path $root (Join-Path $p $sub)
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  }
}
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null
New-Item -ItemType Directory -Force -Path $out | Out-Null

# -------- 1. dotnet publish (win-x64 self-contained) --------
Write-Host "[1/6] dotnet publish (win-x64 self-contained) -> $publish"
dotnet publish (Join-Path $root "src/AguiGroupChat.NativeBridge/AguiGroupChat.NativeBridge.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# -------- 2. trim foreign runtimes (keep win-x64 only) --------
$runtimeRoot = Join-Path $publish 'runtimes'
if (Test-Path $runtimeRoot) {
  Get-ChildItem $runtimeRoot -Directory | Where-Object { $_.Name -notlike 'win*' } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force
  }
}

# -------- 3. bundle launcher scripts + README (ASCII file names) --------
Write-Host "[3/6] bundle launcher scripts + README"

# start-nativebridge.bat: has config (SERVER+TOKEN) -> --config static connect; otherwise waiting mode
$bat = Join-Path $publish "start-nativebridge.bat"
$batText = @'
@echo off
cd /d "%~dp0"
set HIDDEN=%~1
set CFG=bridge-config.txt
if exist "%CFG%" (
  findstr /i /b "SERVER=" "%CFG%" >nul 2>&1
  if not errorlevel 1 (
    findstr /i /b "TOKEN=" "%CFG%" >nul 2>&1
    if not errorlevel 1 goto :withcfg
  )
)
echo [NativeBridge] Waiting mode (no config yet): the platform page will configure me on login...
AguiGroupChat.NativeBridge.exe
if /i "%HIDDEN%"=="hidden" exit /b 0
pause
exit /b 0
:withcfg
echo [NativeBridge] Starting with %CFG% ...
AguiGroupChat.NativeBridge.exe --config "%CFG%"
if /i "%HIDDEN%"=="hidden" exit /b 0
pause
exit /b 0
'@
[System.IO.File]::WriteAllText($bat, $batText, (New-Object System.Text.UTF8Encoding($false)))

# run-hidden.vbs: no-window launcher (used by HKCU autostart & install custom action)
$vbs = Join-Path $publish "run-hidden.vbs"
$vbsText = @'
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")
base = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = base
sh.Run """" & base & "\start-nativebridge.bat"" hidden", 0, False
'@
[System.IO.File]::WriteAllText($vbs, $vbsText, (New-Object System.Text.UTF8Encoding($false)))

# README.txt (Chinese, UTF-8 BOM for Notepad)
$readme = Join-Path $publish "README.txt"
$readmeText = @'
AguiGroupChat 本机桥（NativeBridge）· Windows 安装包（.msi，无需安装 .NET 运行时）

用途
  让“本机(client)执行”的数字员工技能跑在【发起请求的那台电脑】上。

安装
  双击运行 AguiGroupChat-NativeBridge-<版本>-win-x64.msi（按用户安装，无需管理员权限）。
  安装完成后：
    - 本机桥注册为“登录 Windows 后自动启动”，并立即以隐藏窗口启动；
    - 登录本平台网页时，页面会自动把连接配置下发给本机桥并建立连接（登录即连）；
    - 登出平台时本机桥自动断开并清除本机保存的连接配置（登出即断）。

手动启停 / 排障
  - 开始菜单 → “AG-UI 本机桥”可前台启动（窗口保留，可见隧道日志；关闭窗口即断开）。
  - 卸载：控制面板 → 程序 → AG-UI 本机桥（会结束正在运行的桥并删除本机配置）。

说明
  - 安装包是“通用包”，不含任何连接令牌 / 服务器地址（登录后由网页按当前平台在线配置，
    因此换一台平台服务器也无需重装）。
  - 本机桥连回服务器使用反向隧道，无需公网 IP / 端口映射。
  - 开机自启仅注册当前 Windows 用户（HKCU Run），无需管理员权限。
  - 连接配置以 AES-256-GCM 加密保存在安装目录 bridge-config.txt（密钥 bridge.key），
    卸载 / 登出时清除。
'@
[System.IO.File]::WriteAllText($readme, $readmeText, (New-Object System.Text.UTF8Encoding($true)))

# -------- 4. generate file manifest (files.wxs) by walking the publish dir --------
# 注：桥为自包含发布，包含 Roslyn（C# 技能编译）的多语言卫星资源目录（cs/de/ru/zh-Hans…），
#     因此清单必须保持发布目录层级（卫星子目录各归其位），否则同名 resources.dll 会撞 File Id。
function IsForeignRuntime([string]$path) {
  $idx = $path.IndexOf('\runtimes\')
  if ($idx -lt 0) { return $false }
  $rest = $path.Substring($idx + 10)
  $end = $rest.IndexOf('\')
  if ($end -lt 0) { return $false }
  $rid = $rest.Substring(0, $end)
  return -not ($rid -in @('win', 'win-x64', 'win-x86', 'win-arm64'))
}

function GenerateBridgeFilesWxs([string]$PublishDir) {
  # start-nativebridge.bat is declared explicitly by nativebridge-package.wxs (ConfigTemplate component)
  $skipNames = @('start-nativebridge.bat')

  function ShouldSkipDir([System.IO.DirectoryInfo]$d) {
    return (IsForeignRuntime $d.FullName)
  }

  # 递归生成嵌套 <Directory> 树
  $dirIds = @{}
  $dirXml = New-Object System.Text.StringBuilder
  function EmitDir([System.IO.DirectoryInfo]$d, [int]$depth) {
    $rel = $d.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $id = 'dir_' + ($rel.Replace('\', '_').Replace('/', '_') -replace '[^A-Za-z0-9_]', '_')
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

  $manifest = New-Object System.Collections.Generic.List[string]
  $manifest.Add('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
  if ($dirIds.Count -gt 0) {
    $manifest.Add('  <Fragment>')
    $manifest.Add('    <DirectoryRef Id="BRIDGEDIR">')
    $manifest.Add($dirXml.ToString())
    $manifest.Add('    </DirectoryRef>')
    $manifest.Add('  </Fragment>')
  }
  $manifest.Add('  <Fragment>')
  $manifest.Add('    <ComponentGroup Id="AppFiles" Directory="BRIDGEDIR">')

  $files = Get-ChildItem -Path $PublishDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $skipNames -notcontains $_.Name -and -not (IsForeignRuntime $_.FullName) }
  foreach ($f in $files) {
    $rel = $f.FullName.Substring($PublishDir.Length).TrimStart('\', '/')
    $relSafe = ($rel.Replace('\', '_').Replace('/', '_') -replace '[^A-Za-z0-9_.]', '_')
    $cmpId = 'cmp_' + $relSafe
    # Deterministic GUID by relative path (stable across versions -> clean MSI upgrades)
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($rel))
    $md5.Dispose()
    $guid = [guid]::new($hash).ToString()
    $attr = ''
    if ($f.Directory.FullName -ne $PublishDir) {
      $attr = ' Directory="' + $dirIds[$f.Directory.FullName] + '"'
    }
    $manifest.Add(('      <Component Id="{0}"{1} Guid="{2}">' -f $cmpId, $attr, $guid))
    $manifest.Add(('        <File Id="fil_{0}" Source="{1}" />' -f $relSafe, $f.FullName))
    $manifest.Add('      </Component>')
  }
  $manifest.Add('    </ComponentGroup>')
  $manifest.Add('  </Fragment>')
  $manifest.Add('</Wix>')
  return ($manifest -join "`r`n")
}

Write-Host "[4/6] generate files.wxs"
Get-ChildItem -Path $out -Filter '*.wixpdb' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$filesWxs = GenerateBridgeFilesWxs -PublishDir $publish
Set-Content -Path (Join-Path $out "nativebridge-files.wxs") -Value $filesWxs -Encoding UTF8
if (-not $filesWxs) { throw "generate files.wxs failed" }

# -------- 5. compile MSI --------
Write-Host "[5/6] wix build -> AguiGroupChat-NativeBridge-$Version-win-x64.msi"
$msi = Join-Path $out "AguiGroupChat-NativeBridge-$Version-win-x64.msi"
if (Test-Path $msi) { Remove-Item $msi -Force }
wix build (Join-Path $root "tools/wix/nativebridge-package.wxs") (Join-Path $out "nativebridge-files.wxs") `
  -d "PublishDir=$publish" -d "Version=$Version" -d "AppIcon=$appIcon" -o $msi
if ($LASTEXITCODE -ne 0) { throw "wix build failed" }

# -------- 6. verify + report --------
$exe = Join-Path $publish "AguiGroupChat.NativeBridge.exe"
if (-not (Test-Path $exe)) { throw "publish output missing AguiGroupChat.NativeBridge.exe" }
$size = [math]::Round((Get-Item $msi).Length / 1MB, 1)
Write-Host "[6/6] Done: $msi ($size MB)"
Write-Host 'Install : msiexec /i "' + $msi + '"'
Write-Host 'Next    : log in to the platform as an admin, Profile -> NativeBridge, upload this .msi.'
