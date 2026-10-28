#Requires -RunAsAdministrator

$ErrorActionPreference = 'Stop'

$serviceName = 'MideaDehumidifier'
$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if (-not $service) {
    Write-Host "Service '$serviceName' is not installed."
    exit 0
}

if ($service.Status -ne 'Stopped') {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

& sc.exe delete $serviceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Could not remove service '$serviceName'."
}

Write-Host "Service '$serviceName' was removed. Published files and the database in ProgramData were left in place."
