param(
    [Parameter(Mandatory)] [string] $Thumbprint,
    [Parameter(Mandatory)] [string[]] $Path
)

# A parent PowerShell 7 session can leave module paths that break the Security module in 5.1.
$env:PSModulePath = "$env:windir\System32\WindowsPowerShell\v1.0\Modules"
Import-Module Microsoft.PowerShell.Security

$cert = $null
foreach ($loc in 'CurrentUser', 'LocalMachine') {
    $store = [System.Security.Cryptography.X509Certificates.X509Store]::new('My', $loc)
    $store.Open('ReadOnly')
    $cert = $store.Certificates.Find('FindByThumbprint', $Thumbprint, $false) | Select-Object -First 1
    $store.Close()
    if ($cert) { break }
}
if (-not $cert) { Write-Error "Certificate $Thumbprint not found"; exit 1 }

foreach ($p in ($Path | ForEach-Object { $_ -split ',' })) {
    if (-not (Test-Path $p)) { continue }
    $r = Set-AuthenticodeSignature -FilePath $p -Certificate $cert
    if (-not $r.SignerCertificate) { Write-Error "Failed to sign ${p}: $($r.StatusMessage)"; exit 1 }
}
