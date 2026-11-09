# Builds the release zip in dist from the release build. Run after building the C# and native projects.
# -Tester names it LanaVR-<version>-tester.zip, for the private test releases.
param([switch]$Tester)
$ErrorActionPreference = 'Stop'
$version = '1.15.0'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
# Everything goes inside a LanaVR folder, so extracting the zip gives the player a folder of its own.
$staging = Join-Path $dist 'stage'
$stage = Join-Path $staging 'LanaVR'
$zip = Join-Path $dist ("LanaVR-$version" + $(if ($Tester) { '-tester' } else { '' }) + '.zip')
foreach ($old in $staging, $zip) { if (Test-Path $old) { Remove-Item $old -Recurse -Force } }
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Force $payload | Out-Null
Expand-Archive (Join-Path $root 'tools\dl\bepinex\bepinex.zip') $payload
$plugin = Join-Path $payload 'BepInEx\plugins\LanaVR'
New-Item -ItemType Directory -Force $plugin | Out-Null
Copy-Item (Join-Path $root 'build\LanaVR.dll'), (Join-Path $root 'native\build\Release\LanaVRNative.dll'), (Join-Path $root 'tools\openxr\native\x64\release\bin\openxr_loader.dll') $plugin
New-Item -ItemType Directory -Force (Join-Path $plugin 'skybox') | Out-Null
# Only images the mapping uses are shipped, so other samples and licensed originals in the folder stay private.
$skies = Join-Path $root 'skybox'
$map = Join-Path $skies 'skyboxes.txt'
$images = Get-Content $map | Where-Object { $_ -match '^\s*[^#\s].*=\s*\S' -and $_ -notmatch '^\s*(Tint|Shade):' } | ForEach-Object { ($_ -split '=', 2)[1].Trim() } | Select-Object -Unique
foreach ($image in $images) { Copy-Item (Join-Path $skies $image) (Join-Path $plugin 'skybox') }
Copy-Item $map (Join-Path $plugin 'skybox')
Copy-Item (Join-Path $root 'install\*') $stage -Recurse
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item $staging -Recurse -Force
Get-Item $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
