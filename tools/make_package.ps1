# Builds the release zip in dist from the release build. Run after building the C# and native projects.
# -Tester builds LanaVR-<version>-tester.zip instead, using the placeholder skies from make_tester_skies.ps1.
param([switch]$Tester)
$ErrorActionPreference = 'Stop'
$version = '1.7.0'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$stage = Join-Path $dist 'stage'
$zip = Join-Path $dist ("LanaVR-$version" + $(if ($Tester) { '-tester' } else { '' }) + '.zip')
foreach ($old in $stage, $zip) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force $payload | Out-Null
Expand-Archive (Join-Path $root 'tools\dl\bepinex\bepinex.zip') $payload
$plugin = Join-Path $payload 'BepInEx\plugins\LanaVR'
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item (Join-Path $root 'build\LanaVR.dll'), (Join-Path $root 'native\build\Release\LanaVRNative.dll'), (Join-Path $root 'tools\openxr\native\x64\release\bin\openxr_loader.dll') $plugin
New-Item -ItemType Directory -Force (Join-Path $plugin 'skybox') | Out-Null
# Only images the mapping uses are shipped, so samples and licensed originals in the folder stay private.
$skies = if ($Tester) { Join-Path $root 'skybox\tester' } else { Join-Path $root 'skybox' }
$map = Join-Path $skies 'skyboxes.txt'
$images = Get-Content $map | Where-Object { $_ -match '^\s*[^#\s].*=\s*\S' -and $_ -notmatch '^\s*Tint:' } | ForEach-Object { ($_ -split '=', 2)[1].Trim() } | Select-Object -Unique
foreach ($image in $images) { Copy-Item (Join-Path $skies $image) (Join-Path $plugin 'skybox') }
Copy-Item $map (Join-Path $plugin 'skybox')
Copy-Item (Join-Path $root 'install\*') $stage
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Get-Item $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
