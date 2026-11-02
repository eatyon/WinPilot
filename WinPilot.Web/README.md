# WinPilot Web

Vue 3、TypeScript、Vite、Vue Router、Naive UI。Node.js 版本要求见 `package.json`，依赖由 `package-lock.json` 锁定。

目录和整体构建见 [仓库结构](../Docs/REPOSITORY_STRUCTURE.md#前端目录)。

## 源码约定

Vue 组件及对应适配文件用 PascalCase，普通 TypeScript 模块用 camelCase，组合函数用 `useXxx`，CSS 类名用 kebab-case。导入路径与文件名大小写一致。

应用异步结果前检查取消状态及当前目标；卸载时释放请求和页面资源。取消不报错，弹窗内容在离场动画结束后清理。

## 组件与主题

优先复用现有组件和主题。弹窗、悬停提示和加载反馈分别复用 `AppModal`、`AppTooltip`、`useDelayedLoading`；业务自行防止重复提交。

`src/ui/componentTheme.ts` 配置组件主题，`AppThemeTokens.vue` 映射页面变量。样式按使用范围归属：

- 页面或组件专用样式放在对应 `.vue`。
- 业务内共用样式放在 `features/<功能>/styles.css`，由 `styles/features.css` 导入。
- 全局基础与跨业务样式放在 `styles`，入口为 `src/style.css`。

调整样式时保持作用域、优先级与加载顺序。

## 插件共享 UI

共享 UI 清单位于 `src/features/plugins/page/pluginPageComponents.ts`，逐项动态导入并复用本体实现。新增导出时检查构建分块和宿主测试；插件调用方式见 [插件开发指南](../Docs/PLUGIN_DEVELOPMENT.md#样式与布局)。

## 验证与构建

以下命令在本目录执行：

```powershell
npm ci
npm test
npm run build -- --outDir ../../temp/web-validation-dist
```

`npm test` 覆盖 API、插件宿主和 UI，构建包含 TypeScript 检查。上方构建使用隔离目录；默认 `npm run build` 会清空并写入 `../WinPilot/wwwroot`。

开发服务：`npm run dev -- --host 127.0.0.1 --port 5178 --strictPort`，需另行提供后端 API。涉及系统操作的测试使用隔离环境，构建产物需另行部署到运行目录。

依赖及字体许可见 [第三方组件声明](../THIRD_PARTY_NOTICES.md)，更新依赖时同步核对声明与随包许可。
