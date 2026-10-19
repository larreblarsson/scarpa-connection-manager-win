# 1. Automatically request Administrator privileges (required for installing certificates)
if (!([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Requesting Administrator privileges to install the certificate..." -ForegroundColor Yellow
    Start-Process PowerShell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs
    exit
}

# 2. Set the working directory to the folder where this script is located
$ScriptDir = Split-Path -Parent$MyInvocation.MyCommand.Definition
Set-Location -Path $ScriptDir

# 3. Find the Certificate and Installer files in the folder
$CertFile = Get-ChildItem -Path$ScriptDir -Filter "*.cer" | Select-Object -First 1
$AppPackage = Get-ChildItem -Path$ScriptDir -Filter "*.msixbundle" | Select-Object -First 1

if (-not $CertFile) { 
    Write-Host "Error: Could not find a .cer certificate file in this folder." -ForegroundColor Red
    Pause; exit 
}
if (-not $AppPackage) { 
    Write-Host "Error: Could not find the .msixbundle installer in this folder." -ForegroundColor Red
    Pause; exit 
}

# 4. Install the certificate into the Local Machine's Trusted People store
Write-Host "Installing certificate: $($CertFile.Name)..." -ForegroundColor Cyan
Import-Certificate -FilePath $CertFile.FullName -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
Write-Host "Certificate successfully added to Trusted Root!" -ForegroundColor Green

# 5. Launch the native Windows App Installer UI
Write-Host "Launching installer for $($AppPackage.Name)..." -ForegroundColor Cyan
Invoke-Item $AppPackage.FullName

Write-Host "You can now click 'Install' in the window that appears." -ForegroundColor Green
Start-Sleep -Seconds 5