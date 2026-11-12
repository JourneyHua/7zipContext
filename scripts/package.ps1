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
$stage = Join-Path (Join-Path $root 'dist-stage') ('package-' + [Guid]::NewGuid().ToString('N'))
$setupOut = Join-Path $stage 'Setup'
$comprobeOut = Join-Path $stage 'ComProbe'
$zip = Join-Path $release '7zipContext-Setup.zip'

. (Join-Path $PSScriptRoot 'deployment.ps1')
$deploymentContext = Enter-DeploymentLease
try {
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

if (-not $SkipAppPublish) {
    & (Join-Path $root 'scripts\publish.ps1')
    if ($LASTEXITCODE -ne 0) { throw '应用发布失败' }
}

& $dotnet publish (Join-Path $root 'src\7zipContext.Setup\7zipContext.Setup.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    -o $setupOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw '安装工具发布失败' }

# install.ps1 的「菜单命令可执行（Invoke 实测）」必须保留，所以 ComProbe 也进 payload。
& $dotnet publish (Join-Path $root 'tests\7zipContext.ComProbe\7zipContext.ComProbe.csproj') `
    -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false `
    -o $comprobeOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'ComProbe 发布失败' }

# SkipAppPublish must not silently package a previous self-contained dist.
if (Test-Path -LiteralPath (Join-Path $root 'dist\coreclr.dll')) {
    throw 'dist 包含旧的自包含运行时，请先重新发布框架依赖版本，再打包。'
}
$runtimeConfigPath = Join-Path $root 'dist\7zipContext.runtimeconfig.json'
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$frameworks = @($runtimeConfig.runtimeOptions.frameworks) + @($runtimeConfig.runtimeOptions.framework)
if (-not ($frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' -and $_.version -like '8.*' })) {
    throw '主程序必须依赖系统 .NET 8 Desktop Runtime。'
}

if (Test-Path -LiteralPath $bundle) { Remove-DeploymentWorkspace $bundle $release }
New-Item -ItemType Directory -Path $bundle -Force | Out-Null
New-Item -ItemType Directory -Path $release -Force | Out-Null

# 主程序、安装器和菜单组件统一依赖系统 .NET 8 Desktop Runtime x64。
# 安装包只携带项目组件，不附带运行时。
Copy-Item (Join-Path $setupOut '*') $bundle -Recurse -Force

$payload = Join-Path $bundle 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
Copy-Item (Join-Path $root 'dist') (Join-Path $payload 'dist') -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $payload 'scripts') -Force | Out-Null
foreach ($script in 'install.ps1', 'uninstall.ps1', '7zipContext-menu.ps1', 'deployment.ps1', 'DeploymentFiles.cs') {
    Copy-Item (Join-Path $root "scripts\$script") (Join-Path $payload "scripts\$script") -Force
}
Copy-Item (Join-Path $root 'package') (Join-Path $payload 'package') -Recurse -Force

$comprobeDestination = Join-Path $payload 'tests\7zipContext.ComProbe\bin\Release\net8.0-windows'
New-Item -ItemType Directory -Path $comprobeDestination -Force | Out-Null
Copy-Item (Join-Path $comprobeOut '*') $comprobeDestination -Recurse -Force

$expected = @(
    '7zipContext.Setup.exe',
    'ZipContext.Core.dll',
    'payload\dist\7zipContext.exe',
    'payload\dist\7zipContext.dll',
    'payload\dist\7zipContext.runtimeconfig.json',
    'payload\dist\7zipContext.deps.json',
    'payload\dist\7zipContext.Shell.comhost.dll',
    'payload\dist\7zipContext.Classic.comhost.dll',
    'payload\scripts\install.ps1',
    'payload\scripts\uninstall.ps1',
    'payload\scripts\7zipContext-menu.ps1',
    'payload\scripts\deployment.ps1',
    'payload\scripts\DeploymentFiles.cs',
    'payload\package\AppxManifest.xml',
    'payload\tests\7zipContext.ComProbe\bin\Release\net8.0-windows\7zipContext.ComProbe.exe'
)
$missing = $expected | Where-Object { -not (Test-Path (Join-Path $bundle $_)) }
if ($missing.Count -gt 0) { throw "安装包内容缺失：$($missing -join ', ')" }

$guide = @'
7zipContext 图形化安装包
========================

安装：
1. 先安装 .NET 8 Desktop Runtime x64（已有则跳过）：
   https://dotnet.microsoft.com/download/dotnet/8.0
2. 完整解压本 ZIP，不要直接在压缩软件窗口里运行。
3. 双击 7zipContext.Setup.exe。
4. 点击「安装 / 修复」，等待窗口提示成功。
5. 可选：勾选「设为默认打开方式」（默认不勾选），安装成功后在系统设置中为 .zip/.7z/.rar 选择 7-Zip。
   安装和修复只添加可选打开方式，保留现有默认程序。

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
- 主程序、安装器和菜单扩展均需要系统 .NET 8 Desktop Runtime x64；本 ZIP 不包含运行时。
  缺少时请手动安装：
  https://dotnet.microsoft.com/download/dotnet/8.0
- 安装和卸载期间资源管理器会短暂重启，这是正常现象。
'@
[System.IO.File]::WriteAllText((Join-Path $bundle '使用说明.txt'), $guide,
    [System.Text.UTF8Encoding]::new($true))

$zipCandidate = Join-Path $release ('7zipContext-Setup-' + [Guid]::NewGuid().ToString('N') + '.zip')
$zipBackup = $zip + '.' + [Guid]::NewGuid().ToString('N') + '.bak'
try {
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zipCandidate -CompressionLevel Optimal
    if (Test-Path -LiteralPath $zip) { [IO.File]::Replace($zipCandidate, $zip, $zipBackup) }
    else { [IO.File]::Move($zipCandidate, $zip) }
    if (Test-Path -LiteralPath $zipBackup) {
        try { [IO.File]::Delete($zipBackup) }
        catch { Write-Warning "旧 ZIP 保留在：$zipBackup" }
    }
} finally {
    if (Test-Path -LiteralPath $zipCandidate) { [IO.File]::Delete($zipCandidate) }
}
if (Test-Path -LiteralPath $stage) {
    try { Remove-DeploymentWorkspace $stage (Join-Path $root 'dist-stage') }
    catch { Write-Warning ('安装包已生成，准备目录保留：' + $_) }
}

$sha256 = [Security.Cryptography.SHA256]::Create()
$zipStream = [IO.File]::OpenRead($zip)
try { $hash = [BitConverter]::ToString($sha256.ComputeHash($zipStream)).Replace('-', '') }
finally { $zipStream.Dispose(); $sha256.Dispose() }
Write-Host "安装包已生成：$zip" -ForegroundColor Green
Write-Host "SHA256：$hash" -ForegroundColor DarkGray

} finally { Exit-DeploymentLease $deploymentContext }
