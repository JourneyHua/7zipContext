$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../scripts/deployment.ps1')
$work = Join-Path ([IO.Path]::GetTempPath()) ('7zipContext-purge-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
$script:passed = 0
function Check($Name,$Condition) { if (-not $Condition) { throw "FAIL $Name" }; $script:passed++; Write-Output "PASS $Name" }
function Engine($Root,$Name,$Origin) {
    $dir = Join-Path (Join-Path $Root 'engine') $Name
    [void][IO.Directory]::CreateDirectory($dir)
    [IO.File]::WriteAllText((Join-Path $dir 'payload.bin'),'fixture')
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $key=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($dir.ToUpperInvariant()))).Replace('-','') } finally { $sha.Dispose() }
    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'engine-records'))
    if ($Origin) { [IO.File]::WriteAllText((Join-Path (Join-Path $Root 'engine-records') ($key+'.json')),(@{path=$dir;origin=$Origin;version='1'} | ConvertTo-Json)) }
    return $dir
}
try {
    $root=Join-Path $work 'mixed'
    $local=Engine $root 'local' 'local'
    $old=Engine $root 'old' 'downloaded'
    $unknown=Engine $root 'unknown' $null
    [IO.File]::WriteAllText((Join-Path $root 'engine.path'),$local)
    $r=Remove-DownloadedEngineCache $root
    Check 'old downloaded cache removed while active is local' (-not [IO.Directory]::Exists($old))
    Check 'local and unknown engines preserved' ([IO.Directory]::Exists($local) -and [IO.Directory]::Exists($unknown) -and $r.Preserved.Count -eq 2)
    Check 'local pointer preserved' ([IO.File]::ReadAllText((Join-Path $root 'engine.path')) -eq $local)
    Check 'successful cleanup reports no failures' ($r.Failures.Count -eq 0 -and $r.Deleted.Count -eq 1)
    $root=Join-Path $work 'downloaded'
    $active=Engine $root 'active' 'downloaded'
    [IO.File]::WriteAllText((Join-Path $root 'engine.path'),$active)
    [IO.File]::WriteAllText((Join-Path $root 'engine.json'),(@{path=$active;origin='downloaded'} | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $root 'update-check.json'),'fixture')
    $r=Remove-DownloadedEngineCache $root
    Check 'active downloaded engine removed' (-not [IO.Directory]::Exists($active))
    Check 'deleted active pointer and status cleared' (-not [IO.File]::Exists((Join-Path $root 'engine.path')) -and -not [IO.File]::Exists((Join-Path $root 'update-check.json')))
    $root=Join-Path $work 'locked'
    $active=Engine $root 'active' 'downloaded'
    [IO.File]::WriteAllText((Join-Path $root 'engine.path'),$active)
    $held=[IO.File]::Open((Join-Path $active 'payload.bin'),[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try { $r=Remove-DownloadedEngineCache $root } finally { $held.Dispose() }
    Check 'locked engine returns failure and preserves pointer' ($r.Failures.Count -gt 0 -and [IO.File]::Exists((Join-Path $root 'engine.path')))
    $r=Remove-DownloadedEngineCache $root
    Check 'retry after lock released succeeds' ($r.Failures.Count -eq 0 -and -not [IO.Directory]::Exists($active))
    $root=Join-Path $work 'legacy'
    $active=Engine $root 'legacy' $null
    [IO.File]::WriteAllText((Join-Path $root 'engine.json'),(@{path=$active;origin='downloaded'} | ConvertTo-Json))
    $r=Remove-DownloadedEngineCache $root
    Check 'legacy matching provenance is supported' (-not [IO.Directory]::Exists($active))
    $root=Join-Path $work 'conflict'
    $active=Engine $root 'active' 'downloaded'
    [IO.File]::WriteAllText((Join-Path $root 'engine.json'),(@{path=$active;origin='local'} | ConvertTo-Json))
    $r=Remove-DownloadedEngineCache $root
    Check 'conflicting provenance preserves directory' ([IO.Directory]::Exists($active) -and $r.Deleted.Count -eq 0)
    $denied=$false
    try { Remove-VerifiedPurgePath $work (Join-Path $work 'child') } catch { $denied=$true }
    Check 'out of boundary deletion rejected' ($denied -and [IO.Directory]::Exists($work))
    $file=Join-Path $work 'locked-dist.bin'; [IO.File]::WriteAllText($file,'fixture')
    $held=[IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $denied=$false
    try { Remove-VerifiedPurgePath $file $work } catch { $denied=$true } finally { $held.Dispose() }
    Check 'file deletion failure is observable' ($denied -and [IO.File]::Exists($file))
    "Purge fixture result: $script:passed passed / 0 failed"
} finally {
    Remove-VerifiedPurgePath $work ([IO.Path]::GetTempPath())
}