# 7zipContext classic context-menu handler: register / unregister.
# Registers 7zipContext.Classic.comhost.dll (managed IContextMenu) into HKCU - no admin needed.
# ASCified on purpose: Windows PowerShell 5.1 misparses UTF-8 without BOM.
param(
    [switch]$Install,
    [switch]$Uninstall,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$comhost = Join-Path $root 'dist\7zipContext.Classic.comhost.dll'
$clsid = '{B9A10020-8C3F-4A2E-9E6D-5F1A2B3C4D20}'
$clsKey = "Software\Classes\CLSID\$clsid"

# One handler, several shell targets: files, folders, folder background, desktop.
# (Registering only on '*' made folders/desktop silently skip it, which looked like "load failed".)
$handlerName = '7zipContext'
# Historical registration names must remain literal for cleanup.
$legacyNames = @('yayaPreview', 'yayaClassicSpike')
$scopes = @(
    "*\shellex\ContextMenuHandlers\$handlerName",
    "Directory\shellex\ContextMenuHandlers\$handlerName",
    "Directory\Background\shellex\ContextMenuHandlers\$handlerName",
    "DesktopBackground\shellex\ContextMenuHandlers\$handlerName"
)
$legacyScopes = foreach ($n in $legacyNames) {
    "*\shellex\ContextMenuHandlers\$n"
    "Directory\shellex\ContextMenuHandlers\$n"
    "Directory\Background\shellex\ContextMenuHandlers\$n"
    "DesktopBackground\shellex\ContextMenuHandlers\$n"
}

$hkcu = [Microsoft.Win32.Registry]::CurrentUser

function Restart-Explorer {
    if ($NoRestart) { Write-Host 'skip explorer restart (-NoRestart)' -ForegroundColor DarkGray; return }
    Write-Host 'restarting explorer...' -ForegroundColor DarkGray
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200
    if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) {
        Start-Process explorer.exe
    }
}

if ($Uninstall) {
    foreach ($s in $scopes) { $hkcu.DeleteSubKeyTree("Software\Classes\$s", $false) }
    foreach ($s in $legacyScopes) { $hkcu.DeleteSubKeyTree("Software\Classes\$s", $false) }
    $hkcu.DeleteSubKeyTree($clsKey, $false)

    $left = 0
    foreach ($p in (@($clsKey) + ($scopes | ForEach-Object { "Software\Classes\$_" }))) {
        if ($hkcu.OpenSubKey($p)) { $left++; Write-Host "  leftover: $p" -ForegroundColor Red }
    }
    Write-Host "classic menu unregistered (residue: $left)" -ForegroundColor Green
    Restart-Explorer
    exit $(if ($left -eq 0) { 0 } else { 1 })
}

if (-not (Test-Path $comhost)) { throw "missing comhost: $comhost (run scripts\publish.ps1 first)" }

# drop the pre-rename registrations so the menu does not grow two identical handlers
foreach ($s in $legacyScopes) { $hkcu.DeleteSubKeyTree("Software\Classes\$s", $false) }

$k = $hkcu.CreateSubKey($clsKey)
$k.SetValue('', '7zipContext preview (classic menu)')
$ip = $hkcu.CreateSubKey("$clsKey\InprocServer32")
$ip.SetValue('', $comhost)
$ip.SetValue('ThreadingModel', 'Apartment')

foreach ($s in $scopes) {
    $h = $hkcu.CreateSubKey("Software\Classes\$s")
    $h.SetValue('', $clsid)
}

$back = $hkcu.OpenSubKey("$clsKey\InprocServer32")
if (-not $back -or $back.GetValue('') -ne $comhost) { throw "readback mismatch for $clsKey" }

Write-Host 'classic menu registered' -ForegroundColor Green
Write-Host "  CLSID   : $clsid"
Write-Host "  Inproc  : $comhost"
Write-Host "  handler : $($scopes.Count) shell targets ($handlerName)"
Write-Host '  readback OK' -ForegroundColor Green

# fresh log each install
$log = Join-Path $env:TEMP '7zipContext-classic.log'
if (Test-Path $log) { Remove-Item $log -Force }
Write-Host "  log: $log"

Restart-Explorer
exit 0
