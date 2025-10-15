param(
    [string]$SiteName    = "Default Web Site",
    [string]$AppName     = "adquery",
    [string]$AppPoolName = "adquery_pool",
    [string]$TargetPath  = "D:\inetpub\adquery",
    [switch]$Force
)

$ErrorActionPreference = "Stop"
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $scriptRoot "AdQueryOrchestrator.csproj"
$publishDir  = Join-Path $scriptRoot "publish"

Write-Host "AdQuery Orchestrator Deployment" -ForegroundColor Cyan
Write-Host "Site     : $SiteName" -ForegroundColor Gray
Write-Host "App      : /$AppName" -ForegroundColor Gray
Write-Host "App Pool : $AppPoolName" -ForegroundColor Gray
Write-Host "Target   : $TargetPath" -ForegroundColor Gray
Write-Host ""

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Run this script from an elevated PowerShell session."
    exit 1
}

try {
    Import-Module WebAdministration -ErrorAction Stop
} catch {
    Write-Error "Unable to load WebAdministration module: $($_.Exception.Message)"
    exit 1
}

# Build project
Write-Host "Building project..." -ForegroundColor Yellow
if (-not (Test-Path $projectFile)) {
    Write-Error "Cannot locate project file: $projectFile"
    exit 1
}

if (Test-Path $publishDir) {
    Remove-Item $publishDir -Recurse -Force
}

& dotnet publish $projectFile -c Release -o $publishDir | Out-Null
Write-Host "Build completed." -ForegroundColor Green

# Stop the app pool if we have one
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    Write-Host "Stopping app pool '$AppPoolName'..." -ForegroundColor Yellow
    Stop-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
} else {
    Write-Warning "App pool '$AppPoolName' not found."
}

# Kill lingering worker processes so DLLs can be overwritten
[System.Diagnostics.Process]::GetProcessesByName("w3wp") | ForEach-Object {
    Write-Host "Stopping worker process $($_.Id)..." -ForegroundColor Yellow
    try { $_.Kill(); $_.WaitForExit() } catch {}
}

# Prepare target directory (preserve logs folder)
Write-Host "Preparing target directory..." -ForegroundColor Yellow
if (-not (Test-Path $TargetPath)) {
    New-Item -ItemType Directory -Path $TargetPath -Force | Out-Null
} elseif ($Force) {
    [System.IO.Directory]::EnumerateFileSystemEntries($TargetPath) |
        Where-Object { [System.IO.Path]::GetFileName($_) -ne "logs" } |
        ForEach-Object {
            try {
                Remove-Item $_ -Recurse -Force
            } catch {
                Write-Warning "Failed to remove '$_': $($_.Exception.Message)"
            }
        }
}

# Copy files
Write-Host "Copying files..." -ForegroundColor Yellow
robocopy $publishDir $TargetPath /E /COPY:DAT /R:2 /W:2 | Out-Null

# Ensure logs directory exists
$logsPath = Join-Path $TargetPath "logs"
if (-not (Test-Path $logsPath)) {
    New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
}

# Permissions
Write-Host "Setting permissions..." -ForegroundColor Yellow
icacls $TargetPath /grant "IIS_IUSRS:(OI)(CI)RW" /grant "IUSR:(OI)(CI)R" /T | Out-Null

# IIS application
Write-Host "Configuring IIS application..." -ForegroundColor Yellow
$appPath = "IIS:\Sites\$SiteName\$AppName"
if (-not (Test-Path $appPath)) {
    New-WebApplication -Site $SiteName -Name $AppName -PhysicalPath $TargetPath -ApplicationPool $AppPoolName | Out-Null
    Write-Host "Created IIS application '/$AppName'." -ForegroundColor Green
} else {
    Set-ItemProperty $appPath -Name applicationPool -Value $AppPoolName
    Set-ItemProperty $appPath -Name physicalPath   -Value $TargetPath
    Write-Host "Updated IIS application '/$AppName'." -ForegroundColor Green
}

# Reinforce Windows authentication configuration
$appcmd = Join-Path $env:SystemRoot "System32\inetsrv\appcmd.exe"
$configScope = "$SiteName/$AppName"
if (Test-Path $appcmd) {
    Write-Host "Ensuring Windows authentication settings..." -ForegroundColor Yellow
    $commands = @(
        @("set", "config", $configScope, "/section:windowsAuthentication", "/enabled:true", "/commit:apphost"),
        @("set", "config", $configScope, "/section:windowsAuthentication", "/useKernelMode:false", "/commit:apphost"),
        @("set", "config", $configScope, "/section:windowsAuthentication", "/useAppPoolCredentials:true", "/commit:apphost"),
        @("set", "config", $configScope, "/section:anonymousAuthentication", "/enabled:false", "/commit:apphost")
    )

    foreach ($args in $commands) {
        try {
            & $appcmd @args | Out-Null
        } catch {
            Write-Warning "Failed to apply IIS auth setting ($($args -join ' ')): $($_.Exception.Message)"
        }
    }
} else {
    Write-Warning "Unable to locate appcmd.exe; authentication settings were not refreshed."
}

# Start app pool
if (Test-Path "IIS:\AppPools\$AppPoolName") {
    Write-Host "Starting app pool '$AppPoolName'..." -ForegroundColor Yellow
    try {
        Start-WebAppPool -Name $AppPoolName -ErrorAction Stop
        Start-Sleep -Seconds 2
    } catch {
        Write-Warning "Failed to start app pool '$AppPoolName': $($_.Exception.Message)"
    }
}

# Test endpoint
Write-Host "Testing HTTP endpoint..." -ForegroundColor Yellow
Start-Sleep -Seconds 5
$httpUrl = "http://localhost/$AppName"
$httpStatus = $null
$httpError = $null
try {
    $response = Invoke-WebRequest -Uri $httpUrl -UseDefaultCredentials -UseBasicParsing -TimeoutSec 10
    $httpStatus = $response.StatusCode
} catch {
    $httpError = $_.Exception.Message
}

switch ($httpStatus) {
    200 { Write-Host "- Application responded with HTTP 200." -ForegroundColor Green }
    401 { Write-Warning "Application returned HTTP 401 (Unauthorized). Validate via browser." }
    404 { Write-Warning "Application returned HTTP 404 (Not Found). Check static assets and routing." }
    $null { Write-Warning "HTTP validation could not be completed: $httpError" }
    default { Write-Warning "Application responded with HTTP $httpStatus. Please verify manually." }
}

Write-Host ""; Write-Host "Deployment summary" -ForegroundColor Green
Write-Host "-----------------"
Write-Host "App Pool : $AppPoolName"
Write-Host "Site     : $SiteName"
Write-Host "App      : /$AppName"
Write-Host "Physical : $TargetPath"
Write-Host "URL      : $httpUrl"
Write-Host ""; Write-Host "Deployment complete." -ForegroundColor Green
