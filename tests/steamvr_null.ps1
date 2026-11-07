# Turns SteamVR's built-in null HMD driver on or off for testing.
#   .\tests\steamvr_null.ps1 -Enable
#   .\tests\steamvr_null.ps1 -Disable   (stops SteamVR, restores your settings, and confirms they stayed)
#
# SteamVR can write its settings as it shuts down, which once undid a restore done too early. So Disable
# waits for every SteamVR process to end and then checks again. Enable won't back up settings that already
# use the null driver, so a backup can never hold a test configuration.
param([switch]$Enable, [switch]$Disable)
$settings = 'C:\Program Files (x86)\Steam\config\steamvr.vrsettings'
$backup = Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\steamvr.vrsettings.backup'
$names = 'vrmonitor', 'vrcompositor', 'vrdashboard', 'vrwebhelper', 'vrstartup', 'steamvr_room_setup', 'vrserver'

function Stop-SteamVR {
    for ($i = 0; $i -lt 30; $i++) {
        $running = Get-Process $names -ErrorAction SilentlyContinue
        if (-not $running) { break }
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep 1
    }
    if (Get-Process $names -ErrorAction SilentlyContinue) { throw 'SteamVR would not close, so its settings were left alone.' }
    Start-Sleep 3
}
function Uses-NullDriver { Select-String -LiteralPath $settings -Pattern '"forcedDriver"\s*:\s*"null"' -Quiet }

if ($Enable) {
    Stop-SteamVR
    if (-not (Test-Path $backup)) {
        if (Uses-NullDriver) { throw "SteamVR's settings already use the null driver, so they are not a safe backup. Nothing was changed." }
        Copy-Item $settings $backup
    }
    $j = Get-Content $settings -Raw | ConvertFrom-Json
    if (-not $j.steamvr) { $j | Add-Member steamvr ([pscustomobject]@{}) }
    foreach ($kv in @{ forcedDriver = 'null'; activateMultipleDrivers = $true; enableHomeApp = $false }.GetEnumerator()) {
        $j.steamvr | Add-Member $kv.Key $kv.Value -Force
    }
    $j | Add-Member driver_null ([pscustomobject]@{ enable = $true; renderWidth = 1512; renderHeight = 1680; windowWidth = 1512; windowHeight = 840; displayFrequency = 90.0 }) -Force
    # A null HMD never moves, so without this SteamVR treats it as idle and stops asking for images.
    $j | Add-Member power ([pscustomobject]@{ pauseCompositorOnStandby = $false; turnOffScreensTimeout = 86400; autoLaunchSteamVROnButton = $false }) -Force
    [IO.File]::WriteAllText($settings, ($j | ConvertTo-Json -Depth 20))
    'Null HMD enabled.'
}
if ($Disable) {
    Stop-SteamVR
    if (-not (Test-Path $backup)) { 'No backup found, nothing to restore.'; return }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        Copy-Item $backup $settings -Force
        Start-Sleep 10
        if ((Get-FileHash $settings).Hash -eq (Get-FileHash $backup).Hash) { break }
        Stop-SteamVR
    }
    if ((Get-FileHash $settings).Hash -ne (Get-FileHash $backup).Hash) { throw "SteamVR's settings were not restored. The backup is at $backup" }
    Remove-Item $backup
    'SteamVR settings restored from the backup, and confirmed unchanged 10 seconds later.'
}
