# Pure temporary-directory tests; no installation, registration or shell process control.
$ErrorActionPreference = 'Stop'
. (Join-Path (Split-Path -Parent $PSScriptRoot) 'scripts\deployment.ps1')
$work = Join-Path ([IO.Path]::GetTempPath()) ('7zc-deploy-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$script:passed = 0
function Assert-Deployment([string]$Name, [bool]$Condition) {
    if (-not $Condition) { throw "FAIL $Name" }
    $script:passed++
    Write-Output "PASS $Name"
}
function New-Fixture([string]$Name) {
    $parent = Join-Path $work $Name
    $source = Join-Path $parent 'candidate'
    $target = Join-Path $parent 'registered'
    [IO.Directory]::CreateDirectory($source) | Out-Null
    [IO.Directory]::CreateDirectory($target) | Out-Null
    foreach ($file in 'a.txt', 'b.txt') {
        [IO.File]::WriteAllText((Join-Path $source $file), ('new-' + $file))
        [IO.File]::WriteAllText((Join-Path $target $file), ('old-' + $file))
    }
    [IO.File]::WriteAllText((Join-Path $source '7zipContext.ini'), 'default')
    [IO.File]::WriteAllText((Join-Path $target '7zipContext.ini'), 'custom')
    return [pscustomobject]@{ Source = $source; Target = $target; Transaction = (Join-Path $parent 'transaction') }
}
$lease = $null
try {
    $fixture = New-Fixture '文件切换'
    $transaction = [ZipContext.Deployment.FileDeployment]::Prepare($fixture.Source, $fixture.Target, $fixture.Transaction, [string[]]@('7zipContext.ini'), $false)
    Assert-Deployment 'prepare preserves existing installation' ([IO.File]::ReadAllText((Join-Path $fixture.Target 'a.txt')) -eq 'old-a.txt')
    $transaction.Apply()
    Assert-Deployment 'shared backend applies new files' ([IO.File]::ReadAllText((Join-Path $fixture.Target 'a.txt')) -eq 'new-a.txt')
    Assert-Deployment 'shared backend preserves ini' ([IO.File]::ReadAllText((Join-Path $fixture.Target '7zipContext.ini')) -eq 'custom')
    $loaded = [ZipContext.Deployment.FileDeployment]::Load($fixture.Transaction, $fixture.Target)
    $loaded.Rollback()
    Assert-Deployment 'framework runtime restores persisted backup' ([IO.File]::ReadAllText((Join-Path $fixture.Target 'a.txt')) -eq 'old-a.txt')
    Assert-Deployment 'registered root stays in place' ([IO.Directory]::Exists($fixture.Target))
    $loaded.Cleanup()
    Assert-Deployment 'successful rollback cleans backups' (-not [IO.Directory]::Exists($fixture.Transaction))

    $fixture = New-Fixture 'copy-failure'
    $transaction = [ZipContext.Deployment.FileDeployment]::Prepare($fixture.Source, $fixture.Target, $fixture.Transaction, [string[]]@('7zipContext.ini'), $false)
    [IO.File]::Delete((Join-Path $fixture.Source 'b.txt'))
    $failed = $false
    try { $transaction.Apply() } catch { $failed = $true }
    Assert-Deployment 'partial copy failure restores files' ($failed -and $transaction.Journal.State -eq 'rolledBack' -and [IO.File]::ReadAllText((Join-Path $fixture.Target 'a.txt')) -eq 'old-a.txt')
    $transaction.Cleanup()

    $fixture = New-Fixture 'commit'
    $transaction = [ZipContext.Deployment.FileDeployment]::Prepare($fixture.Source, $fixture.Target, $fixture.Transaction, [string[]]@('7zipContext.ini'), $false)
    $transaction.Apply()
    $warning = $transaction.Commit()
    Assert-Deployment 'commit only removes backup after switch' (-not $warning -and -not [IO.Directory]::Exists($fixture.Transaction) -and [IO.File]::ReadAllText((Join-Path $fixture.Target 'a.txt')) -eq 'new-a.txt')

    $lease = Enter-DeploymentLease (Join-Path $work 'locks')
    $blocked = $false
    try { $other = [ZipContext.Deployment.DeploymentLease]::Acquire((Join-Path $work 'locks'), $null); $other.Dispose() }
    catch { $blocked = $true }
    Assert-Deployment 'PowerShell lease blocks independent writer' $blocked
    $member = [ZipContext.Deployment.DeploymentLease]::Acquire((Join-Path $work 'locks'), $lease.Lease.Token)
    try { Assert-Deployment 'PowerShell child can borrow operation token' ($member.Token -eq $lease.Lease.Token) }
    finally { $member.Dispose() }
    Write-Output "Deployment fixture result: $script:passed passed / 0 failed"
} finally {
    Exit-DeploymentLease $lease
    $absolute = [IO.Path]::GetFullPath($work)
    $prefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected test cleanup target' }
    [ZipContext.Deployment.FileDeployment]::DeleteOwnedTree($absolute)
}
