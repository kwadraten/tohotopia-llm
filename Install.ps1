param(
    [string]$GameRoot,
    [string]$ModsRoot
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $candidates = @(@(
        foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
            foreach ($steamPath in @('Program Files (x86)\Steam', 'Program Files\Steam', 'Steam', 'SteamLibrary')) {
                $candidate = Join-Path $drive.Root (Join-Path $steamPath 'steamapps\common\帝国幻想乡')
                if (Test-Path -LiteralPath (Join-Path $candidate 'TOHOTOPIA.exe') -PathType Leaf) { $candidate }
            }
        }
    ) | Select-Object -Unique)
    if ($candidates.Count -ne 1) { throw 'Specify the game directory with -GameRoot "path\to\帝国幻想乡".' }
    $GameRoot = $candidates[0]
}
if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'TOHOTOPIA.exe') -PathType Leaf)) { throw "Invalid game directory: $GameRoot" }
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
if (Get-Process TOHOTOPIA -ErrorAction SilentlyContinue) { throw 'Close the game before installing.' }
$hotfix = Join-Path $GameRoot 'TOHOTOPIA_Data\StreamingAssets\HybridCLR\HotfixDlls\TH1.Hotfix.dll.bytes'
if (-not (Test-Path -LiteralPath $hotfix -PathType Leaf) -or
    -not [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($hotfix)).Contains('SetMethodOverride')) {
    throw 'Install the current TH1 Mod Loader with TH1ModManager.exe first (Workshop item 3829555573).'
}
if ([string]::IsNullOrWhiteSpace($ModsRoot)) {
    $info = @(Get-Content -LiteralPath (Join-Path $GameRoot 'TOHOTOPIA_Data\app.info'))
    if ($info.Count -lt 2) { throw 'Cannot determine Unity persistentDataPath. Specify -ModsRoot.' }
    $company = $info[0].Trim().Replace('.', '_')
    $product = $info[1].Trim().Replace('.', '_')
    $ModsRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) "AppData\LocalLow\$company\$product\Mods"
}
# Developer convenience only. Workshop subscribers do not run this script.
$source = Join-Path $PSScriptRoot 'dist\LLMBattle'
foreach ($file in @('mod.dll', 'mod_loader.txt', 'mod_info.json', 'package.build', 'settings.example.json', 'AgentHost\AgentHost.exe', 'Settings\TH1LLMBattlePanel.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $file) -PathType Leaf)) { throw "Missing $file. Run Build.ps1 first." }
}
$target = [IO.Path]::GetFullPath((Join-Path $ModsRoot 'LLMBattle'))
New-Item -ItemType Directory -Path $target -Force | Out-Null
if (Test-Path -LiteralPath (Join-Path $target 'mod.dll')) { Copy-Item -LiteralPath (Join-Path $target 'mod.dll') -Destination (Join-Path $target 'mod.dll.bak') -Force }
Get-ChildItem -LiteralPath $source | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $target -Recurse -Force }
# Drop the previous panel executable name if still installed.
Remove-Item -LiteralPath (Join-Path $target 'Settings\Settings.exe') -Force -ErrorAction SilentlyContinue
$config = Join-Path $ModsRoot 'mods_config.txt'
if (Test-Path -LiteralPath $config) {
    $enabled = @(Get-Content -LiteralPath $config | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($enabled -cnotcontains 'LLMBattle') { [IO.File]::AppendAllText($config, "`r`nLLMBattle`r`n") }
}
$oldDll = Join-Path $GameRoot 'BepInEx\plugins\LLMBattle\Tohotopia.LLMBattle.dll'
if (Test-Path -LiteralPath $oldDll) { Move-Item -LiteralPath $oldDll -Destination ($oldDll + '.disabled-' + [Guid]::NewGuid().ToString('N')) }
# Desktop entry point: the panel runs standalone and on demand, so the mod
# itself stays fully headless.
$desktop = [Environment]::GetFolderPath('Desktop')
$linkPath = Join-Path $desktop 'TOHOTOPIA大模型AI控制面板.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($linkPath)
$link.TargetPath = Join-Path $target 'Settings\TH1LLMBattlePanel.exe'
$link.WorkingDirectory = Join-Path $target 'Settings'
$link.IconLocation = Join-Path $target 'Settings\TH1LLMBattlePanel.exe,0'
$link.Description = 'TOHOTOPIA AI 接管设置与账号管理'
$link.Save()
# Remove the previous shortcut name if present.
Remove-Item -LiteralPath (Join-Path $desktop 'TOHOTOPIA AI 设置.lnk') -Force -ErrorAction SilentlyContinue
Write-Output "Local development mod: $target"
Write-Output "Desktop shortcut created: $linkPath"
Write-Output 'The loader will initialize user data and migrate legacy settings on first startup.'
