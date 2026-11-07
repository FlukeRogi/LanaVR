# Captures a screenshot of every backdrop in the game, into backdrop_previews. Runs flat with saves and
# achievements blocked. Some levels can't be jumped to directly, so a failed jump restarts the game and
# tries the backdrop's next level.
param([string[]]$Only = @())
. "$PSScriptRoot\lab.ps1"
Add-Type -AssemblyName System.Drawing
$out = "$Root\backdrop_previews"; New-Item -ItemType Directory -Force $out | Out-Null
$env:SteamAppId = '1608230'; $env:SteamGameId = '1608230'
Set-LanaKey 'OpenXR' 'Runtime' 'off'; Set-LanaKey 'Debug' 'LabCommands' 'true'

function Enter-Play {
    [void](Stop-Lana)
    Start-Process "$Game\Planet of Lana.exe" -WorkingDirectory $Game; Start-Sleep 28
    [void](Lab 'exec Main Menu [View]/Buttons/SlotsButton' 3); [void](Lab 'execi 0 Slots Group/Slot Button' 3); [void](Lab 'exec Slot [View]/Group/PlayButton' 3)
    Start-Sleep 24
}
function Is-Stuck { (Lab 'dump' 1) -match 'CANVAS Loading \[View\] en=True' }
function Has-Scene([string]$Name) { (Lab 'dump' 1) -match ("scene\[\d+\] " + [regex]::Escape($Name) + " idx") }

Enter-Play
$rows = (Lab 'scenes' 2) -split "`n" | Where-Object { $_ -match ' \| .* \| build=True' } | ForEach-Object { $p = $_ -split ' \| '; [pscustomobject]@{ Scene = $p[0].Trim(); Backdrop = $p[1].Trim() } }
$groups = $rows | Where-Object Backdrop -ne '(none)' | Group-Object Backdrop
foreach ($group in $groups) {
    if ($Only.Count -gt 0 -and $Only -notcontains $group.Name) { continue }
    $done = $false
    foreach ($scene in ($group.Group | Select-Object -First 4)) {
        Write-Output "$($group.Name): trying $($scene.Scene)"
        [void](Lab "goto $($scene.Scene)" 1); Start-Sleep 20
        if ((Is-Stuck) -or -not (Has-Scene $group.Name)) { Write-Output "  jump failed, restarting"; Enter-Play; continue }
        Start-Sleep 6
        $file = ($group.Name -replace '[^A-Za-z0-9]+', '_')
        [void](Lab "shot $file.raw.png" 3)
        $img = [Drawing.Image]::FromFile("$Lab\$file.raw.png"); $b = New-Object Drawing.Bitmap 1280, 720; $g = [Drawing.Graphics]::FromImage($b); $g.DrawImage($img, 0, 0, 1280, 720)
        $b.Save("$out\$file.png", [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose(); $img.Dispose()
        [IO.File]::Delete("$Lab\$file.raw.png")
        Write-Output "  saved $file.png from $($scene.Scene)"
        $done = $true; break
    }
    if (-not $done) { Write-Output "  NOT CAPTURED: $($group.Name)" }
}
[void](Stop-Lana)
Write-Output 'capture finished'
