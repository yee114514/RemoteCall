# RemoteCall 插件安装脚本
# 用法：pwsh -File scripts\install.ps1 [-ClassIslandRoot <目录>] [-AddComponent]
#
#   -ClassIslandRoot   ClassIsland 解压目录（默认 E:\ClassIsland_app_windows_x64_full_folder）
#   -AddComponent      把「远程消息」组件写进主界面布局（会先备份 Default.json）

[CmdletBinding()]
param(
    [string]$ClassIslandRoot = "E:\ClassIsland_app_windows_x64_full_folder",
    [switch]$AddComponent,
    [switch]$StopClassIsland
)

$ErrorActionPreference = 'Stop'

$running = Get-Process -Name 'ClassIsland.Desktop', 'ClassIsland' -ErrorAction SilentlyContinue
if ($running) {
    if ($StopClassIsland) {
        Write-Host "==> 关闭正在运行的 ClassIsland"
        foreach ($p in $running) {
            if (-not $p.CloseMainWindow()) { $p.Kill() }
            if (-not $p.WaitForExit(15000)) { $p.Kill(); $p.WaitForExit(5000) | Out-Null }
        }
        Start-Sleep -Milliseconds 800
    }
    else {
        throw "ClassIsland 正在运行，插件文件被占用。请先关闭 ClassIsland，或加 -StopClassIsland 让脚本自动关闭。"
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\RemoteCall\RemoteCall.csproj'
$outputDir = Join-Path $repoRoot 'src\RemoteCall\bin\Release\net8.0'
$pluginId = 'remote.call'
$componentId = '7c4b2e51-9f3a-4d6e-b1c8-2a5d9e7f3b64'

Write-Host "==> 构建 $project"
dotnet build $project -c Release --nologo | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "构建失败，未安装。"
}

$dataRoot = Join-Path $ClassIslandRoot 'data'
if (-not (Test-Path $dataRoot)) {
    throw "找不到 ClassIsland 数据目录：$dataRoot（请检查 -ClassIslandRoot）"
}

$pluginsRoot = Join-Path $dataRoot 'Plugins'
$target = Join-Path $pluginsRoot $pluginId
Write-Host "==> 安装到 $target"
if (Test-Path $target) {
    Get-ChildItem $target -Filter '*.disabled' | Remove-Item -Force
    Remove-Item $target -Recurse -Force
}
New-Item -ItemType Directory -Path $target -Force | Out-Null

Get-ChildItem $outputDir -File |
    Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { Copy-Item $_.FullName -Destination $target -Force }

$installed = Get-ChildItem $target -File | Select-Object -ExpandProperty Name
Write-Host ("    已复制：" + ($installed -join ', '))

if ($AddComponent) {
    $layoutPath = Join-Path $dataRoot 'Config\ComponentLayouts\Default.json'
    if (-not (Test-Path $layoutPath)) {
        Write-Warning "没有找到组件布局文件 $layoutPath，跳过组件注入。请在 ClassIsland 里手动添加「远程消息」组件。"
    }
    else {
        $running = Get-Process -Name 'ClassIsland' -ErrorAction SilentlyContinue
        if ($running) {
            Write-Warning "检测到 ClassIsland 正在运行，它退出时会覆盖布局文件。请关闭 ClassIsland 后重新运行本脚本的 -AddComponent。"
        }
        else {
            $json = Get-Content $layoutPath -Raw
            $node = [System.Text.Json.Nodes.JsonNode]::Parse($json)
            $lines = $node['Lines']
            if ($null -eq $lines -or $lines.Count -eq 0) {
                Write-Warning "组件布局里没有可用行，跳过组件注入。"
            }
            else {
                $children = $lines[0]['Children']
                $exists = $false
                foreach ($child in $children) {
                    if ($null -ne $child['Id'] -and $child['Id'].ToString().ToLower() -eq $componentId) {
                        $exists = $true
                    }
                }

                if ($exists) {
                    Write-Host "==> 主界面布局里已经有「远程消息」组件，跳过。"
                }
                else {
                    Copy-Item $layoutPath "$layoutPath.bak" -Force
                    $entry = [System.Text.Json.Nodes.JsonNode]::Parse("{`"Id`":`"$componentId`"}")
                    $children.Insert(0, $entry)
                    $options = [System.Text.Json.JsonSerializerOptions]::new()
                    $options.WriteIndented = $false
                    $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
                    Set-Content -Path $layoutPath -Value $node.ToJsonString($options) -NoNewline -Encoding utf8
                    Write-Host "==> 已把「远程消息」组件插入主界面第一行首列（备份：$layoutPath.bak）"
                }
            }
        }
    }
}

Write-Host ""
Write-Host "安装完成。"
Write-Host "1) 启动 ClassIsland（或重启），插件目录：$target"
Write-Host "2) 打开 ClassIsland 应用设置 →「远程消息」，可以看到默认密码（admin）与端口。"
Write-Host "3) 手机连同一个局域网，访问 http://<本机IP>:8000/ ，用默认密码 admin 登录。"
Write-Host "4) 若手机打不开，请允许 ClassIsland 通过 Windows 防火墙："
Write-Host "   New-NetFirewallRule -DisplayName 'ClassIsland RemoteCall' -Direction Inbound -Program '$ClassIslandRoot\ClassIsland.exe' -Action Allow -Profile Private"
