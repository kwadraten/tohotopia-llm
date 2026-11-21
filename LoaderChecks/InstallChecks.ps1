param([string]$LoaderDll = 'C:\Program Files (x86)\Steam\steamapps\workshop\content\3774440\3829555573\0.8.5d\TH1.Hotfix.dll')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$testRoot = Join-Path $repo ('work\loader-install-check-' + [Guid]::NewGuid().ToString('N'))
$game = Join-Path $testRoot 'game'
$mods = Join-Path $testRoot 'Mods'
$legacy = Join-Path $game 'BepInEx\plugins\LLMBattle'
$hotfixDir = Join-Path $game 'TOHOTOPIA_Data\StreamingAssets\HybridCLR\HotfixDlls'
New-Item -ItemType Directory -Path $hotfixDir,(Join-Path $legacy 'runtime'),$mods -Force | Out-Null
Set-Content -LiteralPath (Join-Path $game 'TOHOTOPIA.exe') -Value 'fixture'
Copy-Item -LiteralPath $LoaderDll -Destination (Join-Path $hotfixDir 'TH1.Hotfix.dll.bytes')
Set-Content -LiteralPath (Join-Path $legacy 'Tohotopia.LLMBattle.dll') -Value 'legacy'
Set-Content -LiteralPath (Join-Path $legacy 'runtime\settings.json') -Value '{"maxDecisionsPerTurn":19}'
Set-Content -LiteralPath (Join-Path $legacy 'runtime\accounts.bin') -Value 'account-preservation-fixture'
[IO.File]::WriteAllText((Join-Path $mods 'mods_config.txt'), 'OtherMod')
& (Join-Path $repo 'Install.ps1') -GameRoot $game -ModsRoot $mods
$target = Join-Path $mods 'LLMBattle'
if (-not (Test-Path -LiteralPath (Join-Path $target 'package.build'))) { throw 'Package identity missing' }
if (-not (Test-Path -LiteralPath (Join-Path $legacy 'runtime\accounts.bin'))) { throw 'Legacy accounts lost' }
if (Test-Path -LiteralPath (Join-Path $legacy 'Tohotopia.LLMBattle.dll')) { throw 'Legacy entry still active' }
if ((Get-Content -LiteralPath (Join-Path $mods 'mods_config.txt') | Where-Object { $_ -ceq 'LLMBattle' }).Count -ne 1) { throw 'Enable list invalid' }
& (Join-Path $repo 'Install.ps1') -GameRoot $game -ModsRoot $mods
if ((Get-Content -LiteralPath (Join-Path $mods 'mods_config.txt') | Where-Object { $_ -ceq 'LLMBattle' }).Count -ne 1) { throw 'Duplicate enable entry' }
if (-not (Test-Path -LiteralPath (Join-Path $target 'mod.dll.bak'))) { throw 'Update backup missing' }
if (Test-Path -LiteralPath (Join-Path $target 'install-path.txt')) { throw 'Installer path registration is no longer needed' }
Write-Output 'PASS complete local package, no path registration, legacy data preserved for startup migration, repeated update preserves enable list'
# Keep the fixture for inspection; it contains no real credentials.
Write-Output "Fixture: $testRoot"
