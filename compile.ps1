#!/usr/bin/env pwsh
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$compiledRoot = Join-Path $root 'compiled'
Remove-Item -Recurse -Force $compiledRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $compiledRoot | Out-Null

dotnet restore "$root/Absynthium_ReservedSlots.sln"

Write-Host '[INFO] Build Absynthium_ReservedSlots...'
dotnet build "$root/Absynthium_ReservedSlots.csproj" -c Release -f net8.0 --nologo

$buildDir = Join-Path $root 'Release/Absynthium_ReservedSlots'
$pluginTarget = Join-Path $compiledRoot 'counterstrikesharp/plugins/Absynthium_ReservedSlots'
Remove-Item -Recurse -Force $pluginTarget -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null

# Ne copier que les DLL nécessaires dans le dossier du plugin
Get-ChildItem -Path $buildDir -Filter '*.dll' | ForEach-Object {
  Copy-Item -Path $_.FullName -Destination $pluginTarget -Force
}
Write-Host "  -> DLL copiees dans $pluginTarget"

$configSource = Join-Path $root 'configs/plugins/Absynthium_ReservedSlots'
$configTarget = Join-Path $compiledRoot 'counterstrikesharp/configs/plugins/Absynthium_ReservedSlots'
if (Test-Path $configSource) {
  Remove-Item -Recurse -Force $configTarget -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Path $configTarget -Force | Out-Null
  Copy-Item -Path (Join-Path $configSource '*') -Destination $configTarget -Recurse -Force
  Write-Host "  -> Configuration copiee dans $configTarget"
}

Write-Host "[OK] Artifacts prets dans $compiledRoot"
