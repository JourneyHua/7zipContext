# 7zipContext 安装脚本（Q12：便携版 + 一键注册，无需管理员）
#
#   powershell -ExecutionPolicy Bypass -File scripts\install.ps1
#
# 参数：
#   -SkipPublish     跳过 dotnet publish（只改了注册逻辑、重跑时装得快）
#   -RestartExplorer 完成后自动重启资源管理器（菜单立即可见；不加则提示手动）
#
# 步骤：发布 → 注册 Win11 新版右键菜单（AppX）→ 清理 v1 残留 → 注册经典菜单 handler → 安装自检
# 卸载：powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1

param(
    [switch]$SkipPublish,
    [switch]$RestartExplorer
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # 压掉 Add-AppxPackage 的部署进度刷屏
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$pkgName = '7zipContext.ContextMenu'
$exe = Join-Path $dist '7zipContext.exe'

function Say([string]$msg, [string]$color = 'Gray') { Write-Host $msg -ForegroundColor $color }
function Step([int]$n, [string]$msg) { Write-Host "[$n/4] $msg" -ForegroundColor Cyan }

# ---- 0. 杀掉可能锁住 dist 文件的进程（COM 服务器常驻，重装必撞） ----
Get-Process -Name '7zipContext.Shell', '7zipContext' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); Say "  已停止残留进程 $($_.ProcessName) ($($_.Id))" 'DarkGray' } catch { }
}
# 菜单动词/预览处理器由系统 dllhost.exe 代理承载（SurrogateServer），
# 它会加载并锁住 dist\7zipContext.Shell.dll —— 不清理就会导致 AppX 重注册报 0x80073D02（文件被占用）。
Get-Process -Name 'dllhost' -ErrorAction SilentlyContinue | ForEach-Object {
    try {
        $hosting = $_.Modules | Where-Object { $_.FileName -like '*7zipContext*' }
        if ($hosting) { $_.Kill(); Say "  已停止占用 dist 的代理宿主 dllhost ($($_.Id))" 'DarkGray' }
    } catch { }
}
Start-Sleep -Milliseconds 500

# ---- 1. 发布 ----
if ($SkipPublish) {
    Step 1 '跳过发布（-SkipPublish）'
} else {
    Step 1 '发布 7zipContext ...'
    & (Join-Path $root 'scripts\publish.ps1')
}

$missing = @('7zipContext.exe', '7zipContext.Shell.comhost.dll',
             '7zipContext.Classic.comhost.dll', 'tools\7z.exe', 'tools\7zG.exe', 'tools\7zFM.exe') |
    Where-Object { -not (Test-Path (Join-Path $dist $_)) }
if ($missing.Count -gt 0) {
    throw "dist 产物不完整，缺少：$($missing -join ', ')。请去掉 -SkipPublish 重跑。"
}

# ---- 2. 注册 Win11 新版右键菜单（AppX loose 注册，dist 即包根） ----
Step 2 '注册 Win11 新版右键菜单 ...'
Copy-Item (Join-Path $root 'package\AppxManifest.xml') (Join-Path $dist 'AppxManifest.xml') -Force
# 图标资源：Square44x44Logo / Square150x150Logo / StoreLogo（父项图标就取 44x44 那张）
Copy-Item (Join-Path $root 'package\*.png') $dist -Force

Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
# 改名前的旧包必须注销，否则两套 AppX 菜单并存（旧包指向 yaya.*.dll，文件已不存在）
Get-AppxPackage -Name 'yaya.ContextMenu' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue | Out-Null

$menuRegistered = $false
$registerError = ''
for ($attempt = 1; $attempt -le 2; $attempt++) {
    try {
        Add-AppxPackage -Register (Join-Path $dist 'AppxManifest.xml') -ErrorAction Stop
        $menuRegistered = $true
        Say '  [OK] 菜单注册成功（新版右键菜单已生效）' 'Green'
        break
    } catch {
        $registerError = $_.Exception.Message
        # 0x80073D02：包内文件被占用（多由 dllhost 代理宿主未释放引起）→ 再清一次后重试
        if ($attempt -lt 2 -and $registerError -match '0x80073D02') {
            Get-Process -Name 'dllhost' -ErrorAction SilentlyContinue | ForEach-Object {
                try {
                    if ($_.Modules | Where-Object { $_.FileName -like '*7zipContext*' }) { $_.Kill() }
                } catch { }
            }
            Start-Sleep -Milliseconds 800
            continue
        }
    }
}
if (-not $menuRegistered) {
    Say '  [X] 菜单注册失败。' 'Yellow'
    Say "    $registerError" 'Yellow'
    Say '    若提示部署被拒绝：开启「开发者模式」（设置 → 系统 → 开发者选项）后重跑本脚本。' 'Yellow'
    Say '    （失败也可用「显示更多选项」里的经典菜单，功能等价）' 'Yellow'
}

# ---- 3. 清理 v1 残留（注册表动词 / 预览窗格 / 文件关联） ----
Step 3 '清理 v1 残留注册（动词 / 预览窗格 / 文件关联）...'

# v2 只做右键菜单：不注册文件关联、不注册预览窗格，只把旧版残留清干净。
# WinExe 进程 $LASTEXITCODE 为空（5.1 对 GUI 程序不写），必须 -PassThru 取退出码
$proc = Start-Process -FilePath $exe -ArgumentList @('--install') -Wait -PassThru -WindowStyle Hidden
if ($proc.ExitCode -ne 0) {
    Say "  [X] 7zipContext.exe --install 返回失败（exit=$($proc.ExitCode)）。" 'Yellow'
} else {
    Say '  [OK] v1 残留清理完成（v2 不再注册任何文件关联/预览窗格）' 'Green'
}
New-Item 'HKCU:\Software\7zipContext' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\7zipContext' -Name 'InstallMarker' -Value 1 -Force | Out-Null

# ---- 3b. 注册经典菜单预览 handler（7zipContext.Classic，托管 IContextMenu） ----
Say '  注册经典菜单预览（7zipContext.Classic）...' 'DarkGray'
& (Join-Path $root 'scripts\7zipContext-menu.ps1') -Install -NoRestart | Out-Null
if ($LASTEXITCODE -ne 0) { Say '  [X] 经典菜单 handler 注册失败。' 'Yellow' }
else { Say '  [OK] 经典菜单 handler 已注册（4 个 shell 目标）' 'Green' }

# ---- 4. 安装自检 ----
Step 4 '安装自检 ...'
$ok = 0; $bad = 0
function Check([string]$name, [bool]$pass, [string]$hint = '') {
    if ($pass) { $script:ok++; Say "  [OK] $name" 'Green' }
    else { $script:bad++; Say "  [X] $name$(if ($hint) { " — $hint" })" 'Yellow' }
}

Check 'dist 产物完整' ($missing.Count -eq 0)

$pkg = Get-AppxPackage -Name $pkgName
Check "AppX 菜单包（$pkgName）" ($menuRegistered -and $pkg -and $pkg.Status -eq 'Ok') '重跑 install 或开启开发者模式'

# v1 残留必须为 0：预览窗格处理器 + 文件关联 ProgId。
# v2 不再注册这两样，但老机器上的注册表只能靠 7zipContext.exe --install 清掉 —— 所以必须验。
$previewLeft = Test-Path 'HKCU:\Software\Classes\CLSID\{B9A10010-8C3F-4A2E-9E6D-5F1A2B3C4D10}'
Check '预览窗格处理器残留（IPreviewHandler）' (-not $previewLeft) 'v1 残留未清掉'

# 默认打开方式：.zip/.7z/.rar 的 ProgId 必须指向 7zFM.exe
# （Windows 不允许静默改默认程序，这里只保证「打开方式」里可用）
$fmOk = 0
$hkcuAssoc = [Microsoft.Win32.Registry]::CurrentUser
foreach ($ext in '.zip', '.7z', '.rar') {
    $k = $hkcuAssoc.OpenSubKey("Software\Classes\7zipContext$ext\shell\open\command")
    if ($k -and $k.GetValue('') -like '*7zFM.exe*') { $fmOk++; $k.Close() }
}
Check "默认打开方式已注册（7zFM $fmOk/3）" ($fmOk -eq 3) 'yaya.exe --install 失败，或 tools\7zFM.exe 缺失'

# comhost 真实 COM 激活（Explorer 同款路径；进程内冒烟测试覆盖不到这里）
$comhostOk = $true
$comhostHint = ''
foreach ($probeClsid in @('B9A10001-8C3F-4A2E-9E6D-5F1A2B3C4D01')) {
    try {
        $t = [Type]::GetTypeFromCLSID([guid]$probeClsid)
        $o = [Activator]::CreateInstance($t)
        if ($o) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($o) | Out-Null }
    } catch {
        $comhostOk = $false
        $comhostHint = '缺 .NET Desktop 运行时：安装 .NET 8 Desktop Runtime 后重跑'
    }
}
Check '菜单动词 COM 激活（comhost）' $comhostOk $comhostHint

# 经典菜单 handler（7zipContext.Classic）。教训：comhost 的 CLSID→类型映射不随增量编译刷新，
# 类改名后只跑 publish 会得到 0x80040111 CLASS_E_CLASSNOTAVAILABLE —— 注册表一切正常，
# 菜单里却什么都没有。只有实例化探针抓得到，所以必须探。
$hkcu2 = [Microsoft.Win32.Registry]::CurrentUser
$classicComhost = Join-Path $dist '7zipContext.Classic.comhost.dll'
$ck = $hkcu2.OpenSubKey('Software\Classes\CLSID\{B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20}\InprocServer32')
$classicInproc = if ($ck) { $ck.GetValue('') } else { '' }
$classicScopes = 0
foreach ($s in '*', 'Directory', 'Directory\Background', 'DesktopBackground') {
    $sk = $hkcu2.OpenSubKey("Software\Classes\$s\shellex\ContextMenuHandlers\7zipContext")
    if ($sk) { $classicScopes++; $sk.Close() }
}
Check "经典菜单 handler 注册（$classicScopes/4）" `
    (($classicInproc -eq $classicComhost) -and $classicScopes -eq 4) 'scripts\7zipContext-menu.ps1 -Install 失败'

$classicActive = $false
try {
    $t2 = [Type]::GetTypeFromCLSID([guid]'B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20')
    $o2 = [Activator]::CreateInstance($t2)
    if ($o2) { [System.Runtime.InteropServices.Marshal]::ReleaseComObject($o2) | Out-Null; $classicActive = $true }
} catch { }
Check '经典菜单 handler 实例化（comhost）' $classicActive 'comhost 类型映射过期？清 obj/bin 全量重发'

# 菜单命令真实可执行（Invoke 走 Explorer 点击时的同款路径）。
# 历史教训：dllhost 代理宿主里 AppContext.BaseDirectory 是空字符串，7zipContext.exe 定位失败，
# 所有菜单项点下去都静默 E_FAIL（解压/预览/压缩全无反应），而"激活成功"的自检照样全绿。
$probeExe = Join-Path $root 'tests\7zipContext.ComProbe\bin\Release\net8.0-windows\7zipContext.ComProbe.exe'
if (Test-Path $probeExe) {
    $tmp = Join-Path $env:TEMP ('7zipContext-invoke-check-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
    try {
        New-Item -ItemType Directory -Path (Join-Path $tmp 'src') -Force | Out-Null
        Set-Content -Path (Join-Path $tmp 'src\probe.txt') -Value '7zipContext' -Encoding ascii
        Push-Location (Join-Path $tmp 'src')
        try {
            & (Join-Path $dist 'tools\7z.exe') a -tzip (Join-Path $tmp 'probe.zip') 'probe.txt' -y -bso0 -bsp0 | Out-Null
        } finally { Pop-Location }

        $out = & $probeExe invoke-verb (Join-Path $tmp 'probe.zip') 'B9A10001-8C3F-4A2E-9E6D-5F1A2B3C4D01' 2>&1
        $m = ($out | Select-String 'Invoke hr=(0x[0-9A-Fa-f]{8})').Matches
        $hr = if ($m.Count -gt 0) { $m[0].Groups[1].Value } else { 'n/a' }
        Start-Sleep -Seconds 3
        $extracted = Test-Path (Join-Path $tmp 'probe')
        Check "菜单命令可执行（Invoke 实测 $hr）" ($hr -eq '0x00000000' -and $extracted) `
            '点菜单不会有任何反应：见 %TEMP%\7zipContext-invoke-error.log'
    } catch {
        Check '菜单命令可执行（Invoke 实测）' $false $_.Exception.Message
    } finally {
        if (Test-Path $tmp) { [System.IO.Directory]::Delete($tmp, $true) }
    }
} else {
    Say '  [--] 跳过 Invoke 实测（未编译 tests\7zipContext.ComProbe）' 'DarkGray'
}

$legacy = 0
foreach ($ext in '.zip', '.7z', '.rar', '.tar', '.gz', '.cab', '.iso') {
    foreach ($v in 'SmartExtract', 'SmartExtractDelete', 'ExtractToName', 'ExtractToFolder', 'Preview') {
        if (Test-Path "HKCU:\Software\Classes\SystemFileAssociations\$ext\shell\7zipContext.$v") { $legacy++ }
    }
}
Check '旧版重复动词已清零' ($legacy -eq 0) "残留 $legacy 个"

Say ''
if ($bad -eq 0) {
    Say '安装完成，全部自检通过。' 'Green'
} else {
    Say "安装完成，但有 $bad 项自检未过（见上方 [X]）。" 'Yellow'
}
if ($menuRegistered) {
    Say '  - Win11 新版右键：右键压缩包即见「智能解压到此处」等菜单项' 'Gray'
}
Say '  - 经典右键：右键 → 显示更多选项 → 智能解压到此处 ...' 'Gray'
Say '  - 经典菜单预览：右键压缩包 → 菜单下半部分直接铺开包内结构（规则改 dist\7zipContext.ini）' 'Gray'
Say '  - 卸载：powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1' 'Gray'

if ($RestartExplorer) {
    Say '正在重启资源管理器 ...' 'DarkGray'
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Start-Process explorer.exe }
} elseif ($menuRegistered) {
    Say '  （菜单若未出现：注销重登，或加 -RestartExplorer 参数重跑）' 'DarkGray'
}

exit $(if ($bad -eq 0) { 0 } else { 1 })
