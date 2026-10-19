# HTTP API

本文用于查找 WinPilot 提供的接口及其调用约定，覆盖 JSON、SSE、WebSocket 和网页资源路由。插件管理与页面会话接口由 WinPilot 提供；具体插件的业务消息不在本文定义。

路径均相对 WinPilot 服务地址，无 API 版本前缀。端口以程序设置为准。

## 公共约定

### 请求与响应

- 除另注外，请求体为 `application/json`；路径和查询参数须做 URL 编码。
- 宿主 JSON 字段使用 camelCase；枚举值及插件原始 JSON 按对应接口约定。应解析 JSON，不依赖原始字符的转义形式。
- 表中成功响应未注明类型时均为 JSON；`204` 无正文。创建任务、快捷标签成功时，`Location` 指向资源路径。
- `200` 不一定表示业务操作成功，须检查接口约定的结果字段；`202` 仅表示已受理。
- 错误正文尚未统一：可能为 ProblemDetails、含 `errors` 的 ValidationProblem、`{ message }`、`{ detail }` 或空正文。先判断状态码，再读取可用错误信息，不依赖错误文案作分类。
- 表中错误列仅列端点专用错误，不重复通用的请求绑定错误 `400`、媒体类型错误 `415` 和未处理故障 `500`；“—”不表示绝不会失败。取消可能直接结束响应，不约定独立状态码。

### 认证与安全模式

除本节注明的例外外，以下接口在设置密码后均要求携带有效的 `WinPilot.Session` Cookie；首次未设密码时无需登录。

- `GET /health`、`POST /login` 不要求已有会话；登录成功通过 `Set-Cookie` 建立会话。
- `POST /_manage` 受伪装页及访客 Cookie 策略控制，不是普通登录接口。
- 未认证的 `/api`、`/plugin-assets` 请求返回空正文 `401`，不重定向；网页导航与静态资源另见文末。
- 安全模式下，受限操作返回 `403`；由安全模式中间件拒绝时含 `code: "safe_mode_restricted"`。只读配置可能被遮罩或裁剪，不应将其当作完整设置覆盖保存。
- 认证配置失效后的安全保护收尾失败可能返回 `503`。

### 流式接口

SSE 使用 `text/event-stream`，事件正文见各端点说明；WebSocket 握手成功为 `101`。连接建立后的错误通过流内消息或连接结束报告，不能只检查初始 HTTP 状态。

下表的 `{name}` 等表示路径参数；`{**path}` 匹配剩余路径。`{bot}` 表示 `telegram`、`feishu`、`onebot`、`qqbot` 四组实际端点。

## 基础与认证

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/health` | 不要求会话 | 200 `{ status: "ok" }` | — |
| GET | `/api/about` | `version` 为显示版本字符串 | 200 `{ version }` | — |
| POST | `/login` | JSON `{ password }`；成功设置会话 Cookie；未配置密码 404，来源锁定 429；伪装页模式无管理入口凭据时可能提前返回 302/404 | 204 | 401、404、429 |
| POST | `/_manage` | 无请求体；伪装页开启且已设密码时，使用访客 Cookie 获取管理入口凭据 | 204 | 404 |

## 安全设置

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/settings/security/password` | 不返回密码内容 | 200 `{ configured }` | — |
| PUT | `/api/settings/security/password` | JSON `{ currentPassword, newPassword, confirmation }`；首次设置无需校验旧密码；成功后全部会话失效，需重新登录 | 204 | 400、500 |
| GET | `/api/settings/security/notifications` | 返回 `{ channel, events }` | 200 安全事件通知设置 | — |
| PUT | `/api/settings/security/notifications` | JSON `{ channel, events }`；`PasswordSet`、`PasswordChanged` 须成对选择 | 200 已保存设置 | 400、500 |
| GET | `/api/security/safe-mode` | 只读，切换由本机操作完成 | 200 `{ enabled }` | — |
| GET | `/api/security/safe-mode/events` | `data` 为 `{ enabled }`，含初始状态及后续变化；`no-cache` | 200 SSE | — |

## 系统与桌面

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/system/status` | 单次采集，需刷新时重新请求 | 200 CPU、内存、GPU、磁盘占用 | — |
| GET | `/api/desktop/screenshot` | 单次截图，`no-store`，携带 Last-Modified | 200 图片，类型由截图结果指定 | 503 |
| POST | `/api/desktop/keyboard` | JSON `{ keys }`；非法按键 400，系统发送失败 500 | 204 | 400、500 |

## 程序与快捷标签

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/programs/users` | 交互用户及当前用户标记 | 200 用户数组 | — |
| GET | `/api/programs` | 查询 `userName` 必填 | 200 进程数组 | 400 |
| POST | `/api/programs/usage` | JSON `{ processIds }`；单次采集 | 200 进程占用 | 400 |
| POST | `/api/programs/close` | JSON `{ processIds, force }` | 204 | 400 |
| POST | `/api/programs/launch` | JSON 含 `programPath, arguments, workingDirectory, runElevated, silent`；起始目录留空时使用目标程序所在目录；成功不代表目标程序持续运行 | 204 | 400 |
| GET | `/api/programs/tags` | 跳过损坏或身份不一致的标签，不返回占位记录 | 200 标签数组 | — |
| POST | `/api/programs/tags` | JSON 为程序启动参数加 `name`；Location 指向标签路径，同名冲突 409 | 201 标签 | 400、409 |
| PUT | `/api/programs/tags/{name}` | 请求体同创建；路径为原名称，正文可使用新名称 | 200 标签 | 400、404、409 |
| DELETE | `/api/programs/tags/{name}` | 无请求体；标签不存在 404 | 204 | 400、404 |
| POST | `/api/programs/tags/{name}/launch` | 标签不存在 404，普通启动失败仍为 400 | 204 | 400、404 |

## 自动化任务

任务保存对象为 `{ name, enabled, trigger, actions }`，只提交所选类型需要的字段。保存响应是规范化后的保存快照，不是原请求回显。

- 触发类型：`Cron`、`Process`、`WinPilotStartup`、`Extension`。程序事件使用 `event: "Started"/"Exited"` 和 `processPaths`。
- Cron 为六字段：秒、分、时、日、月、周。例如 `0 30 8 * * *` 表示每天 08:30；周日支持 0 或 7，周一至周六为 1～6。
- `enabled` 只控制自动触发；禁用不停止当前执行。手动运行允许已禁用任务，同任务执行中不重入、不排队。
- 动作 `type: "TaskState"` 使用 `taskName` 和 `state: "Toggle"/"Enable"/"Disable"` 控制目标任务的启用状态。
- 列表中的只读 `cronAvailable` 与 `enabled` 独立，不属于保存字段；无后续时间不自动禁用任务，编辑或重新启用后重新计算。
- 保存校验的 `errors` 可含 `name`、`trigger.cron`、`actions[0].programPath` 等字段路径，但不是所有 400/409 都含字段错误。

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/tasks` | 按类型返回配置字段；Cron 任务附只读 `cronAvailable` | 200 任务数组 | — |
| GET | `/api/tasks/variables` | 用于通知内容等支持变量替换的字段 | 200 可用变量数组 | — |
| GET | `/api/tasks/cron/preview` | 查询 `expression`：秒、分、时、日、月、周六字段；按服务端时区返回未来最多 5 个带时区偏移的时间，无后续时间返回空数组 | 200 `{ nextRuns }` | 400 |
| GET | `/api/tasks/extensions/triggers` | 包含可用状态 | 200 扩展触发定义数组 | — |
| GET | `/api/tasks/extensions/actions` | 包含可用状态及 `dynamicOptions` 标记 | 200 扩展动作定义数组 | — |
| GET | `/api/tasks/extensions/actions/{pluginId}/{actionId}/options/{parameterKey}` | 按需调用动态选项解析器 | 200 选项数组 | 400、404 |
| POST | `/api/tasks` | JSON `{ name, enabled, trigger, actions }`；Location 指向任务路径；校验错误含字段路径 `errors` | 201 已保存的规范任务 | 400、409 |
| PUT | `/api/tasks/{name}` | 请求体同创建；路径为原名称，正文可使用新名称；响应为规范化后的保存快照 | 200 已保存的规范任务 | 400、404、409 |
| PATCH | `/api/tasks/{name}/enabled` | JSON `{ enabled }`；只控制自动触发，禁用不取消当前执行 | 200 更新后的任务 | 400、404 |
| DELETE | `/api/tasks/{name}` | 删除并请求取消当前执行 | 204 | 400、404 |
| POST | `/api/tasks/{name}/run` | 无请求体；提交手动执行，不等待动作完成 | 204 | 400、404 |

## 文件管理

文件操作按原因返回 `400`（无效输入）、`403`（权限不足）、`404`（不存在）、`409`（冲突）或 `500`（服务器故障）。批量上传须逐项读取结果；其他批量文件操作不保证整批回滚。

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/files/roots` | 列出可访问驱动器 | 200 驱动器数组 | 400、403、404、409、500 |
| GET | `/api/files/directory` | 查询 `path` 必填有效路径 | 200 文件夹及条目 | 400、403、404、409、500 |
| POST | `/api/files/upload` | `multipart/form-data`，含 `directory` 和文件；逐项检查 `{ fileName, success, error }`，单项失败仍为 200；整体请求超限 413 | 200 逐文件结果数组 | 400、403、404、409、413、500 |
| POST | `/api/files/download` | JSON `{ paths }`；响应携带下载文件名，支持 Range | 200 文件/ZIP 流；范围请求可为 206 | 400、403、404、409、416、500 |
| POST | `/api/files/folder` | JSON `{ parentPath, name }`；同名冲突 409 | 204 | 400、403、404、409、500 |
| POST | `/api/files/file` | JSON `{ parentPath, name }`；同名冲突 409 | 204 | 400、403、404、409、500 |
| POST | `/api/files/rename` | JSON `{ path, name }`，`name` 为新名称 | 204 | 400、403、404、409、500 |
| POST | `/api/files/delete` | JSON `{ paths }`；移入回收站，不提供逐项结果或整批回滚保证 | 204 | 400、403、404、409、500 |
| POST | `/api/files/copy` | JSON `{ sourcePaths, destinationDirectory }` | 204 | 400、403、404、409、500 |
| POST | `/api/files/move` | JSON `{ sourcePaths, destinationDirectory }` | 204 | 400、403、404、409、500 |
| GET | `/api/files/edit` | 查询 `path` 必填有效文件路径 | 200 `{ path, content, length, lastWriteTime, encoding }` | 400、403、404、409、500 |
| POST | `/api/files/edit` | JSON 含 `path, content, encoding, originalLength, originalLastWriteTime`；提交读取时的长度和修改时间，冲突 409 | 204 | 400、403、404、409、500 |

## 日志

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/logs/dates` | 日期格式 `yyyy-MM-dd` | 200 `{ date }` 数组 | 403、503 |
| GET | `/api/logs` | 查询 `date` 必填；可选 `level, keyword, offset, limit, snapshot`；后续分页沿用返回的 `snapshot`，避免新增日志导致偏移 | 200 `{ entries, hasMore, snapshot }` | 400、403、503 |

## 常规与高级设置

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/settings/general` | 读取常规设置 | 200 `{ port, lanAccessEnabled, startupMode }` | — |
| PATCH | `/api/settings/general/lan-access` | JSON `{ enabled }`；下次启动应用监听地址 | 200 常规设置 | 500 |
| PATCH | `/api/settings/general/port` | JSON `{ port }`，范围 1～65535；下次启动生效 | 200 常规设置 | 400、500 |
| PATCH | `/api/settings/general/startup-mode` | JSON `{ startupMode }`：`Disabled`、`Enabled`、`Delayed`；保存并更新开机启动计划任务 | 200 常规设置 | 400、500 |
| GET | `/api/settings/advanced/unauthenticated-web-page` | 自定义未认证页面开关 | 200 `{ enabled }` | — |
| PUT | `/api/settings/advanced/unauthenticated-web-page` | JSON `{ enabled }`；开启时创建缺失的默认页面 | 200 `{ enabled }` | 500 |

## 更新

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/settings/updates` | `source`：`GitHub/Cnb`；`channel`：`Stable/Preview` | 200 更新设置 | — |
| PUT | `/api/settings/updates` | JSON `{ source, channel }`；清除检查缓存，安装进行中返回 409 | 200 已保存更新设置 | 400、409、500 |
| POST | `/api/update/check` | 检查失败用 `checkFailed` 表示，不自动切换更新源 | 200 更新检查结果 | — |
| GET | `/api/update/status` | 检查 `state/message` 判断安装结果，安装失败仍为 200 | 200 更新进度 | — |
| POST | `/api/update/install` | JSON `{ version, source, channel }`；仅表示已受理，后续查询 `/api/update/status` | 202 | 409 |

## 命令机器人

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/settings/notifications/command-bots` | 只读取配置，不建立状态订阅 | 200 四渠道启用开关 | — |
| GET | `/api/notifications/command-bots/commands` | 已注册命令，包含通配命令 | 200 命令分组数组 | — |
| GET | `/api/settings/notifications/command-bots/{bot}` | 普通渠道为 `{ enabled, prefix }`；OneBot 另含 `webSocketUrl, token`；安全模式仅返回 `enabled`，`no-store` | 200 机器人设置 | — |
| PUT | `/api/settings/notifications/command-bots/{bot}` | 普通渠道提交 `{ enabled, prefix? }`，省略 prefix 时保留原值；OneBot 提交完整选项；响应为状态而非设置 | 200 机器人状态 | 400、403、500 |
| GET | `/api/notifications/command-bots/{bot}/status` | 单次读取 | 200 机器人状态 | — |
| POST | `/api/notifications/command-bots/{bot}/reconnect` | 无请求体；请求重新连接，不保证返回时已连接成功 | 200 机器人状态 | 400、403、500 |
| GET | `/api/notifications/command-bots/{bot}/events` | `data` 为机器人状态，含初始及后续状态；`no-cache` | 200 SSE | — |

## 通知

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/notifications/channels` | 首项空 value 表示“已启用渠道” | 200 `{ value, label }` 数组 | — |
| GET | `/api/settings/notifications` | 安全模式仅返回各渠道启用开关；`no-store` | 200 通知设置 | — |
| PATCH | `/api/settings/notifications/{channel}` | JSON 提交通知设置，仅保存指定渠道字段；`channel` 取通知渠道 ID，响应为整份设置 | 200 合并后的通知设置 | 400、500 |
| POST | `/api/notifications/test` | JSON `{ channel, title, content, imagePath }`；指定具体渠道，标题或内容至少一项非空；忽略渠道启用开关，逐项检查 `succeeded` | 200 渠道投递结果数组 | 400 |
| POST | `/api/notifications/qqbot/bind-user` | 无请求体；`data` JSON 的 `type` 为 `code/success/error`，分别携带 `code/openId/message`；取消结束流，`no-cache` | 200 SSE | — |
| POST | `/api/notifications/qqbot/bind-group` | 同 bind-user 协议，获取群 OpenID；超时和绑定失败由 `type: "error"` 报告 | 200 SSE | — |

## 终端

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| ANY（WS 握手通常为 GET） | `/api/terminal/ws` | 非 WS 请求 400；发送 `type: create/input/interrupt/resize/close`，错误以 `error` 消息返回；消息上限 16 KiB，断线清理所属会话 | 101，WebSocket 事件 | 400、403 |

## 插件管理与配置

这些是宿主提供的插件管理接口，不代表具体插件的业务 API。插件启动结果需检查 `state/errors`；不要将所有 `200` 都视为插件已正常运行。

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/plugins` | 包含状态和错误信息 | 200 插件信息数组 | 500 |
| GET | `/api/plugins/{id}` | 含 `hasReadme`，false 时无需请求 README；可选查询 `waitForLifecycle=true` 等待生命周期操作结束 | 200 插件信息 | 400、404、500 |
| GET | `/api/plugins/{id}/readme` | README 不存在 404；内容上限 512 KiB | 200 `text/plain; charset=utf-8` | 400、404、500 |
| POST | `/api/plugins/{id}/enable` | 无请求体；启动失败可返回 200，须检查 `state/errors` | 200 插件信息 | 400、404、500 |
| POST | `/api/plugins/{id}/disable` | 停止并保存禁用状态 | 200 插件信息 | 400、404、500 |
| DELETE | `/api/plugins/{id}` | 查询 `deleteData` 必填，决定是否同时删除插件数据 | 204 | 400、404、500 |
| POST | `/api/plugins/scan` | 无请求体；单插件失败查看 `state/errors`，整体扫描失败 500 | 200 插件信息数组 | 500 |
| GET | `/api/plugins/{id}/configuration` | 按声明补默认值，不改文件；安全模式按声明投影并遮罩敏感值；`no-store` | 200 插件配置 | 400、404、500 |
| PUT | `/api/plugins/{id}/configuration` | JSON 提交配置对象；补默认并校验，保存后重启已启用插件；返回保存快照，已存配置损坏 500 | 200 已保存规范配置 | 400、404、500 |
| GET | `/api/plugins/{id}/configuration/schema` | 补当前选项，安全模式隐藏敏感默认值；`no-store` | 200 配置声明数组 | 400、404、500 |
| GET | `/api/plugins/options/settings/{source}` | 当前支持 `notificationChannels` | 200 选项数组 | 400、500 |
| GET | `/api/plugins/{id}/status` | 无状态时返回空对象 | 200 插件原始 JSON 或 `{}` | 400、404、500 |
| GET | `/api/plugins/{id}/settings/display/status` | 首页展示、键选择及标题 | 200 状态展示设置 | 400、404、500 |
| PUT | `/api/plugins/{id}/settings/display/status` | 校验并保存展示参数 | 200 已保存展示设置 | 400、404、500 |
| GET | `/api/plugins/{id}/settings/display/configuration` | 是否注册到首页 | 200 配置展示设置 | 400、404、500 |
| PUT | `/api/plugins/{id}/settings/display/configuration` | JSON `{ registerOnHome }` | 200 已保存展示设置 | 400、404、500 |
| GET | `/api/plugins/{id}/settings/display/features` | 首页展示、功能选择及标题 | 200 功能展示设置 | 400、404、500 |
| PUT | `/api/plugins/{id}/settings/display/features` | 校验并保存展示参数 | 200 已保存展示设置 | 400、404、500 |

## 插件功能与页面会话

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/api/plugins/{id}/features` | 来自插件声明文件 | 200 功能声明数组 | 400、404、500 |
| POST | `/api/plugins/{id}/features/{featureId}/execute` | 无请求体；`succeeded=false` 是插件业务失败，仍为 200；执行异常 500 | 200 `{ succeeded, message }` | 400、404、500 |
| POST | `/api/plugins/{id}/features/{featureId}/page-sessions` | 无请求体；使用返回的 `id` 调用会话接口 | 200 `{ id, pluginName, feature, webVersion }` | 400、404、500 |
| POST | `/api/plugin-page-sessions/{sessionId}/messages` | 请求体为插件消息 JSON，上限 64 KiB；会话失效 404，可重建；插件内部异常 500，不视为会话失效 | 200 插件返回的 JSON | 400、404、413、500 |
| POST | `/api/plugin-page-sessions/{sessionId}/uploads` | 请求体为 `application/octet-stream`，单块上限 1 MiB；查询 `command` 必填，`parameters` 为 JSON 对象字符串，省略时为 `{}`；客户端自行分块 | 200 插件返回的 JSON | 400、404、413、415、500 |
| DELETE | `/api/plugin-page-sessions/{sessionId}` | 幂等关闭，不存在也成功 | 204 | 500 |
| POST | `/api/plugin-page-sessions/{sessionId}/heartbeat` | 更新会话活动时间，不存在/失效 404 | 204 | 404、500 |
| GET | `/api/plugins/{id}/events` | `data` 为插件 JSON 状态；有初始状态才发送，后续允许合并待发送状态，不保证逐条交付；`no-cache` | 200 SSE | 400、404、500 |
| POST | `/api/plugins/{id}/web/log` | JSON `{ level, message }`；level 为 warn/error，其他值按 Information；插件禁用时 400，含 `code: "plugin_disabled"` | 200 `{ accepted: true }` | 400、404、500 |

## 网页与资源路由

本节是浏览器与资源请求的路由行为，不是业务 JSON API。认证、安全模式或反向代理可能在到达目标端点前拒绝请求。

| 方法 | 路径 | 请求与说明 | 成功响应 | 错误 |
| --- | --- | --- | --- | --- |
| GET | `/plugin-assets/{id}/v/{version}/{**path}` | 版本不匹配 404；非 HTML/JSON 资源使用私有长期缓存 | 200 静态资源，按资源类型返回 | 400、404、500 |
| GET | `/plugin-assets/{id}/{**path}` | 非版本路径 `no-store` | 200 静态资源，按资源类型返回 | 400、404、500 |
| GET、HEAD | `/`、`/login` 及非文件型 SPA 深层路径 | 已认证或未设密码时进入 SPA；未认证页面导航按策略跳转 /login 或伪装首页；`/login` 与 POST 登录共用路径 | 200 HTML；HEAD 无正文 | 302、401、404 |
| GET、HEAD | `/assets/{**path}` 及 wwwroot 静态文件 | 普通静态资源可匿名；伪装模式有自身资源/导航策略；index.html 为 no-cache，哈希 assets 私有长期缓存 | 200/304，按文件类型返回；支持范围的文件可为 206 | 404、416 |
| 任意方法 | 未注册的 `/api` 或 `/api/{**path}` | 返回“API 不存在”ProblemDetails；不覆盖已注册端点自身返回的 404 | — | 404 |
| 不支持的方法 | 已注册的 API 路径 | 由路由器生成正确 Allow，兜底不接管；认证/安全模式可先返回 401/403 | — | 405 |
