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

if (-not $GameDir) { $GameDir = Find-Game }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'Planet of Lana.exe'))) {
    Write-Host 'Could not find Planet of Lana in your Steam libraries.' -ForegroundColor Red
    $GameDir = Read-Host 'Paste the game folder path (the one containing "Planet of Lana.exe")'
    if (-not (Test-Path (Join-Path $GameDir 'Planet of Lana.exe'))) { throw "Planet of Lana.exe is not in: $GameDir" }
}
Write-Host "Game folder: $GameDir"

if (Get-Process 'Planet of Lana' -ErrorAction SilentlyContinue) { throw 'Please close Planet of Lana first.' }

if ($Action -eq 'Ask') {
    $c = Read-Host 'Type 1 to install LanaVR, or 2 to uninstall it'
    $Action = switch ($c) { '1' { 'Install' } '2' { 'Uninstall' } default { throw 'Nothing was changed.' } }
}

if ($Action -eq 'Install') {
    Copy-Item (Join-Path $here 'payload\*') $GameDir -Recurse -Force
    Write-Host ''
    Write-Host 'LanaVR is installed. Start Planet of Lana from Steam.' -ForegroundColor Green
    Write-Host 'The first start takes a minute or two while BepInEx prepares the game.'
    Write-Host 'To choose SteamVR or Virtual Desktop, see README.txt.'
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
