LLM Battle / 帝国幻想乡 LLM AI

前置：TH1 Mod Loader
https://steamcommunity.com/sharedfiles/filedetails/?id=3829555573

安装加载器后，订阅本 mod，在加载器管理器中启用 LLM Battle，启动游戏即可。
无需运行本 mod 安装脚本，也不需要另装 .NET 或 BepInEx。
设置窗口会自动打开；按 F8 可重新打开。配置模型账号后，由房主在普通 Steam 房间中启用 AI 接管。

账号、设置、日志：%LOCALAPPDATA%\TohotopiaLLM\runtime
工坊更新不会覆盖用户数据。首次启动会自动迁移旧版本地 mod 的 runtime 数据。

从旧版 BepInEx 版升级：关闭游戏，双击本目录的 Remove-BepInEx.bat。
脚本会把旧 runtime 中的账号与设置并入上述目录，再删除游戏目录中的 BepInEx、dotnet、
winhttp.dll、doorstop_config.ini 等文件。若装有其他 BepInEx 插件，脚本会停止并列出名称；
确认不再需要后在 PowerShell 中加 -Force 运行。加 -BackupRoot "目录" 可改为移动备份，加 -WhatIf 只预览。
