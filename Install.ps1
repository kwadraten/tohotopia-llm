param(
    [string]$GameRoot
)
$ErrorActionPreference='Stop'

if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $candidates = @(@(
        foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
            foreach ($steamPath in @('Program Files (x86)\Steam', 'Program Files\Steam', 'Steam', 'SteamLibrary')) {
                $candidate = Join-Path $drive.Root (Join-Path $steamPath 'steamapps\common\帝国幻想乡')
                if (Test-Path -LiteralPath (Join-Path $candidate 'TOHOTOPIA.exe') -PathType Leaf) {
                    $candidate
                }
            }
        }
    ) | Select-Object -Unique)
    if (@($candidates).Count -eq 0) {
        throw 'Could not find TOHOTOPIA in the default Steam folders. Specify the game directory with -GameRoot "path\to\帝国幻想乡".'
    }
    if (@($candidates).Count -gt 1) {
        throw ("Multiple TOHOTOPIA installations found. Specify one with -GameRoot:`n" + ($candidates -join "`n"))
    }
    $GameRoot = $candidates[0]
}

if (-not (Test-Path -LiteralPath (Join-Path $GameRoot 'TOHOTOPIA.exe') -PathType Leaf)) {
    throw "Game directory is invalid or TOHOTOPIA.exe is missing: $GameRoot"
}
$GameRoot = (Resolve-Path -LiteralPath $GameRoot).Path
$runtimeDll=Join-Path $GameRoot 'BepInEx\core\Il2CppInterop.Runtime.dll'
$runtimeSource=Join-Path $PSScriptRoot 'Mod\lib\Il2CppInterop.Runtime.dll'
if (-not (Test-Path -LiteralPath $runtimeDll -PathType Leaf)) {
    throw "BepInEx runtime not found at $runtimeDll. Install BepInEx before running this script."
}
$requiredFiles = @(
    $runtimeSource
    (Join-Path $PSScriptRoot 'Mod\bin\Release\net6.0\Tohotopia.LLMBattle.dll')
    (Join-Path $PSScriptRoot 'Mod\lib\IL2CppSharp.dll')
    (Join-Path $PSScriptRoot 'Mod\lib\IL2CppSharp.Hooking.dll')
    (Join-Path $PSScriptRoot 'settings.example.json')
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required installation file not found: $file. Build the project first." }
}
foreach ($directory in @('dist\AgentHost', 'dist\Settings')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot $directory) -PathType Container)) {
        throw "Required build directory not found: $directory. Build the project first."
    }
}
Write-Output "Game directory: $GameRoot"
if(Get-Process TOHOTOPIA -ErrorAction SilentlyContinue){throw 'Close the game before installing.'}
if((Get-FileHash -LiteralPath $runtimeDll).Hash -ne (Get-FileHash -LiteralPath $runtimeSource).Hash){
    Copy-Item -LiteralPath $runtimeDll -Destination ($runtimeDll+'.bak') -Force
    Copy-Item -LiteralPath $runtimeSource -Destination $runtimeDll -Force
}
$target=Join-Path $GameRoot 'BepInEx\plugins\LLMBattle'
New-Item -ItemType Directory -Path $target -Force | Out-Null
if(Test-Path -LiteralPath (Join-Path $target 'Tohotopia.LLMBattle.dll')){Copy-Item -LiteralPath (Join-Path $target 'Tohotopia.LLMBattle.dll') -Destination (Join-Path $target 'Tohotopia.LLMBattle.dll.bak') -Force}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mod\bin\Release\net6.0\Tohotopia.LLMBattle.dll') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mod\lib\IL2CppSharp.dll') -Destination $target -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Mod\lib\IL2CppSharp.Hooking.dll') -Destination $target -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'dist\AgentHost') -Destination $target -Recurse -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'dist\Settings') -Destination $target -Recurse -Force
$runtime=Join-Path $target 'runtime'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
if(!(Test-Path -LiteralPath (Join-Path $runtime 'settings.json'))){Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'settings.example.json') -Destination (Join-Path $runtime 'settings.json')}
$probe=Join-Path $GameRoot 'BepInEx\plugins\AIProbe\TOHOTOPIA.AI.Probe.dll'
if(Test-Path -LiteralPath $probe){Move-Item -LiteralPath $probe -Destination ($probe+'.disabled') -Force}
$probeRoot=Split-Path $probe
foreach($dependency in @('IL2CppSharp.dll','IL2CppSharp.Hooking.dll')){
    $probeDependency=Join-Path $probeRoot $dependency
    if(Test-Path -LiteralPath $probeDependency){Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('Mod\lib\'+$dependency)) -Destination $probeDependency -Force}
}
Write-Output ('Installed to '+$target)
