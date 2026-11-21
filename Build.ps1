param([string]$GameRoot)
$ErrorActionPreference = 'Stop'
# GameRoot remains accepted for older developer commands. The build needs no game files.
$stage = Join-Path $PSScriptRoot ('work\build-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'LLMBattle'
New-Item -ItemType Directory -Path $package -Force | Out-Null
foreach ($component in @('AgentHost', 'Settings')) {
    dotnet publish (Join-Path $PSScriptRoot "$component\$component.csproj") -c Release -r win-x64 --self-contained true -p:PublishAot=true -p:PublishTrimmed=true -p:DebugType=none -o (Join-Path $package $component)
    if ($LASTEXITCODE -ne 0) { throw "$component build failed" }
}
dotnet build (Join-Path $PSScriptRoot 'Mod\Mod.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Mod build failed' }
$output = Join-Path $PSScriptRoot 'Mod\bin\Release\netstandard2.1'
Copy-Item -LiteralPath (Join-Path $output 'Tohotopia.LLMBattle.dll') -Destination (Join-Path $package 'mod.dll')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mod\README.txt'), (Join-Path $PSScriptRoot 'Remove-BepInEx.ps1'), (Join-Path $PSScriptRoot 'Remove-BepInEx.bat') -Destination $package
Copy-Item -LiteralPath (Join-Path $output 'package.build'), (Join-Path $PSScriptRoot 'Mod\mod_loader.txt'), (Join-Path $PSScriptRoot 'Mod\mod_info.json'), (Join-Path $PSScriptRoot 'settings.example.json'), (Join-Path $PSScriptRoot 'LICENSE'), (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt') -Destination $package
$dist = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$target = [IO.Path]::GetFullPath((Join-Path $dist 'LLMBattle'))
# Replace only this known output folder; keep the previous package recoverable in work/.
if (-not $target.StartsWith([IO.Path]::GetFullPath($dist) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid package output path' }
if (Test-Path -LiteralPath $target) { Move-Item -LiteralPath $target -Destination (Join-Path $stage 'previous-package') }
Move-Item -LiteralPath $package -Destination $target
Write-Output "Workshop-ready content folder: $target"
Write-Output 'Includes NativeAOT win-x64 host and settings executables. No installer or separate .NET installation is required.'
