# TOHOTOPIA LLM AI

[简体中文](README.md) | [English](README.en.md)

Let language models control the host's AI seats in a TOHOTOPIA Steam multiplayer lobby. Each seat can use a different provider and model, and the host can return control to the built-in AI during a match.

## Installation and use

You need Windows, an installed copy of TOHOTOPIA, BepInEx IL2CPP in the game directory, and both the .NET 10 Desktop Runtime and ASP.NET Core Runtime. Model requests also require an account or API key. The runtimes are available from the [.NET download page](https://dotnet.microsoft.com/download/dotnet/10.0).

1. Download the ZIP from [Releases](https://github.com/kwadraten/tohotopia-llm/releases) and extract it. Close the game, open PowerShell in the extracted folder, and run:

   ```powershell
   .\Install.ps1
   ```

   The script searches common Steam locations on available drives. If it cannot find the game or finds multiple installations, specify the directory:

   ```powershell
   .\Install.ps1 -GameRoot "D:\Steam\steamapps\common\帝国幻想乡"
   ```

   Installation updates the BepInEx runtime and backs up replaced files. Existing plugin settings are preserved.
2. Start the game. The settings window opens automatically; press `F8` in game to reopen it. Configure provider credentials under account management, choose a model for each seat under AI control, and save the configuration.
3. As host, create a normal Steam lobby, add enough built-in AI seats, and start the match. Use the settings window to enable model control, restore the built-in AI, and inspect logs. Up to four AI seats can be controlled.

## How it works

The BepInEx plugin takes over actions for the host-controlled native AI seats in a multiplayer match and connects to the in-game chat system. It uses the game's fog-of-war and unit visibility calculations to collect each AI's visible map, units, and messages, then sends those observations and the current action candidates to the local AgentHost. The model chooses from those candidates and is not given other players' private state.

The agent uses Microsoft Agent Framework. For each decision, it combines visible state, relevant rules, a short strategic memory, and feedback from the previous plan. It asks the model for a plan of up to 12 actions; the model can also query locally exported game rules. The host validates the returned candidate IDs, and the game revalidates each action before execution. After actions are executed, the plugin collects fresh state and candidates. If a later action is no longer valid, it stops the plan and sends the reason and updated state back for replanning. The turn ends when no action is appropriate or the per-turn limit is reached. Models can also communicate through in-game chat and process visible messages outside their own turn.

If a model request or decision fails, the affected seat returns to the built-in AI for that turn; other seats continue, and the failed seat retries on its next turn. The WinForms window manages accounts, seats, global rules, and logs, and starts the local AgentHost.

## Development and packaging

Install the .NET 10 SDK and use a game installation with BepInEx. The mod project references assemblies from the game directory, which must be specified for the build:

```powershell
.\Build.ps1 -GameRoot "D:\Steam\steamapps\common\帝国幻想乡"
dotnet run --project .\Checks\Checks.csproj -c Release
```

`Build.ps1` publishes AgentHost and Settings into `dist` and builds the mod DLL. After building, create a ZIP with the directory structure expected by the installer:

```powershell
$package = "work\package\tohotopia-llm"
New-Item -ItemType Directory -Force "$package\Mod\bin\Release\net6.0", "$package\Mod\lib", "$package\dist" | Out-Null
Copy-Item Install.ps1, settings.example.json, LICENSE, THIRD-PARTY-NOTICES.txt $package
Copy-Item Mod\bin\Release\net6.0\Tohotopia.LLMBattle.dll "$package\Mod\bin\Release\net6.0"
Copy-Item Mod\lib\Il2CppInterop.Runtime.dll, Mod\lib\IL2CppSharp.dll, Mod\lib\IL2CppSharp.Hooking.dll, Mod\lib\IL2CppSharp-LICENSE.txt "$package\Mod\lib"
Copy-Item dist\AgentHost, dist\Settings "$package\dist" -Recurse
Compress-Archive -Path $package -DestinationPath dist\tohotopia-llm.zip -Force
```

Use an empty staging directory for each package so old files are not included. See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) for third-party components.
