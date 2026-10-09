<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { RouterLink, RouterView, useRoute } from 'vue-router';
import AppIcon from './components/AppIcon.vue';
import { getPageScroller, lockScroll } from './ui/scrollLock';
import { connectionStatus, markConnectionRestored } from './features/connection/connectionStatus';
import {
  safeModeEnabled,
  safeModeMessage,
  startSafeModeSync,
  stopSafeModeSync,
} from './features/security/safeMode';
const route = useRoute(),
  mobileMenuOpen = ref(false);
const pageTitle = computed(() => String(route.meta.title ?? 'WinPilot'));
const pageDescription = computed(() => String(route.meta.description ?? '本机控制与自动化'));
const isPluginsSection = computed(() => route.path === '/plugins' || route.path.startsWith('/plugins/'));
const isPublicPage = computed(() => route.meta.public === true);
const showPageTitleLock = computed(
  () => safeModeEnabled.value && ['/terminal', '/files', '/settings'].includes(route.path),
);
const toasts = ref<{ id: number; message: string; type: 'success' | 'error' }[]>([]);
let releaseMobileMenuScroll: (() => void) | undefined;
watch(mobileMenuOpen, (open) => {
  releaseMobileMenuScroll?.();
  releaseMobileMenuScroll = open ? lockScroll(getPageScroller()) : undefined;
}, { flush: 'sync' });
let toastSequence = 0;
function showToast(event: Event) {
  const detail = (event as CustomEvent<{ message: string; type?: 'success' | 'error' }>).detail;
  if (!detail?.message) return;
  const item = { id: ++toastSequence, message: detail.message, type: detail.type ?? ('error' as const) };
  toasts.value.push(item);
  window.setTimeout(() => {
    toasts.value = toasts.value.filter((toast) => toast.id !== item.id);
  }, 4200);
}
function handleSessionExpired() {
  markConnectionRestored();
  window.location.assign('/login');
}
watch(
  isPublicPage,
  (publicPage) => {
    if (publicPage) stopSafeModeSync();
    else startSafeModeSync();
  },
  { immediate: true },
);
onMounted(() => {
  window.addEventListener('winpilot:toast', showToast);
  window.addEventListener('winpilot:session-expired', handleSessionExpired);
});
onUnmounted(() => {
  releaseMobileMenuScroll?.();
  window.removeEventListener('winpilot:toast', showToast);
  window.removeEventListener('winpilot:session-expired', handleSessionExpired);
  stopSafeModeSync();
});
</script>
<template>
  <div
    v-if="isPublicPage && connectionStatus === 'disconnected'"
    class="connection-notice connection-notice-public"
    role="alert"
  >
    与 WinPilot 的连接已中断
  </div>
  <RouterView v-if="isPublicPage" />
  <div v-else class="app-shell">
    <aside class="sidebar" aria-label="主导航">
      <RouterLink class="product-mark" to="/settings?tab=about">
        <span class="product-symbol">W</span>
        <span>WinPilot</span>
        <span v-if="safeModeEnabled" class="safe-mode-brand-lock" title="安全模式已开启，可通过本机托盘关闭">
          <AppIcon name="lock" />
        </span>
      </RouterLink>
      <nav class="sidebar-nav">
        <p class="nav-section-label">工作区</p>
        <RouterLink exact-active-class="active" to="/home">
          <AppIcon name="home" />
          <span>首页</span>
        </RouterLink>
        <RouterLink exact-active-class="active" to="/desktop">
          <AppIcon name="desktop" />
          <span>桌面</span>
        </RouterLink>
        <RouterLink exact-active-class="active" to="/programs">
          <AppIcon name="apps" />
          <span>程序</span>
        </RouterLink>
        <RouterLink exact-active-class="active" to="/tasks">
          <AppIcon name="schedule" />
          <span>任务</span>
        </RouterLink>
        <RouterLink :class="{ active: isPluginsSection }" to="/plugins">
          <AppIcon name="plugins" />
          <span>插件</span>
        </RouterLink>
        <RouterLink exact-active-class="active" to="/terminal">
          <AppIcon name="terminal" />
          <span>终端</span>
        </RouterLink>
        <RouterLink exact-active-class="active" to="/files">
          <AppIcon name="files" />
          <span>文件</span>
        </RouterLink>
        <RouterLink exact-active-class="active" to="/logs">
          <AppIcon name="logs" />
          <span>日志</span>
        </RouterLink>
      </nav>
      <div class="sidebar-footer">
        <RouterLink exact-active-class="active" to="/settings">
          <AppIcon name="settings" />
          <span>设置</span>
        </RouterLink>
      </div>
    </aside>
    <div class="workspace">
      <header class="workspace-header">
        <div>
          <p class="page-context">WinPilot</p>
          <h1 class="workspace-title">
            {{ pageTitle }}
            <span v-if="showPageTitleLock" class="safe-mode-title-lock" :title="safeModeMessage">
              <AppIcon name="lock" />
            </span>
          </h1>
          <p class="page-description">{{ pageDescription }}</p>
        </div>
      </header>
      <main class="page-content">
        <div v-if="connectionStatus === 'disconnected'" class="connection-notice" role="alert">
          与 WinPilot 的连接已中断
        </div>
        <RouterView />
      </main>
    </div>
    <nav class="mobile-nav" aria-label="移动导航">
      <RouterLink exact-active-class="active" to="/home">
        <AppIcon name="home" />
        <span>首页</span>
      </RouterLink>
      <RouterLink exact-active-class="active" to="/desktop">
        <AppIcon name="desktop" />
        <span>桌面</span>
      </RouterLink>
      <button type="button" :class="{ active: mobileMenuOpen }" @click="mobileMenuOpen = true">
        <AppIcon name="more" />
        <span>更多</span>
      </button>
    </nav>
    <div v-if="mobileMenuOpen" class="mobile-menu-backdrop" @click.self="mobileMenuOpen = false">
      <section class="mobile-menu">
        <div class="mobile-menu-header">
          <strong>更多功能</strong>
          <button class="modal-close" type="button" aria-label="关闭" @click="mobileMenuOpen = false">
            <AppIcon name="close" />
          </button>
        </div>
        <RouterLink to="/programs" @click="mobileMenuOpen = false">
          <AppIcon name="apps" />
          <span>程序</span>
        </RouterLink>
        <RouterLink to="/tasks" @click="mobileMenuOpen = false">
          <AppIcon name="schedule" />
          <span>任务</span>
        </RouterLink>
        <RouterLink to="/plugins" @click="mobileMenuOpen = false">
          <AppIcon name="plugins" />
          <span>插件</span>
        </RouterLink>
        <RouterLink to="/terminal" @click="mobileMenuOpen = false">
          <AppIcon name="terminal" />
          <span>终端</span>
        </RouterLink>
        <RouterLink to="/files" @click="mobileMenuOpen = false">
          <AppIcon name="files" />
          <span>文件</span>
        </RouterLink>
        <RouterLink to="/logs" @click="mobileMenuOpen = false">
          <AppIcon name="logs" />
          <span>日志</span>
        </RouterLink>
        <div class="mobile-menu-divider"></div>
        <RouterLink to="/settings" @click="mobileMenuOpen = false">
          <AppIcon name="settings" />
          <span>设置</span>
        </RouterLink>
      </section>
    </div>
  </div>
  <div class="toast-stack" aria-live="polite">
    <div v-for="toast in toasts" :key="toast.id" class="toast" :class="`toast-${toast.type}`">
      {{ toast.message }}
    </div>
  </div>
</template>

<style scoped>
.mobile-menu-header {
  margin-bottom: 8px;
  border-bottom: 1px solid var(--border);
}
.mobile-menu-header .modal-close {
  padding: 0;
  color: var(--text-muted);
}
.mobile-menu-header .modal-close:hover {
  color: var(--text);
}
.mobile-menu-header .modal-close .app-icon {
  width: 18px;
  height: 18px;
}
</style>
