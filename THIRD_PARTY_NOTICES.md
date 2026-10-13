# 第三方组件声明

WinPilot 使用或参考以下第三方项目，各组件保留原有许可证与版权声明

## 参考项目

| 项目 | 参考内容 | 许可证 |
| --- | --- | --- |
| [BetterGI](https://github.com/babalae/better-genshin-impact) | 数据结构和通知功能设计 | [GPL-3.0](https://github.com/babalae/better-genshin-impact/blob/main/LICENSE) |

## 运行依赖

### C# 依赖（NuGet）

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| [Cronos](https://github.com/HangfireIO/Cronos) | Cron 任务调度 | [MIT](https://github.com/HangfireIO/Cronos/blob/main/LICENSE) |
| [protobuf-net / protobuf-net.Core](https://github.com/protobuf-net/protobuf-net) | 飞书长连接消息序列化 | [Apache-2.0](https://github.com/protobuf-net/protobuf-net/blob/main/Licence.txt) |
| [System.Management](https://github.com/dotnet/runtime) | Windows 进程事件监听 | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| [Microsoft.Toolkit.Uwp.Notifications](https://github.com/CommunityToolkit/WindowsCommunityToolkit) | Windows 桌面通知 | [MIT](https://github.com/CommunityToolkit/WindowsCommunityToolkit/blob/main/License.md) |
| [TaskScheduler](https://github.com/dahall/taskscheduler) | Windows 启动任务管理 | [MIT](https://github.com/dahall/taskscheduler/blob/master/license.md) |
| [Microsoft.Extensions.Logging.Abstractions](https://github.com/dotnet/runtime) | 日志接口与插件契约 | [MIT](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |

### 前端依赖（npm）

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| [Vue](https://github.com/vuejs/core) | Web 界面与响应式状态 | [MIT](https://github.com/vuejs/core/blob/main/LICENSE) |
| [Vue Router（vue-router）](https://github.com/vuejs/router) | 页面路由 | [MIT](https://github.com/vuejs/router/blob/main/LICENSE) |
| [@xterm/xterm / @xterm/addon-fit](https://github.com/xtermjs/xterm.js) | Web 终端与尺寸适配 | [MIT](https://github.com/xtermjs/xterm.js/blob/master/LICENSE) |
| [DOMPurify](https://github.com/cure53/DOMPurify) | HTML 内容净化 | [MPL-2.0](https://github.com/cure53/DOMPurify/blob/main/LICENSE-MPL) 或 [Apache-2.0](https://github.com/cure53/DOMPurify/blob/main/LICENSE)（任选其一） |
| [markdown-it](https://github.com/markdown-it/markdown-it) | Markdown 渲染 | [MIT](https://github.com/markdown-it/markdown-it/blob/master/LICENSE) |

## 前端传递依赖

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| [entities](https://github.com/fb55/entities) | HTML 字符实体处理 | [BSD-2-Clause](https://github.com/fb55/entities/blob/main/LICENSE) |
| [linkify-it](https://github.com/markdown-it/linkify-it) | 链接识别 | [MIT](https://github.com/markdown-it/linkify-it/blob/master/LICENSE) |
| [mdurl](https://github.com/markdown-it/mdurl) | URL 解析与编码 | [MIT](https://github.com/markdown-it/mdurl/blob/master/LICENSE) |
| [punycode.js](https://github.com/mathiasbynens/punycode.js) | 国际化域名转换 | [MIT](https://github.com/mathiasbynens/punycode.js/blob/main/LICENSE-MIT.txt) |
| [uc.micro](https://github.com/markdown-it/uc.micro) | Unicode 字符分类 | [MIT](https://github.com/markdown-it/uc.micro/blob/master/LICENSE.txt) |

分发第三方组件时，保留对应的许可文本、版权和附加 NOTICE。本清单不替代组件的原始声明
