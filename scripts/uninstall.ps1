# 7zipContext 卸载脚本：还原一切注册（不留痕迹）
#
#   powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
#
# 参数：
#   -Purge           顺带删除 dist\ 发布目录（默认保留）
#   -PurgeEngine     顺带删除已下载的 7-Zip 引擎（默认保留；本机已有的引擎不会动）
#   -RestartExplorer 完成后重启资源管理器
#
# 注册表清理在本脚本内独立完成（不依赖 dist\7zipContext.exe），
# dist 已删/换机残留同样卸得干净。卸载后自动扫描验证 0 残留。

param(
    [switch]$Purge,
    [switch]$PurgeEngine,
    [switch]$RestartExplorer
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$pkgName = '7zipContext.ContextMenu'

function Say([string]$msg, [string]$color = 'Gray') { Write-Host $msg -ForegroundColor $color }
function Step([int]$n, [string]$msg) { Write-Host "[$n/3] $msg" -ForegroundColor Cyan }

try {
    . (Join-Path $PSScriptRoot 'deployment.ps1')
    $deploymentContext = Enter-DeploymentLease
} catch {
    [Console]::Error.WriteLine("无法取得部署锁：$_")
    exit 1
}
try {
# ---- 0. 停掉常驻进程（COM 服务器/主程序，否则文件与注册清理可能被挡） ----
Get-Process -Name '7zipContext.Shell', '7zipContext' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); Say "  已停止 $($_.ProcessName) ($($_.Id))" 'DarkGray' } catch { }
}
Start-Sleep -Milliseconds 300

# ---- 1. 注销 AppX 包（Win11 新版右键菜单） ----
Step 1 '注销 Win11 新版右键菜单（AppX 包）...'
$pkg = Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue
if ($pkg) {
    $pkg | Remove-AppxPackage -ErrorAction SilentlyContinue
    Say '  已注销' 'Green'
} else {
    Say '  未发现注册，跳过' 'DarkGray'
}

Get-AppxPackage -Name 'yaya.ContextMenu' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue

# ---- 2. 注册表清理（独立清单，全量） ----
Step 2 '清理注册表（动词 / 预览窗格 / 经典菜单 handler / 文件关联）...'
$removed = 0
$cleanupKeys = [System.Collections.Generic.List[string]]::new()
function Remove-RegKey([string]$path) {
    $script:cleanupKeys.Add($path)
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
        $script:removed++
    }
}

# 2a. 新旧动词（AppX 时代的旧注册表动词 + 历史遗留）
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.tgz', '.gz', '.bz2', '.xz', '.zst', '.cab', '.iso', '.arj', '.lzh') {
    foreach ($v in 'SmartExtract', 'SmartExtractDelete', 'ExtractToName', 'ExtractToFolder', 'Preview') {
        Remove-RegKey "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\7zipContext.$v"
    }
}
foreach ($t in '*', 'Directory') {
    foreach ($v in 'AddZip', 'Add7z', 'QuickZip') {
        Remove-RegKey "HKCU:\Software\Classes\$t\shell\7zipContext.$v"
    }
}

# 2b. Explorer 预览窗格处理器（CLSID + 各扩展 ShellEx）
Remove-RegKey 'HKCU:\Software\Classes\CLSID\{B9A10010-8C3F-4A2E-9E6D-5F1A2B3C4D10}'
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.tgz', '.gz', '.bz2', '.xz', '.zst', '.cab', '.iso', '.arj', '.lzh') {
    Remove-RegKey "HKCU:\Software\Classes\$ext\ShellEx\{8895b1c6-b41f-4c1c-a562-0d564250836f}"
}

# 2e. 经典菜单预览 handler（7zipContext.Classic，托管 IContextMenu，explorer 进程内加载）
# 注意：键名含 '*'，必须用 -LiteralPath（通配符会被当模式展开）
# 历史键名 yayaPreview / yayaClassicSpike 用原字面量，不能被批量改名带跑
Remove-RegKey 'HKCU:\Software\Classes\CLSID\{B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20}'
foreach ($s in '*', 'Directory', 'Directory\Background', 'DesktopBackground') {
    foreach ($n in '7zipContext', 'yayaPreview', 'yayaClassicSpike') {
        Remove-RegKey "HKCU:\Software\Classes\$s\shellex\ContextMenuHandlers\$n"
    }
}
# 改名前的 ProgId（yaya.zip / yaya.7z …）同样要清
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    Remove-RegKey "HKCU:\Software\Classes\yaya$ext"
}

# 2c. 文件关联（ProgId + OpenWithProgids）
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    Remove-RegKey "HKCU:\Software\Classes\7zipContext$ext"
    # 只清掉指向本项目的默认值，不能留下指向已删除 ProgId 的悬空关联。
    $extensionKey = "HKCU:\Software\Classes\$ext"
    if (Test-Path -LiteralPath $extensionKey) {
        $extension = Get-Item -LiteralPath $extensionKey
        if ($extension.GetValue('') -in @("7zipContext$ext", "yaya$ext")) {
            $extension.DeleteValue('', $false)
        }
        $extension.Close()
    }
    $owp = "HKCU:\Software\Classes\$ext\OpenWithProgids"
    if (Test-Path -LiteralPath $owp) {
        $v = Get-Item -LiteralPath $owp
        foreach ($id in @("7zipContext$ext", "yaya$ext")) {
            if ($v.GetValueNames() -contains $id) {
                Remove-ItemProperty -LiteralPath $owp -Name $id -Force -ErrorAction SilentlyContinue
                $removed++
            }
        }
        $v.Close()
    }
}

# 2d. 7zipContext 自身设置（默认格式 / 安装标记）
Remove-RegKey 'HKCU:\Software\7zipContext'

Say "  已清理 $removed 处注册表项" 'Green'

# ---- 3. 自检残留 ----
Step 3 '卸载自检 ...'
$left = 0
foreach ($path in $cleanupKeys) {
    if (Test-Path -LiteralPath $path) { $left++ }
}
if (Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue) { $left++ }
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.gz', '.cab', '.iso') {
    foreach ($v in 'SmartExtract', 'SmartExtractDelete', 'ExtractToName', 'ExtractToFolder', 'Preview') {
        if (Test-Path "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\7zipContext.$v") { $left++ }
    }
}
if (Test-Path 'HKCU:\Software\Classes\CLSID\{B9A10010-8C3F-4A2E-9E6D-5F1A2B3C4D10}') { $left++ }
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    if (Test-Path "HKCU:\Software\Classes\7zipContext$ext") { $left++ }
}
if (Test-Path 'HKCU:\Software\7zipContext') { $left++ }
if (Get-AppxPackage -Name 'yaya.ContextMenu' -ErrorAction SilentlyContinue) { $left++ }
if (Test-Path -LiteralPath 'HKCU:\Software\Classes\CLSID\{B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20}') { $left++ }
foreach ($scope in '*', 'Directory', 'Directory\Background', 'DesktopBackground') {
    foreach ($name in '7zipContext', 'yayaPreview', 'yayaClassicSpike') {
        if (Test-Path -LiteralPath "HKCU:\Software\Classes\$scope\shellex\ContextMenuHandlers\$name") { $left++ }
    }
}
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    if (Test-Path -LiteralPath "HKCU:\Software\Classes\yaya$ext") { $left++ }
    $key = Get-Item -LiteralPath "HKCU:\Software\Classes\$ext" -ErrorAction SilentlyContinue
    if ($key) {
        if ($key.GetValue('') -in @("7zipContext$ext", "yaya$ext")) { $left++ }
        $key.Close()
    }
    $key = Get-Item -LiteralPath "HKCU:\Software\Classes\$ext\OpenWithProgids" -ErrorAction SilentlyContinue
    if ($key) {
        if (@($key.GetValueNames() | Where-Object { $_ -in @("7zipContext$ext", "yaya$ext") }).Count -gt 0) { $left++ }
        $key.Close()
    }
}

if ($left -eq 0) {
    Say '  [OK] 0 残留' 'Green'
} else {
    Say "  [X] 仍有 $left 处残留（重启后重跑本脚本，或重启 explorer 后再查）" 'Yellow'
}

# ---- 文件清理：失败计入最终退出码 ----
$purgeFailures = [Collections.Generic.List[string]]::new()
if ($left -gt 0 -and ($Purge -or $PurgeEngine)) { $purgeFailures.Add('注册仍有残留，已保留程序与引擎文件供重试。') }
if ($Purge -and $left -eq 0) {
    try { Remove-VerifiedPurgePath $dist $root; Say '  dist 已删除或不存在' 'Green' }
    catch { $purgeFailures.Add("dist：$_") }
}
if ($PurgeEngine -and $left -eq 0) {
    $engineRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) '7zipContext'
    $engineGate = $null
    try {
        [void][IO.Directory]::CreateDirectory($engineRoot)
        $engineGate = [IO.File]::Open((Join-Path $engineRoot 'engine.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $cleanup = Remove-DownloadedEngineCache $engineRoot
        foreach ($failure in $cleanup.Failures) { $purgeFailures.Add($failure) }
        Say "  下载引擎目录已删除 $($cleanup.Deleted.Count) 个，本机或来源未知目录保留 $($cleanup.Preserved.Count) 个" 'Gray'
        foreach ($preserved in $cleanup.Preserved) { Say "  已保留：$preserved" 'DarkGray' }
    } catch { $purgeFailures.Add("引擎清理失败或无法取得锁：$_") }
    finally { if ($engineGate) { $engineGate.Dispose() } }
}
foreach ($failure in $purgeFailures) { Say "  [X] $failure" 'Yellow' }
$signature = @'
[DllImport("shell32.dll")] public static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
'@
$type = Add-Type -MemberDefinition $signature -Name 'ZipContextShell' -Namespace 'ZipContextUn' -PassThru -ErrorAction SilentlyContinue
if ($type) { $type::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero) }

if ($RestartExplorer) {
    Say '正在重启资源管理器 ...' 'DarkGray'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
}

Say ''
if ($left -gt 0 -or $purgeFailures.Count -gt 0) { Say '卸载尚未完成，请处理上述注册残留或文件删除错误后重试。' 'Yellow' } else { Say '卸载完成。' 'Green' }
if (-not $PurgeEngine) { Say '  （7-Zip 引擎已保留；加 -PurgeEngine 可删除本工具下载的引擎）' 'Gray' }
exit $(if ($left -eq 0 -and $purgeFailures.Count -eq 0) { 0 } else { 1 })

} finally { Exit-DeploymentLease $deploymentContext }
