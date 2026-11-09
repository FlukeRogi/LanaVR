# End-to-end tests: drives the real game through the mock HMD and a virtual gamepad.
# Run from anywhere: powershell -ExecutionPolicy Bypass -File tests\run_tests.ps1
# Uses the save slot given by -Slot, starts a new game there if it is empty, and restores VR settings afterwards.
param([int]$Slot = 2)  # The first slots hold real progress that syncs through Steam Cloud. Tests stay on the third.
. "$PSScriptRoot\lab.ps1"
$ErrorActionPreference = 'Stop'
$script:Pass = 0; $script:Fail = 0
function Check([string]$Name, [bool]$Ok, [string]$Detail = '') {
    if ($Ok) { $script:Pass++; Write-Output "PASS  $Name  $Detail" } else { $script:Fail++; Write-Output "FAIL  $Name  $Detail" }
}
function Val([string]$Text, [string]$Key) { [regex]::Match($Text, "\b$Key=([^\s]+)").Groups[1].Value }
function Num([string]$Text, [string]$Key) { [double]::Parse((Val $Text $Key), [Globalization.CultureInfo]::InvariantCulture) }
function Pad([string]$Button, [int]$Times = 1) { foreach ($i in 1..$Times) { [void](Lab "pad $Button" 1) } }
function Near([double]$A, [double]$B, [double]$Tol = 0.02) { [math]::Abs($A - $B) -le $Tol }

function Set-Key([string]$Section, [string]$Key, [string]$Value) {
    $t = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { '' }
    if ($t -match "(?m)^$Key = .*$") { $t = [regex]::Replace($t, "(?m)^$Key = .*$", ("$Key = " + $Value.Replace('$', '$$'))) }
    else { $t += "`r`n[$Section]`r`n$Key = $Value`r`n" }
    Set-Content $Cfg $t -Encoding ascii -NoNewline
}

# Mean absolute difference between two regions of side-by-side dumps, on a coarse grid (0 to 255).
Add-Type -AssemblyName System.Drawing
function Diff-Images([string]$A, [string]$B) {
    $x = [Drawing.Bitmap]::FromFile($A); $y = [Drawing.Bitmap]::FromFile($B); $sum = 0.0; $n = 0
    for ($j = 8; $j -lt $x.Height; $j += 16) { for ($i = 8; $i -lt $x.Width; $i += 16) {
        $p = $x.GetPixel($i, $j); $q = $y.GetPixel($i, $j)
        $sum += [math]::Abs($p.R - $q.R) + [math]::Abs($p.G - $q.G) + [math]::Abs($p.B - $q.B); $n += 3 } }
    $x.Dispose(); $y.Dispose(); $sum / $n
}
function Dump-Pose([string]$Name) { [void](Get-EyeDump $Name 960); Copy-Item "$Lab\mock_left.bmp" "$Lab\$Name.L.bmp" -Force; Copy-Item "$Lab\mock_right.bmp" "$Lab\$Name.R.bmp" -Force }
function Layer([int]$Index) {
    $l = (Get-Content "$Lab\mock_layers.txt")[$Index + 1]
    $pos = [regex]::Match($l, 'pos=([^ ]+)').Groups[1].Value.Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
    $rot = [regex]::Match($l, 'rot=([^ ]+)').Groups[1].Value.Split(',') | ForEach-Object { [double]::Parse($_, [Globalization.CultureInfo]::InvariantCulture) }
    $size = [regex]::Match($l, 'size=([^x]+)x([^ ]+)')
    [pscustomobject]@{ Eye = [int][regex]::Match($l, 'eye=(\d)').Groups[1].Value; Pos = $pos; Rot = $rot
        W = [double]::Parse($size.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture); H = [double]::Parse($size.Groups[2].Value, [Globalization.CultureInfo]::InvariantCulture)
        Format = [int][regex]::Match($l, 'format=(\d+)').Groups[1].Value }
}

function Get-DisplayPrefs { $p = Get-ItemProperty 'HKCU:\SOFTWARE\Wishfully\Planet of Lana'; ($p.PSObject.Properties | Where-Object Name -match 'Resolution (Width|Height)_|Fullscreen mode_|Use Native_' | ForEach-Object { "$($_.Name)=$($_.Value)" }) -join ';' }

# --- Setup ---------------------------------------------------------------------------------------
[void](Stop-Lana)
$displayBefore = Get-DisplayPrefs
$saveFolder = "$env:USERPROFILE\AppData\LocalLow\Wishfully\Planet of Lana"
$savesBefore = (Get-ChildItem $saveFolder -Filter *.sav | ForEach-Object { $_.Name + (Get-FileHash $_.FullName).Hash }) -join ';'
$savedCfg = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { $null }
Set-Key 'OpenXR' 'Runtime' "$Root\tools\mock_runtime.json"
Set-Key 'Debug' 'LabCommands' 'true'
# Every backdrop has a sky now, so one is taken out of the installed list to test what happens with an unlisted one.
$installedMap = "$Game\BepInEx\plugins\LanaVR\skybox\skyboxes.txt"
$savedMap = Get-Content $installedMap
# A flat shade on the night backdrop, replacing its scanned one, so the sky checks can read it exactly.
@($savedMap | Where-Object { $_ -notmatch '^Chapter 03 Backdrop 1 =' }) + 'Shade: Chapter 01 Backdrop Night = -100000 0.5 0.5 0.5 | 100000 0.5 0.5 0.5' | Set-Content $installedMap -Encoding ascii
Set-Key 'Screen' 'Width' '3'; Set-Key 'Screen' 'Distance' '2.5'; Set-Key 'Stereo' 'Strength' '1'; Set-Key 'Stereo' 'HeadTracking' '1'
# The sky and water checks read exact pixels, so these sections run without the scenery. Reset turns it back on.
Set-Key 'Skybox' 'Quality' '1'; Set-Key 'Water' 'Level' '0'; Set-Key 'Water' 'RippleStrength' '1'; Set-Key 'Water' 'ReflectionQuality' '1'; Set-Key 'Water' 'ScreenReflection' 'true'; Set-Key 'Scenery' 'Enabled' 'false'; Set-Key 'Scenery' 'Scale' '1'; Set-Key 'Scenery' 'Position' '0.5'
if (Test-Path "$Lab\mock_nostage.txt") { [IO.File]::Delete("$Lab\mock_nostage.txt") }
Set-Head
foreach ($f in 'mock_log.txt', 'mock_dump.txt', 'mock_exit.txt', 'cmd.txt') { if (Test-Path "$Lab\$f") { Clear-Content "$Lab\$f"; if ($f -ne 'mock_log.txt') { [IO.File]::Delete("$Lab\$f") } } }
$env:SteamAppId = '1608230'; $env:SteamGameId = '1608230'
Start-Process "$Game\Planet of Lana.exe" -WorkingDirectory $Game
Start-Sleep 30

# --- Session lifecycle ----------------------------------------------------------------------------
$vr = Lab 'vr' 1
Check 'VR session reaches FOCUSED' ((Val $vr 'active') -eq 'True' -and (Val $vr 'state') -eq '5') $vr.Trim()
$desktopRes = Val $vr 'win'
Check 'VR leaves the desktop resolution alone' ($desktopRes -ne '1280x720') "win=$desktopRes"
$a = Num $vr 'submitted'; Start-Sleep 2; $b = Num (Lab 'vr' 1) 'submitted'
Check 'Frames keep being submitted' ($b -gt $a + 100) "$a -> $b"

# --- Layers ---------------------------------------------------------------------------------------
Dump-Pose 't_center'
$first = (Get-Content "$Lab\mock_layers.txt")[0]
Check 'Sky behind two screen layers' ($first -match 'layers=3') $first
$L = Layer 0; $R = Layer 1
Check 'One layer per eye' ($L.Eye -eq 1 -and $R.Eye -eq 2)
Check 'Screen is 3 m wide at 16:9' ((Near $L.W 3) -and (Near ($L.W / $L.H) (16 / 9)))
Check 'Screen is 2.5 m in front of the recentered head' ((Near $L.Pos[0] 0) -and (Near $L.Pos[1] 0) -and (Near $L.Pos[2] -2.5))
Check 'Both eyes share one screen rectangle' ((Near $L.Pos[2] $R.Pos[2] 0.0001) -and (Near $L.W $R.W 0.0001))
Check 'sRGB swapchain, for gamma-space images' ($L.Format -eq 29) "format=$($L.Format)"

# --- Stereo ---------------------------------------------------------------------------------------
$stereoDiff = Diff-Images "$Lab\t_center.L.bmp" "$Lab\t_center.R.bmp"
Check 'Stereo: the eyes see different images' ($stereoDiff -gt 2) ("diff={0:F2}" -f $stereoDiff)
[void](Lab 'set stereo 0' 1); Start-Sleep 1; Dump-Pose 't_flat'
$flatDiff = Diff-Images "$Lab\t_flat.L.bmp" "$Lab\t_flat.R.bmp"
# A small residue remains between any two renders, so this is judged against the difference stereo makes.
Check 'Stereo strength 0: the eyes match' ($flatDiff -lt ($stereoDiff * 0.15)) ("diff={0:F2} with stereo on={1:F2}" -f $flatDiff, $stereoDiff)
[void](Lab 'set stereo 2' 1); Start-Sleep 1; Dump-Pose 't_double'
$doubleDiff = Diff-Images "$Lab\t_double.L.bmp" "$Lab\t_double.R.bmp"
Check 'Stereo strength 2 widens the difference' ($doubleDiff -gt $stereoDiff) ("{0:F2} > {1:F2}" -f $doubleDiff, $stereoDiff)
[void](Lab 'set stereo 1' 1)

# --- Head tracking --------------------------------------------------------------------------------
Set-Head; Start-Sleep 1; Dump-Pose 't_h0'
Dump-Pose 't_h0b'
$idle = Diff-Images "$Lab\t_h0.L.bmp" "$Lab\t_h0b.L.bmp"
Set-Head -X 0.3; Start-Sleep 1; Dump-Pose 't_hx'
$moved = Diff-Images "$Lab\t_h0.L.bmp" "$Lab\t_hx.L.bmp"
Check 'Head tracking: moving sideways changes the view' ($moved -gt ($idle + 2)) ("moved={0:F2} idle={1:F2}" -f $moved, $idle)
$L = Layer 0
Check 'Head tracking: the screen stays fixed in the room' ((Near $L.Pos[0] 0) -and (Near $L.Pos[2] -2.5))
[void](Lab 'set head 0' 1); Start-Sleep 1; Dump-Pose 't_hoff'; Set-Head; Start-Sleep 1; Dump-Pose 't_hoff0'
$off = Diff-Images "$Lab\t_hoff.L.bmp" "$Lab\t_hoff0.L.bmp"
# The scene animates, so the head move must add no more than idle animation does.
Check 'Head tracking 0 disables the window parallax' ($off -lt ($idle + 2.5) -and $moved -gt ($idle + 2.5)) ("off={0:F2} idle={1:F2} on={2:F2}" -f $off, $idle, $moved)
[void](Lab 'set head 1' 1)

# --- Recenter, using the two-stick gamepad shortcut -----------------------------------------------
Set-Head -X 1 -Y 0.2 -Z 0 -Yaw 30; Start-Sleep 1
Pad 'recenter'; Start-Sleep 1; Dump-Pose 't_rc'
$L = Layer 0
$ex = 1 - 2.5 * [math]::Sin([math]::PI / 6); $ez = -2.5 * [math]::Cos([math]::PI / 6)
Check 'Recenter moves the screen in front of the head' ((Near $L.Pos[0] $ex) -and (Near $L.Pos[1] 0.2) -and (Near $L.Pos[2] $ez)) ("pos=$($L.Pos -join ',') expected=$ex,0.2,$ez")
Check 'Recenter turns the screen to face the head' ((Near $L.Rot[1] ([math]::Sin([math]::PI / 12)) 0.01) -and (Near $L.Rot[3] ([math]::Cos([math]::PI / 12)) 0.01)) "rot=$($L.Rot -join ',')"
Check 'Recenter stays level' ((Near $L.Rot[0] 0 0.001) -and (Near $L.Rot[2] 0 0.001))
Set-Head; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1

# --- Skybox ---------------------------------------------------------------------------------------
# A cubemap whose colours encode each texel's own direction, so any rendered pixel can be checked
# against the direction it should be facing. Cross cells are {column, row, face}.
function New-DirectionCube([string]$Path, [int]$Face) {
    $b = New-Object Drawing.Bitmap ($Face * 4), ($Face * 3)
    $cells = @(@(1, 0, 'top'), @(0, 1, 'left'), @(1, 1, 'front'), @(2, 1, 'right'), @(3, 1, 'back'), @(1, 2, 'bottom'))
    foreach ($c in $cells) { for ($j = 0; $j -lt $Face; $j++) { for ($i = 0; $i -lt $Face; $i++) {
        $u = 2 * ($i + 0.5) / $Face - 1; $v = 2 * ($j + 0.5) / $Face - 1
        switch ($c[2]) {
            'front'  { $x = $u;  $y = -$v; $z = 1 }
            'right'  { $x = 1;   $y = -$v; $z = -$u }
            'left'   { $x = -1;  $y = -$v; $z = $u }
            'back'   { $x = -$u; $y = -$v; $z = -1 }
            'top'    { $x = $u;  $y = 1;   $z = $v }
            'bottom' { $x = $u;  $y = -1;  $z = -$v }
        }
        $n = [math]::Sqrt($x * $x + $y * $y + $z * $z)
        $b.SetPixel($c[0] * $Face + $i, $c[1] * $Face + $j, [Drawing.Color]::FromArgb(255, [int](($x / $n + 1) * 127.5), [int](($y / $n + 1) * 127.5), [int](($z / $n + 1) * 127.5)))
    } } }
    $b.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}
function New-DirectionPanorama([string]$Path, [int]$Width) {
    $h = [int]($Width / 2); $b = New-Object Drawing.Bitmap $Width, $h
    for ($j = 0; $j -lt $h; $j++) { for ($i = 0; $i -lt $Width; $i++) {
        $lon = (($i + 0.5) / $Width - 0.5) * 2 * [math]::PI; $lat = (0.5 - ($j + 0.5) / $h) * [math]::PI
        $x = [math]::Cos($lat) * [math]::Sin($lon); $y = [math]::Sin($lat); $z = [math]::Cos($lat) * [math]::Cos($lon)
        $b.SetPixel($i, $j, [Drawing.Color]::FromArgb(255, [int](($x + 1) * 127.5), [int](($y + 1) * 127.5), [int](($z + 1) * 127.5)))
    } }
    $b.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
}
function Rotate-Vector([double[]]$Q, [double[]]$V) {
    $ux = $Q[1] * $V[2] - $Q[2] * $V[1] + $Q[3] * $V[0]; $uy = $Q[2] * $V[0] - $Q[0] * $V[2] + $Q[3] * $V[1]; $uz = $Q[0] * $V[1] - $Q[1] * $V[0] + $Q[3] * $V[2]
    @(($V[0] + 2 * ($Q[1] * $uz - $Q[2] * $uy)), ($V[1] + 2 * ($Q[2] * $ux - $Q[0] * $uz)), ($V[2] + 2 * ($Q[0] * $uy - $Q[1] * $ux)))
}
function Head-Quat([double]$Yaw, [double]$Pitch) {
    $y = $Yaw * [math]::PI / 360; $p = $Pitch * [math]::PI / 360
    @(([math]::Cos($y) * [math]::Sin($p)), ([math]::Sin($y) * [math]::Cos($p)), (-[math]::Sin($y) * [math]::Sin($p)), ([math]::Cos($y) * [math]::Cos($p)))
}
function Wait-Sky([string]$Want) {
    for ($i = 0; $i -lt 40; $i++) { $st = Val (Lab 'sky' 1) 'status'; if ($st -like $Want) { return $st }; Start-Sleep -Milliseconds 500 }
    $st
}
# Largest error, in 0 to 255 colour levels, between rendered pixels and the directions they should face.
function Measure-SkyError([double]$Yaw, [double]$Pitch, [double]$SkyYaw = 0) {
    Set-Head -Yaw $Yaw -Pitch $Pitch; Start-Sleep -Milliseconds 700
    [void](Get-EyeDump 't_sky' 320)
    $q = Head-Quat $Yaw $Pitch; $inv = @(0, (-[math]::Sin($SkyYaw * [math]::PI / 360)), 0, [math]::Cos($SkyYaw * [math]::PI / 360))
    $img = [Drawing.Bitmap]::FromFile("$Lab\mock_sky_left.bmp"); $w = $img.Width; $h = $img.Height; $worst = 0.0; $tan = [math]::Tan(0.87)
    foreach ($fx in 0.5, 0.2, 0.8, 0.35, 0.65) { foreach ($fy in 0.5, 0.2, 0.8) {
        $ray = @(((2 * $fx - 1) * $tan), ((1 - 2 * $fy) * $tan), -1.0)
        $d = Rotate-Vector $inv (Rotate-Vector $q $ray)
        $n = [math]::Sqrt($d[0] * $d[0] + $d[1] * $d[1] + $d[2] * $d[2])
        $p = $img.GetPixel([int]($fx * $w), [int]($fy * $h))
        $worst = [math]::Max($worst, [math]::Abs($p.R - ($d[0] / $n + 1) * 127.5))
        $worst = [math]::Max($worst, [math]::Abs($p.G - ($d[1] / $n + 1) * 127.5))
        $worst = [math]::Max($worst, [math]::Abs($p.B - (-$d[2] / $n + 1) * 127.5))
    } }
    $img.Dispose(); $worst
}
[void](Lab 'waterdebug 3' 1)  # The sky alone, for the sky checks. The water has its own section below.
$skyDir = "$Lab\sky_test"; New-Item -ItemType Directory -Force $skyDir | Out-Null
$found = Lab 'sky' 1
Check 'The main menu shows its listed skybox' ((Wait-Sky 'ready_*') -eq 'ready_1024' -and (Val $found 'file') -eq 'FS020_Day_Sunless.lvsky' -and (Val $found 'backdrop') -eq 'Main_Menu') $found.Trim()
Check 'skyboxes.txt lists the backgrounds' ((Num $found 'mapped') -ge 15) $found.Trim()
New-DirectionCube "$skyDir\dir64.png" 64
[void](Lab "skyload $skyDir\dir64.png|4096" 1)
Check 'A 256x192 skybox loads' ((Wait-Sky 'ready_*') -eq 'ready_64')
foreach ($pose in @(@('front', 0, 0), @('left', 90, 0), @('right', -90, 0), @('back', 180, 0), @('up', 0, 80), @('down', 0, -80), @('corner', 45, 35))) {
    $e = Measure-SkyError $pose[1] $pose[2]
    Check "Sky faces the right way looking $($pose[0])" ($e -lt 16) ("worst error={0:F1} of 255" -f $e)
}
Set-Head -Yaw 40; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
$e = Measure-SkyError 40 0 40
Check 'Recenter turns the sky with the screen' ($e -lt 16) ("worst error={0:F1}" -f $e)
Set-Head; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
# The same direction check for panoramas, including the join at the back and both poles.
New-DirectionPanorama "$skyDir\pano512.png" 512
[void](Lab "skyload $skyDir\pano512.png|4096" 1)
Check 'A 512x256 panorama loads' ((Wait-Sky 'ready_*') -eq 'ready_128')
foreach ($pose in @(@('front', 0, 0), @('left', 90, 0), @('right', -90, 0), @('back, across the join', 180, 0), @('up', 0, 85), @('down', 0, -85), @('corner', 45, 35), @('back corner', 170, -35))) {
    $e = Measure-SkyError $pose[1] $pose[2]
    Check "Panorama faces the right way looking $($pose[0])" ($e -lt 16) ("worst error={0:F1} of 255" -f $e)
}
Set-Head; Start-Sleep 1
foreach ($size in @(@(1000, 500, 250), @(1001, 500, 250), @(4096, 2048, 1024))) {
    $src = [Drawing.Image]::FromFile("$Root\skybox\backdrop.png"); $b = New-Object Drawing.Bitmap $size[0], $size[1]; $g = [Drawing.Graphics]::FromImage($b); $g.DrawImage($src, 0, 0, $size[0], $size[1])
    $p = "$skyDir\pano$($size[0]).png"; $b.Save($p, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose(); $src.Dispose()
    [void](Lab "skyload $p|4096" 1); $st = Wait-Sky 'ready_*'
    Check "A $($size[0])x$($size[1]) panorama gives $($size[2]) pixel faces" ($st -eq "ready_$($size[2])") $st
}
[void](Lab "skyload $skyDir\pano4096.png|128" 1); $st = Wait-Sky 'ready_*'
Check 'Oversized panoramas are scaled to the limit' ($st -eq 'ready_128') $st
$src = [Drawing.Image]::FromFile("$skyDir\pano1000.png"); $src.Save("$skyDir\pano1000.jpg", [Drawing.Imaging.ImageFormat]::Jpeg); $src.Dispose()
[void](Lab "skyload $skyDir\pano1000.jpg|4096" 1); $st = Wait-Sky 'ready_*'
Check 'JPG panoramas load' ($st -eq 'ready_250') $st
[void](Lab "skyload $skyDir\dir64.png|4096" 1); [void](Wait-Sky 'ready_*')
[void](Get-EyeDump 't_sky' 320)
$skyEyes = Diff-Images "$Lab\mock_sky_left.bmp" "$Lab\mock_sky_right.bmp"
Check 'The sky is at infinity, the same in both eyes' ($skyEyes -lt 0.01) ("diff={0:F3}" -f $skyEyes)
Check 'The sky layer is at full headset resolution' ((Get-Content "$Lab\mock_layers.txt" | Select-String 'projection view=0 chain=2064x2272') -ne $null)
foreach ($size in @(@(1000, 750, 250), @(1025, 769, 256), @(4096, 3072, 1024))) {
    $src = [Drawing.Image]::FromFile("$Root\skybox\backdrop.png"); $b = New-Object Drawing.Bitmap $size[0], $size[1]; $g = [Drawing.Graphics]::FromImage($b); $g.DrawImage($src, 0, 0, $size[0], $size[1])
    $p = "$skyDir\s$($size[0]).png"; $b.Save($p, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose(); $src.Dispose()
    [void](Lab "skyload $p|4096" 1)
    $st = Wait-Sky 'ready_*'
    Check "A $($size[0])x$($size[1]) image gives $($size[2]) pixel faces" ($st -eq "ready_$($size[2])") $st
}
[void](Lab "skyload $skyDir\s4096.png|128" 1)
$st = Wait-Sky 'ready_*'; Check 'Oversized images are scaled to the limit' ($st -eq 'ready_128') $st
$b = New-Object Drawing.Bitmap 600, 600; $p = "$skyDir\square.png"; $b.Save($p, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose()
[void](Lab "skyload $p|4096" 1); $st = Wait-Sky 'failed*'
Check 'A wrongly shaped image is refused' ($st -like 'failed*') $st
Start-Sleep 1; [void](Get-EyeDump 't_sky' 320)
Check 'Without a sky the game still shows its screen' ((Get-Content "$Lab\mock_layers.txt")[0] -match 'layers=2')
[void](Lab "skyload $skyDir\nothing.png|4096" 1); $st = Wait-Sky 'failed*'
Check 'A missing image is reported, not fatal' ($st -like 'failed*' -and ((Val (Lab 'vr' 1) 'active') -eq 'True')) $st
'not an image' | Set-Content "$skyDir\broken.png" -Encoding ascii
[void](Lab "skyload $skyDir\broken.png|4096" 1); $st = Wait-Sky 'failed*'
Check 'A corrupt image is reported, not fatal' ($st -like 'failed*' -and ((Val (Lab 'vr' 1) 'active') -eq 'True')) $st
# A packed sky holds its bytes after a header, scrambled at the start, so it can't simply be renamed or opened.
$pack = [Text.Encoding]::ASCII.GetBytes("LVSKY1`r`n") + [Text.Encoding]::ASCII.GetBytes('not an image, even once unscrambled')
[IO.File]::WriteAllBytes("$skyDir\broken.lvsky", $pack)
[void](Lab "skyload $skyDir\broken.lvsky|4096" 1); $st = Wait-Sky 'failed*'
Check 'A damaged packed sky is reported, not fatal' ($st -like 'failed*') $st
$packedHead = [byte[]]::new(16); $fs = [IO.File]::OpenRead("$Root\skybox\FS020_Day_Sunless.lvsky"); [void]$fs.Read($packedHead, 0, 16); $fs.Close()
Check 'A packed sky isn''t a readable PNG' ([Text.Encoding]::ASCII.GetString($packedHead, 8, 8) -notmatch 'PNG') ''
[void](Lab "skyload $skyDir\dir64.png|4096" 1); [void](Wait-Sky 'ready_*')
Set-Head -Yaw 0; [void](Lab 'skyshade 0.5 0.5 0.5' 1); Start-Sleep 4; [void](Get-EyeDump 't_sky' 320)
$half = [Drawing.Bitmap]::FromFile("$Lab\mock_sky_left.bmp"); $hp = $half.GetPixel(1032, 1136); $half.Dispose()
[void](Lab 'skyshade 1 1 1' 1); Start-Sleep 4; [void](Get-EyeDump 't_sky' 320)
$full = [Drawing.Bitmap]::FromFile("$Lab\mock_sky_left.bmp"); $fp = $full.GetPixel(1032, 1136); $full.Dispose()
Check 'A shade dims the sky' ($hp.B -lt $fp.B -and $hp.B -gt 0) "half=$($hp.B) full=$($fp.B)"
# Crossfade: solid colours, read from the middle of the view.
function New-SolidCube([string]$Path, [Drawing.Color]$Color) { $b = New-Object Drawing.Bitmap 256, 192; $g = [Drawing.Graphics]::FromImage($b); $g.Clear($Color); $b.Save($Path, [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $b.Dispose() }
function Read-SkyCentre { [void](Get-EyeDump 't_sky' 320); $i = [Drawing.Bitmap]::FromFile("$Lab\mock_sky_left.bmp"); $p = $i.GetPixel(1032, 1136); $i.Dispose(); $p }
New-SolidCube "$skyDir\red.png" ([Drawing.Color]::FromArgb(255, 255, 0, 0)); New-SolidCube "$skyDir\blue.png" ([Drawing.Color]::FromArgb(255, 0, 0, 255))
[void](Lab "skyload $skyDir\red.png|4096" 1); [void](Wait-Sky 'ready_*'); Start-Sleep 1
$p0 = Read-SkyCentre
Check 'Fade test starts from the first sky' ($p0.R -gt 240 -and $p0.B -lt 15) "R=$($p0.R) B=$($p0.B)"
[void](Lab "skyload $skyDir\blue.png|4096|8" 1); [void](Wait-Sky 'ready_*'); Start-Sleep 2
$p1 = Read-SkyCentre
Check 'A new sky crossfades in' ($p1.R -gt 15 -and $p1.R -lt 250 -and $p1.B -gt 15 -and $p1.B -lt 250) "R=$($p1.R) B=$($p1.B)"
Start-Sleep 9
$p2 = Read-SkyCentre
Check 'The crossfade finishes on the new sky' ($p2.B -gt 240 -and $p2.R -lt 15) "R=$($p2.R) B=$($p2.B)"
[void](Lab "skyload $skyDir\missing.png|4096|2" 1); $st = Wait-Sky 'failed*'; Start-Sleep 1
$p3 = Read-SkyCentre
Check 'A failed fade keeps the current sky' ($st -like 'failed*' -and $p3.B -gt 240 -and $p3.R -lt 15) "$st R=$($p3.R) B=$($p3.B)"
[void](Lab "skyload $Game\BepInEx\plugins\LanaVR\skybox\backdrop.png|4096" 1); [void](Wait-Sky 'ready_*')
[void](Lab 'waterdebug 0' 1)

# --- Water ----------------------------------------------------------------------------------------
function From-Srgb([double]$V) { $c = $V / 255; if ($c -le 0.04045) { $c / 12.92 } else { [math]::Pow(($c + 0.055) / 1.055, 2.4) } }
# Eye position exactly as the mock HMD reports it.
function Eye-Position([int]$Eye, [double]$X, [double]$Y, [double]$Z, [double]$Yaw) {
    $half = if ($Eye -eq 0) { -0.0315 } else { 0.0315 }; $yr = $Yaw * [math]::PI / 180
    @(($X + [math]::Cos($yr) * $half), $Y, ($Z - [math]::Sin($yr) * $half))
}
function Pixel-Ray([double]$Fx, [double]$Fy, [double[]]$Q) {
    $tan = [math]::Tan(0.87); $d = Rotate-Vector $Q @(((2 * $Fx - 1) * $tan), ((1 - 2 * $Fy) * $tan), -1.0)
    $n = [math]::Sqrt($d[0] * $d[0] + $d[1] * $d[1] + $d[2] * $d[2]); @(($d[0] / $n), ($d[1] / $n), ($d[2] / $n))
}
# Largest error in metres between where each eye's pixels meet the water, and where they should.
function Measure-WaterError([double]$WaterY, [double]$X = 0, [double]$Y = 0, [double]$Z = 0, [double]$Yaw = 0, [double]$Pitch = 0) {
    Set-Head -X $X -Y $Y -Z $Z -Yaw $Yaw -Pitch $Pitch; Start-Sleep -Milliseconds 700
    [void](Get-EyeDump 't_sky' 320)
    $q = Head-Quat $Yaw $Pitch; $worst = 0.0; $n = 0
    foreach ($eye in 0, 1) {
        $file = if ($eye -eq 0) { 'mock_sky_left.bmp' } else { 'mock_sky_right.bmp' }
        $img = [Drawing.Bitmap]::FromFile("$Lab\$file"); $e = Eye-Position $eye $X $Y $Z $Yaw
        foreach ($fx in 0.25, 0.5, 0.75) { foreach ($fy in 0.6, 0.75, 0.9) {
            $d = Pixel-Ray $fx $fy $q
            if ($d[1] -gt -0.1) { continue }
            $t = ($WaterY - $e[1]) / $d[1]
            if ($t -ge 19) { continue }
            $worst = [math]::Max($worst, [math]::Abs((From-Srgb $img.GetPixel([int]($fx * $img.Width), [int]($fy * $img.Height)).R) * 20 - $t)); $n++
        } }
        $img.Dispose()
    }
    if ($n -lt 6) { return 99.0 }
    $worst
}
$w = Lab 'water' 1
Check 'Water sits at the floor the headset reports' ((Near (Num $w 'y') -1.6) -and (Val $w 'floorKnown') -eq '1') $w.Trim()
[void](Lab 'waterdebug 1' 1)
$e = Measure-WaterError -1.6; Check 'Each eye finds the water at the right distance' ($e -lt 0.1) ("worst={0:F3} m" -f $e)
$e = Measure-WaterError -1.6 -X 0.5 -Y 0.3 -Z -0.4 -Yaw 30 -Pitch -30; Check 'The water stays put when the head moves' ($e -lt 0.1) ("worst={0:F3} m" -f $e)
[void](Lab 'set water 0.5' 1); Start-Sleep 1
$e = Measure-WaterError -1.1; Check 'Water height raises the water' ($e -lt 0.1) ("worst={0:F3} m" -f $e)
[void](Lab 'set water 1' 1); Set-Head -Y -0.8; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
$w = Lab 'water' 1; Check 'The water never rises above the head' (Near (Num $w 'y') -1.0) $w.Trim()
[void](Lab 'set water 0' 1); Set-Head -Y -1.3; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
$w = Lab 'water' 1; Check 'A floor too close to the head is not believed' ((Near (Num $w 'y') -2.8) -and (Val $w 'floorKnown') -eq '0') $w.Trim()
[void](Lab 'set water 0' 1); Set-Head; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
# Where the reflected ray should land on the 3 m screen, 2.5 m ahead.
[void](Lab 'set ripple 0' 1); [void](Lab 'waterdebug 2' 1); Set-Head -Pitch -40; Start-Sleep 1; [void](Get-EyeDump 't_sky' 320)
$img = [Drawing.Bitmap]::FromFile("$Lab\mock_sky_left.bmp"); $q = Head-Quat 0 -40; $eyePos = Eye-Position 0 0 0 0 0; $worst = 0.0; $n = 0
foreach ($fx in 0.3, 0.4, 0.5, 0.6, 0.7) { foreach ($fy in 0.2, 0.3, 0.4, 0.5, 0.6, 0.7) {
    $d = Pixel-Ray $fx $fy $q; if ($d[1] -ge -0.05) { continue }
    $t = (-1.6 - $eyePos[1]) / $d[1]; $px = $eyePos[0] + $d[0] * $t; $py = -1.6; $pz = $eyePos[2] + $d[2] * $t
    if ($d[2] -ge 0 -or ($pz + 2.5) -le 0) { continue }
    $s = -($pz + 2.5) / $d[2]; $hx = $px + $d[0] * $s; $hy = $py - $d[1] * $s
    $u = $hx / 3 + 0.5; $v = 0.5 - $hy / 1.6875
    if ($u -lt 0.1 -or $u -gt 0.9 -or $v -lt 0.1 -or $v -gt 0.9) { continue }
    $p = $img.GetPixel([int]($fx * $img.Width), [int]($fy * $img.Height))
    $worst = [math]::Max($worst, [math]::Max([math]::Abs((From-Srgb $p.R) - $u), [math]::Abs((From-Srgb $p.G) - $v))); $n++
} }
$img.Dispose()
Check 'The water reflects the screen in the right place' ($n -ge 3 -and $worst -lt 0.02) ("$n points, worst={0:F3} of the screen" -f $worst)
[void](Lab 'waterdebug 0' 1); Start-Sleep 1; [void](Get-EyeDump 't_refl' 320); Copy-Item "$Lab\mock_sky_left.bmp" "$Lab\t_refl.L.bmp" -Force
[void](Lab 'set screenreflection 0' 1); Start-Sleep 1; [void](Get-EyeDump 't_sky' 320)
$off = Diff-Images "$Lab\t_refl.L.bmp" "$Lab\mock_sky_left.bmp"; Copy-Item "$Lab\mock_sky_left.bmp" "$Lab\t_refl_off.L.bmp" -Force
[void](Lab 'set screenreflection 1' 1); Start-Sleep 1
# The reflection covers part of this view, and the animated menu alone changes it by about 0.1.
Check 'Turning the screen reflection off removes it from the water' ($off -gt 0.5) ("diff={0:F2}" -f $off)
$img = [Drawing.Bitmap]::FromFile("$Lab\t_refl.L.bmp")
Set-Head -Yaw 180 -Pitch -40; Start-Sleep 1; [void](Get-EyeDump 't_sky' 320)
$noRefl = Diff-Images "$Lab\t_refl.L.bmp" "$Lab\mock_sky_left.bmp"; $img.Dispose()
Check 'The game image shows in the water' ($noRefl -gt 3) ("diff={0:F2}" -f $noRefl)
# Turned away from the screen, so only the ripples can change these pixels.
Set-Head -Yaw 90 -Pitch -30; Start-Sleep 1; [void](Get-EyeDump 't_sky' 320); Copy-Item "$Lab\mock_sky_left.bmp" "$Lab\t_still.bmp" -Force
Start-Sleep 1; [void](Get-EyeDump 't_sky' 320)
$still = Diff-Images "$Lab\t_still.bmp" "$Lab\mock_sky_left.bmp"
# Still water still reflects the animated menu, which reads up to about 0.12 here. Rippling water reads about 4.
Check 'Ripple strength 0 gives still water' ($still -lt 0.3) ("diff={0:F3}" -f $still)
[void](Lab 'set ripple 1' 1); Start-Sleep 1; [void](Get-EyeDump 't_sky' 320); Copy-Item "$Lab\mock_sky_left.bmp" "$Lab\t_move.bmp" -Force
Start-Sleep 2; [void](Get-EyeDump 't_sky' 320)
$move = Diff-Images "$Lab\t_move.bmp" "$Lab\mock_sky_left.bmp"
Check 'The water ripples' ($move -gt 0.3) ("diff={0:F3}" -f $move)
$eyes = Diff-Images "$Lab\mock_sky_left.bmp" "$Lab\mock_sky_right.bmp"
Check 'The water has stereo depth' ($eyes -gt 0.1) ("diff={0:F3}" -f $eyes)
Set-Head; Start-Sleep 1

# --- VR menu, using only the gamepad --------------------------------------------------------------
$audioBefore = (Lab 'audio' 1).Trim()
$settingsBefore = (Get-FileHash "$env:USERPROFILE\AppData\LocalLow\Wishfully\Planet of Lana\settings").Hash
[void](Lab 'exec Main Menu [View]/Buttons/SettingsButton' 3)
Pad 'down' 5
$m = Lab 'menu' 1
Check 'Settings list has a VR entry' ($m -match 'selected=SettingsVRButton') $m.Trim()
Pad 'a'; Start-Sleep 1
$m = Lab 'menu' 1
Check 'A opens the VR page on Recenter' ($m -match 'open=True' -and $m -match 'selected=RecenterButton') $m.Trim()
Dump-Pose 't_page'
Set-Head; Pad 'down'; Pad 'right' 2; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Screen size slider' (Near (Num $c 'width') 3.5) $c.Trim()
Dump-Pose 't_size'; $L = Layer 0
Check 'The screen resizes live' ((Near $L.W 3.5) -and (Near ($L.W / $L.H) (16 / 9))) "W=$($L.W)"
Pad 'down'; Pad 'right' 2; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Screen depth slider' (Near (Num $c 'distance') 3.0) $c.Trim()
Dump-Pose 't_depth'; $L = Layer 0
Check 'The screen moves live' (Near $L.Pos[2] -3.0) "z=$($L.Pos[2])"
Pad 'down'; Pad 'left' 3; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Stereo strength slider' (Near (Num $c 'stereo') 0.7) $c.Trim()
Pad 'down'; Pad 'left' 2; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Head tracking slider' (Near (Num $c 'head') 0.8) $c.Trim()
Pad 'right' 40; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Sliders stop at their maximum' (Near (Num $c 'head') 2.0) $c.Trim()
Pad 'down'; Start-Sleep 1
$m = Lab 'menu' 1; Check 'Sky quality row is reachable' ($m -match 'selected=VR SKY QUALITY') $m.Trim()
Pad 'right'; Start-Sleep 1; $c = Lab 'cfg' 1; Check 'Sky quality goes up to High' (Near (Num $c 'skyquality') 2) $c.Trim()
Check 'High quality reloads the sky at 2048' ((Wait-Sky 'ready_2048') -eq 'ready_2048') (Lab 'sky' 1).Trim()
Pad 'left' 2; Start-Sleep 1; $c = Lab 'cfg' 1; Check 'Sky quality goes down to Low' (Near (Num $c 'skyquality') 0) $c.Trim()
Check 'Low quality reloads the sky at 512' ((Wait-Sky 'ready_512') -eq 'ready_512') (Lab 'sky' 1).Trim()
Pad 'right'; Start-Sleep 1
Pad 'down'; Pad 'right' 2; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Water height slider' (Near (Num $c 'water') 0.1) $c.Trim()
Pad 'down'; Pad 'left' 3; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Ripple strength slider' (Near (Num $c 'ripple') 0.7) $c.Trim()
Check 'The VR page leaves audio untouched' ((Lab 'audio' 1).Trim() -eq $audioBefore)
Pad 'down'; $m = Lab 'menu' 1
Check 'Screen reflection row is reachable' ($m -match 'selected=VR SCREEN REFLECTION') $m.Trim()
$c = Lab 'cfg' 1; Check 'Screen reflection defaults to on' ($c -match 'screenreflection=True') $c.Trim()
Pad 'left'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Left turns the screen reflection off' ($c -match 'screenreflection=False') $c.Trim()
Pad 'right'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Right turns the screen reflection on' ($c -match 'screenreflection=True') $c.Trim()
Pad 'left'; Start-Sleep 1
Pad 'down'; $m = Lab 'menu' 1
Check 'Reflection quality row is reachable' ($m -match 'selected=VR REFLECTION QUALITY') $m.Trim()
Pad 'right'; Start-Sleep 1; $c = Lab 'cfg' 1; Check 'Water reflection goes up to High' (Near (Num $c 'reflection') 2) $c.Trim()
Pad 'left' 2; Start-Sleep 1; $c = Lab 'cfg' 1; Check 'Water reflection goes down to Low' (Near (Num $c 'reflection') 0) $c.Trim()
Pad 'right'; Start-Sleep 1
Pad 'down'; $m = Lab 'menu' 1
Check '3D scenery row is reachable' ($m -match 'selected=VR 3D SCENERY') $m.Trim()
Pad 'right'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Right turns 3D scenery on' ($c -match 'scenery=True') $c.Trim()
Pad 'left'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Left turns 3D scenery off' ($c -match 'scenery=False') $c.Trim()
Pad 'down'; $m = Lab 'menu' 1
Check 'Scenery size row is reachable' ($m -match 'selected=VR SCENERY SIZE') $m.Trim()
Pad 'left'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Scenery size slider' (Near (Num $c 'scenerysize') 0.95) $c.Trim()
$c = Lab 'cfg' 1; Check 'Cutscene borders default to off' ($c -match 'borders=False') $c.Trim()
Pad 'down'; $m = Lab 'menu' 1
Check 'Cutscene borders row is reachable' ($m -match 'selected=VR CUTSCENE BORDERS') $m.Trim()
Pad 'right'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Right turns cutscene borders on' ($c -match 'borders=True') $c.Trim()
Pad 'left'; Start-Sleep 1
$c = Lab 'cfg' 1; Check 'Left turns cutscene borders off' ($c -match 'borders=False') $c.Trim()
Pad 'right'; Start-Sleep 1
Pad 'down'; $m = Lab 'menu' 1
Check 'Reset row is reachable' ($m -match 'selected=ResetToDefaultButton') $m.Trim()
Pad 'a'; Start-Sleep 1
$c = Lab 'cfg' 1
Check 'Reset restores the defaults' ((Near (Num $c 'width') 3) -and (Near (Num $c 'distance') 2.5) -and (Near (Num $c 'stereo') 1) -and (Near (Num $c 'head') 1) -and (Near (Num $c 'skyquality') 1) -and (Near (Num $c 'water') -0.5) -and (Near (Num $c 'ripple') 1) -and (Near (Num $c 'reflection') 1) -and ($c -match 'screenreflection=True') -and ($c -match 'borders=False') -and ($c -match 'scenery=True') -and (Near (Num $c 'scenerysize') 1)) $c.Trim()
Pad 'b'; Start-Sleep 1
$m = Lab 'menu' 1
Check 'B closes the VR page, back to Settings' ($m -match 'open=False' -and $m -match 'selected=SettingsVRButton') $m.Trim()
Pad 'b'; Start-Sleep 2
$canvases = Lab 'dump' 1
Check 'B again returns to the main menu' ($canvases -match 'CANVAS Main Menu \[View\] en=True') ''
Check 'The game''s settings file was not changed' ((Get-FileHash "$env:USERPROFILE\AppData\LocalLow\Wishfully\Planet of Lana\settings").Hash -eq $settingsBefore)

# --- Gameplay and the pause menu ------------------------------------------------------------------
[void](Lab 'exec Main Menu [View]/Buttons/SlotsButton' 3)
$r = Lab "slot $Slot" 3; Check 'The test save slot opens' ($r -match 'opened slot') $r.Trim()
[void](Lab 'exec Slot [View]/Group/PlayButton' 3)
Start-Sleep 30
$d = Lab 'dump' 1
Check 'Gameplay camera is live' ($d -match 'Camera\.main=CAMERA/Gameplay Camera/Camera') ''
Check 'Game cameras are enabled during game logic' ($d -match 'CAM CAMERA/Gameplay Camera/Camera en=True') ''
# Counts real frames. The drone scene measured about 73 to 88 frames per second on a 1080 Ti, so this
# guards against a serious slowdown and does not claim a locked 90.
$a = Num (Lab 'vr' 1) 'submitted'; $t = Get-Date; Start-Sleep 6; $b = Num (Lab 'vr' 1) 'submitted'
$rate = ($b - $a) / ((Get-Date) - $t).TotalSeconds
Check 'Gameplay frame rate stays above 70 on the 90 Hz mock' ($rate -gt 70) ("{0:F1} frames/s" -f $rate)
$cone = Lab 'cone' 1
if ((Num $cone 'volumeLights') -gt 0) {
    Check 'Drone light cameras stay out of the eye pass' ((Val $cone 'volumeCameraInEyePass') -eq 'False') ("lights=$(Val $cone 'volumeLights')")
    Check 'Drone light keeps its own depth buffer' ($cone -match 'depthBuf=\d+x\d+ Depth') ''
} else { Write-Output 'SKIP  Drone light checks: this save slot has no drone on screen' }
$pp = Lab 'pp' 1; $rt = [regex]::Match($pp, 'REFL .* rt=(\d+)').Groups[1].Value
if ($rt) { Check 'Water reflection texture is reduced' ([int]$rt -lt 3000) "width=$rt" }
Dump-Pose 't_game'
$gameStereo = Diff-Images "$Lab\t_game.L.bmp" "$Lab\t_game.R.bmp"
Check 'Gameplay is in stereo' ($gameStereo -gt 2) ("diff={0:F2}" -f $gameStereo)
# --- 3D scenery, which Reset turned back on, and which the frame rate above was measured with. ---
$s = Lab 'scenery' 1
Check 'The scenery is built from the game''s village' ((Val $s 'state') -eq 'Ready' -and (Num $s 'props') -gt 20 -and (Num $s 'missing') -eq 0) $s.Trim()
Check 'Loading the scenery causes only a small hitch' ((Num $s 'worstFrameMs') -lt 500) "worst frame $(Val $s 'worstFrameMs') ms"
Check 'The scenery is drawing' ((Val $s 'drawing') -eq 'True') ''
function Sky-Copy([string]$Name) { [void](Get-EyeDump 't_sky' 320); Copy-Item "$Lab\mock_sky_left.bmp" "$Lab\$Name.L.bmp" -Force; Copy-Item "$Lab\mock_sky_right.bmp" "$Lab\$Name.R.bmp" -Force }
Set-Head -Pitch -75; Start-Sleep 1; Sky-Copy 't_deck'
$deckEyes = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck.R.bmp"
Check 'The scenery has stereo depth' ($deckEyes -gt 2) ("diff={0:F2}" -f $deckEyes)
Set-Head -X 0.3 -Pitch -75; Start-Sleep 1; Sky-Copy 't_deck_moved'
$deckMoved = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck_moved.L.bmp"
Check 'The scenery stays put when the head moves' ($deckMoved -gt 5) ("diff={0:F2}" -f $deckMoved)
# Tiny head moves must change the picture evenly. Scenery placed far from the world's origin snapped to a 2 mm grid,
# which gave runs of identical images followed by jumps, seen in a headset as a fast jitter.
function Scenery-Diff([string]$A, [string]$B) {
    $x = [Drawing.Bitmap]::FromFile($A); $y = [Drawing.Bitmap]::FromFile($B); $sum = 0.0; $n = 0
    for ($j = 4; $j -lt $x.Height; $j += 8) { for ($i = 4; $i -lt $x.Width; $i += 8) {
        $p = $x.GetPixel($i, $j); $q = $y.GetPixel($i, $j)
        $sum += [math]::Abs($p.R - $q.R) + [math]::Abs($p.G - $q.G) + [math]::Abs($p.B - $q.B); $n += 3 } }
    $x.Dispose(); $y.Dispose(); $sum / $n
}
function Scenery-Steps([string]$Axis) {
    $steps = @()
    for ($i = 0; $i -le 8; $i++) {
        $d = 0.2 + $i * 0.00025
        if ($Axis -eq 'X') { Set-Head -X $d -Pitch -40 } else { Set-Head -Y $d -Pitch -40 }
        Start-Sleep -Milliseconds 400
        [void](Lab "sceneryshot t_step$i.png" 1)
        if ($i -gt 0) { $steps += Scenery-Diff "$Lab\t_step$($i - 1).png" "$Lab\t_step$i.png" }
    }
    $steps
}
foreach ($axis in 'X', 'Y') {
    $steps = @(Scenery-Steps $axis); $m = $steps | Measure-Object -Minimum -Maximum
    Check "The scenery follows tiny head moves evenly on $axis" ($m.Minimum -gt 0 -and $m.Maximum -lt $m.Minimum * 3) ("smallest={0:F3} largest={1:F3}" -f $m.Minimum, $m.Maximum)
}
Set-Head -Pitch -75; Start-Sleep 1
[void](Lab 'set scenerysize 1.33' 1); Start-Sleep 1; Sky-Copy 't_deck_big'
$deckBig = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck_big.L.bmp"
Check 'Scenery size changes the scenery' ($deckBig -gt 5) ("diff={0:F2}" -f $deckBig)
[void](Lab 'set scenerysize 1' 1); Start-Sleep 1
$c = Lab 'cfg' 1; Check 'The scenery has finished fading in' (Near (Num $c 'fade') 1) $c.Trim()
[void](Lab 'set sceneryposition 1.2' 1); Start-Sleep 1; Sky-Copy 't_deck_fwd'
$deckFwd = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck_fwd.L.bmp"
Check 'Standing further forward moves the scenery' ($deckFwd -gt 3) ("diff={0:F2}" -f $deckFwd)
[void](Lab 'set sceneryposition 9' 1); $c = Lab 'cfg' 1
Check 'The standing position is kept on the platform' (Near (Num $c 'standing') 1.2) $c.Trim()
[void](Lab 'set sceneryposition 0.5' 1); Start-Sleep 1
$f = Lab 'focus' 1; Check 'The game''s window claims the foreground' ($f -match 'foreground=True') $f.Trim()
Set-Head -Yaw 0 -Pitch -75; Start-Sleep 1; Sky-Copy 't_deck'
Set-Head -Yaw 40 -Pitch -75; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1; Sky-Copy 't_deck_turned'
$deckTurned = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck_turned.L.bmp"
Check 'Recentering turns the scenery with the screen' ($deckTurned -lt 8) ("diff={0:F2}" -f $deckTurned)
Set-Head -Pitch -75; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
[void](Lab 'set scenery 0' 1); Start-Sleep 1; Sky-Copy 't_deck_off'
$deckOff = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck_off.L.bmp"
Check 'Turning the scenery off removes it' ($deckOff -gt 10) ("diff={0:F2}" -f $deckOff)
$s = Lab 'scenery' 1; Check 'The scenery stops drawing when off' ((Val $s 'drawing') -eq 'False') $s.Trim()
[void](Lab 'set scenery 1' 1); Start-Sleep 1; Sky-Copy 't_deck_on'
$deckOn = Diff-Images "$Lab\t_deck.L.bmp" "$Lab\t_deck_on.L.bmp"
Check 'Turning the scenery on brings it back at once' ($deckOn -lt 8) ("diff={0:F2}" -f $deckOn)
Set-Head; Start-Sleep 1; Pad 'recenter'; Start-Sleep 1
# --- Cutscene borders. The game is told to show them, as a cutscene would. ---
function Top-Band([string]$File) {
    $x = [Drawing.Bitmap]::FromFile($File); $sum = 0.0; $n = 0; $y = [int]($x.Height * 0.04)
    for ($i = 8; $i -lt $x.Width; $i += 16) { $p = $x.GetPixel($i, $y); $sum += ($p.R + $p.G + $p.B) / 3; $n++ }
    $x.Dispose(); $sum / $n
}
[void](Lab 'bars on instant' 2)
$b = Lab 'barsstate' 1
Check 'The game believes its borders are showing' ($b -match 'canvas=True') $b.Trim()
Check 'Borders set to off hide both bars' ($b -notmatch 'active=True') $b.Trim()
Dump-Pose 't_borders_off'; $bandOff = Top-Band "$Lab\t_borders_off.L.bmp"
[void](Lab 'set borders 1' 2)
$b = Lab 'barsstate' 1
Check 'Borders set to on apply straight away' ($b -notmatch 'active=False') $b.Trim()
Dump-Pose 't_borders_on'; $bandOn = Top-Band "$Lab\t_borders_on.L.bmp"
Check 'The borders reach the VR eye image only when on' ($bandOn -lt 2 -and $bandOff -gt $bandOn + 5) ("top of picture: off={0:F1} on={1:F1}" -f $bandOff, $bandOn)
[void](Lab 'set borders 0' 2); [void](Lab 'bars off instant' 2)
$b = Lab 'barsstate' 1
Check 'The borders close again with the game' ($b -match 'canvas=False') $b.Trim()
# Walking sends movement to the selected UI object, and jump presses it. The VR page once opened this way.
$strayEntry = $false
foreach ($dir in 'right', 'left', 'up', 'down', 'up', 'up', 'left', 'down', 'down', 'right', 'up', 'left') {
    Pad $dir; if ((Lab 'menu' 1) -match 'selected=(SettingsVRButton|RecenterButton|VR |ResetToDefaultButton)') { $strayEntry = $true }
}
Check 'Walking never selects a hidden VR button' (-not $strayEntry) ([regex]::Match((Lab 'menu' 1), 'selected=\S+').Value)
Pad 'a'; Start-Sleep 1
Check 'Jumping never opens the VR page' ((Lab 'menu' 1) -match 'open=False')
[void](Lab 'menu open' 1); $m = Lab 'menu' 1
Check 'The VR page refuses to open without Settings' ($m -match 'open=False' -and $m -match 'pageCanvas=False') ''
Pad 'start'; Start-Sleep 2
$d = Lab 'dump' 1
Check 'Start opens the game''s pause menu' ($d -match 'CANVAS Pause \[View\] en=True') ''
Dump-Pose 't_pause'
[void](Lab 'exec Pause [View]/Group/SettingsButton' 2)
foreach ($i in 1..8) { if ((Lab 'menu' 1) -match 'selected=SettingsVRButton') { break }; Pad 'down' }  # The list wraps around.
Pad 'a'; Start-Sleep 1
$m = Lab 'menu' 1; Check 'The VR page opens from the pause menu' ($m -match 'open=True') $m.Trim()
Dump-Pose 't_pause_vr'
Pad 'b'; Start-Sleep 1; Pad 'b'; Start-Sleep 1; Pad 'b'; Start-Sleep 2
$d = Lab 'dump' 1
Check 'Backing out resumes the game' ($d -notmatch 'CANVAS Pause \[View\] en=True') ''
# Start does nothing on the game's own Settings page, and must leave the VR page able to back out.
Pad 'start'; Start-Sleep 2
[void](Lab 'exec Pause [View]/Group/SettingsButton' 2)
Pad 'start'; Start-Sleep 2
$d = Lab 'dump' 1
Check 'Start leaves the game''s own Settings page open' ($d -match 'CANVAS Settings \[View\] en=True' -and $d -match 'CANVAS Pause \[View\] en=True') ''
foreach ($i in 1..8) { if ((Lab 'menu' 1) -match 'selected=SettingsVRButton') { break }; Pad 'down' }
Pad 'a'; Start-Sleep 1
Check 'The VR page opens again from pause' ((Lab 'menu' 1) -match 'open=True') ''
Pad 'start'; Start-Sleep 2
Pad 'b'; Start-Sleep 1
$m = Lab 'menu' 1; $d = Lab 'dump' 1
Check 'B still closes the VR page after Start' ($m -match 'open=False' -and $m -match 'pageCanvas=False' -and $d -match 'CANVAS Settings \[View\] en=True') ([regex]::Match($m, 'open=\S+ selected=\S+').Value)
Pad 'a'; Start-Sleep 1
Check 'The VR page reopens' ((Lab 'menu' 1) -match 'open=True') ''
# If the game ever closes Settings underneath the page, the page must follow it.
[void](Lab 'settingscanvas 0' 2)
$m = Lab 'menu' 1
Check 'The VR page closes when the game closes Settings' ($m -match 'open=False' -and $m -match 'pageCanvas=False') ([regex]::Match($m, 'open=\S+ .*pageCanvas=\S+').Value)
[void](Lab 'settingscanvas 1' 2)
$m = Lab 'menu' 1; $d = Lab 'dump' 1
Check 'Settings contents come back afterwards' ($d -match 'CANVAS Settings \[View\] en=True' -and $m -match 'entryInteractable=True') ([regex]::Match($m, 'entryInteractable=\S+').Value)
Pad 'b'; Start-Sleep 1; Pad 'b'; Start-Sleep 2
$d = Lab 'dump' 1
Check 'Backing out still returns to play' ($d -notmatch 'CANVAS (Pause|Settings) \[View\] en=True') ''

# --- Skybox follows the game's backdrop -----------------------------------------------------------
function Wait-Backdrop([string]$Want) { for ($i = 0; $i -lt 30; $i++) { $x = Lab 'sky' 1; if ((Val $x 'backdrop') -eq $Want) { return $x }; Start-Sleep 1 }; Lab 'sky' 1 }
[void](Lab 'goto Chapter 01 Scene 65' 1); $x = Wait-Backdrop 'Chapter_01_Backdrop_Night'
Check 'Night backdrop shows the night sky' ((Val $x 'file') -eq 'FS002_Night_Moonless.lvsky') $x.Trim()
Start-Sleep 18
[void](Lab 'goto Chapter 03 Scene 70' 1); $x = Wait-Backdrop 'Chapter_03_Backdrop_1'
Check 'An unlisted backdrop keeps the current sky' ((Val $x 'file') -eq 'FS002_Night_Moonless.lvsky' -and (Val $x 'status') -like 'ready*') $x.Trim()
Start-Sleep 18
[void](Lab 'goto Chapter 01 Scene 10' 1); $x = Wait-Backdrop 'Chapter_01_Backdrop'
Check 'The daytime backdrop brings the day sky back' ((Val $x 'file') -eq 'FS020_Day_Sunless.lvsky') $x.Trim()
Start-Sleep 20
Check 'The game still runs after level changes' ((Val (Lab 'vr' 1) 'active') -eq 'True' -and (Lab 'dump' 1) -notmatch 'CANVAS Loading \[View\] en=True') ''

# --- The sky follows the game's colour fades, on a white sky so the tint can be read directly -----
function Read-Tint { Start-Sleep 3; [void](Get-EyeDump 't_sky' 320); $i = [Drawing.Bitmap]::FromFile("$Lab\mock_sky_left.bmp"); $p = $i.GetPixel(1032, 300); $i.Dispose(); $p }
function Near-Colour($P, [double]$R, [double]$G, [double]$B) { (Near $P.R ($R * 255) 8) -and (Near $P.G ($G * 255) 8) -and (Near $P.B ($B * 255) 8) }
New-SolidCube "$skyDir\white.png" ([Drawing.Color]::White)
[void](Lab 'goto Chapter 01 Scene 10' 1); [void](Wait-Backdrop 'Chapter_01_Backdrop'); Start-Sleep 18
[void](Lab "skyload $skyDir\white.png|4096" 1); [void](Wait-Sky 'ready_*'); [void](Lab 'waterdebug 3' 1)
[void](Lab 'ppw PP Sunset 0' 1); [void](Lab 'ppw PP Gray 0' 1)
$p = Read-Tint; Check 'No fade leaves the sky untinted' (Near-Colour $p 1 1 1) "R=$($p.R) G=$($p.G) B=$($p.B)"
[void](Lab 'ppw PP Sunset 1' 1); $p = Read-Tint; Check 'PP Sunset tints the sky' (Near-Colour $p 1 0.56 0.52) "R=$($p.R) G=$($p.G) B=$($p.B)"
[void](Lab 'ppw PP Sunset 0.5' 1); $p = Read-Tint; Check 'A half-finished fade gives half the tint' (Near-Colour $p 1 0.78 0.76) "R=$($p.R) G=$($p.G) B=$($p.B)"
[void](Lab 'ppw PP Sunset 1' 1); [void](Lab 'ppw PP Gray 1' 1); $p = Read-Tint; Check 'PP Gray takes over from PP Sunset' (Near-Colour $p 0.56 0.55 0.81) "R=$($p.R) G=$($p.G) B=$($p.B)"
[void](Lab 'ppw PP Gray 0.5' 1); $p = Read-Tint; Check 'The two fades blend in the game''s order' (Near-Colour $p 0.78 0.555 0.665) "R=$($p.R) G=$($p.G) B=$($p.B)"
[void](Lab 'goto Chapter 01 Scene 65' 1); $x = Wait-Backdrop 'Chapter_01_Backdrop_Night'; Start-Sleep 18
[void](Lab "skyload $skyDir\white.png|4096" 1); [void](Wait-Sky 'ready_*'); [void](Lab 'ppw PP Gray 1' 1); [void](Lab 'ppw PP Sunset 1' 1)
$p = Read-Tint; Check 'A Shade line darkens the sky, and the night sky ignores the daytime fades' (Near-Colour $p 0.5 0.5 0.5) "R=$($p.R) G=$($p.G) B=$($p.B)"
[void](Lab 'ppw PP Gray 0' 1); [void](Lab 'ppw PP Sunset 0' 1); [void](Lab 'waterdebug 0' 1)
[void](Lab 'goto Chapter 01 Scene 10' 1); [void](Wait-Backdrop 'Chapter_01_Backdrop'); Start-Sleep 18

# --- Runtime loss and recovery --------------------------------------------------------------------
'' | Set-Content "$Lab\mock_nostage.txt" -Encoding ascii  # The next session comes from a headset with no floor.
'' | Set-Content "$Lab\mock_exit.txt" -Encoding ascii; Start-Sleep 3
$vr = Lab 'vr' 1; $d = Lab 'dump' 1
Check 'Session loss falls back to the flat game' ((Val $vr 'active') -eq 'False') $vr.Trim()
Check 'Flat fallback keeps the desktop resolution' ((Val $vr 'win') -eq $desktopRes) "win=$(Val $vr 'win')"
Check 'Flat fallback restores overlay UI' ($d -notmatch 'mode=ScreenSpaceCamera') ''
Check 'Flat fallback restores the camera' ($d -match 'CAM CAMERA/Gameplay Camera/Camera en=True') ''
Start-Sleep 9
$vr = Lab 'vr' 1
Check 'VR resumes when the runtime returns' ((Val $vr 'active') -eq 'True' -and (Val $vr 'state') -eq '5') $vr.Trim()
$w = Lab 'water' 1; Check 'Without a floor, the floor is taken as 1.5 m below the head, and the water is 0.5 m below that' ((Near (Num $w 'y') -2.0) -and (Val $w 'floorKnown') -eq '0') $w.Trim()
[IO.File]::Delete("$Lab\mock_nostage.txt")
Dump-Pose 't_resumed'
$resumed = Diff-Images "$Lab\t_resumed.L.bmp" "$Lab\t_resumed.R.bmp"
Check 'Resumed session is still in stereo' ($resumed -gt 2) ("diff={0:F2}" -f $resumed)

# --- Log checks -----------------------------------------------------------------------------------
$bad = @(Select-String -Path "$Lab\mock_log.txt" -Pattern 'BAD CALL')
Check 'The mock HMD saw no OpenXR misuse' ($bad.Count -eq 0) (($bad | Select-Object -First 3 | ForEach-Object Line) -join '; ')
$errs = @(Get-Content "$Game\BepInEx\LogOutput.log" | Select-String -Pattern '\[Error|Exception' | Where-Object { $_.Line -notmatch 'Il2CppInteropGen|\[lab\]' })
Check 'No errors or exceptions in the mod log' ($errs.Count -eq 0) (($errs | Select-Object -First 3 | ForEach-Object Line) -join '; ')

# --- Cleanup --------------------------------------------------------------------------------------
[void](Stop-Lana)
Check 'Testing changes no save files' (((Get-ChildItem $saveFolder -Filter *.sav | ForEach-Object { $_.Name + (Get-FileHash $_.FullName).Hash }) -join ';') -eq $savesBefore)
Check 'VR leaves the saved display mode alone after quitting' ((Get-DisplayPrefs) -eq $displayBefore) (Get-DisplayPrefs)
if ($null -ne $savedCfg) { Set-Content $Cfg $savedCfg -Encoding ascii -NoNewline }
$savedMap | Set-Content $installedMap -Encoding ascii
Write-Output ''
Write-Output "$script:Pass passed, $script:Fail failed"
exit $script:Fail
