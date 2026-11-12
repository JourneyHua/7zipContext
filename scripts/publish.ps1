# Publish to isolated output first; dist stays at its registered AppX path.
param([switch]$KeepRollback)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'deployment.ps1')
$deploymentContext = Enter-DeploymentLease
$stageParent = Join-Path $root 'dist-stage'
$stage = Join-Path $stageParent ('publish-' + [Guid]::NewGuid().ToString('N'))
$dist = Join-Path $root 'dist'
$transaction = $null
$keepStage = $false
$wasExplorer = $false
try {
    # Recover interrupted switches before a new build. No registration changes here.
    if (Test-Path -LiteralPath $stageParent) {
        foreach ($oldStage in Get-ChildItem -LiteralPath $stageParent -Directory -Filter 'publish-*') {
            try {
            $oldWork = Join-Path $oldStage.FullName 'transaction'
            if (Test-Path -LiteralPath (Join-Path $oldWork 'transaction.xml')) {
                $old = [ZipContext.Deployment.FileDeployment]::Load($oldWork, $dist)
                if ($old.Journal.State -ne 'committed' -and $old.Journal.State -ne 'rolledBack') {
                    $oldExplorer = $false
                    try {
                        if ($old.Journal.State -ne 'prepared') { $oldExplorer = Stop-DeploymentProcesses -IncludeExplorer }
                        $old.Rollback()
                        Write-Host '已恢复上次未完成发布的文件。' -ForegroundColor Yellow
                    } finally { Restore-DeploymentExplorer $oldExplorer }
                }
            }
            Remove-DeploymentWorkspace $oldStage.FullName $stageParent
            } catch { throw "无法恢复或清理上次部署：$($oldStage.FullName)，文件已保留。$_" }
        }
    }
    $dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
    if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
    $plans = @(
        @{ Name = '7zipContext.App'; SelfContained = 'true' },
        @{ Name = '7zipContext.Shell'; SelfContained = 'false' },
        @{ Name = '7zipContext.Classic'; SelfContained = 'false' }
    )
    foreach ($plan in $plans) {
        Write-Host "发布 $($plan.Name) ..." -ForegroundColor DarkGray
        & $dotnet publish (Join-Path $root "src\$($plan.Name)\$($plan.Name).csproj") `
            -c Release -r win-x64 --self-contained $plan.SelfContained -p:PublishSingleFile=false `
            -o (Join-Path $stage $plan.Name) --nologo -v q | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "$($plan.Name) 发布失败，当前程序文件尚未更改。" }
    }
    $candidate = Join-Path $stage 'candidate'
    foreach ($plan in $plans) {
        [ZipContext.Deployment.FileDeployment]::CopyTree((Join-Path $stage $plan.Name), $candidate, $false)
    }
    foreach ($file in 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        Copy-Item -LiteralPath (Join-Path $root $file) -Destination (Join-Path $candidate $file) -Force
    }
    $expected = @('7zipContext.exe', '7zipContext.Shell.comhost.dll', '7zipContext.Classic.comhost.dll',
                  'LICENSE', 'THIRD-PARTY-NOTICES.md', 'coreclr.dll')
    $missing = $expected | Where-Object { -not (Test-Path -LiteralPath (Join-Path $candidate $_)) }
    if ($missing.Count) { throw "候选产物缺失：$($missing -join ', ')" }
    # All compilation, merging, validation and backup finish before stopping shell hosts.
    $transaction = [ZipContext.Deployment.FileDeployment]::Prepare($candidate, $dist,
        (Join-Path $stage 'transaction'), [string[]]@('7zipContext.ini'), $true)
    $wasExplorer = Stop-DeploymentProcesses -IncludeExplorer
    $transaction.Apply()
    if ($KeepRollback) {
        $keepStage = $true
        Write-Host '文件已切换，安装流程保留回滚备份。' -ForegroundColor DarkGray
        return $transaction
    }
    $warning = $transaction.Commit()
    if ($warning) { Write-Warning $warning; $keepStage = $true }
    Write-Host "发布完成 → $dist（$($expected.Count)/$($expected.Count) 必需产物齐全）。" -ForegroundColor Green
} catch {
    $original = $_
    if ($null -ne $transaction -and $transaction.Journal.State -ne 'committed') {
        try {
            if ($transaction.Journal.State -ne 'prepared' -and $transaction.Journal.State -ne 'rolledBack') {
                $wasExplorer = (Stop-DeploymentProcesses -IncludeExplorer) -or $wasExplorer
            }
            $transaction.Rollback()
            Write-Warning '发布失败，旧文件已恢复。'
        } catch {
            $keepStage = $true
            throw "发布失败，恢复未全部完成。备份：$stage。原始错误：$original。恢复错误：$_"
        }
    }
    throw $original
} finally {
    Restore-DeploymentExplorer $wasExplorer
    if (-not $keepStage -and (Test-Path -LiteralPath $stage)) {
        try { Remove-DeploymentWorkspace $stage $stageParent }
        catch { Write-Warning "准备目录已保留：$stage；$_" }
    }
    Exit-DeploymentLease $deploymentContext
}
