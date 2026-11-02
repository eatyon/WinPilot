<script setup lang="ts">
import { computed, h } from 'vue';
import { NButton, NDropdown, NMenu } from 'naive-ui';
import { RouterLink, useRoute, useRouter } from 'vue-router';
import AppIcon from './AppIcon.vue';

defineProps<{ collapsed: boolean; mobile?: boolean }>();
const route = useRoute();
const router = useRouter();
const activeKey = computed(() => `/${route.path.split('/')[1]}`);
function option(path: string, label: string, icon: string) {
  return {
    key: path,
    text: label,
    label: () => h(RouterLink, { to: path }, () => label),
    icon: () => h(AppIcon, { name: icon }),
  };
}
const options = [
  option('/home', '首页', 'home'),
  option('/desktop', '桌面', 'desktop'),
  option('/programs', '程序', 'apps'),
  option('/tasks', '任务', 'schedule'),
  option('/plugins', '插件', 'plugins'),
  option('/terminal', '终端', 'terminal'),
  option('/files', '文件', 'files'),
  option('/logs', '日志', 'logs'),
];
const settings = [option('/settings', '设置', 'settings')];
const mobileOptions = [
  ...options.map(item => ({ ...item, label: item.text })),
  { type: 'divider' as const, key: 'divider' },
  ...settings.map(item => ({ ...item, label: item.text })),
];
</script>

<template>
  <NDropdown v-if="mobile" trigger="click" :options="mobileOptions" :value="activeKey" @select="key => router.push(String(key))">
    <NButton quaternary class="app-icon-button" aria-label="打开导航菜单" aria-haspopup="menu">
      <template #icon><AppIcon name="menu" /></template>
    </NButton>
  </NDropdown>
  <template v-else>
    <nav class="app-menu" aria-label="工作区">
      <NMenu :value="activeKey" :options="options" :collapsed="collapsed" :collapsed-width="64" :collapsed-icon-size="22" />
    </nav>
    <div class="app-menu-footer">
      <hr class="app-menu-divider">
      <NMenu aria-label="设置" :value="activeKey" :options="settings" :collapsed="collapsed" :collapsed-width="64" :collapsed-icon-size="22" />
    </div>
  </template>
</template>

<style>
.app-menu-footer { margin-top: auto; }
.app-menu-divider { margin: 4px 8px; border: 0; border-top: 1px solid var(--border); transition: border-color .3s cubic-bezier(.4, 0, .2, 1); }
</style>
