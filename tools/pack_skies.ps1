# Packs the sky images that skybox\skyboxes.txt lists as .lvsky files into skybox, from the purchased originals.
# A packed sky is the original image encrypted with AES-256-CBC: the header "LVSKY2`r`n", a random 16-byte IV, then
# the ciphertext. The key is in native\SkyKey.h, which is made here the first time and never goes in the repository.
# The native loader in Skybox.cpp is built with the same key, so rebuild it after a new key is made.
param([string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'Fantasy Skybox'))
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$skies = Join-Path $root 'skybox'
$keyFile = Join-Path $root 'native\SkyKey.h'
if (-not (Test-Path $keyFile)) {
    $new = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($new)
    $hex = ($new | ForEach-Object { '0x{0:x2}' -f $_ }) -join ', '
    "// Private key for the packed skies. Keep this file out of the repository.`r`n#pragma once`r`n#include <cstdint>`r`nstatic const uint8_t kSkyKey[32] = { $hex };`r`n" | Set-Content $keyFile -Encoding ascii -NoNewline
    "Made a new key in $keyFile. Rebuild the native project."
}
$key = [byte[]]([regex]::Matches((Get-Content $keyFile -Raw), '0x([0-9a-f]{2})') | ForEach-Object { [Convert]::ToByte($_.Groups[1].Value, 16) })
if ($key.Length -ne 32) { throw "$keyFile doesn't hold a 32-byte key" }
$magic = [Text.Encoding]::ASCII.GetBytes("LVSKY2`r`n")
$names = Get-Content (Join-Path $skies 'skyboxes.txt') | Where-Object { $_ -match '^\s*[^#\s].*=\s*\S+\.lvsky\s*$' -and $_ -notmatch '^\s*(Tint|Shade):' } | ForEach-Object { ($_ -split '=', 2)[1].Trim() } | Select-Object -Unique
$originals = Get-ChildItem $Source -Recurse -File
$total = 0
foreach ($name in $names) {
    $base = [IO.Path]::GetFileNameWithoutExtension($name)
    $original = $originals | Where-Object BaseName -eq $base | Select-Object -First 1
    if (-not $original) { throw "$base isn't in $Source" }
    $bytes = [IO.File]::ReadAllBytes($original.FullName)
    $aes = [Security.Cryptography.Aes]::Create()
    $aes.Key = $key; $aes.Mode = 'CBC'; $aes.Padding = 'PKCS7'; $aes.GenerateIV()
    $cipher = $aes.CreateEncryptor().TransformFinalBlock($bytes, 0, $bytes.Length)
    $out = [IO.File]::Create((Join-Path $skies $name))
    $out.Write($magic, 0, $magic.Length); $out.Write($aes.IV, 0, 16); $out.Write($cipher, 0, $cipher.Length); $out.Dispose()
    $aes.Dispose()
    $total += $cipher.Length + 24
    '{0} {1:N1} MB' -f $name, ($bytes.Length / 1MB)
}
'{0} skies, {1:N0} MB' -f @($names).Count, ($total / 1MB)
