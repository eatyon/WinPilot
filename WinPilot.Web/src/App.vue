<script setup lang="ts">
import AppTooltip from './components/AppTooltip.vue';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { RouterView, useRoute } from 'vue-router';
import { NBackTop, NButton, NLayout, NLayoutHeader, NLayoutSider, NScrollbar } from 'naive-ui';
import { readSidebarCollapsed, saveSidebarCollapsed } from './ui/navigationLayout';
import AppearanceSettingsDrawer from './components/AppearanceSettingsDrawer.vue';
import AppPageView from './components/AppPageView.vue';
import AppTheme from './components/AppTheme.vue';
import AppNavigation from './components/AppNavigation.vue';
import AppIcon from './components/AppIcon.vue';
import { getPageScroller } from './ui/scrollLock';
import { createBackTopVisibility } from './ui/backTopVisibility';
import {
  safeModeEnabled,
  startSafeModeSync,
  stopSafeModeSync,
} from './features/security/safeMode';
const route = useRoute();
function handleBackTopClick(event: MouseEvent) {
  if (!document.documentElement.classList.contains('interaction-animations-disabled')) return;
  event.stopImmediatePropagation();
  getPageScroller().scrollTo({ top: 0, behavior: 'instant' });
}
const backTopVisibility = createBackTopVisibility();
const backTopShown = ref(false);
function updateBackTop(event: Event) {
  const scroller = event.target as HTMLElement;
  backTopShown.value = backTopVisibility.update(Math.min(scroller.scrollTop, Math.max(0, scroller.scrollHeight - scroller.clientHeight)));
}
watch(() => route.path, () => {
  backTopVisibility.reset();
  backTopShown.value = false;
});
const pageTitle = computed(() => String(route.meta.title ?? 'WinPilot'));
const desktopMedia = window.matchMedia('(min-width: 600px)');
const isDesktop = ref(desktopMedia.matches);
const appearanceSettingsOpen = ref(false);
const sidebarMedia = window.matchMedia('(max-width: 1023px)');
let preferredSidebarCollapsed = readSidebarCollapsed();
const sidebarCollapsed = ref(sidebarMedia.matches || preferredSidebarCollapsed);
function updateSidebarWidth() {
  sidebarCollapsed.value = sidebarMedia.matches || preferredSidebarCollapsed;
}
function setSidebarCollapsed(collapsed: boolean) {
  sidebarCollapsed.value = preferredSidebarCollapsed = collapsed;
  saveSidebarCollapsed(collapsed);
}
function updateViewport() { isDesktop.value = desktopMedia.matches; }
const isPublicPage = computed(() => route.meta.public === true);
watch(
  () => route.matched.length ? isPublicPage.value : undefined,
  (publicPage) => {
    if (publicPage === undefined) return;
    if (publicPage) stopSafeModeSync();
    else startSafeModeSync();
  },
  { immediate: true },
);
onMounted(() => {
  desktopMedia.addEventListener('change', updateViewport);
  sidebarMedia.addEventListener('change', updateSidebarWidth);
});
onUnmounted(() => {
  desktopMedia.removeEventListener('change', updateViewport);
  sidebarMedia.removeEventListener('change', updateSidebarWidth);
  stopSafeModeSync();
});
</script>
<template>
  <AppTheme>
    <RouterView v-if="isPublicPage" />
    <NLayout v-else-if="route.matched.length" class="app-frame" :content-class="`app-shell${sidebarCollapsed ? ' sidebar-collapsed' : ''}`">
      <NLayoutHeader class="app-header" role="banner">
        <AppTooltip v-if="isDesktop" :text="sidebarCollapsed ? '展开导航栏' : '收起导航栏'">
          <NButton
            quaternary
            class="app-icon-button"
            :aria-label="sidebarCollapsed ? '展开导航栏' : '收起导航栏'"
            :aria-expanded="!sidebarCollapsed"
            aria-controls="desktop-navigation"
            @click="setSidebarCollapsed(!sidebarCollapsed)"
          >
            <template #icon><AppIcon :name="sidebarCollapsed ? 'menu-expand' : 'menu-collapse'" /></template>
          </NButton>
        </AppTooltip>
        <AppNavigation v-else mobile :collapsed="false" />
        <h1 v-if="!isDesktop" class="mobile-page-title">
          <span class="mobile-page-title-text">{{ pageTitle }}</span>
          <AppTooltip v-if="safeModeEnabled" text="安全模式已开启，部分功能不可用">
            <span class="safe-mode-indicator" role="img" aria-label="安全模式已开启">
              <AppIcon name="lock" />
            </span>
          </AppTooltip>
        </h1>
        <div v-if="isDesktop" class="app-brand-group">
          <span class="app-brand">
            <span class="product-symbol" aria-hidden="true"></span>
            WinPilot
          </span>
          <AppTooltip v-if="safeModeEnabled" text="安全模式已开启，部分功能不可用">
            <span class="safe-mode-indicator" role="img" aria-label="安全模式已开启">
              <AppIcon name="lock" />
            </span>
          </AppTooltip>
        </div>
        <AppTooltip text="外观设置">
          <NButton quaternary class="app-icon-button appearance-settings-trigger" aria-label="外观设置" @click="appearanceSettingsOpen = true">
            <template #icon><AppIcon name="palette" /></template>
          </NButton>
        </AppTooltip>
      </NLayoutHeader>
      <AppearanceSettingsDrawer v-model:show="appearanceSettingsOpen" />
      <NLayout has-sider class="app-sidebar-layout">
        <NLayoutSider
          id="desktop-navigation"
          :collapsed="sidebarCollapsed"
          class="sidebar"
          role="complementary"
          aria-label="主导航"
          collapse-mode="width"
          :width="240"
          :collapsed-width="64"
          :native-scrollbar="false"
          content-class="sidebar-content"
          bordered
        >
          <AppNavigation :collapsed="sidebarCollapsed" />
        </NLayoutSider>
      </NLayout>
      <NScrollbar class="app-workspace" data-page-scroller @scroll="updateBackTop">
        <AppTooltip text="回到顶部">
          <NBackTop
            :listen-to="getPageScroller"
            :right="isDesktop ? 100 : 50"
            :bottom="isDesktop ? 40 : 20"
            :show="backTopShown"
            role="button"
            tabindex="0"
            aria-label="回到顶部"
            @click.capture="handleBackTopClick"
            @keydown.enter.prevent="($event.currentTarget as HTMLElement).click()"
            @keydown.space.prevent="($event.currentTarget as HTMLElement).click()"
          />
        </AppTooltip>
        <main class="page-content" :aria-label="`${pageTitle}内容`">
          <AppPageView />
        </main>
      </NScrollbar>
    </NLayout>
  </AppTheme>
</template>

<style>
.product-symbol { width: 28px; height: 28px; flex: 0 0 28px; }
.safe-mode-indicator { display: inline-flex; flex: none; align-items: center; margin-inline-start: 6px; color: var(--text-muted); vertical-align: middle; transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.safe-mode-indicator .app-icon { width: 15px; height: 15px; }

.n-back-top:focus-visible { outline: 2px solid var(--accent-focus-ring); outline-offset: 2px; }
</style>
