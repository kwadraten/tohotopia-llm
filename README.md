# 帝国幻想乡 LLM AI

[简体中文](README.md) | [English](README.en.md)

在《帝国幻想乡》的 Steam 联机房间中，让大语言模型接管房主创建的 AI 席位。每个席位可以选择自己的模型和提供商；房主可以在对局中恢复原生 AI。

## 安装与使用

需要 Windows、已安装的《帝国幻想乡》和 [TH1 Mod Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3829555573)（需要加载器 0.8.031 或更新版本，已核对游戏 0.8.5d）。游戏内置的「创意工坊 / Mod」菜单只加载 JSON 数据补丁，不能加载本 mod。宿主和设置窗口均使用 NativeAOT，设置界面使用 Avalonia 与 TKCode.Arc 主题，无需另装 .NET。模型调用需要账号或 API Key。

1. 按加载器的说明安装 TH1 Mod Loader。订阅本 mod，在加载器管理器中启用 LLM Battle。加载器直接发现工坊目录并加载，无需运行本 mod 安装脚本或复制 DLL。
2. 双击桌面的「TOHOTOPIA大模型AI控制面板」（面板首次启动会自动创建快捷方式；找不到时在订阅的工坊目录中运行 `Settings\TH1LLMBattlePanel.exe`，或搜索 `TH1LLMBattlePanel`）。在「账号管理」中配置提供商凭据，在「AI 接管」中为每个席位选择模型并保存接管配置。游戏内也可按 `F8` 打开面板。
3. 由房主创建普通 Steam 房间，添加足够数量的原生 AI 席位并开始对局。面板可启用模型接管、恢复原生 AI，以及查看日志。每次最多接管 4 个 AI 席位。面板关闭即退出，不影响游戏内接管继续运行。

账号、设置和日志保存在 `%LOCALAPPDATA%\TohotopiaLLM\runtime`，与订阅内容分开。工坊更新、游戏更新或 Steam 库目录移动不会覆盖用户数据。首次启动自动迁移旧版本地 mod 或 BepInEx 安装的 runtime 数据。

从旧版 BepInEx 版升级的玩家，请关闭游戏后运行订阅目录中的 `Remove-BepInEx.bat`。它会保留账号和设置，并清除游戏目录中的 BepInEx、Doorstop 及其附带的 .NET 运行时；如果检测到其他 BepInEx 插件则停止，需加 `-Force` 确认。可用 `-WhatIf` 预览，用 `-BackupRoot` 改为移动备份。

## 工作原理

模型目录优先读取本地缓存；缓存缺失或损坏时使用 FSharp.ModelsDev 内置离线快照。目录超过一天后在后台更新，下载失败保留现有目录，五分钟后可重试，不阻塞设置初始化。

加载器通过 `ModEntry.Initialize()` 启动本 mod，利用方法覆盖接口接入游戏主循环和聊天发送。模组在真实联机对局中接管房主控制的原生 AI 行动，并接入游戏内聊天。插件使用游戏自身的迷雾与单位可见性计算，整理该 AI 能看到的地图、单位和通信，再连同当前可执行的行动候选交给本地 AgentHost。模型只能从候选中选择行动，不能查看其他玩家的私有状态。

Agent 使用 Microsoft Agent Framework。每次决策会结合可见状态、相关规则、上一轮的简短战略记忆和行动反馈，请模型规划最多 12 步；模型可查询本地导出的游戏规则。宿主检查返回的候选 ID，游戏逐步重新验证并执行。动作执行后重新收集状态和候选；如果后续动作失效，就中止该序列，把原因和新状态交给 Agent 重新规划。没有合适的行动或达到每回合行动上限时结束回合。模型还可以通过游戏聊天频道交流，并在非自身回合处理可见消息。

模型请求或决策处理失败时，重新初始化原生 AI 并交还该席位的当前回合，其他席位继续运行；失败席位在下一回合重试模型。如果原生 AI 初始化失败，该席位的当前回合会结束。Avalonia 窗口管理模型账号、席位、全局规则和日志，并启动本地 AgentHost。

## 开发与打包

需要 .NET 10 SDK，以及 Visual Studio C++ 桌面生成工具和 Windows SDK（用于宿主和设置窗口的 NativeAOT 发布）。游戏端编译为 `netstandard2.1`，构建不再需要游戏或 BepInEx 程序集；JSON 使用游戏自带的 Newtonsoft.Json。运行以下命令：

```powershell
.\Build.ps1
dotnet run --project .\Checks\Checks.csproj -c Release
dotnet run --project .\SettingsChecks -c Release
```

`Build.ps1` 生成完整的 `dist\LLMBattle` 发布目录，包含入口、`package.build`、默认配置，以及 NativeAOT 编译的 Windows x64 AgentHost 和设置窗口。可单独运行迁移检查：

```powershell
dotnet run --project .\LoaderChecks -c Release -- "加载器目录\0.8.5d\TH1.Hotfix.dll"
.\LoaderChecks\RemoveBepInExChecks.ps1
```

将 `dist\LLMBattle` 原样作为工坊上传的 contentfolder，不要混入本机 runtime 数据。发布条目时，将加载器 `3829555573` 设置为 Steam 工坊必需物品。`package.build` 与入口程序集一起生成，用于定位当前加载版本的文件；不要手工替换单独的 DLL。启用顺序由加载器管理，程序更新由 Steam 负责。

开发时可用 `.\Install.ps1 -GameRoot "游戏目录"` 复制完整发布包到本地 `Mods\LLMBattle`。此脚本只用于本地开发，订阅用户无需运行。不要同时启用旧的本地副本和新的工坊副本。
