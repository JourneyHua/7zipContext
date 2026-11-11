# 7zipContext 发布脚本：分项目独立发布后合并到 dist\
# 关键：三个项目绝不能共用 -o 输出目录——框架依赖发布会清理同目录里
# 不属于自己的文件（曾把自包含运行时 180+ 个 dll 全删掉，7zipContext.exe 启动即挂）。
# 产出：7zipContext.exe / Shell+Classic comhost / tools\7z*.exe+7z.dll / 第三方许可文本
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
# 它会锁住 dist\classic\*.dll —— 发布前必须先让它卸载，发布后再拉起来。
$killedExplorer = $false
if (Get-Process -Name explorer -ErrorAction SilentlyContinue) {
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    $killedExplorer = $true
    Start-Sleep -Milliseconds 1200
}

foreach ($plan in $plans) {
    Write-Host "  发布 $($plan.Name) ..." -ForegroundColor DarkGray
    $out = Join-Path $stage $plan.Name
    & $dotnet publish (Join-Path $root "src\$($plan.Name)\$($plan.Name).csproj") `
        -c Release -r win-x64 --self-contained $plan.SelfContained -p:PublishSingleFile=false `
        -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "$($plan.Name) 发布失败" }
}

# 合并到 dist（App 优先铺底运行时，后两个只添 comhost 产物）
if (Test-Path $dist) { [System.IO.Directory]::Delete($dist, $true) }
New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($plan in $plans) {
    Copy-Item (Join-Path $stage "$($plan.Name)\*") $dist -Recurse -Force
}

# 7z 后端：7z.exe + 7z.dll（控制台/列表）+ 7zG.exe（GUI 进度窗与密码框）
# 外加 7zFM.exe（默认打开方式）+ Lang\zh-cn.txt（中文界面）
New-Item -ItemType Directory -Path (Join-Path $dist 'tools') -Force | Out-Null
foreach ($f in '7z.exe', '7z.dll', '7zG.exe', '7zFM.exe') {
    Copy-Item (Join-Path $root "tools\$f") (Join-Path $dist "tools\$f") -Force
}
$langSrc = Join-Path $root 'tools\Lang'
if (Test-Path $langSrc) {
    New-Item -ItemType Directory -Path (Join-Path $dist 'tools\Lang') -Force | Out-Null
    Copy-Item (Join-Path $langSrc '*') (Join-Path $dist 'tools\Lang') -Force
}

# LGPL 要求二进制再分发随附许可信息：7-Zip 官方许可全文放 tools\，
# 项目 LICENSE 与第三方声明放 dist 根，保证 dist\ 单独分发时也是合规的。
foreach ($f in '7-Zip-License.txt') {
    Copy-Item (Join-Path $root "tools\$f") (Join-Path $dist "tools\$f") -Force
}
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
    Copy-Item (Join-Path $root $f) (Join-Path $dist $f) -Force
}

# 产物核对
$expect = @('7zipContext.exe', '7zipContext.Shell.comhost.dll',
            '7zipContext.Classic.comhost.dll', 'tools\7z.exe', 'tools\7zG.exe', 'tools\7z.dll',
            'tools\7-Zip-License.txt', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'coreclr.dll')
$missing = $expect | Where-Object { -not (Test-Path (Join-Path $dist $_)) }
if ($missing.Count -gt 0) { throw "发布产物缺失：$($missing -join ', ')" }

if (Test-Path $stage) { [System.IO.Directory]::Delete($stage, $true) }

if ($killedExplorer -and -not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
    Start-Process explorer.exe
}
Write-Host "发布完成 → $dist（$($expect.Count)/$($expect.Count) 产物齐全，含自包含运行时）" -ForegroundColor Green
