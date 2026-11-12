# Shared deployment primitives. Loading this file never stops processes or changes registrations.
if (-not ('ZipContext.Deployment.FileDeployment' -as [type])) {
    $deploymentSource = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'DeploymentFiles.cs'))
    $deploymentSource = $deploymentSource.Replace('#nullable disable', '')
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        Add-Type -TypeDefinition $deploymentSource -ReferencedAssemblies 'System.dll', 'System.Core.dll', 'System.Xml.dll' -ErrorAction Stop
    } else {
        Add-Type -TypeDefinition $deploymentSource -ErrorAction Stop
    }
}

function Enter-DeploymentLease([string]$ConfigurationRoot = (Join-Path $env:LOCALAPPDATA '7zipContext')) {
    $ErrorActionPreference = 'Stop'
    $previous = $env:ZIPCONTEXT_DEPLOYMENT_TOKEN
    $lease = [ZipContext.Deployment.DeploymentLease]::Acquire($ConfigurationRoot, $previous)
    $env:ZIPCONTEXT_DEPLOYMENT_TOKEN = $lease.Token
    return [pscustomobject]@{ Lease = $lease; Previous = $previous }
}

function Exit-DeploymentLease($Context) {
    if ($null -eq $Context) { return }
    $Context.Lease.Dispose()
    $env:ZIPCONTEXT_DEPLOYMENT_TOKEN = $Context.Previous
}

function Remove-DeploymentWorkspace([string]$Directory, [string]$ExpectedParent) {
    $absolute = [IO.Path]::GetFullPath($Directory)
    $prefix = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing workspace removal outside $ExpectedParent"
    }
    [ZipContext.Deployment.FileDeployment]::DeleteOwnedTree($absolute)
}

function Stop-DeploymentProcesses([switch]$IncludeExplorer) {
    Get-Process -Name '7zipContext.Shell', '7zipContext' -ErrorAction SilentlyContinue | ForEach-Object {
        $deploymentProcess = $_
        try { $deploymentProcess.Kill(); $null = $deploymentProcess.WaitForExit(5000) } catch { }
        finally { $deploymentProcess.Dispose() }
    }
    Get-Process -Name dllhost -ErrorAction SilentlyContinue | ForEach-Object {
        $deploymentProcess = $_
        try {
            if ($deploymentProcess.Modules | Where-Object { $_.FileName -like '*7zipContext*' }) { $deploymentProcess.Kill(); $null = $deploymentProcess.WaitForExit(5000) }
        } catch { }
        finally { $deploymentProcess.Dispose() }
    }
    $wasRunning = $IncludeExplorer -and [bool](Get-Process -Name explorer -ErrorAction SilentlyContinue)
    if ($wasRunning) {
        Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object {
            $deploymentProcess = $_
            try { $deploymentProcess.Kill(); $null = $deploymentProcess.WaitForExit(5000) } catch { }
            finally { $deploymentProcess.Dispose() }
        }
    }
    return $wasRunning
}

function Restore-DeploymentExplorer([bool]$WasRunning) {
    if ($WasRunning -and -not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        try { Start-Process explorer.exe }
        catch { Write-Warning "Explorer restart failed: $($_.Exception.Message)" }
    }
}

function Test-DeploymentComActivation([guid]$Clsid) {
    # Keep in-process comhost DLLs out of the installer host so rollback can restore them.
    $code = '$ErrorActionPreference="Stop"; try { $t=[Type]::GetTypeFromCLSID([guid]"' + $Clsid.ToString() + '"); $o=[Activator]::CreateInstance($t); if($null -eq $o){exit 1}; [Runtime.InteropServices.Marshal]::ReleaseComObject($o) > $null; exit 0 } catch { exit 1 }'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $start.Arguments = '-NoProfile -EncodedCommand ' + $encoded
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (-not $process.WaitForExit(10000)) { try { $process.Kill(); $null = $process.WaitForExit(3000) } catch { }; return $false }
        return $process.ExitCode -eq 0
    } finally { $process.Dispose() }
}

# Caller holds deployment then engine lock. These functions only touch files.
function Remove-VerifiedPurgePath([string]$Path, [string]$Parent) {
    $ErrorActionPreference = 'Stop'
    $absolute = [IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd('\','/') + '\'
    if (-not $absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw "删除路径越界：$absolute" }
    $ancestor = $absolute
    while ($ancestor) {
        if ((Test-Path -LiteralPath $ancestor) -and (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "拒绝删除重解析路径：$ancestor" }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
    if ([IO.Directory]::Exists($absolute)) { [ZipContext.Deployment.FileDeployment]::DeleteOwnedTree($absolute) }
    elseif ([IO.File]::Exists($absolute)) { [IO.File]::Delete($absolute) }
    if (Test-Path -LiteralPath $absolute) { throw "删除后仍有残留：$absolute" }
}

function Remove-DownloadedEngineCache([string]$ConfigurationRoot) {
    $ErrorActionPreference = 'Stop'
    $root = [IO.Path]::GetFullPath($ConfigurationRoot).TrimEnd('\','/')
    $engineDir = Join-Path $root 'engine'
    $records = Join-Path $root 'engine-records'
    $failures = [Collections.Generic.List[string]]::new()
    $deleted = [Collections.Generic.List[string]]::new()
    $preserved = [Collections.Generic.List[string]]::new()
    foreach ($scope in @($root,$engineDir,$records)) {
        $ancestor = $scope
        while ($ancestor) {
            if ((Test-Path -LiteralPath $ancestor) -and (([IO.File]::GetAttributes($ancestor) -band [IO.FileAttributes]::ReparsePoint) -ne 0)) { throw "引擎路径包含重解析点：$ancestor" }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
    }
    $pointer = Join-Path $root 'engine.path'
    $active = if ([IO.File]::Exists($pointer)) { [IO.Path]::GetFullPath([IO.File]::ReadAllText($pointer).Trim()).TrimEnd('\','/') } else { $null }
    $legacy = Join-Path $root 'engine.json'
    if ([IO.Directory]::Exists($engineDir)) {
        foreach ($directory in [IO.Directory]::GetDirectories($engineDir)) {
            $normalized = [IO.Path]::GetFullPath($directory).TrimEnd('\','/')
            $sha = [Security.Cryptography.SHA256]::Create()
            try { $key = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalized.ToUpperInvariant()))).Replace('-','') }
            finally { $sha.Dispose() }
            $record = Join-Path $records ($key + '.json')
            $matching = @(); $uncertain = $false
            foreach ($candidate in @($record,$legacy)) {
                if (-not [IO.File]::Exists($candidate)) { continue }
                try {
                    if (([IO.File]::GetAttributes($candidate) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '记录为重解析点' }
                    $meta = [IO.File]::ReadAllText($candidate) | ConvertFrom-Json
                    $metaPath = [IO.Path]::GetFullPath($meta.path).TrimEnd('\','/')
                    if ([String]::Equals($metaPath,$normalized,[StringComparison]::OrdinalIgnoreCase)) { $matching += [pscustomobject]@{ File=$candidate; Origin=$meta.origin } }
                    elseif ($candidate -eq $record) { $uncertain = $true }
                } catch { if ($candidate -eq $record) { $uncertain = $true } }
            }
            if ($uncertain -or $matching.Count -eq 0 -or @($matching | Where-Object { $_.Origin -ne 'downloaded' }).Count -gt 0) { $preserved.Add($normalized); continue }
            try {
                Remove-VerifiedPurgePath $normalized $engineDir
                $deleted.Add($normalized)
                foreach ($meta in $matching) { Remove-VerifiedPurgePath $meta.File $root }
            } catch { $failures.Add("${normalized}：$_") }
        }
    }
    if ($active -and @($deleted | Where-Object { [String]::Equals($_,$active,[StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        foreach ($name in @('engine.path','engine.json','last-ensure.json','last-query.json','last-update-check','last-update-result.json','update-check.json')) {
            try { Remove-VerifiedPurgePath (Join-Path $root $name) $root } catch { $failures.Add("${name}：$_") }
        }
    }
    [pscustomobject]@{ Deleted=$deleted.ToArray(); Preserved=$preserved.ToArray(); Failures=$failures.ToArray() }
}