# Captures each backdrop without its foreground, as a style reference, into backdrop_skies. These are the
# game's own art, so this folder is never part of the mod's package. Runs flat with saves and achievements
# blocked. Some levels can't be jumped to directly, so a failed jump restarts the game and tries the next one.
param([string[]]$Only = @(), [int]$FarDistance = 1000)
. "$PSScriptRoot\lab.ps1"
Add-Type -AssemblyName System.Drawing
$out = "$Root\backdrop_skies"; New-Item -ItemType Directory -Force $out | Out-Null
$env:SteamAppId = '1608230'; $env:SteamGameId = '1608230'
Set-LanaKey 'OpenXR' 'Runtime' 'off'; Set-LanaKey 'Debug' 'LabCommands' 'true'

function Enter-Play {
    [void](Stop-Lana)
    Start-Process "$Game\Planet of Lana.exe" -WorkingDirectory $Game; Start-Sleep 26
    [void](Lab 'exec Main Menu [View]/Buttons/SlotsButton' 3); [void](Lab 'slot 2' 3); [void](Lab 'exec Slot [View]/Group/PlayButton' 3)
    Start-Sleep 22
}
function Is-Stuck { (Lab 'dump' 1) -match 'CANVAS Loading \[View\] en=True' }
function Has-Scene([string]$Name) { (Lab 'dump' 1) -match ("scene\[\d+\] " + [regex]::Escape($Name) + " idx") }
function Save-Shot([string]$From, [string]$To) {
    $img = [Drawing.Image]::FromFile($From); $b = New-Object Drawing.Bitmap 1920, 1080; $g = [Drawing.Graphics]::FromImage($b); $g.DrawImage($img, 0, 0, 1920, 1080)
    $b.Save($To, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose(); $img.Dispose(); [IO.File]::Delete($From)
}

Enter-Play
$rows = (Lab 'scenes' 2) -split "`n" | Where-Object { $_ -match ' \| .* \| build=True' } | ForEach-Object { $p = $_ -split ' \| '; [pscustomobject]@{ Scene = $p[0].Trim(); Backdrop = $p[1].Trim() } }
foreach ($group in ($rows | Where-Object Backdrop -ne '(none)' | Group-Object Backdrop)) {
    if ($Only.Count -gt 0 -and $Only -notcontains $group.Name) { continue }
    $done = $false
    foreach ($scene in ($group.Group | Select-Object -First 4)) {
        Write-Output "$($group.Name): trying $($scene.Scene)"
        [void](Lab "goto $($scene.Scene)" 1); Start-Sleep 20
        if ((Is-Stuck) -or -not (Has-Scene $group.Name)) { Write-Output "  jump failed, restarting"; Enter-Play; continue }
        $file = ($group.Name -replace '[^A-Za-z0-9]+', '_'); $dir = "$out\$file"; New-Item -ItemType Directory -Force "$dir\layers" | Out-Null
        [void](Lab 'ts 0' 2)
        [void](Lab 'bdonly' 2); [void](Lab "shot sky_a_$file.png" 3); Save-Shot "$Lab\sky_a_$file.png" "$dir\view.png"
        [void](Lab "skylayers $FarDistance" 2); [void](Lab "shot sky_b_$file.png" 3); Save-Shot "$Lab\sky_b_$file.png" "$dir\sky_only.png"
        $listing = Lab "bdexport $dir\layers|$FarDistance" 8
        ($listing -split "`n" | Where-Object { $_ -match ' \| ' }) | Set-Content "$dir\layers.txt" -Encoding utf8
        [void](Lab 'bdrestore' 2); [void](Lab 'ts 1' 1)
        Write-Output "  saved $file from $($scene.Scene), $((Get-ChildItem "$dir\layers" -File).Count) layers"
        $done = $true; break
    }
    if (-not $done) { Write-Output "  NOT CAPTURED: $($group.Name)" }
}
[void](Stop-Lana)
Write-Output 'capture finished'
