param([string]$GameRoot='C:\Program Files (x86)\Steam\steamapps\common\帝国幻想乡')
$ErrorActionPreference='Stop'
if(Get-Process TOHOTOPIA -ErrorAction SilentlyContinue){throw 'Close the game before installing.'}
$runtimeDll=Join-Path $GameRoot 'BepInEx\core\Il2CppInterop.Runtime.dll'
$runtimeSource=Join-Path $PSScriptRoot 'Mod\lib\Il2CppInterop.Runtime.dll'
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
