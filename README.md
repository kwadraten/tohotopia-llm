# 帝国幻想乡 LLM AI

[简体中文](README.md) | [English](README.en.md)

在《帝国幻想乡》的 Steam 联机房间中，让大语言模型接管房主创建的 AI 席位。每个席位可以选择自己的模型和提供商；房主可以在对局中恢复原生 AI。

## 安装与使用

需要 Windows、已安装的《帝国幻想乡》、游戏目录中的 BepInEx IL2CPP，以及 .NET 10 Desktop Runtime 和 ASP.NET Core Runtime。模型调用还需要可用的账号或 API Key。运行库可从 [.NET 下载页面](https://dotnet.microsoft.com/download/dotnet/10.0)获取。

1. 从 [Releases](https://github.com/kwadraten/tohotopia-llm/releases) 下载 ZIP 并解压。关闭游戏，在解压出的文件夹打开 PowerShell，运行：

   ```powershell
   .\Install.ps1
   ```

   脚本会查找各盘常见的 Steam 游戏目录。如果未找到游戏，或存在多个安装位置，请手动指定：

   ```powershell
   .\Install.ps1 -GameRoot "D:\Steam\steamapps\common\帝国幻想乡"
   ```

   安装会更新 BepInEx 运行时并备份被替换的文件。已有的插件设置会保留。
2. 启动游戏。设置窗口会自动打开；关闭后可在游戏中按 `F8` 再次打开。在「账号管理」中配置提供商凭据，在「AI 接管」中为每个席位选择模型并保存接管配置。
3. 由房主创建普通 Steam 房间，添加足够数量的原生 AI 席位并开始对局。设置窗口可启用模型接管、恢复原生 AI，以及查看日志。每次最多接管 4 个 AI 席位。

## 工作原理

BepInEx 插件在真实联机对局中接管房主控制的原生 AI 行动，并接入游戏内聊天。插件使用游戏自身的迷雾与单位可见性计算，整理该 AI 能看到的地图、单位和通信，再连同当前可执行的行动候选交给本地 AgentHost。模型只能从候选中选择行动，不能查看其他玩家的私有状态。

Agent 使用 Microsoft Agent Framework。每次决策会结合可见状态、相关规则、上一轮的简短战略记忆和行动反馈，请模型规划最多 12 步；模型可查询本地导出的游戏规则。宿主检查返回的候选 ID，游戏逐步重新验证并执行。动作执行后重新收集状态和候选；如果后续动作失效，就中止该序列，把原因和新状态交给 Agent 重新规划。没有合适的行动或达到每回合行动上限时结束回合。模型还可以通过游戏聊天频道交流，并在非自身回合处理可见消息。

模型请求或决策处理失败时，重新初始化原生 AI 并交还该席位的当前回合，其他席位继续运行；失败席位在下一回合重试模型。如果原生 AI 初始化失败，该席位的当前回合会结束。WinForms 窗口管理模型账号、席位、全局规则和日志，并启动本地 AgentHost。

## 开发与打包

需要 .NET 10 SDK，以及安装了 BepInEx 的游戏副本。模组项目引用游戏目录中的程序集；构建时必须指定游戏目录：

```powershell
.\Build.ps1 -GameRoot "D:\Steam\steamapps\common\帝国幻想乡"
dotnet run --project .\Checks\Checks.csproj -c Release
```

`Build.ps1` 将 AgentHost 和设置窗口发布到 `dist`，并构建模组 DLL。构建完成后，可以按安装脚本要求的目录结构制作 ZIP：

```powershell
$package = "work\package\tohotopia-llm"
New-Item -ItemType Directory -Force "$package\Mod\bin\Release\net6.0", "$package\Mod\lib", "$package\dist" | Out-Null
Copy-Item Install.ps1, settings.example.json, LICENSE, THIRD-PARTY-NOTICES.txt $package
Copy-Item Mod\bin\Release\net6.0\Tohotopia.LLMBattle.dll "$package\Mod\bin\Release\net6.0"
Copy-Item Mod\lib\Il2CppInterop.Runtime.dll, Mod\lib\IL2CppSharp.dll, Mod\lib\IL2CppSharp.Hooking.dll, Mod\lib\IL2CppSharp-LICENSE.txt "$package\Mod\lib"
Copy-Item dist\AgentHost, dist\Settings "$package\dist" -Recurse
Compress-Archive -Path $package -DestinationPath dist\tohotopia-llm.zip -Force
```

重复打包前请使用空的暂存目录，避免将旧文件带入安装包。第三方组件信息见 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)。
