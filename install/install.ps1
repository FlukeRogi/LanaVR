# Installs or removes LanaVR. Run through "Install LanaVR.bat".
param(
    [ValidateSet('Install', 'Uninstall', 'Ask')][string]$Action = 'Ask',
    [string]$GameDir = ''
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Find-Steam {
    # The machine-wide value keeps the folder's capitals. The per-user one is all lower case.
    $steam = (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath
    if (-not $steam) { $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath }
    if (-not $steam) { $steam = 'C:\Program Files (x86)\Steam' }
    $steam = $steam.Replace('/', '\')
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value.Replace('\', '\') }
    }
    foreach ($l in $libraries) {
        $p = Join-Path $l 'steamapps\common\Planet of Lana'
        if (Test-Path (Join-Path $p 'Planet of Lana.exe')) { return $p }
    }
    return ''
}

function Find-Gog {
    # GOG Galaxy records each installed game under its product ID. 1812138501 is Planet of Lana.
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games\1812138501', 'HKLM:\SOFTWARE\GOG.com\Games\1812138501') {
        $p = (Get-ItemProperty $key -ErrorAction SilentlyContinue).path
        if ($p -and (Test-Path (Join-Path $p 'Planet of Lana.exe'))) { return $p.TrimEnd('\') }
    }
    $p = 'C:\Program Files (x86)\GOG Galaxy\Games\Planet of Lana'
    if (Test-Path (Join-Path $p 'Planet of Lana.exe')) { return $p }
    return ''
}

function Test-Modded([string]$Dir) { Test-Path (Join-Path $Dir 'BepInEx\plugins\LanaVR\LanaVR.dll') }

Write-Host 'Welcome to the Planet of Lana virtual screen VR mod by FlukeRogi' -ForegroundColor Cyan
Write-Host ''
$store = ''
if (-not $GameDir) {
    $found = @()
    $p = Find-Steam; if ($p) { $found += [pscustomobject]@{ Store = 'Steam'; Path = $p } }
    $p = Find-Gog; if ($p) { $found += [pscustomobject]@{ Store = 'GOG'; Path = $p } }
    if ($found.Count -eq 1) { $GameDir = $found[0].Path; $store = $found[0].Store }
    elseif ($found.Count -gt 1) {
        Write-Host 'Planet of Lana is installed from more than one store:'
        for ($i = 0; $i -lt $found.Count; $i++) {
            Write-Host ("  {0}  {1,-6}" -f ($i + 1), $found[$i].Store) -NoNewline -ForegroundColor White
            Write-Host $found[$i].Path -NoNewline -ForegroundColor Gray
            if (Test-Modded $found[$i].Path) { Write-Host '  (LanaVR installed)' -ForegroundColor Green } else { Write-Host '' }
        }
        Write-Host ''
        $c = Read-Host ('Type the number of the version to use (1-{0})' -f $found.Count)
        Write-Host ''
        $n = 0
        if (-not [int]::TryParse($c, [ref]$n) -or $n -lt 1 -or $n -gt $found.Count) { throw 'Nothing was changed.' }
        $GameDir = $found[$n - 1].Path; $store = $found[$n - 1].Store
    }
}
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'Planet of Lana.exe'))) {
    Write-Host 'Could not find Planet of Lana from Steam or GOG.' -ForegroundColor Red
    $GameDir = Read-Host 'Paste the game folder path (the one containing "Planet of Lana.exe")'
    if (-not (Test-Path (Join-Path $GameDir 'Planet of Lana.exe'))) { throw "Planet of Lana.exe is not in: $GameDir" }
}
if (-not $store) { $store = if (Test-Path (Join-Path $GameDir 'GalaxyConfig.json')) { 'GOG' } elseif ($GameDir -match '\steamapps\') { 'Steam' } else { '' } }
Write-Host "Game folder: " -NoNewline -ForegroundColor Gray; Write-Host $GameDir -NoNewline -ForegroundColor White
if ($store) { Write-Host " ($store)" -ForegroundColor Gray } else { Write-Host '' }
Write-Host ''

if (Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue) { throw 'Please close Planet of Lana first.' }

if ($Action -eq 'Ask') {
    $c = Read-Host 'Type 1 to install LanaVR, or 2 to uninstall it'
    Write-Host ''
    $Action = switch ($c) { '1' { 'Install' } '2' { 'Uninstall' } default { throw 'Nothing was changed.' } }
}

if ($Action -eq 'Install') {
    Copy-Item (Join-Path $here 'payload\*') $GameDir -Recurse -Force
    # The developer test mode blocks saves and Steam achievements, so a player's install must never have it on.
    $cfg = Join-Path $GameDir 'BepInEx\config\com.lanavr.planetoflana.cfg'
    if (Test-Path $cfg) {
        $text = Get-Content $cfg
        $fixed = $text -replace '^LabCommands = true', 'LabCommands = false'
        if (($fixed -join "`n") -ne ($text -join "`n")) { Set-Content $cfg $fixed -Encoding utf8; Write-Host 'Switched off the developer test mode, which blocks saves and achievements.'; Write-Host '' }
    }
    # A slightly faster start: no BepInEx console window (the log file is still written), and BepInEx
    # loads the game's interop assemblies when they are used instead of all of them up front.
    $bep = Join-Path $GameDir 'BepInEx\config\BepInEx.cfg'
    if (Test-Path $bep) {
        $t = Get-Content $bep -Raw
        $t = [regex]::Replace($t, '(?s)(\[Logging\.Console\].*?\r?\nEnabled = )\w+', '${1}false')
        $t = [regex]::Replace($t, '(?m)^PreloadIL2CPPInteropAssemblies = \w+', 'PreloadIL2CPPInteropAssemblies = false')
        Set-Content $bep $t -Encoding utf8 -NoNewline
    } else {
        # BepInEx fills in every other setting with its defaults on the first start.
        New-Item -ItemType Directory -Force (Split-Path $bep) | Out-Null
        "[IL2CPP]`r`n`r`nPreloadIL2CPPInteropAssemblies = false`r`n`r`n[Logging.Console]`r`n`r`nEnabled = false`r`n" | Set-Content $bep -Encoding utf8 -NoNewline
    }
    $launcher = switch ($store) { 'Steam' { 'Steam' } 'GOG' { 'GOG Galaxy' } default { 'Steam or GOG Galaxy' } }
    Write-Host "LanaVR is installed. Start Planet of Lana from $launcher." -ForegroundColor Green
    Write-Host ''
    Write-Host 'The first start may take up to a minute or two depending on your PC.' -ForegroundColor Yellow
    Write-Host 'This is a first time only setup - loading again afterwards will be fast.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Please refer to the README.txt for further mod information.' -ForegroundColor Gray
    return
}

if (-not (Test-Modded $GameDir) -and -not (Test-Path (Join-Path $GameDir 'BepInEx'))) {
    Write-Host 'LanaVR is not installed in this copy of the game, so nothing was changed.' -ForegroundColor Yellow
    return
}

# Uninstall: the mod's own files always go. The BepInEx loader stays if other mods are using it.
$plugins = Join-Path $GameDir 'BepInEx\plugins'
if (Test-Path (Join-Path $plugins 'LanaVR')) { Remove-Item (Join-Path $plugins 'LanaVR') -Recurse -Force }
$cfg = Join-Path $GameDir 'BepInEx\config\com.lanavr.planetoflana.cfg'
if (Test-Path $cfg) { Remove-Item $cfg -Force }
$others = @(Get-ChildItem $plugins -ErrorAction SilentlyContinue)
if ($others.Count -gt 0) {
    Write-Host "LanaVR is removed. BepInEx stays, because other mods are in $plugins." -ForegroundColor Yellow
    return
}
foreach ($item in 'BepInEx', 'dotnet', 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'changelog.txt') {
    $p = Join-Path $GameDir $item
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
Write-Host 'LanaVR and BepInEx are removed, and the game is back to its original files.' -ForegroundColor Green
