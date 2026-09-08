# AG-UI GroupChat NativeBridge - self-contained Windows package builder
# Usage: powershell -ExecutionPolicy Bypass -File tools/publish-nativebridge.ps1 [-Version 1.0.120]
# Produces: dist/nativebridge/AguiGroupChat-NativeBridge-<Version>-win-x64.zip
#   - win-x64 self-contained publish (target machine needs NO .NET runtime)
#   - zip root contains the publish output + 启动本机桥.bat (reads bridge-config.txt) + 使用说明.txt
#   - upload the zip via the web UI (资料弹窗 → 本机桥 → 管理员上传) so users can download it
param(
  [string]$Version = "1.0.120",
  [string]$OutDir = "dist/nativebridge"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

Write-Host "[1/4] clean Release intermediates (force fresh build)"
foreach ($p in @("src/AguiGroupChat.NativeBridge", "src/AguiGroupChat.SkillHosting")) {
  foreach ($sub in @('bin\Release', 'obj\Release')) {
    $dir = Join-Path $root (Join-Path $p $sub)
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  }
}

$out = Join-Path $root $OutDir
$publish = Join-Path $out "win-x64"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $publish | Out-Null

Write-Host "[2/4] dotnet publish (win-x64 self-contained) -> $publish"
dotnet publish (Join-Path $root "src/AguiGroupChat.NativeBridge/AguiGroupChat.NativeBridge.csproj") `
  -c Release -r win-x64 --self-contained true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# 剔除 runtimes/ 下非 Windows 平台原生库（如有；只保留 win-x64）
$runtimeRoot = Join-Path $publish 'runtimes'
if (Test-Path $runtimeRoot) {
  Get-ChildItem $runtimeRoot -Directory | Where-Object { $_.Name -notlike 'win*' } | ForEach-Object {
    Remove-Item $_.FullName -Recurse -Force
  }
}

Write-Host "[3/4] bundle launcher scripts + README into the publish folder"
# 本机桥包内文件（zip 内一律 ASCII 文件名，避免 Compress-Archive 在 PowerShell 5.1 按 ANSI/GBK
# 写中文条目导致其它系统解压乱码）：
#   bridge-config.txt         —— 连接配置模板；服务器端下载时按当前平台地址/令牌动态重写后下发
#   start-nativebridge.bat    —— 前台启动（解析 bridge-config.txt）；参数 hidden 时不 pause（供自启）
#   run-hidden.vbs            —— 无窗口启动 start-nativebridge.bat（WScript Run 隐藏窗口）
#   install-autostart.bat     —— 注册 HKCU Run 开机自启（复制到 %LOCALAPPDATA% 后注册并立即启动）
#   uninstall-autostart.bat   —— 移除开机自启注册

# ---- bridge-config.txt（下载时由服务器重写；此处占位模板） ----
$cfg = Join-Path $publish "bridge-config.txt"
$cfgText = @'
# AguiGroupChat NativeBridge connection config
# (key=value per line; lines starting with # are comments)
SERVER=
TOKEN=
AGENT=
CLIENT=
LOCAL_PORT=17321
'@
[System.IO.File]::WriteAllText($cfg, $cfgText, (New-Object System.Text.UTF8Encoding($false)))

# ---- start-nativebridge.bat ----
$bat = Join-Path $publish "start-nativebridge.bat"
$batText = @'
@echo off
cd /d "%~dp0"
set HIDDEN=%~1
set CFG=bridge-config.txt
if not exist "%CFG%" (
  echo [NativeBridge] bridge-config.txt not found; creating a template...
  > "%CFG%" echo # NativeBridge connection config (key=value per line; lines starting with # are comments)
  >> "%CFG%" echo SERVER=
  >> "%CFG%" echo TOKEN=
  >> "%CFG%" echo AGENT=
  >> "%CFG%" echo CLIENT=
  >> "%CFG%" echo LOCAL_PORT=17321
  if /i "%HIDDEN%"=="hidden" exit /b 1
  echo Please edit %CFG% ^(fill in SERVER and TOKEN^) then run this script again.
  pause
  exit /b 1
)
echo [NativeBridge] Starting with %CFG% ...
AguiGroupChat.NativeBridge.exe --config "%CFG%"
if /i "%HIDDEN%"=="hidden" exit /b 0
pause
exit /b 0
'@
[System.IO.File]::WriteAllText($bat, $batText, (New-Object System.Text.UTF8Encoding($false)))

# ---- run-hidden.vbs（无窗口启动，供开机自启用） ----
$vbs = Join-Path $publish "run-hidden.vbs"
$vbsText = @'
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")
base = fso.GetParentFolderName(WScript.ScriptFullName)
sh.CurrentDirectory = base
sh.Run """" & base & "\start-nativebridge.bat"" hidden", 0, False
'@
[System.IO.File]::WriteAllText($vbs, $vbsText, (New-Object System.Text.UTF8Encoding($false)))

# ---- install-autostart.bat ----
$inst = Join-Path $publish "install-autostart.bat"
$instText = @'
@echo off
setlocal
cd /d "%~dp0"
set APPNAME=AguiGroupChatNativeBridge
if "%LOCALAPPDATA%"=="" set LOCALAPPDATA=%USERPROFILE%\AppData\Local
set DEST=%LOCALAPPDATA%\AguiGroupChat\NativeBridge
if /i "%DEST%\"=="%~dp0" goto :inplace
echo [NativeBridge] Copying files to %DEST% ...
if not exist "%DEST%" mkdir "%DEST%"
xcopy "%~dp0*" "%DEST%\" /E /Y /Q /I >nul
:inplace
if not exist "%DEST%\bridge-config.txt" goto :nocfg
set TOKEN=
for /f "usebackq eol=# tokens=1,* delims==" %%a in ("%DEST%\bridge-config.txt") do if /i "%%a"=="TOKEN" set "TOKEN=%%b"
if "%TOKEN%"=="" (
  echo [NativeBridge] WARNING: bridge-config.txt has no TOKEN yet. Start will fail until an
  echo            admin-provided token is filled in.
)
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v %APPNAME% /t REG_SZ /d "wscript.exe \"%DEST%\run-hidden.vbs\"" /f >nul
echo [NativeBridge] Autostart registered. Starting now (window closes automatically)...
start "" wscript.exe "%DEST%\run-hidden.vbs"
echo [NativeBridge] Done. It will also auto-start after you sign in to Windows.
pause
exit /b 0
:nocfg
echo [NativeBridge] bridge-config.txt missing in %DEST% - copy failed?
pause
exit /b 1
'@
[System.IO.File]::WriteAllText($inst, $instText, (New-Object System.Text.UTF8Encoding($false)))

# ---- uninstall-autostart.bat ----
$uninst = Join-Path $publish "uninstall-autostart.bat"
$uninstText = @'
@echo off
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v AguiGroupChatNativeBridge /f >nul 2>&1
echo [NativeBridge] Autostart removed. The files under %%LOCALAPPDATA%%\AguiGroupChat\NativeBridge
if not "%LOCALAPPDATA%"=="" if exist "%LOCALAPPDATA%\AguiGroupChat\NativeBridge" (
  echo            are still there - delete that folder if you no longer need it.
) else (
  echo            are still there - delete the folder if you no longer need it.
)
pause
exit /b 0
'@
[System.IO.File]::WriteAllText($uninst, $uninstText, (New-Object System.Text.UTF8Encoding($false)))

# ---- README.txt（中文说明，UTF-8 with BOM 便于记事本打开） ----
$readme = Join-Path $publish "README.txt"
$readmeText = @'
AguiGroupChat 本机桥（NativeBridge）· Windows 安装包（无需安装 .NET 运行时）

用途
  让“本机(client)执行”的数字员工技能跑在【发起请求的那台电脑】上。
  从平台网页（资料弹窗 → 本机桥）下载的安装包已自动预填本平台服务器地址；
  管理员下载的包含加密连接令牌（TOKEN 为 enc:v1: 密文 + 同目录 bridge.key），本机桥启动时自动解密。

快速安装（推荐，注册开机自启）
  1) 解压本包到任意目录。
  2) 双击 install-autostart.bat：会把桥复制到 %LOCALAPPDATA%\AguiGroupChat\NativeBridge、
     注册“登录 Windows 后自动启动”，并立即以隐藏窗口启动本机桥。
  3) 如需取消自启：双击 uninstall-autostart.bat（不删除已复制文件）。

手动前台启动（排障用）
  双击 start-nativebridge.bat（窗口会保留，可看到隧道连接日志；关闭窗口即断开）。

配置（bridge-config.txt，位于包内 / 安装目录）
  SERVER=平台地址（如 https://hub.example.com）—— 平台网页下载的包已自动填好
  TOKEN=连接令牌 —— 管理员下载的包为 enc:v1: 加密值（bridge.key 为同目录解密密钥，勿删/勿外传）；
        普通用户下载的包此处为空，请联系平台管理员索取后填入（或让管理员下载含令牌的包给你）
  AGENT=（可选）只服务某个数字员工 id；留空 = 服务整个平台
  CLIENT=（可选）本机标识；留空 = 自动生成并持久化的唯一编号
  LOCAL_PORT=（可选）本机回环发现端口，默认 17321

说明
  - 本机桥连回服务器使用“反向隧道”，无需公网 IP / 端口映射。
  - 本包为 Windows x64 自包含发布：目标电脑不需要安装 .NET 运行时。
  - 开机自启仅注册当前 Windows 用户（HKCU Run），无需管理员权限。
  - 令牌加密存储用于避免明文外泄；bridge.key 与安装包一起分发，属于静态加密，
    请在受信范围内传递安装包，勿公开发布。
'@
[System.IO.File]::WriteAllText($readme, $readmeText, (New-Object System.Text.UTF8Encoding($true)))

Write-Host "[4/4] compress zip"
$zipName = "AguiGroupChat-NativeBridge-$Version-win-x64.zip"
$zip = Join-Path $out $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }
# zip 根目录含 exe / dll / bat / txt（publish 目录即 zip 根；不套一层父目录，解压即用）
# 用 .NET ZipFile（绕开 Windows PowerShell 5.1 Compress-Archive 的路径/编码异常）
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
if (-not (Test-Path $zip)) { throw "zip creation failed" }

$exe = Join-Path $publish "AguiGroupChat.NativeBridge.exe"
if (-not (Test-Path $exe)) { throw "publish output missing AguiGroupChat.NativeBridge.exe" }

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($size MB)"
Write-Host "Next: log in to the platform as an admin, open Profile -> NativeBridge, upload this zip."
Write-Host 'Then users download the same package from the profile popup (or give them the zip directly).'
