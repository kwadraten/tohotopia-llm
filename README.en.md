# TOHOTOPIA LLM AI

[简体中文](README.md) | [English](README.en.md)

Let language models control the host's AI seats in a TOHOTOPIA Steam multiplayer lobby. Each seat can use a different provider and model, and the host can return control to the built-in AI during a match.

## Installation and use

You need Windows, TOHOTOPIA, and [TH1 Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3829555573) (loader 0.8.031 or newer required; checked against game 0.8.5d). The game's built-in Workshop / Mod menu only loads JSON data patches and cannot load this mod. Both host and settings app use NativeAOT; the settings UI uses Avalonia with the TKCode.Arc theme. No separate .NET installation is required. Model requests require an account or API key.

1. Install TH1 Mod Loader following its instructions. Subscribe to this mod and enable LLM Battle in the loader manager. The loader discovers its Workshop folder directly; no mod installer or DLL copying is needed.
2. Open the "TOHOTOPIA LLM AI Control Panel" from the desktop shortcut (created automatically on first launch; if missing, run `Settings\TH1LLMBattlePanel.exe` inside the subscribed Workshop folder or search for `TH1LLMBattlePanel`). Configure provider credentials under account management, choose a model for each seat under AI control, and save the configuration. `F8` in game also opens the panel.
3. As host, create a normal Steam lobby, add enough built-in AI seats, and start the match. Use the panel to enable model control, restore the built-in AI, and inspect logs. Up to four AI seats can be controlled. Closing the panel exits it without stopping the in-game takeover.

Accounts, settings, and logs live in `%LOCALAPPDATA%\TohotopiaLLM\runtime`, outside subscribed content. Workshop updates, game updates, and Steam library moves preserve this data. First startup migrates runtime data from an older local mod or BepInEx installation automatically.

Players upgrading from the old BepInEx version should close the game and run `Remove-BepInEx.bat` from the subscribed folder. It keeps accounts and settings, then removes BepInEx, Doorstop and its bundled .NET runtime from the game folder. It stops if other BepInEx plugins are installed; add `-Force` to confirm. Use `-WhatIf` to preview and `-BackupRoot` to move files to a backup instead of deleting them.

## How it works

The model catalog loads from local cache, falling back to the embedded FSharp.ModelsDev snapshot when the cache is missing or invalid. Catalogs older than one day refresh in the background. Failed downloads preserve the existing catalog and allow another attempt after five minutes without blocking settings initialization.

The loader starts `ModEntry.Initialize()` and connects the mod to the game loop and chat sender through method overrides. The mod takes over actions for the host-controlled native AI seats in a multiplayer match and connects to the in-game chat system. It uses the game's fog-of-war and unit visibility calculations to collect each AI's visible map, units, and messages, then sends those observations and the current action candidates to the local AgentHost. The model chooses from those candidates and is not given other players' private state.

The agent uses Microsoft Agent Framework. For each decision, it combines visible state, relevant rules, a short strategic memory, and feedback from the previous plan. It asks the model for a plan of up to 12 actions; the model can also query locally exported game rules. The host validates the returned candidate IDs, and the game revalidates each action before execution. After actions are executed, the plugin collects fresh state and candidates. If a later action is no longer valid, it stops the plan and sends the reason and updated state back for replanning. The turn ends when no action is appropriate or the per-turn limit is reached. Models can also communicate through in-game chat and process visible messages outside their own turn.

If a model request or decision fails, the built-in AI is reinitialized and takes over the affected seat's current turn. Other seats continue, and the failed seat retries the model on its next turn. If native AI initialization fails, the affected turn ends. The Avalonia window manages accounts, seats, global rules, and logs, and starts the local AgentHost.

## Development and packaging

Install the .NET 10 SDK, Visual Studio Desktop development with C++ build tools, and the Windows SDK for NativeAOT publishing. The game mod targets `netstandard2.1` and uses the game’s Newtonsoft.Json runtime; building requires no game or BepInEx assemblies:

```powershell
.\Build.ps1
dotnet test .\Tests\HostTests
dotnet test .\Tests\PanelTests
dotnet test .\Tests\ModTests
```

`Build.ps1` creates a complete `dist\LLMBattle` content folder including the loader files, `package.build`, defaults, and NativeAOT Windows x64 host and settings executables. The environment-dependent ModTests audits are enabled through environment variables:

```powershell
$env:TOHOTOPIA_LOADER_DLL = "loader-directory\0.8.5d\TH1.Hotfix.dll"
$env:TOHOTOPIA_PACKAGE_DIR = "dist\LLMBattle"
dotnet test .\Tests\ModTests
```

Likewise, the end-to-end PanelTests lifecycle tests run when `TOHOTOPIA_PANEL_EXE` points at a built `TH1LLMBattlePanel.exe`; `HostTests` honors `TOHOTOPIA_NATIVE_HOST` (AOT host HTTP contracts) and `TOHOTOPIA_RULES_SNAPSHOT` (real rule snapshot audit). These tests skip automatically when unset.

Upload `dist\LLMBattle` directly to the Workshop, excluding personal runtime data. Set loader item `3829555573` as a required Workshop item when publishing. Keep `package.build` with its matching entry DLL. The loader owns enablement and load order; Steam handles program updates.

For local development, `.\Install.ps1 -GameRoot "game-directory"` copies the complete package to local `Mods\LLMBattle`. Subscribers do not run this script. Avoid enabling both a legacy local copy and the Workshop copy.
