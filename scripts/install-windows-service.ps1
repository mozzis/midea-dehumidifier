#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$serviceName = 'MideaDehumidifier'
$installPath = Join-Path $env:ProgramFiles 'MideaDehumidifier\Api'
$dataPath = Join-Path $env:ProgramData 'MideaDehumidifier'
$executablePath = Join-Path $installPath 'MideaDehumidifier.Api.exe'

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service '$serviceName' already exists. Stop and remove it before reinstalling."
}

# Only needed until the device token/key has been fetched and cached in the database.
$cloudAccount = [Environment]::GetEnvironmentVariable('Midea__CloudAccount', 'Machine')
$cloudPassword = [Environment]::GetEnvironmentVariable('Midea__CloudPassword', 'Machine')
$cloudKind = [Environment]::GetEnvironmentVariable('Midea__Cloud', 'Machine')

New-Item -ItemType Directory -Path $installPath, $dataPath -Force | Out-Null
dotnet publish (Join-Path $PSScriptRoot '..\src\MideaDehumidifier.Api\MideaDehumidifier.Api.csproj') -c Release -r win-x64 --self-contained false -o $installPath
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed; the Windows service was not installed.'
}

New-Service `
    -Name $serviceName `
    -DisplayName 'Midea Dehumidifier API' `
    -Description 'Polls the Midea dehumidifier over the LAN, stores humidity history in SQLite and serves the plot.' `
    -BinaryPathName "`"$executablePath`"" `
    -StartupType Automatic | Out-Null

$environment = @(
    'ASPNETCORE_ENVIRONMENT=Production',
    'DOTNET_ENVIRONMENT=Production',
    "Midea__DatabasePath=$(Join-Path $dataPath 'midea.db')"
)
if ($cloudAccount) { $environment += "Midea__CloudAccount=$cloudAccount" }
if ($cloudPassword) { $environment += "Midea__CloudPassword=$cloudPassword" }
if ($cloudKind) { $environment += "Midea__Cloud=$cloudKind" }

New-ItemProperty `
    -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" `
    -Name Environment `
    -PropertyType MultiString `
    -Value $environment `
    -Force | Out-Null

& sc.exe failure $serviceName 'reset=' '86400' 'actions=' 'restart/5000/restart/15000/restart/60000' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Could not configure service recovery actions.'
}

Start-Service -Name $serviceName
(Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Write-Host "Service '$serviceName' is running and configured to start automatically."
Write-Host "Data is stored in $dataPath. Check http://localhost:5090/api/health to confirm the API is responding."
