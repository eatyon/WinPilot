# 插件开发指南

WinPilot 通过 C# DLL 加载插件，插件实现 `WinPilot.PluginContract.ICSharpPlugin`，通过共享接口访问状态、功能页、任务和机器人命令

首次开发先完成最小可用插件的构建与安装，再按需要添加配置、功能页、任务扩展或机器人命令。插件作为独立项目开发，只引用 `WinPilot.PluginContract`，不引用主程序项目

- [开发环境与目录](#开发环境与目录)
- [最小可用插件](#最小可用插件)
- [构建与安装](#构建与安装)
- [生命周期与数据](#生命周期与数据)
- [配置声明](#配置声明)
- [状态与功能](#状态与功能)
- [自定义功能页](#自定义功能页)
- [任务扩展](#任务扩展)
- [机器人命令](#机器人命令)
- [宿主能力](#宿主能力)
- [调试与发布检查](#调试与发布检查)

## 开发环境与目录

使用 .NET 10 或更新版本的 SDK，准备与目标 WinPilot 版本对应的源码，以项目引用方式使用 `WinPilot.PluginContract`。普通插件可以使用 `net10.0`；使用 Windows API 或 WinForms 时选择对应的 Windows TargetFramework

以下示例将插件项目与 WinPilot 源码仓库并列放置，后续项目引用路径按此目录结构填写

```text
workspace/
├─ WinPilot/
│  └─ WinPilot.PluginContract/
└─ ExamplePlugin/
   ├─ ExamplePlugin.csproj
   ├─ ExamplePlugin.cs
   ├─ manifest.json
   ├─ features.json
   └─ settings.json
```

## 最小可用插件

### 项目文件

`ExamplePlugin.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <EnableDynamicLoading>true</EnableDynamicLoading>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\WinPilot\WinPilot.PluginContract\WinPilot.PluginContract.csproj" />
    <None Update="manifest.json;features.json;settings.json" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
    <None Update="web\**\*" CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`EnableDynamicLoading` 用于将插件的托管依赖复制到输出目录。宿主会统一使用自己的 PluginContract 程序集，发布插件时不需要携带 `WinPilot.PluginContract.dll`

### 插件声明

`manifest.json`：

```json
{
  "id": "example-plugin",
  "name": "示例插件",
  "version": "1.0.0",
  "description": "演示插件配置、状态和功能注册",
  "authors": [{ "name": "开发者", "links": "https://github.com/example" }],
  "status_ui": true,
  "features_ui": "features.json",
  "settings_ui": "settings.json",
  "main": "ExamplePlugin.dll"
}
```

| 字段 | 约束与用途 |
| --- | --- |
| `id` | 必填，使用小写字母、数字和连字符，格式如 `example-plugin`，所有已安装插件之间唯一 |
| `name`、`version` | 必填，插件名称和版本 |
| `main` | 必填，插件根目录中存在的 DLL 文件名，不能包含目录路径 |
| `description`、`authors` | 可选，作者链接必须使用 HTTP 或 HTTPS |
| `winpilot_version` | 可选，最低宿主版本，使用 `System.Version` 可解析的值，如 `1.0.0`，不带 `v` 或预发布后缀 |
| `status_ui` | 是否显示插件发布的状态，省略时为 false |
| `features_ui`、`settings_ui` | 可选，插件目录内的 JSON 声明文件路径 |

根据实际使用的接口填写最低宿主版本。保持插件 ID 稳定，配置和运行数据都按 ID 存放

### 配置与功能声明

`settings.json`：

```json
[
  {
    "name": "message",
    "type": "text",
    "label": "提示文字",
    "default": "插件运行正常",
    "required": true
  }
]
```

`features.json`：

```json
{
  "features": [
    {
      "id": "show-message",
      "name": "显示提示",
      "description": "返回配置中的提示文字",
      "mode": "action"
    }
  ]
}
```

### C# 入口

`ExamplePlugin.cs`：

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WinPilot.PluginContract;

public sealed class ExamplePlugin : ICSharpPlugin
{
    public void Start(CSharpPluginContext context)
    {
        var configuration = context.Storage.ReadConfig<Configuration>() ?? new();
        var message = string.IsNullOrWhiteSpace(configuration.Message)
            ? "插件运行正常"
            : configuration.Message;

        context.Status.Publish(JsonSerializer.Serialize(new { 状态 = "运行中" }));
        context.Logger.LogInformation("示例插件已启动");

        if (context.Features.Actions is { } actions)
        {
            context.Track(actions.Register("show-message",
                () => PluginFeatureActionResult.Success(message)));
        }
    }

    public sealed class Configuration
    {
        public string Message { get; set; } = "插件运行正常";
    }
}
```

入口类必须可以通过公共无参数构造函数实例化，一个入口 DLL 中只保留一个符合条件的 `ICSharpPlugin` 实现。`Start` 同步执行，应完成初始化和注册后尽快返回，初始化失败可以抛出异常，由宿主记录加载失败

## 构建与安装

在插件项目目录执行：

```powershell
dotnet publish .\ExamplePlugin.csproj -c Release -o .\publish
```

将发布文件安装到运行中的 WinPilot 所在目录：

```text
User/Plugin/ExamplePlugin/
├─ ExamplePlugin.dll
├─ ExamplePlugin.deps.json   有独立依赖时保留
├─ manifest.json
├─ features.json
├─ settings.json
├─ 插件自有依赖              按需提供
└─ web/                     有功能页时提供
```

只使用 .NET 共享框架和 `WinPilot.PluginContract` 的插件，不需要附带 .NET 运行时、`WinPilot.PluginContract.dll` 或 `.deps.json`。使用其他独立程序集或 NuGet 依赖时，携带实际需要的依赖 DLL，并保留 `.deps.json` 供宿主解析依赖路径。`.deps.json` 是依赖描述文件，不是插件配置，也不包含依赖本身

不需要复制源码、`obj`、测试结果或调试符号。插件是类库，不使用 single-file 或 Native AOT 发布。ZIP 包可以采用上述单个插件文件夹结构，用户解压到 `User/Plugin/` 后加载

宿主只扫描 `User/Plugin/` 的直接子目录。安装插件后，在插件页点击“重新扫描”，按需启用插件。扫描将重新校验未运行的插件；新发现的插件按保存的启用设置启动，目录已移除的插件停止并保留数据。同 ID 更换目录时释放旧实例，重复 ID 的新增目录会被忽略。无效插件不能修改启用状态；启用或重启时重新读取并校验插件文件，校验失败时显示原因并保存为禁用。更新插件时先禁用，替换文件后再启用

插件根目录可附带 `README.md`，宿主会在插件信息页展示，文件不能超过 512 KiB。用户使用说明放在这里，页面脚本与样式仍放在 `web/`

## 生命周期与数据

### 目录与配置

| 接口或路径 | 用途 |
| --- | --- |
| `context.PluginDirectory` | 插件安装目录，存放 DLL、声明和网页资源 |
| `context.Storage.DataDirectory` | `User/PluginData/<id>/`，存放持久化数据 |
| `context.Storage.ReadConfig<T>()` | 读取该数据目录的 `config.json`，属性名匹配不区分大小写 |
| `context.Storage.ReadData<T>(fileName)` | 读取自定义 JSON 数据，参数只能是单级文件名 |
| `context.Track(resource)` | 注册插件资源，停用、重启或重新加载时按相反顺序释放 |

宿主管理数据目录里的 `plugin.json` 生命周期设置和 `config.json` 用户配置。插件自定义数据使用其他文件名，不写入安装目录，也不要覆盖宿主管理的文件

通过宿主设置页面保存配置后，插件会重启，因此可以在 `Start` 中读取配置。自定义数据写入由插件负责，注意并发访问，并采用临时文件替换等方式避免中断后留下损坏文件

`config.json` 不存在时，`ReadConfig<T>()` 从空对象 `{}` 反序列化；自定义文件不存在时，`ReadData<T>()` 从 `null` 反序列化。文件访问失败或 JSON 无效时会抛出异常，不会自动恢复默认数据。必要配置可以让初始化失败；可恢复的业务数据应由插件明确处理，避免把读取失败当成空数据写回

### 资源释放

`ICSharpPlugin` 没有 `Stop` 方法，宿主也不会自动调用入口实例的 `Dispose`。把注册句柄、计时器和自定义后台服务交给 `context.Track`；后台服务在释放时取消并结束任务，解除事件订阅并释放资源。页面专属资源交给 `session.Track`，不要让插件后台任务依赖页面的生命周期

`context.Track` 和 `session.Track` 只接受 `IDisposable`，不能直接跟踪只实现 `IAsyncDisposable` 的对象。后台服务应实现 `IDisposable`，在释放时取消自己的生命周期令牌并等待任务退出，再释放其使用的资源；后台任务必须响应取消，避免卸载时阻塞。仅跟踪 `CancellationTokenSource` 不会在释放时自动取消任务，需要显式调用 `Cancel`

## 配置声明

宿主依据 `settings.json` 生成设置界面，根节点为数组

| `type` | 存储类型 |
| --- | --- |
| `text`、`textarea`、`select` | 字符串 |
| `number` | 数字 |
| `checkbox` | 布尔值 |
| `multi-checkbox` | 字符串数组 |
| `collection` | 按 fixed 或 dynamic 模式存储 |

普通字段使用 `name`、`type`、`label`、`description`、`default` 和 `required`。同一层的 `name` 不能重复，默认值必须匹配字段类型。`text` 可以声明 `masked: true`，界面隐藏内容，安全模式下返回等长圆点，不返回默认值

`number` 支持可选的 `min`、`max` 和 `step`，配置组内同样适用。`step` 为正数或 `"any"`，未设置则不限步长，以 `min` 或 0 为基准。默认值（未设置时为 0）须符合约束，保存时由宿主统一校验

`select` 和 `multi-checkbox` 的 `options` 可以使用字符串数组，或 `{ "value": "值", "label": "显示文字" }` 数组。`select` 可改用 `optionsSource: "notificationChannels"` 获取宿主通知渠道，不能同时声明 `options`

`collection` 支持以下结构，集合字段不能再嵌套集合：

- `mode: "fixed"`：通过 `items` 声明各项，每项有 `name` 和 `fields`，配置存为以各项 name 为键的对象
- `mode: "dynamic"`：通过 `item.fields` 声明字段，配置存为数组；宿主为每项维护 `__winpilot.id` 和非空 `__winpilot.remark`，插件读取时应保留这些元数据

缺失字段会由宿主补充默认值；未指定默认值时，数字为 0、布尔值为 false、多选为空数组，其他普通字段为空字符串

## 状态与功能

使用 `context.Status.Publish(json)` 发布有效 JSON。状态界面按对象的键显示，因此建议发布扁平对象，键作为显示名称，值使用文字、数字或布尔值。宿主校验 JSON 后比较根节点的原始 JSON 文本，相同文本不会重复推送；对象字段顺序或内部空白变化仍会触发推送，建议固定序列化方式

`StatusJson` 保存最近一次发布的数据；`SubscriberCountChanged` 可用于根据浏览器订阅数量调整状态采样频率，核心业务不应依赖浏览器是否打开

功能 ID 使用小写字母、数字和连字符，注册 ID 必须与 `features.json` 一致

| 模式 | 声明与实现 |
| --- | --- |
| `action` | `context.Features.Actions.Register(id, handler)`，返回带有非空 Message 的 `PluginFeatureActionResult`；可声明 `confirm: true` 请求操作确认 |
| `page` | 声明指向 ES 模块的 `entry`，通过 `context.Features.Pages.Register(id, opened)` 注册页面会话；模块默认导出带有 `mount` 函数的对象 |

Action 回调是同步的，不适合直接执行耗时操作。耗时业务使用功能页启动操作并查询进度，或使用异步任务动作

## 自定义功能页

一个功能页由 `features.json` 声明、C# 页面会话处理器和 `web/` 中的 ES 模块组成。宿主创建页面会话后，加载模块并调用 `mount(root, host)`。`root` 是独立的 `ShadowRoot`，插件负责在其中渲染业务内容

### 声明与文件位置

在 `features.json` 的 `features` 数组中加入：

```json
{
  "id": "dashboard",
  "name": "状态面板",
  "description": "查看插件状态",
  "mode": "page",
  "entry": "dashboard/index.js"
}
```

对应的插件发布目录包含：

```text
ExamplePlugin/
├─ ExamplePlugin.dll
├─ manifest.json
├─ features.json
└─ web/
   └─ dashboard/
      ├─ index.js
      └─ style.css
```

`entry` 相对 `web/`，必须以 `.js` 结尾，不能使用绝对路径或包含 `..`。入口默认导出带有 `mount` 函数的对象，`styles` 可省略

使用 Vue、React 等框架时，将插件自己的前端构建为浏览器可加载的 ES 模块，连同依赖和资源一起发布到 `web/`。框架应用挂载到 `root` 内创建的元素，并在卸载时释放实例。CSS 通过 `styles` 声明，不依赖直接导入 CSS 或裸包名的浏览器解析

### 注册 C# 消息处理器

在插件的 `Start` 中注册与声明相同的功能 ID：

```csharp
if (context.Features.Pages is { } pages)
{
    context.Track(pages.Register("dashboard", session =>
    {
        session.HandleMessages((message, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = JsonDocument.Parse(message);
            if (request.RootElement.ValueKind != JsonValueKind.Object
                || !request.RootElement.TryGetProperty("command", out var commandElement)
                || commandElement.ValueKind != JsonValueKind.String
                || !request.RootElement.TryGetProperty("parameters", out var parameters)
                || parameters.ValueKind != JsonValueKind.Object)
                return Task.FromResult(JsonSerializer.Serialize(new { error = "请求格式无效" }));

            var command = commandElement.GetString();
            var response = command == "get-status"
                ? context.Status.StatusJson ?? "{}"
                : JsonSerializer.Serialize(new { error = "未知操作" });
            return Task.FromResult(response);
        });
    }));
}
```

每个页面会话只能注册一个消息处理器和一个上传处理器，消息处理器按 `command` 分发业务。多个请求可能并发执行，共享数据需要同步保护。页面专属服务或资源交给 `session.Track`，插件后台业务仍由 `context.Track` 管理

`host.send` 发送 `{ command, parameters }`，省略 parameters 时为 `{}`。C# 返回有效 JSON 字符串，前端得到解析后的值；HTTP 失败或响应不是有效 JSON 时 Promise 拒绝。业务错误如 `{ "error": "未知操作" }` 仍是成功的 HTTP 响应，插件前端需要自行判断

### 编写前端入口

`web/dashboard/index.js`：

```javascript
export default {
  styles: [new URL('./style.css', import.meta.url)],
  mount(root, host) {
    const document = root.ownerDocument
    const button = document.createElement('button')
    button.type = 'button'
    button.className = 'wp-button wp-button-primary'
    button.textContent = '刷新状态'
    const status = document.createElement('pre')
    status.className = 'status-output'
    root.append(button, status)

    async function refresh() {
      button.disabled = true
      try {
        const result = await host.send('get-status')
        if (host.signal.aborted) return
        if (result && typeof result === 'object' && 'error' in result)
          throw new Error(String(result.error))
        status.textContent = JSON.stringify(result, null, 2)
      } catch (error) {
        if (!host.signal.aborted)
          host.notify(error instanceof Error ? error.message : '读取状态失败', 'error')
      } finally {
        if (!host.signal.aborted) button.disabled = false
      }
    }

    button.addEventListener('click', refresh, { signal: host.signal })
    void refresh()
  },
}
```

`web/dashboard/style.css`：

```css
:host {
  display: block;
}

.status-output {
  margin: 12px 0 0;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  background: var(--surface);
  color: var(--text);
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}
```

`styles` 接受字符串或 URL 数组，使用 `new URL('./style.css', import.meta.url)` 可保留模块的版本化资源路径。宿主等待所有声明样式加载后再调用 `mount`，单个样式加载超过 15 秒会使初始化失败

JS 使用相对路径导入模块；图片、字体等资源通过 `new URL('./image.png', import.meta.url)` 或 `host.assetUrl('dashboard/image.png')` 获取。`assetUrl` 参数相对 `web/`，不带开头的 `/`，不要保存宿主生成的资源版本路径

### 页面接口

| 接口 | 用法 |
| --- | --- |
| `host.sessionId` | 当前页面会话标识，仅在本次挂载期间使用 |
| `host.send(command, parameters?, signal?)` | 发送 JSON 消息，返回解析后的响应 |
| `host.upload(command, blob, parameters?, signal?)` | 上传二进制数据，返回解析后的 JSON 响应 |
| `host.assetUrl(path)` | 获取相对 `web/` 的版本化资源地址 |
| `host.signal` | 页面退出、初始化失败或会话重建时取消 |
| `host.onDispose(cleanup)` | 注册同步清理函数，返回取消注册的函数 |
| `host.notify(message, type?)` | 显示提示，type 为 `success` 或 `error`，默认 `success` |
| `host.confirm(options)` | 打开确认弹窗，返回 `Promise<boolean>` |
| `host.openModal(options)` | 打开自定义内容弹窗，返回弹窗句柄 |
| `host.ui` | 增强选择器和操作菜单 |
| `host.scroll` | 读取或调整宿主页面滚动位置 |

`mount` 可以返回同步卸载函数，也可以返回最终得到卸载函数的 Promise。计时器、观察器和框架实例通过 `host.onDispose` 或卸载函数释放；DOM 事件可绑定 `host.signal`。清理函数按注册的相反顺序执行，一个清理函数抛出异常不会阻止其他清理函数。异步挂载在页面关闭后才返回时，返回的卸载函数会立即执行

`send` 和 `upload` 自动绑定页面信号；传入额外 signal 时，任一信号取消都会取消请求。自行调用 fetch 时也要传入页面或弹窗信号。C# 处理器必须遵守收到的 CancellationToken，页面会话关闭或插件停用时会取消正在处理的请求

### 上传二进制数据

在同一个 `pages.Register` 回调中增加上传处理器。以下示例仅返回收到的文件大小，不保存文件：

```csharp
session.HandleUploads((upload, cancellationToken) =>
{
    cancellationToken.ThrowIfCancellationRequested();
    if (upload.Command != "inspect-file")
        return Task.FromResult(JsonSerializer.Serialize(new { error = "未知上传操作" }));

    using var parameters = JsonDocument.Parse(upload.ParametersJson);
    if (parameters.RootElement.ValueKind != JsonValueKind.Object
        || !parameters.RootElement.TryGetProperty("name", out var name)
        || name.ValueKind != JsonValueKind.String
        || string.IsNullOrWhiteSpace(name.GetString()))
        return Task.FromResult(JsonSerializer.Serialize(new { error = "缺少文件名" }));

    return Task.FromResult(JsonSerializer.Serialize(new
    {
        name = name.GetString(),
        bytes = upload.Content.Length
    }));
});
```

前端在文件选择事件中调用 `await host.upload('inspect-file', file, { name: file.name })`，file 为选择得到的 File 对象

| 请求 | 限制 |
| --- | --- |
| JSON 消息 | 单次请求体最多 64 KiB，按 UTF-8 字节数计算 |
| 二进制上传 | 单次数据为 1 字节到 1 MiB，直接发送 Blob，不使用 Base64 |
| 上传 command | 非空，最多 128 个字符 |
| 上传 parameters | JSON 对象，序列化后最多 4096 个字符 |

超过请求体上限返回 HTTP 413。未注册上传处理器时，宿主在读取数据前拒绝请求。处理器返回前应完成请求数据的使用；需要后台处理时先复制数据并自行管理任务生命周期

实际存储路径、文件类型校验、分块顺序、合并和未完成上传清理由插件实现，宿主不会替插件保存上传文件。大文件应分块发送，每块不超过 1 MiB；取消上传后也需清理已经持久化的分块

### 样式与布局

页面高度自然参与宿主滚动，业务样式放在自己的 ShadowRoot 内，使用 `:host` 设置页面级变量。主题颜色优先使用 `--text`、`--text-muted`、`--surface`、`--surface-hover` 和 `--border`，这些变量会随宿主主题切换更新

插件继承主程序配色，按用途选择主题变量，不自行维护默认强调色

| 用途 | 主题变量 |
| --- | --- |
| 强调文字、链接和图标 | `--accent-text`、`--accent-text-hover` |
| 实心主按钮 | 背景 `--accent-solid`、`--accent-solid-hover`，文字与图标 `--accent-contrast` |
| 选中项和焦点 | 背景 `--accent-bg`，文字 `--accent-text`，边框 `--accent-border`，焦点环 `--accent-focus-ring` |
| 错误文字、危险提示 | `--danger-text`、`--danger-bg`、`--danger-border` |
| 实心危险按钮 | 背景 `--danger-solid`、`--danger-solid-hover`，文字与图标 `--danger-contrast` |
| 成功提示 | `--success-text`、`--success-bg`、`--success-border` |
| 警告提示 | `--warning-text`、`--warning-bg`、`--warning-border` |

次级按钮使用中性背景，悬停使用 `--surface-hover`。图表数据系列颜色可以独立定义，但线条和图例应共用同一颜色变量

宿主已提供以下可选控件样式，无需另行加载公共 CSS：

| 控件 | class 或属性 |
| --- | --- |
| 按钮 | `wp-button`，可叠加 `wp-button-primary`、`wp-button-danger`、`wp-button-icon` |
| 输入框、文本域 | `wp-input`，错误状态使用 `aria-invalid="true"` |
| 复选框 | `wp-checkbox` |
| checkbox 开关 | `wp-switch` |
| 禁用状态 | 原生 `disabled` 属性 |

布局、卡片和业务组件样式仍由插件提供。页面和弹窗各有独立 ShadowRoot，全局样式选择器不会自动进入这些区域；不要向 `document.head` 注入业务样式或向 `document.body` 挂载业务浮层。ShadowRoot 用于隔离样式，不提供权限隔离，宿主只应安装可信插件

### 选择器与操作菜单

普通 `<select>` 添加选项后调用 `host.ui.enhanceSelect(root)`，弹窗内使用 `scope.ui.enhanceSelect(root)`。控件保留原生 select 的值和 change 事件，并观察新增控件与选项；`<select multiple>` 使用复选框列表，多次勾选时保持展开

返回对象提供 `refresh(select)`、`open(select)`、`close(select?)` 和 `dispose()`。代码直接修改 select.value 或选项的 selected 后，调用 refresh 更新显示。监听可取消的 `app-select-beforeopen` 事件，可以先 preventDefault，异步取得选项后更新 select，再调用 open

操作菜单调用 `ui.enhanceMenu(trigger, content, options?)`。trigger 是按钮，content 中放置使用 `app-select-option` 和 `role="menuitem"` 的业务按钮，二者先挂载在同一 ShadowRoot 内。宿主负责定位、键盘导航和关闭，业务按钮事件由插件处理

菜单 options 支持 `width`、`maxHeight`，单位为像素；`beforeOpen()` 返回 false 可取消打开，此回调是同步的。需要异步加载时先取消，加载完成后让 beforeOpen 允许打开，再调用返回对象的 `open()`。返回对象也提供 `close()`、`dispose()`；分组、标题和空状态可使用 `app-menu-group`、`app-menu-heading`、`app-menu-message`

选择器和菜单默认字号 12px、字重 600，使用 `--wp-picker-font-size` 和 `--wp-picker-font-weight` 覆盖。局部容器变量不会自动传到独立弹窗，页面与弹窗共用样式文件时可在 `:host` 中统一设置。选择器和菜单注册的监听器与观察器由对应页面或弹窗自动清理

### 确认与内容弹窗

简单确认使用 `await host.confirm({ title, message, acceptText, danger })`。title 默认“提示”，acceptText 默认“确认”；danger 为 true 时使用危险操作按钮。取消或页面退出返回 false，只有确认时返回 true

复杂内容使用 `host.openModal`。弹窗的 `mount(root, scope)` 中，scope 提供 `signal`、`onDispose`、`ui`，不包含页面的 send、upload 等接口；通过闭包访问 host，并把 `scope.signal` 传入弹窗发起的请求：

```javascript
let modal
modal = host.openModal({
  title: '插件状态',
  size: 'medium',
  async mount(root, scope) {
    const result = await host.send('get-status', {}, scope.signal)
    if (scope.signal.aborted) return
    if (result && typeof result === 'object' && 'error' in result)
      throw new Error(String(result.error))
    const content = root.ownerDocument.createElement('pre')
    content.className = 'status-output'
    content.textContent = JSON.stringify(result, null, 2)
    root.append(content)
  },
  actions: [{ label: '关闭', onClick: () => modal.close() }],
})
```

每次打开弹窗创建新内容，不移动页面中已经挂载的节点。宿主提供标题、关闭按钮、遮罩、操作栏、滚动和焦点管理，插件只挂载内容

| 选项 | 说明 |
| --- | --- |
| `title`、`mount` | 必填，标题和内容挂载函数 |
| `styles` | 省略时使用页面声明的样式；传空数组时只保留宿主基础与控件样式 |
| `size` | `small`、`medium`、`large`，省略时使用宿主默认尺寸 |
| `dismissible` | 默认 true，控制用户关闭；false 不阻止代码调用 close |
| `dismissOnBackdrop` | 默认 false，是否允许点击遮罩关闭；仍受 dismissible 限制 |
| `actions` | 省略时提供取消按钮，空数组隐藏操作栏 |

操作按钮结构为 `{ label, onClick, kind?, disabled? }`，kind 为 `primary`、`secondary` 或 `danger`，默认 `secondary`。异步 onClick 返回 Promise，宿主在执行期间阻止重复提交和用户关闭；成功后不会自动关闭，需主动调用句柄的 close。操作异常会显示提示并保留弹窗，内容初始化失败则关闭弹窗

句柄提供 `close()`、`update({ title, size, actions, dismissible, dismissOnBackdrop })`、`closed: Promise<void>` 和 `signal`。update 不替换 styles 或 mount。关闭弹窗会取消其信号并清理资源，关闭父弹窗也会关闭其上所有弹窗；页面退出时关闭全部弹窗

### 滚动与会话恢复

| 滚动接口 | 用法 |
| --- | --- |
| `host.scroll.getPosition()` | 返回 `{ top, left }`，单位为像素 |
| `host.scroll.restorePosition(position, behavior?)` | 恢复宿主页面位置，behavior 默认 `instant` |
| `host.scroll.scrollTo(element, { offset?, behavior? }?)` | 滚动到当前页面内的元素，offset 默认 0，正值在元素上方保留空间 |

滚动位置与 offset 必须是有限数字。scrollTo 只接受当前页面 ShadowRoot 内且仍挂载的元素；弹窗内容有独立滚动区域，不使用此接口操作弹窗

固定悬浮按钮使用 `bottom: calc(14px + var(--wp-page-bottom-inset, 0px))` 避免被手机底部导航遮挡，该变量在桌面为 0px。弹窗中的按钮使用弹窗自身布局

宿主负责心跳与会话释放，插件无需重复实现：

- 每 30 秒发送一次心跳，回到前台时立即检查；同一时间只发送一个心跳，10 秒超时后取消
- 会话连续 2 分钟没有消息或心跳后由服务端清理，退出页面或停用插件也会关闭会话
- 页面消息或心跳返回会话不存在时，宿主自动创建新会话并重新挂载页面；不会重放导致失效的业务请求
- 普通网络失败不会自动重建页面；入口、样式或 mount 初始化失败会关闭会话并显示错误，不自动重试

重新挂载会重置前端输入和弹窗状态，重要数据由插件保存到数据目录。不要把 sessionId 或 host 存入模块级变量供后续挂载复用

完整页面类型见 [PluginPageTypes.ts](../WinPilot.Web/src/features/plugins/page/PluginPageTypes.ts)，消息与上传的 C# 接口见 [PluginContracts.cs](../WinPilot.PluginContract/PluginContracts.cs)

## 任务扩展

注册返回的句柄均交给 `context.Track`，插件停用后对应能力会撤销

| 能力 | 接口 |
| --- | --- |
| 任务触发器 | `context.Tasks.Triggers.Register(definitions)`，事件发生时调用 `Emit(new PluginTriggerEvent(...))` |
| 任务动作 | `context.Tasks.Actions.Register(definitions, handler)`，异步回调返回 `PluginTaskActionResult`，支持取消和动态选项解析 |
| 通知变量 | `context.Tasks.Variables.Register(definitions, resolver)`，按所需变量名解析；支持 Text 和 Image，图片值使用 `PluginImageContent` |

### 任务触发器

将以下成员加入插件入口类，在 `Start` 中调用 `RegisterTaskTrigger(context)`，再由实际业务完成事件调用 `EmitCompleted(name)`：

```csharp
private IPluginTaskTriggerApi? taskTriggers;

private void RegisterTaskTrigger(CSharpPluginContext context)
{
    if (context.Tasks.Triggers is not { } triggers) return;
    context.Track(triggers.Register([
        new PluginTriggerDefinition("completed", "业务完成")
    ]));
    taskTriggers = triggers;
    if (context.Tasks.Variables is { } variables)
        context.Track(variables.Register([
            new PluginVariableDefinition("$example.name", "业务名称")
        ]));
}

private void EmitCompleted(string name)
{
    taskTriggers?.Emit(new PluginTriggerEvent(
        "completed",
        DateTimeOffset.Now,
        new Dictionary<string, object?> { ["$example.name"] = name }));
}
```

示例同时注册 `$example.name`，使任务页的通知变量列表能够显示它，实际值由触发事件传入。触发器注册后，用户仍需在任务页选择该触发器并启用任务。`Emit` 的 ID 必须已注册，变量会传入该次任务的 `PluginTaskRunContext.Variables`。若触发器声明了筛选参数，同名事件变量用于匹配筛选值；时间使用实际事件时间。插件停用会取消由该插件触发的任务，业务事件订阅仍需由插件解除

### 异步任务动作与动态选项

在 `Start` 中注册一个带选择参数的异步动作：

```csharp
if (context.Tasks.Actions is { } taskActions)
{
    context.Track(taskActions.Register(
        [new PluginTaskActionDefinition("run", "执行示例", [
            new PluginActionParameterDefinition(
                "target", "目标", PluginActionParameterType.Select,
                Required: true,
                Options: [new PluginActionOption("default", "默认目标")])
        ])],
        async (parameters, run, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!parameters.TryGetValue("target", out var value)
                || Convert.ToString(value) is not { Length: > 0 } target)
                return new PluginTaskActionResult(
                    PluginTaskActionResultStatus.InvalidParameters, "请选择目标");

            await Task.Delay(100, cancellationToken);
            context.Logger.LogInformation("示例动作完成：{Target}", target);
            return PluginTaskActionResult.Success();
        },
        (actionId, parameterKey, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PluginActionOption> options =
                actionId == "run" && parameterKey == "target"
                    ? [new PluginActionOption("default", "默认目标")]
                    : [];
            return Task.FromResult(options);
        }));
}
```

动态选项回调只用于 Select 和 MultiCheckbox，示例中的固定列表可以替换为业务配置查询。即使使用动态回调，声明中也必须提供非 null 的 `Options`，选项 Value 和 Label 均不能为空。宿主在查询选项和执行任务时都会调用回调，执行时按当前选项校验，已删除的选项会使原任务参数失效

动作参数类型包括 Text、Number、Select、Checkbox、Textarea、MultiCheckbox，Key 在同一动作内不能重复。回调应校验自己的业务边界，遵守取消令牌；正常完成返回 Success，业务失败返回带 Error 的其他状态，宿主会按任务的失败策略继续或停止后续动作

### 通知变量

变量名需要符合 `^[\$&][a-z][a-z0-9-]*(\.[a-z][a-z0-9_-]*)+$`，文字使用 `$`，图片使用 `&`，例如 `$example.status` 和 `&example.preview`。变量名在所有插件间唯一，建议使用插件自己的前缀

在 `Start` 中注册按需解析的文字与图片变量：

```csharp
if (context.Tasks.Variables is { } variables)
{
    context.Track(variables.Register([
        new PluginVariableDefinition("$example.status", "示例状态"),
        new PluginVariableDefinition("&example.preview", "示例图片",
            Type: PluginVariableType.Image)
    ], async (names, run, cancellationToken) =>
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (name.Equals("$example.status", StringComparison.OrdinalIgnoreCase))
                values[name] = "运行中";
            else if (name.Equals("&example.preview", StringComparison.OrdinalIgnoreCase))
            {
                var path = Path.Combine(context.Storage.DataDirectory, "preview.png");
                if (!File.Exists(path)) throw new FileNotFoundException("示例图片不存在", path);
                values[name] = new PluginImageContent(
                    await File.ReadAllBytesAsync(path, cancellationToken), "image/png");
            }
        }
        return values;
    }));
}
```

图片示例要求插件提前生成数据目录中的 `preview.png`。解析器只计算 names 中请求的变量；图片返回 `PluginImageContent`，文字变量不能返回图片对象。通知图片只支持 PNG 或 JPEG，大小为 1 字节到 10 MB，单条通知只能使用一张图片

只注册变量定义而不提供解析器时，变量值需要由触发事件传入。解析器中的 run 提供任务名称、触发来源、时间和事件变量，可用于按本次任务上下文生成结果

## 机器人命令

通过 `context.Commands.Register(definition, handler)` 注册，返回的句柄交给 `context.Track`。插件停用或句柄释放后撤销注册，并取消相关执行。注册不要求命令机器人已开启，消息处理仍受渠道开关、接收范围和响应前缀限制

### 普通命令

在 `Start` 中注册命令：

```csharp
if (context.Commands is { } commands)
{
    context.Track(commands.Register(
        new PluginCommandDefinition("示例状态", "查看示例插件状态"),
        async (command, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await command.Reply.ReplyTextAsync(context.Status.StatusJson ?? "暂无状态");
        }));
}
```

名称和说明不能为空，名称不能包含空白或控制字符。名称冲突不区分大小写，与本体或已注册命令冲突时跳过注册并记录日志，不因此导致插件启动失败；无效的定义仍会抛出异常

匹配不区分大小写，命令前缀由机器人设置控制，不要写入 `Name`

- 不声明 `Parameters`：只匹配完整命令名
- 声明非空 `Parameters`：允许命令名后以空白分隔参数，参数声明同时用于展示，不自动解析或校验参数
- 设置 `AllowAttachedArguments: true`：允许参数紧接命令名，必须同时声明 `Parameters`

例如 `new PluginCommandDefinition("示例查询", "查询目标", "<目标名称>")`。`command.Name` 为注册名称，`command.Arguments` 为去掉命令名及两端空白后的参数，内部空白和引号保留。多个普通命令同时匹配时，采用名称最长的命令

### 通配命令

使用 `*` 注册未匹配命令的处理器：

```csharp
if (context.Commands is { } commands)
{
    context.Track(commands.Register(
        new PluginCommandDefinition("*", ""),
        async (command, cancellationToken) =>
        {
            if (!command.Arguments.Equals("示例问候", StringComparison.OrdinalIgnoreCase)) return;
            cancellationToken.ThrowIfCancellationRequested();
            await command.Reply.ReplyTextAsync("你好");
        }));
}
```

不同插件可以重复注册 `*`，每个插件最多一个，同一插件重复注册会跳过。不能声明 `Parameters` 或设置 `AllowAttachedArguments`，`Description` 由本体统一为「处理未匹配的命令」，可以传空字符串

消息通过接收范围和响应前缀检查后，按开始执行时的有效注册分发：

1. 匹配本体或普通插件命令：只执行对应命令，失败或未回复也不转交给 `*`
2. 未匹配：广播给所有有效的 `*` 处理器
3. 各插件自行判断，不处理直接结束，允许多个插件回复；全部忽略时不回复

通配回调中，`command.Name` 为 `*`，`command.Arguments` 为去掉响应前缀及两端空白后的完整文本，内部空白和引号保留。处理器并发执行，异常互不影响，回复顺序不保证；插件应限定自己的匹配范围，避免重复执行操作

### 回复与生命周期

使用 `command.Reply` 回复收到消息的会话，无需指定通知发送目标。`ReplyTextAsync` 不接受空白文字；`ReplyImageAsync` 使用 `PluginImageContent`，要求非空图片字节和 `image/png` 或 `image/jpeg` 类型，图片能否发送还取决于渠道支持

处理和回复最多等待 60 秒，不包含排队时间，通配处理器各自受超时限制。机器人关闭、插件停用或注册撤销也会取消执行。回调必须遵守 `CancellationToken`；宿主可以停止等待，但无法强制终止不响应取消的插件代码

`command.Reply` 仅用于当前回调，回调结束、超时或取消后不能继续回复。返回的 `Task` 表示处理生命周期，不需要返回「正在处理」。耗时操作可先回复已受理，再完成业务；如需后台执行，使用插件管理的生命周期取消令牌并处理异常，最终结果通过插件状态、功能页或宿主通知提供

### 帮助与可用命令

普通命令与 `*` 均在所属插件分组内展示，组内保持注册顺序。帮助图片使用上方命令、下方说明的排版，命令按渠道设置加上响应前缀；例如 `/*` 下方显示「处理未匹配的命令」。「可用命令」展示注册用法与说明，不附加渠道前缀。`*` 表示处理范围，不要求用户实际输入星号

## 宿主能力

优先使用 `context.Host` 提供的接口，避免引用主程序内部类

- `Processes.IsRunning(processName)`：检查进程是否运行
- `Security.SafeModeEnabled`：读取当前安全模式状态
- `Actions.ExecuteAsync(step, cancellationToken)`：执行宿主动作，检查返回的 Succeeded 和 Error

| 动作类型 | 参数键 |
| --- | --- |
| `StartProgram` | `programPath`、`arguments`、`workingDirectory`、`runElevated`、`silent` |
| `Delay` | `delaySeconds` |
| `Keyboard` | `keys`，字符串集合 |
| `Shell` | `shell`、`command` |
| `SendNotification` | `content`、`channel`、`image`，图片使用 `PluginImageContent` |

任务动作还可以返回 `PluginHostActionPlan`，由任务执行器处理后续宿主动作。Plan 的 Steps 不能为空，参数值使用 `"$param.target"` 这类字符串引用该动作的参数；直接调用 `context.Host.Actions.ExecuteAsync` 不会解析这些占位符。声明动作时同时提供 HostActionPlan 和回调，宿主优先执行声明中的 Plan，不调用该动作回调

Host.Actions 和 Host.Processes 为可空接口，调用前检查是否可用。Security 提供当前安全模式状态，但直接调用宿主动作接口不会自动代替插件完成业务权限判断，需要限制的插件操作应主动检查该状态。插件 DLL 在宿主进程内运行，具有宿主进程的权限，当前加载机制不是权限沙箱

## 调试与发布检查

1. 构建插件，将发布产物复制到测试实例的插件目录，重新扫描并启用
2. 查看运行目录的 `logs/`，确认 manifest 校验、DLL 加载和 Start 均成功；格式错误的插件可能直接被忽略
3. 验证配置保存后重启、功能执行、页面消息与上传、宿主主题切换，以及弹窗关闭后请求和资源清理
4. 验证禁用或重启后，后台任务结束、事件订阅释放、命令和任务注册撤销；重新扫描不影响已有实例，目录移除后清理对应插件资源
5. 在干净目录安装发布包，确认没有依赖开发机器上的 DLL 或源码路径，并验证最低支持宿主版本

| 问题 | 检查内容 |
| --- | --- |
| DLL 未找到或无法加载 | `main` 与实际 DLL 文件名、依赖文件、PluginContract 版本 |
| 功能执行失败 | 注册 ID 与 `features.json` 是否一致，是否返回非空提示 |
| 功能页初始化失败 | 浏览器中的模块及 CSS 请求、`entry` 与发布路径、默认导出和 `mount` |
| 页面消息失败 | C# 消息处理器、返回 JSON、业务错误判断和取消处理 |
| 上传失败 | command、参数对象、单块大小、上传处理器与分块清理 |
| 返回前台后页面状态重置 | 会话是否已重建，重要状态是否持久化，业务操作是否重复启动 |

接口定义见 [PluginContracts.cs](../WinPilot.PluginContract/PluginContracts.cs) 和 [PluginCommandContracts.cs](../WinPilot.PluginContract/PluginCommandContracts.cs)，加载行为见 [CSharpPluginRuntime.cs](../WinPilot/Features/Plugins/CSharpPluginRuntime.cs)
