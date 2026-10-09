import { createRouter, createWebHistory } from 'vue-router'
import { apiFetch } from '../api/client'
import { connectionStatus, connectionRestoreVersion, markConnectionLostUnlessRestored } from '../features/connection/connectionStatus'
import { showToast } from '../ui/toast'
import { getPageScroller } from '../ui/scrollLock'

const DesktopView = () => import('../features/desktop/DesktopView.vue')
const HomeView = () => import('../features/home/HomeView.vue')
const LogsView = () => import('../features/logs/LogsView.vue')
const SettingsView = () => import('../features/settings/SettingsView.vue')
const TasksView = () => import('../features/tasks/TasksView.vue')
const ProgramsView = () => import('../features/programs/ProgramsView.vue')
const TerminalView = () => import('../features/terminal/TerminalView.vue')
const FilesView = () => import('../features/files/FilesView.vue')
const PluginsView = () => import('../features/plugins/PluginsView.vue')
const PluginDetailView = () => import('../features/plugins/PluginDetailView.vue')
const PluginInfoView = () => import('../features/plugins/PluginInfoView.vue')
const PluginMarketView = () => import('../features/plugins/PluginMarketView.vue')
const PluginPageView = () => import('../features/plugins/PluginPageView.vue')
const LoginView = () => import('../features/auth/LoginView.vue')

const pluginPageMeta = { title: '插件', description: '浏览和管理扩展功能插件' }
const loadedRoutePaths = new Set<string>()

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', component: LoginView, meta: { public: true, title: '登录', description: 'WinPilot 管理员登录' } },
    { path: '/', redirect: '/home' },
    { path: '/home', component: HomeView, meta: { title: '首页', description: '查看电脑系统状态和扩展功能' } },
    { path: '/desktop', component: DesktopView, meta: { title: '桌面', description: '查看桌面并按需发送键盘输入' } },
    { path: '/programs', component: ProgramsView, meta: { title: '程序', description: '查看进程、启动程序和快捷标签' } },
    { path: '/tasks', component: TasksView, meta: { title: '任务', description: '管理后台自动化任务' } },
    { path: '/terminal', component: TerminalView, meta: { title: '终端', description: '在浏览器中使用本机 CMD 或 PowerShell' } },
    { path: '/files', component: FilesView, meta: { title: '文件', description: '浏览和管理本机文件与文件夹' } },
    { path: '/logs', component: LogsView, meta: { title: '日志', description: '查看 WinPilot 的运行记录' } },
    { path: '/plugins', component: PluginsView, meta: pluginPageMeta },
    { path: '/plugins/market', component: PluginMarketView, meta: pluginPageMeta },
    { path: '/plugins/:pluginId', component: PluginDetailView, meta: pluginPageMeta },
    { path: '/plugins/:pluginId/features/:featureId', component: PluginPageView, meta: pluginPageMeta },
    { path: '/plugins/:pluginId/info', component: PluginInfoView, meta: pluginPageMeta },
    { path: '/settings', component: SettingsView, meta: { title: '设置', description: '管理运行、外观、通知与安全等偏好设置' } },
    { path: '/:pathMatch(.*)*', redirect: '/' }
  ]
})

router.beforeEach(async to => {
  if (to.meta.public === true) return true
  if (connectionStatus.value === 'online' && to.matched.every(record => loadedRoutePaths.has(record.path))) return true
  const restoredBeforeRequest = connectionRestoreVersion()
  try {
    const response = await apiFetch('/health', { cache: 'no-store', signal: AbortSignal.timeout(1500) })
    if (response.ok) return true
  } catch { }
  markConnectionLostUnlessRestored(restoredBeforeRequest)
  return false
})

router.afterEach((to, from, failure) => {
  if (failure) return
  for (const record of to.matched) loadedRoutePaths.add(record.path)
  if (to.path !== from.path && to.meta.public !== true) getPageScroller().scrollTop = 0
})

router.onError(error => {
  console.error('页面导航失败', error)
  const restoredBeforeRequest = connectionRestoreVersion()
  void fetch('/health', { cache: 'no-store', signal: AbortSignal.timeout(2500) })
    .then(response => { if (response.ok) showToast('页面加载失败，请刷新网页'); else markConnectionLostUnlessRestored(restoredBeforeRequest) })
    .catch(() => markConnectionLostUnlessRestored(restoredBeforeRequest))
})
