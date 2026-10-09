# WinPilot

WinPilot 是一款 Windows 电脑管理与自动化工具，通过浏览器查看系统状态、管理程序和文件、执行任务，支持在局域网内使用手机或其他设备访问

> WinPilot 以管理员权限运行，可管理文件、控制程序并执行命令，建议仅在本机或可信局域网使用，直接暴露到公网存在电脑被未授权访问或控制的风险

## 功能

| 功能 | 说明 |
| --- | --- |
| 系统状态 | 查看 CPU、内存、磁盘和 GPU 状态 |
| 程序管理 | 查看、启动和关闭程序，保存快捷标签 |
| 自动化任务 | 配置定时任务和事件触发任务，查看执行状态 |
| 文件管理 | 浏览、上传、下载和管理文件 |
| 终端 | 在网页中使用 CMD 或 PowerShell |
| 桌面查看 | 查看桌面截图，发送键盘输入 |
| 通知与命令机器人 | 接收通知，通过机器人发送控制命令 |
| 插件扩展 | 安装插件，扩展管理与自动化功能 |

网页支持浅色、深色主题和多种主题色，适配手机屏幕

## 下载与运行

从 [GitHub Releases](https://github.com/eatyon/WinPilot/releases) 或 [CNB Releases](https://cnb.cool/asdbr/WinPilot/-/releases) 下载 Windows x64 ZIP 发布包，解压到独立目录

运行环境：

- Windows 10 / Windows 11 x64
- x64 .NET 10 ASP.NET Core Runtime
- x64 .NET 10 Desktop Runtime

从 [.NET 10 官方下载页](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0) 分别下载并安装 Windows x64 的 ASP.NET Core Runtime 和 Desktop Runtime

1. 启动 `WinPilot.exe`，允许管理员权限请求
2. 在浏览器打开 [http://127.0.0.1:9123](http://127.0.0.1:9123)
3. 在“设置”中配置管理员密码、通知渠道和其他选项

程序在系统托盘中运行，可通过托盘菜单打开管理页面或退出程序

## 局域网访问

在“设置 → 通用”中开启局域网访问，并在“设置 → 安全”中设置管理员密码。修改局域网访问或端口后，退出并重新启动程序

在同一局域网的设备上访问 `http://电脑的局域网IP:9123`，修改过端口时使用新端口

## 插件

将插件包解压到程序目录的 `User/Plugin/`，每个插件使用独立文件夹。在网页“插件”页点击“重新扫描”，再启用插件

插件需单独安装，插件市场尚未开放

## 更新与备份

在“设置 → 关于 → 软件更新”中检查并安装更新。右侧设置按钮可选择 GitHub 或 CNB 更新源，以及正式版或测试版通道，默认使用 GitHub 正式版

正式版通道只检查正式版本；测试版通道同时检查正式版和测试版，选择版本号最高的版本。切换通道不会自动降级

手动更新时，先退出 WinPilot，再用发布包中的程序和资源文件替换旧文件，保留原有 `User/` 目录

| 目录 | 内容 |
| --- | --- |
| `User/` | 程序配置、任务、标签、插件及插件数据，备份时保留整个目录 |
| `logs/` | 运行日志，排查问题时使用，可从托盘菜单“打开目录 → 日志目录”进入 |

## 常见问题

| 问题 | 处理方式 |
| --- | --- |
| 提示缺少 .NET 运行环境 | 按运行环境说明安装两个 Windows x64 运行时 |
| 提示端口被占用 | 退出程序，备份并修改 `User/config.json` 中的 `port`，重新启动后使用新端口访问 |
| 本机能访问，局域网设备无法访问 | 确认已开启局域网访问并重启，检查 IP、端口及设备所在网络，再检查 Windows 防火墙是否允许 WinPilot 访问当前网络 |
| 提示安全模式下不可执行操作 | 在本机托盘菜单查看“安全模式”。确认密码配置正常后，可通过托盘关闭安全模式 |

## 问题反馈

通过 [GitHub Issues](https://github.com/eatyon/WinPilot/issues) 反馈问题，提供程序版本、操作步骤、截图和相关日志，提交前遮盖密码、Token 等敏感信息

## 开源与开发

WinPilot 使用 [AGPL-3.0](LICENSE) 许可证，第三方组件声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

- [仓库结构与构建](Docs/REPOSITORY_STRUCTURE.md)
- [插件开发指南](Docs/PLUGIN_DEVELOPMENT.md)
