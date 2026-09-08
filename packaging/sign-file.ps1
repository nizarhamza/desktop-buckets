<#
Authenticode-signs a file (the Inno Setup installer, the MSIX package) with the
project certificate, with an RFC 3161 timestamp so the signature stays valid after
the certificate expires. Falls back to an un-timestamped signature if every
timestamp server is unreachable, and verifies the result.
#>
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$PfxPath = "$PSScriptRoot\DesktopBuckets-Dev.pfx",
    [Parameter(Mandatory = $true)][string]$PfxPassword,
    [string[]]$TimestampUrls = @('http://timestamp.digicert.com', 'http://timestamp.sectigo.com')
)

$ErrorActionPreference = "Stop"

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

$signtool = Find-SdkTool 'signtool.exe'
if (-not (Test-Path $Path)) { throw "Nothing to sign at $Path" }

$signed = $false
foreach ($ts in $TimestampUrls) {
    & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword /tr $ts /td SHA256 $Path
    if ($LASTEXITCODE -eq 0) { $signed = $true; break }
    Write-Warning "signtool with timestamp server $ts failed ($LASTEXITCODE); trying the next."
}
if (-not $signed) {
    Write-Warning "No timestamp server reachable; signing without a timestamp."
    & $signtool sign /fd SHA256 /f $PfxPath /p $PfxPassword $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
}

# /pa = Authenticode policy; the dev cert is self-signed so a chain error is expected,
# but the signature itself must be present and intact.
$sig = Get-AuthenticodeSignature $Path
if ($sig.Status -notin @('Valid', 'UnknownError', 'NotTrusted')) { throw "Signature check failed: $($sig.Status) $($sig.StatusMessage)" }
if (-not $sig.SignerCertificate) { throw "No signer certificate on $Path after signing." }
Write-Host "Signed $Path with $($sig.SignerCertificate.Thumbprint)"
