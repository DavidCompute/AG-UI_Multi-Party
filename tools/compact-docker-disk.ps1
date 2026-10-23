# 压缩 Docker Desktop 的 WSL 数据盘（docker_data.vhdx）
#
# 背景：`docker builder prune` / `docker image prune` 只能删掉虚拟磁盘里的内容，
# 宿主机的 ext4.vhdx / docker_data.vhdx 不会自动变小。压缩需要管理员权限。
#
# 用法：右键「以管理员身份运行 PowerShell」，执行：
#   powershell -ExecutionPolicy Bypass -File tools\compact-docker-disk.ps1
#
# 脚本会：停 Docker Desktop -> wsl --shutdown -> 压缩 vhdx -> 打印压缩前后大小。
# 跑完后请手动启动 Docker Desktop（或让 Zed 代你执行 docker compose up -d）。

$ErrorActionPreference = "Stop"

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "需要管理员权限：请以管理员身份重新打开 PowerShell 再运行本脚本。" -ForegroundColor Red
    exit 1
}

$candidates = @(
    (Join-Path $env:LOCALAPPDATA "Docker\wsl\disk\docker_data.vhdx"),
    (Join-Path $env:LOCALAPPDATA "Docker\wsl\data\ext4.vhdx")
) | Where-Object { Test-Path $_ }

if (-not $candidates) {
    Write-Host "未找到 Docker 的 vhdx（可能数据盘在自定义位置）。" -ForegroundColor Yellow
    exit 2
}

Write-Host "[1/4] 停止 Docker Desktop ..."
Get-Process "Docker Desktop", "com.docker.backend", "com.docker.build" -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 5

Write-Host "[2/4] 关闭 WSL ..."
wsl --shutdown
Start-Sleep -Seconds 5

foreach ($vhdx in $candidates) {
    $before = (Get-Item $vhdx).Length / 1GB
    Write-Host ("[3/4] 压缩 {0}（{1:N2} GB）..." -f $vhdx, $before)

    $done = $false
    if (Get-Command Optimize-VHD -ErrorAction SilentlyContinue) {
        try {
            Optimize-VHD -Path $vhdx -Mode Full
            $done = $true
        } catch {
            Write-Host ("Optimize-VHD 失败：{0}" -f $_.Exception.Message) -ForegroundColor Yellow
        }
    }
    if (-not $done) {
        $script = Join-Path $env:TEMP ("compact-" + [guid]::NewGuid().ToString("N") + ".txt")
        @(
            "select vdisk file=`"$vhdx`"",
            "attach vdisk readonly",
            "compact vdisk",
            "detach vdisk",
            "exit"
        ) | Set-Content -Path $script -Encoding ASCII
        diskpart /s $script | Out-Null
        Remove-Item $script -ErrorAction SilentlyContinue
    }

    $after = (Get-Item $vhdx).Length / 1GB
    Write-Host ("      完成：{0:N2} GB -> {1:N2} GB（释放 {2:N2} GB）" -f $before, $after, ($before - $after)) -ForegroundColor Green
}

$free = (Get-PSDrive C).Free / 1GB
Write-Host ("[4/4] C 盘可用：{0:N2} GB" -f $free) -ForegroundColor Green
Write-Host "现在可以启动 Docker Desktop；再执行 docker compose up -d 恢复服务。"
