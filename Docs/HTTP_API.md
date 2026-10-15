# HTTP API

端点与返回约定，以 `WinPilot/Web/` 和 `Program.cs` 的实现为准。路径无版本前缀

- 认证：标“需”的端点在设置密码后要求有效的 `WinPilot.Session` Cookie，未设密码时放行。未认证的 `/api`、`/plugin-assets` 返回空正文 401，不重定向
- 错误：表中列业务错误，通用绑定失败 400、媒体类型不支持 415、未处理异常 500 不逐行重复。正文可能为 ProblemDetails、ValidationProblem、`{ message }`、`{ detail }` 或空正文
- 安全保护：安全模式限制的操作返回 403，中间件附带 `code: "safe_mode_restricted"`；认证保护收尾失败可返回 503
- 类型：JSON 为 `application/json`，SSE 为 `text/event-stream`。SSE/WS 建立后用事件报告业务错误，不再用 HTTP 状态码
- 参数：`{bot}` 为 `telegram`、`feishu`、`onebot`、`qqbot`，其余花括号为路由参数，`{**path}` 匹配剩余路径

| 方法 | 路径 | 成功状态码与响应 | 错误状态码 | 需认证 | 特殊语义 |
| --- | --- | --- | --- | --- | --- |
| GET | `/health` | 200 JSON `{ status: "ok" }` | — | 否 | 健康检查 |
| GET | `/api/about` | 200 JSON `{ version }` | — | 需 | 当前显示版本 |
| POST | `/login` | 204，无正文 | 401、404、429 | 否 | 成功设置会话 Cookie；密码未配置返回 404，来源锁定返回 429；伪装页模式要求管理入口凭据，否则按页面策略返回 302/404 |
| POST | `/_manage` | 204，无正文 | 404 | 条件 | 伪装页开启且已设密码时，凭访客 Cookie 换取管理入口；其他情况仍受认证管线限制 |
| GET | `/api/settings/security/password` | 200 JSON `{ configured }` | — | 需 | 仅返回是否配置密码 |
| PUT | `/api/settings/security/password` | 204，无正文 | 400、500 | 需 | 首次设置或更改密码；成功撤销全部会话 |
| GET | `/api/settings/security/notifications` | 200 JSON 安全事件通知设置 | — | 需 | 包含通知渠道和事件列表 |
| PUT | `/api/settings/security/notifications` | 200 JSON 已保存设置 | 400、500 | 需 | 校验渠道和事件；密码设置、修改事件成对选择 |
| GET | `/api/security/safe-mode` | 200 JSON `{ enabled }` | — | 需 | 只读，切换由本机操作完成 |
| GET | `/api/security/safe-mode/events` | 200 SSE `{ enabled }` | — | 需 | 初始状态及后续变化；`Cache-Control: no-cache` |
| GET | `/api/system/status` | 200 JSON CPU、内存、GPU、磁盘占用 | — | 需 | 单次采集，不是事件流 |
| GET | `/api/desktop/screenshot` | 200 图片，类型由截图结果指定 | 503 | 需 | 单次截图，`no-store`，携带 Last-Modified |
| POST | `/api/desktop/keyboard` | 204，无正文 | 400、500 | 需 | 非法按键 400，系统发送失败 500 |
| GET | `/api/programs/users` | 200 JSON 用户数组 | — | 需 | 交互用户及当前用户标记 |
| GET | `/api/programs` | 200 JSON 进程数组 | 400 | 需 | 查询参数 `userName` 必填 |
| POST | `/api/programs/usage` | 200 JSON 进程占用 | 400 | 需 | 按 `processIds` 单次查询，不自动刷新 |
| POST | `/api/programs/close` | 204，无正文 | 400 | 需 | 按进程 ID 关闭，支持 `force` |
| POST | `/api/programs/launch` | 204，无正文 | 400 | 需 | 已发出启动请求，不代表目标程序运行成功 |
| GET | `/api/programs/tags` | 200 JSON 标签数组 | — | 需 | 损坏文件隔离并记日志，不阻断正常标签 |
| POST | `/api/programs/tags` | 201 JSON 标签 | 400、409 | 需 | Location 指向标签路径；同名冲突 409 |
| PUT | `/api/programs/tags/{name}` | 200 JSON 标签 | 400、404、409 | 需 | 支持重命名；名称或配置无效仍为 400 |
| DELETE | `/api/programs/tags/{name}` | 204，无正文 | 400、404 | 需 | 删除标签 |
| POST | `/api/programs/tags/{name}/launch` | 204，无正文 | 400、404 | 需 | 标签不存在 404，普通启动失败仍为 400 |
| GET | `/api/tasks` | 200 JSON 任务数组 | — | 需 | 仅返回对应触发条件和动作类型的字段 |
| GET | `/api/tasks/variables` | 200 JSON 可用变量数组 | — | 需 | 已注册变量目录 |
| GET | `/api/tasks/extensions/triggers` | 200 JSON 扩展触发定义数组 | — | 需 | 包含可用状态 |
| GET | `/api/tasks/extensions/actions` | 200 JSON 扩展动作定义数组 | — | 需 | 包含可用状态及 `dynamicOptions` 标记 |
| GET | `/api/tasks/extensions/actions/{pluginId}/{actionId}/options/{parameterKey}` | 200 JSON 选项数组 | 400、404 | 需 | 按需调用动态选项解析器 |
| POST | `/api/tasks` | 201 JSON 已保存的规范任务 | 400、409 | 需 | Location 指向任务路径；拒绝 null 结构和无效配置 |
| PUT | `/api/tasks/{name}` | 200 JSON 已保存的规范任务 | 400、404、409 | 需 | 支持重命名，响应与该次保存内容一致 |
| PATCH | `/api/tasks/{name}/enabled` | 200 JSON 更新后的任务 | 400、404 | 需 | 保存启用状态；禁用会请求取消当前执行 |
| DELETE | `/api/tasks/{name}` | 204，无正文 | 400、404 | 需 | 删除并请求取消当前执行 |
| POST | `/api/tasks/{name}/run` | 204，无正文 | 400、404 | 需 | 请求运行，不等待动作完成；同任务防重入 |
| GET | `/api/files/roots` | 200 JSON 驱动器数组 | 400、403、404、409、500 | 需 | 文件操作按原因区分权限、不存在、冲突和服务器错误 |
| GET | `/api/files/directory` | 200 JSON 文件夹及条目 | 400、403、404、409、500 | 需 | 查询参数 `path` |
| POST | `/api/files/upload` | 200 JSON 逐文件结果数组 | 400、403、404、409、413、500 | 需 | multipart，含 `directory` 和文件；逐文件返回 `{ fileName, success, error }`，单项失败仍为 200；请求体超限 413，已识别格式错误 400 |
| POST | `/api/files/download` | 200 文件/ZIP 流；范围请求可为 206 | 400、403、404、409、416、500 | 需 | 按 `paths` 下载，携带文件名，支持 Range |
| POST | `/api/files/folder` | 204，无正文 | 400、403、404、409、500 | 需 | 新建文件夹，同名冲突 409 |
| POST | `/api/files/file` | 204，无正文 | 400、403、404、409、500 | 需 | 新建文件，同名冲突 409 |
| POST | `/api/files/rename` | 204，无正文 | 400、403、404、409、500 | 需 | 重命名 |
| POST | `/api/files/delete` | 204，无正文 | 400、403、404、409、500 | 需 | 移入回收站；不是逐项结果协议 |
| POST | `/api/files/copy` | 204，无正文 | 400、403、404、409、500 | 需 | 批量复制；不是逐项结果协议 |
| POST | `/api/files/move` | 204，无正文 | 400、403、404、409、500 | 需 | 批量移动；不是逐项结果协议 |
| GET | `/api/files/edit` | 200 JSON 内容、编码、长度和修改时间 | 400、403、404、409、500 | 需 | 查询参数 `path` |
| POST | `/api/files/edit` | 204，无正文 | 400、403、404、409、500 | 需 | 以原长度/修改时间检查冲突；冲突 409，不覆盖文件 |
| GET | `/api/logs/dates` | 200 JSON `{ date }` 数组 | 403、503 | 需 | 日期格式 `yyyy-MM-dd` |
| GET | `/api/logs` | 200 JSON `{ entries, hasMore, snapshot }` | 400、403、503 | 需 | `date` 必填；支持 level、keyword、offset、limit、snapshot；分页使用快照 |
| GET | `/api/settings/general` | 200 JSON `{ port, lanAccessEnabled, startupMode }` | — | 需 | 读取常规设置 |
| PATCH | `/api/settings/general/lan-access` | 200 JSON 常规设置 | 500 | 需 | 更新配置，下次启动应用监听地址 |
| PATCH | `/api/settings/general/port` | 200 JSON 常规设置 | 400、500 | 需 | 端口 1～65535，下次启动生效 |
| PATCH | `/api/settings/general/startup-mode` | 200 JSON 常规设置 | 400、500 | 需 | 保存并更新 Windows 计划任务 |
| GET | `/api/settings/advanced/unauthenticated-web-page` | 200 JSON `{ enabled }` | — | 需 | 自定义未认证页面开关 |
| PUT | `/api/settings/advanced/unauthenticated-web-page` | 200 JSON `{ enabled }` | 500 | 需 | 开启时确保默认页面存在 |
| GET | `/api/settings/updates` | 200 JSON 更新设置 | — | 需 | source：GitHub/Cnb；channel：Stable/Preview |
| PUT | `/api/settings/updates` | 200 JSON 已保存更新设置 | 400、409、500 | 需 | 清除检查缓存；安装进行中不允许修改 |
| POST | `/api/update/check` | 200 JSON 更新检查结果 | — | 需 | 检查失败用 `checkFailed` 表示，不自动切换更新源 |
| GET | `/api/update/status` | 200 JSON 更新进度 | — | 需 | 安装失败由 state/message 表示，HTTP 仍为 200 |
| POST | `/api/update/install` | 202，无正文 | 409 | 需 | 仅表示已受理；请求指定 version/source/channel，后续查看 status |
| GET | `/api/notifications/channels` | 200 JSON `{ value, label }` 数组 | — | 需 | 首项空 value 表示“已启用渠道” |
| GET | `/api/settings/notifications` | 200 JSON 通知设置 | — | 需 | 安全模式仅返回各渠道启用开关；`no-store` |
| PATCH | `/api/settings/notifications/{channel}` | 200 JSON 合并后的通知设置 | 400、500 | 需 | 只保存指定渠道，返回整份通知设置，不覆盖其他渠道 |
| POST | `/api/notifications/test` | 200 JSON 渠道投递结果数组 | 400 | 需 | 必须指定具体渠道，忽略该渠道启用开关；200 仍须检查各项 succeeded |
| POST | `/api/notifications/qqbot/bind-user` | 200 SSE | — | 需 | code/success/error 事件获取用户 OpenID；超时和绑定失败通过事件返回，`no-cache` |
| POST | `/api/notifications/qqbot/bind-group` | 200 SSE | — | 需 | code/success/error 事件获取群 OpenID；取消结束流，`no-cache` |
| GET | `/api/settings/notifications/command-bots` | 200 JSON 四渠道启用开关 | — | 需 | 只读取配置，不建立状态订阅 |
| GET | `/api/notifications/command-bots/commands` | 200 JSON 命令分组数组 | — | 需 | 已注册命令，包含通配命令 |
| GET | `/api/settings/notifications/command-bots/{bot}` | 200 JSON 机器人设置 | — | 需 | OneBot 使用自身选项结构；安全模式仅返回 enabled |
| PUT | `/api/settings/notifications/command-bots/{bot}` | 200 JSON 机器人状态 | 400、403、500 | 需 | 保存设置，返回运行状态 |
| GET | `/api/notifications/command-bots/{bot}/status` | 200 JSON 机器人状态 | — | 需 | 单次读取 |
| POST | `/api/notifications/command-bots/{bot}/reconnect` | 200 JSON 机器人状态 | 400、403、500 | 需 | 请求重新连接，不保证返回时已连接成功 |
| GET | `/api/notifications/command-bots/{bot}/events` | 200 SSE 机器人状态 | — | 需 | 初始及后续状态，`no-cache` |
| ANY，通常 GET | `/api/terminal/ws` | 101，WebSocket JSON 事件 | 400、403 | 需 | 非 WS 请求 400；支持 create/input/interrupt/resize/close；错误发 error，消息上限 16 KiB；断线清理所属会话 |
| GET | `/api/plugins` | 200 JSON 插件信息数组 | 500 | 需 | 包含状态和错误信息 |
| GET | `/api/plugins/{id}` | 200 JSON 插件信息 | 400、404、500 | 需 | `waitForLifecycle=true` 等待生命周期操作结束 |
| HEAD | `/api/plugins/{id}/readme` | 200，无正文，text/plain | 400、404、500 | 需 | 仅检查存在性，不检查大小和 UTF-8 内容 |
| GET | `/api/plugins/{id}/readme` | 200 text/plain UTF-8 | 400、404、500 | 需 | README 内容，上限 512 KiB |
| POST | `/api/plugins/{id}/enable` | 200 JSON 插件信息 | 400、404、500 | 需 | 启动失败可返回 200，须检查 state/errors |
| POST | `/api/plugins/{id}/disable` | 200 JSON 插件信息 | 400、404、500 | 需 | 停止并保存禁用状态 |
| DELETE | `/api/plugins/{id}` | 204，无正文 | 400、404、500 | 需 | `deleteData` 查询参数必填，决定是否删除插件数据 |
| POST | `/api/plugins/scan` | 200 JSON 插件信息数组 | 500 | 需 | 单插件失败记入状态；整体扫描失败 500 |
| GET | `/api/plugins/{id}/configuration` | 200 JSON 插件配置 | 400、404、500 | 需 | 按声明补默认值，不改文件；安全模式按声明投影并遮罩敏感值；`no-store` |
| PUT | `/api/plugins/{id}/configuration` | 200 JSON 已保存规范配置 | 400、404、500 | 需 | 补默认并校验，保存后重启已启用插件；返回保存快照 |
| GET | `/api/plugins/{id}/configuration/schema` | 200 JSON 配置声明数组 | 400、404、500 | 需 | 补当前选项，安全模式隐藏敏感默认值；`no-store` |
| GET | `/api/plugins/options/settings/{source}` | 200 JSON 选项数组 | 400、500 | 需 | 当前支持 `notificationChannels` |
| GET | `/api/plugins/{id}/status` | 200 插件 JSON 状态或 `{}` | 400、404、500 | 需 | 无状态时返回空对象 |
| GET | `/api/plugins/{id}/settings/display/status` | 200 JSON 状态展示设置 | 400、404、500 | 需 | 首页展示、键选择及标题 |
| PUT | `/api/plugins/{id}/settings/display/status` | 200 JSON 已保存展示设置 | 400、404、500 | 需 | 校验并保存展示参数 |
| GET | `/api/plugins/{id}/settings/display/features` | 200 JSON 功能展示设置 | 400、404、500 | 需 | 首页展示、功能选择及标题 |
| PUT | `/api/plugins/{id}/settings/display/features` | 200 JSON 已保存展示设置 | 400、404、500 | 需 | 校验并保存展示参数 |
| GET | `/api/plugins/{id}/settings/display/configuration` | 200 JSON 配置展示设置 | 400、404、500 | 需 | 是否注册到首页 |
| PUT | `/api/plugins/{id}/settings/display/configuration` | 200 JSON 已保存展示设置 | 400、404、500 | 需 | 保存 registerOnHome |
| GET | `/api/plugins/{id}/features` | 200 JSON 功能声明数组 | 400、404、500 | 需 | 来自插件声明文件 |
| POST | `/api/plugins/{id}/features/{featureId}/execute` | 200 JSON `{ succeeded, message }` | 400、404、500 | 需 | 插件主动返回 succeeded=false 仍为 200；执行异常 500 |
| POST | `/api/plugins/{id}/features/{featureId}/page-sessions` | 200 JSON `{ id, pluginName, feature, webVersion }` | 400、404、500 | 需 | 创建功能页会话 |
| POST | `/api/plugin-page-sessions/{sessionId}/messages` | 200 插件返回的 JSON | 400、404、413、500 | 需 | 消息上限 64 KiB；会话不存在/失效 404，插件内部异常 500 |
| POST | `/api/plugin-page-sessions/{sessionId}/uploads` | 200 插件返回的 JSON | 400、404、413、415、500 | 需 | application/octet-stream，单块上限 1 MiB；query 含 command 和 JSON 对象 parameters；应用自行分块 |
| DELETE | `/api/plugin-page-sessions/{sessionId}` | 204，无正文 | 500 | 需 | 幂等关闭，不存在也成功 |
| POST | `/api/plugin-page-sessions/{sessionId}/heartbeat` | 204，无正文 | 404、500 | 需 | 更新会话活动时间，不存在/失效 404 |
| GET | `/api/plugins/{id}/events` | 200 SSE 插件 JSON 状态 | 400、404、500 | 需 | 发送可用初始状态及后续变化，允许丢弃待发送的旧状态；`no-cache` |
| POST | `/api/plugins/{id}/web/log` | 200 JSON `{ accepted: true }` | 400、404、500 | 需 | 插件禁用时 400，ProblemDetails 含 `code: "plugin_disabled"` |
| GET | `/plugin-assets/{id}/v/{version}/{**path}` | 200 静态资源，按资源类型返回 | 400、404、500 | 需 | 版本不匹配 404；非 HTML/JSON 资源使用私有长期缓存 |
| GET | `/plugin-assets/{id}/{**path}` | 200 静态资源，按资源类型返回 | 400、404、500 | 需 | 非版本路径 `no-store` |
| GET、HEAD | `/`、`/login` 及非文件型 SPA 深层路径 | 200 HTML；HEAD 无正文 | 302、401、404 | 条件 | 已认证或未设密码时进入 SPA；未认证页面导航按策略跳转 /login 或伪装首页；`/login` 与 POST 登录共用路径 |
| GET、HEAD | `/assets/{**path}` 及 wwwroot 静态文件 | 200/304，按文件类型返回；支持范围的文件可为 206 | 404、416 | 条件 | 普通静态资源可匿名；伪装模式有自身资源/导航策略；index.html 为 no-cache，哈希 assets 私有长期缓存 |
| 任意方法 | 未注册的 `/api` 或 `/api/{**path}` | — | 404 | 需 | 返回“API 不存在”ProblemDetails；按路径段判断，不包括 /apix，不覆盖业务 404 |
| 不支持的方法 | 已注册的 API 路径 | — | 405 | 需 | 由路由器生成正确 Allow，兜底不接管；认证/安全模式可先返回 401/403 |

服务器或反向代理也可能提前拒绝请求。请求取消可直接终止响应，不约定独立状态码
