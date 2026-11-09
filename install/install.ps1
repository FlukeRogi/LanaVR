# Installs or removes LanaVR. Run through "Install LanaVR.bat".
param(
    [ValidateSet('Install', 'Uninstall', 'Ask')][string]$Action = 'Ask',
    [string]$GameDir = ''
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Find-Game {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { $steam = 'C:\Program Files (x86)\Steam' }
    $libraries = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libraries += $m.Groups[1].Value.Replace('\\', '\') }
    }
    foreach ($l in $libraries) {
        $p = Join-Path $l 'steamapps\common\Planet of Lana'
        if (Test-Path (Join-Path $p 'Planet of Lana.exe')) { return $p }
    }
    return ''
}

Write-Host 'Welcome to the Planet of Lana virtual screen VR mod by FlukeRogi' -ForegroundColor Cyan
Write-Host ''
if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'Planet of Lana.exe'))) {
    Write-Host 'Could not find Planet of Lana in your Steam libraries.' -ForegroundColor Red
    $GameDir = Read-Host 'Paste the game folder path (the one containing "Planet of Lana.exe")'
    if (-not (Test-Path (Join-Path $GameDir 'Planet of Lana.exe'))) { throw "Planet of Lana.exe is not in: $GameDir" }
}
Write-Host "Game folder: " -NoNewline -ForegroundColor Gray; Write-Host $GameDir -ForegroundColor White
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
    Write-Host 'LanaVR is installed. Start Planet of Lana from Steam.' -ForegroundColor Green
    Write-Host ''
    Write-Host 'The first start may take up to a minute or two depending on your PC.' -ForegroundColor Yellow
    Write-Host 'This is a first time only setup - loading again afterwards will be fast.' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Please refer to the README.txt for further mod information.' -ForegroundColor Gray
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
