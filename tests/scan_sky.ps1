# Scans the game's levels for where its own sky changes brightness or colour. Runs flat, from the third save slot,
# with saves and achievements blocked. Writes to scan\ under the lab folder: one row per stop in sky_walk.txt, a
# small picture of each stop, and the game's colour triggers in triggers.txt. It can be stopped and run again.
param([string[]]$Only = @(), [int]$Step = 30)
. "$PSScriptRoot\lab.ps1"
$out = "$Lab\scan"; New-Item -ItemType Directory -Force $out | Out-Null
$walk = 'scan\sky_walk.txt'; $trig = "$out\triggers.txt"
$saveFolder = "$env:USERPROFILE\AppData\LocalLow\Wishfully\Planet of Lana"
function Save-Hash { (Get-ChildItem $saveFolder -Filter *.sav | ForEach-Object { $_.Name + (Get-FileHash $_.FullName).Hash }) -join ';' }
$before = Save-Hash
$env:SteamAppId = '1608230'; $env:SteamGameId = '1608230'
Set-LanaKey 'OpenXR' 'Runtime' 'off'; Set-LanaKey 'Debug' 'LabCommands' 'true'
function Enter-Play {
    [void](Stop-Lana)
    Start-Process "$Game\Planet of Lana.exe" -WorkingDirectory $Game; Start-Sleep 28
    [void](Lab 'exec Main Menu [View]/Buttons/SlotsButton' 3); [void](Lab 'slot 2' 3); [void](Lab 'exec Slot [View]/Group/PlayButton' 3)
    Start-Sleep 22
}
function Has-Scene([string]$Name) { (Lab 'dump' 1) -match ("scene\[\d+\] " + [regex]::Escape($Name) + " idx") }
Enter-Play
$scenes = (Lab 'scenes' 2) -split "`n" | Where-Object { $_ -match ' \| .* \| build=True' -and $_ -notmatch '\(none\)' } | ForEach-Object { ($_ -split ' \| ')[0].Trim() }
$Only = $Only -split ','  # A list comes through as one string when run with -File.
if ($Only.Count) { $scenes = $scenes | Where-Object { $Only -contains $_ } }
$done = if (Test-Path "$Lab\$walk") { (Get-Content "$Lab\$walk" | ForEach-Object { ($_ -split '\|')[0] } | Select-Object -Unique) } else { @() }
foreach ($scene in $scenes) {
    if ($done -contains $scene) { continue }
    [void](Lab "goto $scene" 1)
    $ok = $false; for ($i = 0; $i -lt 20; $i++) { Start-Sleep 2; if (Has-Scene $scene) { $ok = $true; break } }
    if (-not $ok) { "$scene : jump failed, restarting"; Enter-Play; [void](Lab "goto $scene" 1); Start-Sleep 20; if (-not (Has-Scene $scene)) { "$scene : NOT SCANNED"; continue } }
    Start-Sleep 3
    (Lab 'darken' 2) -split "`n" | Where-Object { $_ -match '^(PPTRIG|LIGHTTRIG)' -and $_ -match [regex]::Escape($scene) } | Add-Content $trig
    $r = Lab "skywalk $scene|$walk|$Step" 1
    for ($i = 0; $i -lt 90; $i++) { Start-Sleep 2; if ((Lab 'walkstate' 1) -match 'idle') { break } }
    "$scene : " + (($r -split "`n" | Select-String 'walking|nothing').Line) + ' : ' + ((Lab 'walkstate' 1) -split "`n" | Select-String 'idle|walking').Line
}
[void](Stop-Lana); Start-Sleep 4
Set-LanaKey 'OpenXR' 'Runtime' 'auto'; Set-LanaKey 'Debug' 'LabCommands' 'false'
'saves unchanged: ' + ((Save-Hash) -eq $before)
'scan finished'
