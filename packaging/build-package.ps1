<#
Builds and signs the sparse MSIX package DesktopBuckets.Package.msix.

The package contains only AppxManifest.xml + Images\. It is installed with
  Add-AppxPackage -Path DesktopBuckets.Package.msix -ExternalLocation <app dir>
so the real binaries (DesktopBuckets.exe, DesktopBuckets.ShellExt.dll) are
resolved from <app dir> at runtime.
#>
param(
    [string]$Version = "0.1.0.0",
    [string]$PfxPath = "$PSScriptRoot\DesktopBuckets-Dev.pfx",
    [Parameter(Mandatory = $true)][string]$PfxPassword,
    [string]$OutDir = "$PSScriptRoot\out"
)

$ErrorActionPreference = "Stop"

# Normalise version to 4 parts (MSIX requires Major.Minor.Build.Revision).
$p = $Version.Split('.')
while ($p.Count -lt 4) { $p += '0' }
$Version = ($p[0..3] -join '.')

function Find-SdkTool([string]$name) {
    if (Get-Command $name -ErrorAction SilentlyContinue) { return (Get-Command $name).Source }
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "${env:ProgramFiles}\Windows Kits\10\bin")
    foreach ($r in $roots) {
        if (-not (Test-Path $r)) { continue }
        $hit = Get-ChildItem $r -Recurse -Filter $name -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw "Could not find $name in the Windows SDK."
}

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'
Write-Host "makeappx: $makeappx"
Write-Host "signtool: $signtool"

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$layout = Join-Path $OutDir 'layout'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force -Path $layout | Out-Null

& "$PSScriptRoot\make-logos.ps1" -OutDir (Join-Path $layout 'Images') | Out-Null

(Get-Content "$PSScriptRoot\AppxManifest.xml" -Raw).Replace('__VERSION__', $Version) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding UTF8

$msix = Join-Path $OutDir 'DesktopBuckets.Package.msix'
& $makeappx pack /d $layout /p $msix /o /nv
if ($LASTEXITCODE -ne 0) { throw "makeappx failed ($LASTEXITCODE)" }

& "$PSScriptRoot\sign-file.ps1" -Path $msix -PfxPath $PfxPath -PfxPassword $PfxPassword

Write-Host "`nBuilt + signed: $msix  (Identity Version $Version)"
