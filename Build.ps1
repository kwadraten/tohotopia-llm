param([string]$GameRoot)
$ErrorActionPreference='Stop'
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    throw 'Specify the game directory with -GameRoot "path\to\帝国幻想乡".'
}
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'TOHOTOPIA.exe') -PathType Leaf)) {
    throw "Game directory is invalid or TOHOTOPIA.exe is missing: $GameRoot"
}
$GameRoot=(Resolve-Path -LiteralPath $GameRoot).Path
$projectRoot=$PSScriptRoot
dotnet publish (Join-Path $projectRoot 'AgentHost\AgentHost.csproj') -c Release -o (Join-Path $projectRoot 'dist\AgentHost')
if($LASTEXITCODE -ne 0){throw 'AgentHost build failed'}
dotnet publish (Join-Path $projectRoot 'Settings\Settings.csproj') -c Release -o (Join-Path $projectRoot 'dist\Settings')
if($LASTEXITCODE -ne 0){throw 'Settings build failed'}
dotnet build (Join-Path $projectRoot 'Mod\Mod.csproj') -c Release "/p:GameRoot=$GameRoot" /p:RunAnalyzers=false /p:WarningLevel=0
if($LASTEXITCODE -ne 0){throw 'Mod build failed'}
Write-Output 'All components built.'
