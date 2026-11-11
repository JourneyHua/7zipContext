# 生成普通用户使用的图形化安装包 release\7zipContext-Setup.zip
# 包内不包含 7-Zip 本体；安装时仍由 install.ps1 探测或从官方发布页获取。
param(
    [switch]$SkipAppPublish
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$release = Join-Path $root 'release'
$bundle = Join-Path $release '7zipContext-Setup'
$stage = Join-Path $root 'dist-stage'
$setupOut = Join-Path $stage 'Setup'
$comprobeOut = Join-Path $stage 'ComProbe'
$zip = Join-Path $release '7zipContext-Setup.zip'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

if (-not $SkipAppPublish) {
    & (Join-Path $root 'scripts\publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw '应用发布失败' }
}

if (Test-Path $setupOut) { [System.IO.Directory]::Delete($setupOut, $true) }
& $dotnet publish (Join-Path $root 'src\7zipContext.Setup\7zipContext.Setup.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false `
    -o $setupOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '安装工具发布失败' }

# install.ps1 的「菜单命令可执行（Invoke 实测）」必须保留，所以 ComProbe 也进 payload。
if (Test-Path $comprobeOut) { [System.IO.Directory]::Delete($comprobeOut, $true) }
& $dotnet publish (Join-Path $root 'tests\7zipContext.ComProbe\7zipContext.ComProbe.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false `
    -o $comprobeOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'ComProbe 发布失败' }

if (Test-Path $bundle) { [System.IO.Directory]::Delete($bundle, $true) }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
New-Item -ItemType Directory -Path $release -Force | Out-Null

# 安装器自身保持框架依赖（Shell/Classic 本来就需要 .NET 8 Desktop Runtime），
# 主程序仍由 publish.ps1 自包含发布，避免在 ZIP 里重复携带两套运行时。
Copy-Item (Join-Path $setupOut '*') $bundle -Recurse -Force

$payload = Join-Path $bundle 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item (Join-Path $root 'dist') (Join-Path $payload 'dist') -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $payload 'scripts') -Force | Out-Null
foreach ($script in 'install.ps1', 'uninstall.ps1', '7zipContext-menu.ps1') {
    Copy-Item (Join-Path $root "scripts\$script") (Join-Path $payload "scripts\$script") -Force
}
Copy-Item (Join-Path $root 'package') (Join-Path $payload 'package') -Recurse -Force

$comprobeDestination = Join-Path $payload 'tests\7zipContext.ComProbe\bin\Release\net8.0-windows'
New-Item -ItemType Directory -Path $comprobeDestination -Force | Out-Null
Copy-Item (Join-Path $comprobeOut '*') $comprobeDestination -Recurse -Force

$expected = @(
    '7zipContext.Setup.exe',
    'payload\dist\7zipContext.exe',
    'payload\dist\7zipContext.Shell.comhost.dll',
    'payload\dist\7zipContext.Classic.comhost.dll',
    'payload\scripts\install.ps1',
    'payload\scripts\uninstall.ps1',
    'payload\scripts\7zipContext-menu.ps1',
    'payload\package\AppxManifest.xml',
    'payload\tests\7zipContext.ComProbe\bin\Release\net8.0-windows\7zipContext.ComProbe.exe'
)
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $bundle $_)) }
if ($missing.Count -gt 0) { throw "安装包内容缺失：$($missing -join ', ')" }

$guide = @'
7zipContext 图形化安装包
========================

安装：
1. 完整解压本 ZIP，不要直接在压缩软件窗口里运行。
2. 双击 7zipContext.Setup.exe。
3. 点击「安装 / 修复」，等待窗口提示成功。

安装位置：%LOCALAPPDATA%\7zipContext\app\
安装完成后可以删除本 ZIP、解压目录和安装工具，不影响使用。

7-Zip 引擎：
- 本机已有完整 7-Zip 时直接复用，不下载。
- 没有时会从官方发布页下载、校验并解包，无需单独安装。
- 仓库和本安装包都不携带 7-Zip 本体。

卸载：
再次运行 7zipContext.Setup.exe，点击「卸载」。
默认删除程序文件；本工具下载的 7-Zip 引擎是否删除可单独勾选。
本机原先安装的 7-Zip 永远不会被删除。

注意：
- 支持 Windows 11 x64，无需管理员权限。
- 菜单扩展需要 .NET 8 Desktop Runtime；若系统提示缺少运行时，请先安装：
  https://dotnet.microsoft.com/download/dotnet/8.0
- 安装和卸载期间资源管理器会短暂重启，这是正常现象。
'@
[System.IO.File]::WriteAllText((Join-Path $bundle '使用说明.txt'), $guide,
    [System.Text.UTF8Encoding]::new($true))

if (Test-Path $zip) { [System.IO.File]::Delete($zip) }
Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip -CompressionLevel Optimal
if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
Write-Host "安装包已生成：$zip" -ForegroundColor Green
Write-Host "SHA256：$hash" -ForegroundColor DarkGray
