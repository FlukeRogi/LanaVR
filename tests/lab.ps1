# Test helpers. Dot-source this file: . .\tests\lab.ps1
$script:Root = Split-Path $PSScriptRoot -Parent
# LANA_GAME points the tests at another copy of the game, such as the GOG one.
$script:Game = if ($env:LANA_GAME) { $env:LANA_GAME } else { 'C:\Program Files (x86)\Steam\steamapps\common\Planet of Lana' }
$script:Lab = "$env:USERPROFILE\AppData\LocalLow\LanaVR\lab"
$script:Cfg = "$Game\BepInEx\config\com.lanavr.planetoflana.cfg"
New-Item -ItemType Directory -Force $Lab | Out-Null

function Set-LanaRuntime([string]$Value) {
    # Writes only the Runtime key, so the other settings keep their values.
    if (-not (Test-Path $Cfg)) { "[OpenXR]`r`nRuntime = $Value`r`n" | Set-Content $Cfg -Encoding ascii; return }
    $t = Get-Content $Cfg -Raw
    if ($t -match '(?m)^Runtime = .*$') { $t = [regex]::Replace($t, '(?m)^Runtime = .*$', ("Runtime = " + $Value.Replace('$', '$$'))) }
    else { $t += "`r`n[OpenXR]`r`nRuntime = $Value`r`n" }
    Set-Content $Cfg $t -Encoding ascii -NoNewline
}

function Set-LanaKey([string]$Section, [string]$Key, [string]$Value) {
    # Replaces the key if present, otherwise adds it.
    $t = if (Test-Path $Cfg) { Get-Content $Cfg -Raw } else { '' }
    if ($t -match "(?m)^$Key = .*$") { $t = [regex]::Replace($t, "(?m)^$Key = .*$", ("$Key = " + $Value.Replace('$', '$$'))) }
    else { $t += "`r`n[$Section]`r`n$Key = $Value`r`n" }
    Set-Content $Cfg $t -Encoding ascii -NoNewline
}

function Lab([string]$Command, [int]$Wait = 2) {
    if (-not (Test-Path "$Lab\out.txt")) { '' | Set-Content "$Lab\out.txt" -Encoding ascii }
    $before = (Get-Item "$Lab\out.txt").Length
    $Command | Set-Content "$Lab\cmd.txt" -Encoding ascii
    Start-Sleep $Wait
    $fs = [IO.File]::Open("$Lab\out.txt", 'Open', 'Read', 'ReadWrite')
    [void]$fs.Seek($before, 'Begin')
    $r = New-Object IO.StreamReader $fs
    $t = $r.ReadToEnd(); $r.Close(); $t
}

function Start-Lana([int]$Wait = 30) {
    Start-Process "steam://rungameid/1608230"
    Start-Sleep $Wait
}

function Stop-Lana {
    if (Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue) {
        'quit' | Set-Content "$Lab\cmd.txt" -Encoding ascii
        for ($i = 0; $i -lt 15 -and (Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
        # Without the lab channel the quit command is ignored, so ask the window to close.
        Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
        for ($i = 0; $i -lt 10 -and (Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue); $i++) { Start-Sleep 1 }
        Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep 2
        if (Test-Path "$Lab\cmd.txt") { [IO.File]::Delete("$Lab\cmd.txt") }
    }
    [bool](Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue)
}

function Set-Head([double]$X = 0, [double]$Y = 0, [double]$Z = 0, [double]$Yaw = 0, [double]$Ipd = 0.063, [double]$Pitch = 0) {
    $c = [Globalization.CultureInfo]::InvariantCulture
    $text = "{0} {1} {2} {3} {4} {5}" -f $X.ToString($c), $Y.ToString($c), $Z.ToString($c), $Yaw.ToString($c), $Ipd.ToString($c), $Pitch.ToString($c)
    # The mock HMD reads this file every frame, so a write can collide with it. Written to a temporary file
    # and swapped in, so the mock never reads a half-written pose.
    [IO.File]::WriteAllText("$Lab\mock_head.tmp", $text)
    for ($i = 0; $i -lt 50; $i++) {
        try {
            if (Test-Path "$Lab\mock_head.txt") { [IO.File]::Replace("$Lab\mock_head.tmp", "$Lab\mock_head.txt", [NullString]::Value) } else { [IO.File]::Move("$Lab\mock_head.tmp", "$Lab\mock_head.txt") }
            return
        } catch { Start-Sleep -Milliseconds 20 }
    }
    throw 'Could not update the mock head pose'
}

function Get-EyeDump([string]$Name, [int]$Width = 960) {
    # Asks the mock HMD for its next submitted frame, and saves left and right PNGs plus a side-by-side.
    '' | Set-Content "$Lab\mock_dump.txt" -Encoding ascii
    for ($i = 0; $i -lt 40 -and (Test-Path "$Lab\mock_dump.txt"); $i++) { Start-Sleep -Milliseconds 250 }
    if (Test-Path "$Lab\mock_dump.txt") { throw 'The mock HMD did not produce a frame with layers' }
    Add-Type -AssemblyName System.Drawing
    $l = [Drawing.Image]::FromFile("$Lab\mock_left.bmp"); $r = [Drawing.Image]::FromFile("$Lab\mock_right.bmp")
    $h = [int]($Width * $l.Height / $l.Width)
    $b = New-Object Drawing.Bitmap ($Width * 2), $h
    $g = [Drawing.Graphics]::FromImage($b)
    $g.DrawImage($l, 0, 0, $Width, $h); $g.DrawImage($r, $Width, 0, $Width, $h)
    $b.Save("$Lab\$Name.png", [Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose(); $l.Dispose(); $r.Dispose()
    "$Lab\$Name.png"
}
