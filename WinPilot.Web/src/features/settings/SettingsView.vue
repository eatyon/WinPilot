<script setup lang="ts">
import AppLoadError from '../../components/AppLoadError.vue';
import { NButton, NCard, NTab, NTabs } from 'naive-ui';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { createSettingsRequests } from './settingsRequests';
import { useRoute, useRouter } from 'vue-router';
import { readApiError, isAbortError, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import GeneralSettings from './tabs/GeneralSettings.vue';
import NotificationSettings from './tabs/NotificationSettings.vue';
import AdvancedSettings from './tabs/AdvancedSettings.vue';
import SecuritySettings from './tabs/SecuritySettings.vue';
import AboutSettings from './tabs/AboutSettings.vue';
import { safeModeEnabled } from '../security/safeMode';
import { usePanelEffects } from '../../ui/panelEffects';
interface SettingsResponse {
  port: number;
  lanAccessEnabled: boolean;
  startupMode: string;
}
type SaveKey = 'port' | 'lanAccess' | 'startupMode';
type SaveJob = { key: SaveKey; run: () => Promise<void>; resolvers: (() => void)[] };
const requests = createSettingsRequests();
const tabs = [
  { key: 'general', label: '常规', component: GeneralSettings },
  { key: 'notification', label: '通知', component: NotificationSettings },
  { key: 'security', label: '安全', component: SecuritySettings },
  { key: 'advanced', label: '高级', component: AdvancedSettings },
  { key: 'about', label: '关于', component: AboutSettings },
] as const;
const route = useRoute();
const router = useRouter();
const activeKey = computed(() =>
  tabs.some((tab) => tab.key === route.query.tab) ? String(route.query.tab) : 'general',
);
const activeTab = computed(() => tabs.find((tab) => tab.key === activeKey.value) ?? tabs[0]);
const { pageTransitionEnabled } = usePanelEffects();
const tabTransition = ref('settings-slide-next');
watch(activeKey, (next, previous) => {
  tabTransition.value = tabs.findIndex(tab => tab.key === next) > tabs.findIndex(tab => tab.key === previous)
    ? 'settings-slide-next' : 'settings-slide-previous';
}, { flush: 'sync' });
const settingsReadOnly = computed(
  () => safeModeEnabled.value && activeKey.value !== 'about',
);
const port = ref('9123');
const lanAccessEnabled = ref(false);
const startupMode = ref('Disabled');
const isLoading = ref(false);
const savingKey = ref<SaveKey | ''>('');
const saveVersions = { port: 0, lanAccess: 0, startupMode: 0 };
const saveQueue: SaveJob[] = [];
let savedLanAccess = false;
let savedStartupMode = 'Disabled';
const errorMessage = ref('');
const settingsLoaded = ref(false);
const parsedPort = computed(() => Number(port.value));
const isPortValid = computed(
  () => Number.isInteger(parsedPort.value) && parsedPort.value >= 1 && parsedPort.value <= 65535,
);
function selectTab(key: string) {
  if (key !== activeKey.value) void router.push({ query: { ...route.query, tab: key } });
}
onMounted(() => {
  if (activeKey.value === 'general') void loadSettings();
});
watch(activeKey, (key) => {
  if (key === 'general' && !settingsLoaded.value) void loadSettings();
});
onBeforeUnmount(() => {
  requests.dispose();
  for (const job of saveQueue.splice(0)) for (const resolve of job.resolvers) resolve();
  errorMessage.value = '';
});
async function loadSettings() {
  if (!requests.active || isLoading.value) return;
  isLoading.value = true;
  try {
    const response = await requests.fetch('/api/settings/general');
    if (!response.ok)
      throw new Error(await readApiError(response, '无法读取当前设置', requests.json));
    const settings = (await requests.json(response)) as SettingsResponse;
    port.value = String(settings.port);
    lanAccessEnabled.value = settings.lanAccessEnabled;
    startupMode.value = settings.startupMode ?? 'Disabled';
    savedLanAccess = lanAccessEnabled.value;
    savedStartupMode = startupMode.value;
    settingsLoaded.value = true;
    errorMessage.value = '';
  } catch (error) {
    if (requests.active && !isAbortError(error))
      errorMessage.value = userErrorMessage(error, '无法读取当前设置');
  } finally {
    if (requests.active) isLoading.value = false;
  }
}
// 常规设置按字段排队保存；同字段尚未执行的修改合并为最新一次，其他字段不被覆盖。
function enqueueSave(key: SaveKey, run: () => Promise<void>): Promise<void> {
  return new Promise((resolve) => {
    const queued = saveQueue.find((job) => job.key === key);
    if (queued) {
      queued.run = run;
      queued.resolvers.push(resolve);
    } else saveQueue.push({ key, run, resolvers: [resolve] });
    void drainSaves();
  });
}
async function drainSaves() {
  if (savingKey.value) return;
  while (saveQueue.length) {
    const job = saveQueue.shift()!;
    // 只有当前保存项进入处理状态，其他设置保持独立交互。
    savingKey.value = job.key;
    try {
      if (requests.active && !safeModeEnabled.value) await job.run();
    } finally {
      savingKey.value = '';
      for (const resolve of job.resolvers) resolve();
    }
  }
}
function saveLanAccess(enabled: boolean) {
  if (safeModeEnabled.value || !settingsLoaded.value || !requests.active) return;
  const version = ++saveVersions.lanAccess;
  errorMessage.value = '';
  lanAccessEnabled.value = enabled;
  return enqueueSave('lanAccess', async () => {
    try {
      const response = await requests.fetch('/api/settings/general/lan-access', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled }),
      });
      if (!response.ok)
        throw new Error(await readApiError(response, '无法更新局域网访问设置', requests.json));
      requests.ensureActive();
      savedLanAccess = enabled;
      if (version === saveVersions.lanAccess)
        showToast('局域网访问设置保存成功，重启 WinPilot 后生效', 'success');
    } catch (error) {
      if (!requests.active || isAbortError(error) || version !== saveVersions.lanAccess) return;
      lanAccessEnabled.value = savedLanAccess;
      showToast(userErrorMessage(error, '无法更新局域网访问设置'));
    }
  });
}
function savePort() {
  if (safeModeEnabled.value || !settingsLoaded.value || !requests.active) return;
  errorMessage.value = '';
  if (!isPortValid.value) {
    errorMessage.value = '端口必须是 1 到 65535 之间的整数';
    return;
  }
  const value = parsedPort.value;
  const draft = port.value;
  const version = ++saveVersions.port;
  return enqueueSave('port', async () => {
    try {
      const response = await requests.fetch('/api/settings/general/port', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ port: value }),
      });
      if (!response.ok)
        throw new Error(await readApiError(response, '无法保存端口设置', requests.json));
      const settings = (await requests.json(response)) as SettingsResponse;
      if (version === saveVersions.port && port.value === draft) port.value = String(settings.port);
      if (version === saveVersions.port) showToast('端口保存成功，重启 WinPilot 后生效', 'success');
    } catch (error) {
      if (!requests.active || isAbortError(error) || version !== saveVersions.port) return;
      showToast(userErrorMessage(error, '无法保存端口设置'));
    }
  });
}
function saveStartupMode(mode: string) {
  if (safeModeEnabled.value || !settingsLoaded.value || !requests.active) return;
  const version = ++saveVersions.startupMode;
  startupMode.value = mode;
  errorMessage.value = '';
  return enqueueSave('startupMode', async () => {
    try {
      const response = await requests.fetch('/api/settings/general/startup-mode', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ startupMode: mode }),
      });
      if (!response.ok)
        throw new Error(await readApiError(response, '无法保存开机启动设置', requests.json));
      const settings = (await requests.json(response)) as SettingsResponse;
      savedStartupMode = settings.startupMode;
      if (version === saveVersions.startupMode) {
        startupMode.value = settings.startupMode;
        showToast('开机启动设置保存成功', 'success');
      }
    } catch (error) {
      if (!requests.active || isAbortError(error) || version !== saveVersions.startupMode) return;
      startupMode.value = savedStartupMode;
      showToast(userErrorMessage(error, '无法保存开机启动设置'));
    }
  });
}
</script>
<template>
  <section class="settings-view load-error-layout" :aria-busy="isLoading">
    <nav aria-label="设置分页">
      <NCard :content-style="{ padding: '4px' }">
        <NTabs
          :value="activeKey"
          type="segment"
          :theme-overrides="{ tabColorSegment: 'color-mix(in srgb, var(--accent-solid) 12%, var(--surface))', tabTextColorActiveSegment: 'var(--accent-text)' }"
          @update:value="selectTab"
        >
          <NTab
            v-for="tab in tabs"
            :key="tab.key"
            :name="tab.key"
          >
            <NButton text :type="activeKey === tab.key ? 'primary' : 'default'" :aria-current="activeKey === tab.key ? 'page' : undefined">{{ tab.label }}</NButton>
          </NTab>
        </NTabs>
      </NCard>
    </nav>
    <AppLoadError :show="Boolean(errorMessage && activeKey === 'general' && !settingsLoaded)" :retry="loadSettings">{{ errorMessage }}</AppLoadError>
    <form class="settings-form" @submit.prevent>
      <Transition :name="tabTransition" :css="pageTransitionEnabled" :mode="pageTransitionEnabled ? 'out-in' : undefined">
        <div :key="activeKey" data-page-transition :class="{ 'safe-mode-locked': settingsReadOnly }" :inert="settingsReadOnly">
          <component
            :is="activeTab.component"
            :key="activeKey === 'notification' ? `notification-${safeModeEnabled}` : activeKey"
            v-model:port="port"
            :lan-access-enabled="lanAccessEnabled"
            @update:lan-access-enabled="saveLanAccess"
            :startup-mode="startupMode"
            @save-port="savePort"
            @update:startup-mode="saveStartupMode"
            :is-loading="isLoading"
            :is-loaded="settingsLoaded"
            :saving-key="savingKey"
            :is-port-valid="isPortValid"
            :error-message="activeKey === 'general' && settingsLoaded ? errorMessage : ''"
          />
        </div>
      </Transition>
    </form>
  </section>
</template>

<style>
.settings-view { display: grid; width: 100%; gap: var(--page-card-row-gap) 18px; }
.settings-form { display: grid; gap: 16px; }
</style>

<style scoped>
.settings-form { overflow-x: clip; }
.settings-slide-next-enter-active, .settings-slide-next-leave-active,
.settings-slide-previous-enter-active, .settings-slide-previous-leave-active {
  transition: transform 180ms ease, opacity 180ms ease;
}
.settings-slide-next-enter-from, .settings-slide-previous-leave-to { opacity: 0; transform: translateX(24px); }
.settings-slide-next-leave-to, .settings-slide-previous-enter-from { opacity: 0; transform: translateX(-24px); }
.settings-slide-next-leave-active, .settings-slide-previous-leave-active { pointer-events: none; }
@media (prefers-reduced-motion: reduce) {
  .settings-slide-next-enter-active, .settings-slide-next-leave-active,
  .settings-slide-previous-enter-active, .settings-slide-previous-leave-active { transition: none; }
  .settings-slide-next-enter-from, .settings-slide-next-leave-to,
  .settings-slide-previous-enter-from, .settings-slide-previous-leave-to { opacity: 1; transform: none; }
}
</style>
