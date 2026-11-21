$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot
$script = Join-Path $repo 'Remove-BepInEx.ps1'
$root = Join-Path $repo ('work\remove-bepinex-check-' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TohotopiaLLM\runtime'
$marker = 'remove-bepinex-check-' + [Guid]::NewGuid().ToString('N') + '.json'
function Check([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message }; Write-Output "PASS $Message" }
function Invoke-Script([string[]]$Arguments) {
    $ErrorActionPreference = 'Continue'
    (& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script @Arguments 2>&1 | ForEach-Object { "$_" }) -join "`n"
}
$proxies = @{}
function Get-Proxy([string]$Product) {
    if (-not $proxies.ContainsKey($Product)) {
        $source = Join-Path $root ('proxy-' + $proxies.Count + '.cs')
        $dll = [IO.Path]::ChangeExtension($source, '.dll')
        Set-Content -LiteralPath $source -Value "[assembly: System.Reflection.AssemblyTitle(`"$Product`")][assembly: System.Reflection.AssemblyProduct(`"$Product`")] class P {}"
        $csc = Join-Path ([Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()) 'csc.exe'
        if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe' }
        & $csc /nologo /target:library "/out:$dll" $source | Out-Null
        if (-not (Test-Path -LiteralPath $dll)) { throw 'Cannot build the proxy fixture' }
        $proxies[$Product] = $dll
    }
    $proxies[$Product]
}
function New-Game([string]$Name, [string]$ProxyDescription = 'NeighTools') {
    $game = Join-Path $root $Name
    New-Item -ItemType Directory -Path (Join-Path $game 'BepInEx\plugins\LLMBattle\runtime'), (Join-Path $game 'BepInEx\core'), (Join-Path $game 'dotnet') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $game 'TOHOTOPIA.exe') -Value 'game'
    Set-Content -LiteralPath (Join-Path $game 'GameAssembly.dll') -Value 'game'
    Set-Content -LiteralPath (Join-Path $game 'doorstop_config.ini') -Value "[General]`nenabled = true`ntarget_assembly = BepInEx\core\BepInEx.Unity.IL2CPP.dll"
    Set-Content -LiteralPath (Join-Path $game '.doorstop_version') -Value '4.5.0'
    Set-Content -LiteralPath (Join-Path $game 'changelog.txt') -Value "74 changes since v6.0.0-pre.2`n* Il2CppInterop bump"
    Set-Content -LiteralPath (Join-Path $game 'dotnet\coreclr.dll') -Value 'runtime'
    Set-Content -LiteralPath (Join-Path $game "BepInEx\plugins\LLMBattle\runtime\$marker") -Value '{"legacy":true}'
    Copy-Item -LiteralPath (Get-Proxy $ProxyDescription) -Destination (Join-Path $game 'winhttp.dll')
    $game
}
New-Item -ItemType Directory -Path $root -Force | Out-Null
try {
    $game = New-Game 'game'
    New-Item -ItemType Directory -Path (Join-Path $game 'BepInEx\plugins\OtherPlugin') -Force | Out-Null
    $output = Invoke-Script @('-GameRoot', $game)
    Check ($output -match 'OtherPlugin' -and (Test-Path -LiteralPath (Join-Path $game 'BepInEx'))) 'other BepInEx plugins block removal until -Force'

    $output = Invoke-Script @('-GameRoot', $game, '-Force', '-WhatIf')
    Check ((Test-Path -LiteralPath (Join-Path $game 'BepInEx')) -and -not (Test-Path -LiteralPath (Join-Path $data $marker))) '-WhatIf changes neither the game folder nor user data'

    $backup = Join-Path $root 'backup'
    $output = Invoke-Script @('-GameRoot', $game, '-Force', '-BackupRoot', $backup)
    $left = @(Get-ChildItem -LiteralPath $game -Force | ForEach-Object Name | Sort-Object)
    Check (($left -join ',') -eq 'GameAssembly.dll,TOHOTOPIA.exe') 'BepInEx, Doorstop proxy and config, bundled runtime and changelog are removed; game files stay'
    Check ((Get-ChildItem -LiteralPath $backup -Directory | Select-Object -First 1 | Get-ChildItem -Force).Count -eq 6) '-BackupRoot moves every removed item to a timestamped backup'
    Check (Test-Path -LiteralPath (Join-Path $data $marker)) 'LLM Battle runtime data from the plugin folder is preserved in LocalAppData'

    $output = Invoke-Script @('-GameRoot', $game)
    Check ($output -match 'Nothing to do') 'running again on a clean game folder is a no-op'

    $other = New-Game 'foreign-proxy' 'Some other proxy'
    $output = Invoke-Script @('-GameRoot', $other, '-Force')
    Check ((Test-Path -LiteralPath (Join-Path $other 'winhttp.dll')) -and -not (Test-Path -LiteralPath (Join-Path $other 'BepInEx'))) 'a winhttp.dll that is not Doorstop is kept'

    $output = Invoke-Script @('-GameRoot', (Join-Path $root 'missing'))
    Check ($output -match 'TOHOTOPIA.exe missing') 'a folder without TOHOTOPIA.exe is rejected'

    $solo = New-Game 'only-llmbattle'
    $output = Invoke-Script @('-GameRoot', $solo)
    Check (-not (Test-Path -LiteralPath (Join-Path $solo 'BepInEx'))) 'an install whose only plugin is LLM Battle needs no -Force'

    $inside = New-Game 'backup-inside'
    $output = Invoke-Script @('-GameRoot', $inside, '-BackupRoot', $inside)
    Check ($output -match 'outside the game folder' -and (Test-Path -LiteralPath (Join-Path $inside 'BepInEx'))) 'backups inside the game folder are refused before anything is removed'
    Remove-Item -LiteralPath (Join-Path $data $marker)
    $output = Invoke-Script @('-GameRoot', $inside, '-BackupRoot', $inside)
    Check (-not (Test-Path -LiteralPath (Join-Path $data $marker))) 'a refused run does not touch user data'
}
finally {
    Remove-Item -LiteralPath (Join-Path $data $marker) -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
