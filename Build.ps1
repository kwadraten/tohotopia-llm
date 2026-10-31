param([string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\帝国幻想乡')
$ErrorActionPreference='Stop'
$projectRoot=$PSScriptRoot
dotnet publish (Join-Path $projectRoot 'AgentHost\AgentHost.csproj') -c Release -o (Join-Path $projectRoot 'dist\AgentHost')
if($LASTEXITCODE -ne 0){throw 'AgentHost build failed'}
dotnet publish (Join-Path $projectRoot 'Settings\Settings.csproj') -c Release -o (Join-Path $projectRoot 'dist\Settings')
if($LASTEXITCODE -ne 0){throw 'Settings build failed'}
dotnet build (Join-Path $projectRoot 'Mod\Mod.csproj') -c Release /p:RunAnalyzers=false /p:WarningLevel=0
if($LASTEXITCODE -ne 0){throw 'Mod build failed'}
Write-Output 'All components built.'
