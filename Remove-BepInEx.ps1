[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$GameRoot,
    [string]$BackupRoot,
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$appId = '3774440'
function Get-UniquePath([string[]]$Paths) {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($path in $Paths) { $full = [IO.Path]::GetFullPath($path).TrimEnd('\'); if ($seen.Add($full)) { $full } }
}
function Find-GameRoot {
    $steam = Get-UniquePath @(@(
        (Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam' -ErrorAction SilentlyContinue).InstallPath,
        (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    ) | Where-Object { $_ })
    $libraries = Get-UniquePath @(foreach ($root in $steam) {
        $root
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($match in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw -Encoding UTF8), '"path"\s+"([^"]+)"')) { $match.Groups[1].Value.Replace('\\', '\') }
        }
    })
    Get-UniquePath @(foreach ($library in $libraries) {
        $manifest = Join-Path $library "steamapps\appmanifest_$appId.acf"
        if (-not (Test-Path -LiteralPath $manifest)) { continue }
        $text = Get-Content -LiteralPath $manifest -Raw -Encoding UTF8
        if ($text -match '"installdir"\s+"([^"]+)"') {
            $candidate = Join-Path $library ('steamapps\common\' + $Matches[1])
            if (Test-Path -LiteralPath (Join-Path $candidate 'TOHOTOPIA.exe') -PathType Leaf) { $candidate }
        }
    })
}
function Copy-Missing([string]$Source, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
        $target = Join-Path $Destination $item.Name
        if ($item.PSIsContainer) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) { Copy-Missing $item.FullName $target }
        }
        elseif (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $item.FullName -Destination $target }
    }
}

if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $found = @(Find-GameRoot)
    if ($found.Count -ne 1) { throw 'Cannot locate the game. Run again with -GameRoot "path\to\game folder" (the folder containing TOHOTOPIA.exe).' }
    $GameRoot = $found[0]
}
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'TOHOTOPIA.exe') -PathType Leaf)) { throw "Not a game folder (TOHOTOPIA.exe missing): $GameRoot" }
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
if (Get-Process TOHOTOPIA -ErrorAction SilentlyContinue) { throw 'Close the game before removing BepInEx.' }
Write-Output "Game folder: $GameRoot"

$bepinex = Join-Path $GameRoot 'BepInEx'
$config = Join-Path $GameRoot 'doorstop_config.ini'
$targetsBepInEx = (Test-Path -LiteralPath $config) -and ((Get-Content -LiteralPath $config -Raw) -match '(?m)^\s*target_assembly\s*=.*BepInEx')
$items = [Collections.Generic.List[string]]::new()
if (Test-Path -LiteralPath $bepinex) { $items.Add($bepinex) }
foreach ($proxy in 'winhttp.dll', 'version.dll', 'winmm.dll') {
    $path = Join-Path $GameRoot $proxy
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $info = (Get-Item -LiteralPath $path).VersionInfo
    if ("$($info.FileDescription) $($info.ProductName)" -match 'Doorstop|NeighTools|code preloader for Unity') { $items.Add($path) }
    else { Write-Warning "Keeping $proxy (not a Doorstop proxy)." }
}
if (Test-Path -LiteralPath $config) {
    if ($targetsBepInEx) { $items.Add($config) } else { Write-Warning 'Keeping doorstop_config.ini (does not target BepInEx).' }
}
$version = Join-Path $GameRoot '.doorstop_version'
if (Test-Path -LiteralPath $version) { $items.Add($version) }
$dotnet = Join-Path $GameRoot 'dotnet'
if ((Test-Path -LiteralPath (Join-Path $dotnet 'coreclr.dll')) -and ($targetsBepInEx -or (Test-Path -LiteralPath $bepinex))) { $items.Add($dotnet) }
$changelog = Join-Path $GameRoot 'changelog.txt'
if ((Test-Path -LiteralPath $changelog) -and ((@(Get-Content -LiteralPath $changelog -TotalCount 5) -join "`n") -match 'chainloader|Il2CppInterop|Doorstop|BepInEx|Cpp2IL')) { $items.Add($changelog) }

if ($items.Count -eq 0) { Write-Output 'No BepInEx installation found. Nothing to do.'; return }

$plugins = Join-Path $bepinex 'plugins'
$others = @(if (Test-Path -LiteralPath $plugins) { Get-ChildItem -LiteralPath $plugins -Force | Where-Object { $_.Name -ne 'LLMBattle' } | Select-Object -ExpandProperty Name })
if ($others.Count -gt 0 -and -not $Force) {
    throw ("Other BepInEx plugins are installed: " + ($others -join ', ') + ". They will stop working without BepInEx. Run again with -Force to remove anyway.")
}

if ($BackupRoot) {
    $BackupRoot = [IO.Path]::GetFullPath((Join-Path $BackupRoot ('BepInEx-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))))
    if ($BackupRoot.StartsWith($GameRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a backup folder outside the game folder.' }
}

$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TohotopiaLLM\runtime'
foreach ($legacy in @((Join-Path $plugins 'LLMBattle\runtime'))) {
    if (Test-Path -LiteralPath $legacy) {
        if ($PSCmdlet.ShouldProcess($data, "Preserve LLM Battle accounts and settings from $legacy")) {
            Copy-Missing $legacy $data
            Write-Output "Preserved LLM Battle data (existing files kept): $data"
        }
    }
}

foreach ($item in $items) {
    if (-not $PSCmdlet.ShouldProcess($item, $(if ($BackupRoot) { "Move to $BackupRoot" } else { 'Delete' }))) { continue }
    if ($BackupRoot) {
        New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
        Move-Item -LiteralPath $item -Destination (Join-Path $BackupRoot (Split-Path $item -Leaf))
    }
    else { Remove-Item -LiteralPath $item -Recurse -Force }
    Write-Output "Removed: $item"
}
if ($WhatIfPreference) { return }
if ($BackupRoot -and (Test-Path -LiteralPath $BackupRoot)) { Write-Output "Backup: $BackupRoot" }
Write-Output 'BepInEx removed. Use TH1 Mod Loader (Workshop 3829555573) and enable LLM Battle in TH1ModManager.exe.'
Write-Output 'If the game fails to start, use Steam > Properties > Installed Files > Verify integrity.'
