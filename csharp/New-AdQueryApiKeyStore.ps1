<#
.SYNOPSIS
  Writes the Claude API key to a DPAPI-encrypted store outside the web root, so
  redeploying the app can never wipe the secret (F03).

.DESCRIPTION
  Encrypts the supplied key with Windows DPAPI at LocalMachine scope and writes it
  to $Path (default C:\ProgramData\ADQuery\claude-apikey.dat). LocalMachine scope
  lets any process on this server — including the IIS app-pool identity — decrypt
  it, so it does not matter whether you run this as yourself or as the app-pool
  account. The path is outside D:\inetpub\adquery, so no deploy touches it.

  Run this ONCE per server (and again only to rotate the key). The app reads the
  key from this store automatically when Claude:ApiKey in appsettings.json is
  blank (the shipped default).

.EXAMPLE
  # Prompts for the key without echoing it to the screen or shell history:
  .\New-AdQueryApiKeyStore.ps1

.EXAMPLE
  .\New-AdQueryApiKeyStore.ps1 -Path 'D:\secrets\claude-apikey.dat'
#>
[CmdletBinding()]
param(
    [string]$Path = 'C:\ProgramData\ADQuery\claude-apikey.dat'
)

$ErrorActionPreference = 'Stop'

$secure = Read-Host -AsSecureString -Prompt 'Enter the Claude API key'
$bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $plain = [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

if ([string]::IsNullOrWhiteSpace($plain)) {
    Write-Error 'No key entered; nothing written.'
    exit 1
}

Add-Type -AssemblyName System.Security

$directory = Split-Path -Parent $Path
if ($directory -and -not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$bytes = [System.Text.Encoding]::UTF8.GetBytes($plain)
$protected = [System.Security.Cryptography.ProtectedData]::Protect(
    $bytes,
    $null,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)

[System.IO.File]::WriteAllBytes($Path, $protected)

# Scrub the plaintext from memory as best PowerShell allows.
$plain = $null
[System.Array]::Clear($bytes, 0, $bytes.Length)

Write-Host "Wrote DPAPI-encrypted API key to: $Path" -ForegroundColor Green
Write-Host 'The app will use it automatically while Claude:ApiKey in appsettings.json is blank.' -ForegroundColor Gray
