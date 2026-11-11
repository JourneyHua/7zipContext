# 7zipContext 卸载脚本：还原一切注册（不留痕迹）
#
#   powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1
#
# 参数：
#   -Purge           顺带删除 dist\ 发布目录（默认保留）
#   -RestartExplorer 完成后重启资源管理器
#
# 注册表清理在本脚本内独立完成（不依赖 dist\7zipContext.exe），
# dist 已删/换机残留同样卸得干净。卸载后自动扫描验证 0 残留。

param(
    [switch]$Purge,
    [switch]$RestartExplorer
)

$ErrorActionPreference = 'Continue'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$pkgName = '7zipContext.ContextMenu'

function Say([string]$msg, [string]$color = 'Gray') { Write-Host $msg -ForegroundColor $color }
function Step([int]$n, [string]$msg) { Write-Host "[$n/3] $msg" -ForegroundColor Cyan }

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

# ---- 2. 注册表清理（独立清单，全量） ----
Step 2 '清理注册表（动词 / 预览窗格 / 经典菜单 handler / 文件关联）...'
$removed = 0
function Remove-RegKey([string]$path) {
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
foreach ($ext in '.zip', '.7z', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    Remove-RegKey "HKCU:\Software\Classes\yaya$ext"
}

# 2c. 文件关联（ProgId + OpenWithProgids）
foreach ($ext in '.zip', '.7z', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    Remove-RegKey "HKCU:\Software\Classes\7zipContext$ext"
    $owp = "HKCU:\Software\Classes\$ext\OpenWithProgids"
    if (Test-Path -LiteralPath $owp) {
        $v = Get-Item -LiteralPath $owp
        if ($v.GetValueNames() -contains "7zipContext$ext") {
            Remove-ItemProperty -LiteralPath $owp -Name "7zipContext$ext" -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }
}

# 2d. 7zipContext 自身设置（默认格式 / 安装标记）
Remove-RegKey 'HKCU:\Software\7zipContext'

Say "  已清理 $removed 处注册表项" 'Green'

# ---- 3. 自检残留 ----
Step 3 '卸载自检 ...'
$left = 0
if (Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue) { $left++ }
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.gz', '.cab', '.iso') {
    foreach ($v in 'SmartExtract', 'SmartExtractDelete', 'ExtractToName', 'ExtractToFolder', 'Preview') {
        if (Test-Path "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\7zipContext.$v") { $left++ }
    }
}
if (Test-Path 'HKCU:\Software\Classes\CLSID\{B9A10010-8C3F-4A2E-9E6D-5F1A2B3C4D10}') { $left++ }
foreach ($ext in '.zip', '.7z', '.tar', '.tgz', '.gz', '.bz2', '.xz') {
    if (Test-Path "HKCU:\Software\Classes\7zipContext$ext") { $left++ }
}
if (Test-Path 'HKCU:\Software\7zipContext') { $left++ }

if ($left -eq 0) {
    Say '  [OK] 0 残留' 'Green'
} else {
    Say "  [X] 仍有 $left 处残留（重启后重跑本脚本，或重启 explorer 后再查）" 'Yellow'
}

# ---- 收尾 ----
if ($Purge) {
    if (Test-Path $dist) {
        Remove-Item -LiteralPath $dist -Recurse -Force -ErrorAction SilentlyContinue
        Say "  已删除 dist\" 'Green'
    }
}

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
Say "卸载完成。$(if ($Purge) { '' } else { '（dist\ 已保留；加 -Purge 可一并删除）'})" 'Green'
exit $(if ($left -eq 0) { 0 } else { 1 })
