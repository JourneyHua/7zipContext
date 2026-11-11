# 7zipContext 发布脚本：分项目独立发布后合并到 dist\
# 关键：三个项目绝不能共用 -o 输出目录——框架依赖发布会清理同目录里
# 不属于自己的文件（曾把自包含运行时 180+ 个 dll 全删掉，7zipContext.exe 启动即挂）。
# 产出：7zipContext.exe / Shell+Classic comhost / 项目文档（7-Zip 引擎不随包，安装阶段单独获取）
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$stage = Join-Path $root 'dist-stage'

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }

# 7zipContext.App 自包含（便携，开箱即用）；7zipContext.Shell / 7zipContext.Preview 框架依赖
# （COM hosting 不支持自包含 NETSDK1128，comhost 从系统 .NET Desktop 运行时引导）
$plans = @(
    @{ Name = '7zipContext.App';     SelfContained = 'true' },
    @{ Name = '7zipContext.Shell';   SelfContained = 'false' },
    @{ Name = '7zipContext.Classic'; SelfContained = 'false' }
)

# 经典菜单 handler 由 explorer.exe 进程内加载（托管 CLR 在 explorer 里跑），
# 现代菜单动词由系统 dllhost.exe 承载 —— 两者都会锁住 dist\*.dll。
# 只停 explorer 不够：菜单刚被点过/刚跑完安装自检时 dllhost 仍常驻，
# 直接删 dist 会报 UnauthorizedAccessException（实测踩过）。
function Stop-DistLockers {
    Get-Process -Name '7zipContext.Shell', '7zipContext' -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill() } catch { }
    }
    Get-Process -Name dllhost -ErrorAction SilentlyContinue | ForEach-Object {
        try {
            if ($_.Modules | Where-Object { $_.FileName -like '*7zipContext*' }) { $_.Kill() }
        } catch { }
    }
    if (Get-Process -Name explorer -ErrorAction SilentlyContinue) {
        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    }
}

$killedExplorer = (Get-Process -Name explorer -ErrorAction SilentlyContinue) -ne $null
try {
Stop-DistLockers
Start-Sleep -Milliseconds 1200

foreach ($plan in $plans) {
    Write-Host "  发布 $($plan.Name) ..." -ForegroundColor DarkGray
    $out = Join-Path $stage $plan.Name
    & $dotnet publish (Join-Path $root "src\$($plan.Name)\$($plan.Name).csproj") `
        -c Release -r win-x64 --self-contained $plan.SelfContained -p:PublishSingleFile=false `
        -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$($plan.Name) 发布失败" }
}

# 合并到 dist（App 优先铺底运行时，后两个只添 comhost 产物）
# 删除可能仍被残留句柄占用，重试几次并在每次重试前再清一遍锁。
$deleted = $false
for ($attempt = 1; $attempt -le 3 -and -not $deleted; $attempt++) {
    try {
        if (Test-Path $dist) { [System.IO.Directory]::Delete($dist, $true) }
        $deleted = $true
    } catch {
        if ($attempt -eq 3) {
            throw "无法清空 dist\（comhost dll 被占用）。最常见原因：刚在同一个 PowerShell 进程里跑过 " +
                  "install.ps1 自检 —— 它会实例化进程内的经典菜单 COM，DLL 会一直锁在宿主进程里，" +
                  "杀 explorer/dllhost 没用。请**新开一个 PowerShell 窗口**再跑 publish.ps1 或 install.ps1。" +
                  "`n原始错误：$($_.Exception.Message)"
        }
        Stop-DistLockers
        Start-Sleep -Milliseconds 900
    }
}
New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($plan in $plans) {
    Copy-Item (Join-Path $stage "$($plan.Name)\*") $dist -Recurse -Force
}

# 7-Zip 引擎不随包分发：安装阶段由 install.ps1 调 --ensure-engine 获取，
# 落到 %LOCALAPPDATA%\7zipContext\engine\，与 dist\（AppX 包根）彻底解耦。
# 因此这里不拷任何 7z*.exe / 7z.dll / Lang。

# 项目自身文档随发布产物分发（MIT 许可 + 第三方组件来源说明）。
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $f) (Join-Path $dist $f) -Force
}

# 产物核对
$expect = @('7zipContext.exe', '7zipContext.Shell.comhost.dll',
            '7zipContext.Classic.comhost.dll', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'coreclr.dll')
$missing = $expect | Where-Object { -not (Test-Path (Join-Path $dist $_)) }
if ($missing.Count -gt 0) { throw "发布产物缺失：$($missing -join ', ')" }

if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }

Write-Host "发布完成 → $dist（$($expect.Count)/$($expect.Count) 产物齐全，含自包含运行时）" -ForegroundColor Green
}
finally {
    # 即使编译、清空 dist 或复制产物中途失败，也必须恢复桌面和任务栏。
    if ($killedExplorer -and -not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        try { Start-Process explorer.exe }
        catch { Write-Warning "资源管理器自动恢复失败，请手动运行 explorer.exe：$($_.Exception.Message)" }
    }
}
