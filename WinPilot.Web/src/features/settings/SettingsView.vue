<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { createSettingsRequests } from './SettingsRequests';
import { useRoute, useRouter } from 'vue-router';
import { isAbortError, userErrorMessage } from '../../api/client';
import { showToast } from '../../ui/toast';
import GeneralSettings from './tabs/GeneralSettings.vue';
import AppearanceSettings from './tabs/AppearanceSettings.vue';
import NotificationSettings from './tabs/NotificationSettings.vue';
import AdvancedSettings from './tabs/AdvancedSettings.vue';
import SecuritySettings from './tabs/SecuritySettings.vue';
import AboutSettings from './tabs/AboutSettings.vue';
import { safeModeEnabled } from '../security/safeMode';
interface SettingsResponse {
  port: number;
  lanAccessEnabled: boolean;
  startupMode: string;
}
const requests = createSettingsRequests();
const tabs = [
  { key: 'general', label: '常规', component: GeneralSettings },
  { key: 'appearance', label: '外观', component: AppearanceSettings },
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
const settingsReadOnly = computed(
  () => safeModeEnabled.value && !['appearance', 'about'].includes(activeKey.value),
);
const port = ref('9123');
const lanAccessEnabled = ref(false);
const startupMode = ref('Disabled');
const isLoading = ref(true);
const isSaving = ref(false);
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
  void loadSettings();
});
onBeforeUnmount(() => {
  requests.dispose();
  errorMessage.value = '';
});
async function loadSettings() {
  isLoading.value = true;
  errorMessage.value = '';
  try {
    const response = await requests.fetch('/api/settings');
    if (!response.ok) throw new Error('无法读取当前设置');
    const settings = (await requests.json(response)) as SettingsResponse;
    port.value = String(settings.port);
    lanAccessEnabled.value = settings.lanAccessEnabled;
    startupMode.value = settings.startupMode ?? 'Disabled';
    settingsLoaded.value = true;
  } catch (error) {
    if (requests.active && !isAbortError(error))
      errorMessage.value = userErrorMessage(error, '无法读取当前设置');
  } finally {
    if (requests.active) isLoading.value = false;
  }
}
async function saveLanAccess(enabled: boolean) {
  if (safeModeEnabled.value || isSaving.value || !settingsLoaded.value || !requests.active) return;
  isSaving.value = true;
  errorMessage.value = '';
  const previous = lanAccessEnabled.value;
  lanAccessEnabled.value = enabled;
  try {
    const response = await requests.fetch('/api/settings/lan-access', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    });
    if (!response.ok) throw Error();
    requests.ensureActive();
    errorMessage.value = '';
    showToast('局域网访问设置保存成功，重启 WinPilot 后生效', 'success');
  } catch (error) {
    if (!requests.active || isAbortError(error)) return;
    lanAccessEnabled.value = previous;
    errorMessage.value = userErrorMessage(error, '无法更新局域网访问设置');
  } finally {
    isSaving.value = false;
  }
}
async function savePort() {
  if (safeModeEnabled.value || isSaving.value || !settingsLoaded.value || !requests.active) return;
  errorMessage.value = '';
  if (!isPortValid.value) {
    errorMessage.value = '端口必须是 1 到 65535 之间的整数';
    return;
  }
  isSaving.value = true;
  try {
    const response = await requests.fetch('/api/settings/port', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ port: parsedPort.value }),
    });
    if (!response.ok) throw new Error('无法保存端口设置');
    const settings = (await requests.json(response)) as SettingsResponse;
    port.value = String(settings.port);
    showToast('端口保存成功，重启 WinPilot 后生效', 'success');
  } catch (error) {
    if (!requests.active || isAbortError(error)) return;
    errorMessage.value = userErrorMessage(error, '无法保存端口设置');
  } finally {
    isSaving.value = false;
  }
}
async function saveStartupMode(mode: string) {
  if (safeModeEnabled.value || isSaving.value || !settingsLoaded.value || !requests.active) return;
  const previous = startupMode.value;
  startupMode.value = mode;
  errorMessage.value = '';
  isSaving.value = true;
  try {
    const response = await requests.fetch('/api/settings/startup-mode', {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ startupMode: mode }),
    });
    if (!response.ok) throw new Error('无法保存开机启动设置');
    const settings = (await requests.json(response)) as SettingsResponse;
    startupMode.value = settings.startupMode;
    showToast('开机启动设置保存成功', 'success');
  } catch (error) {
    if (!requests.active || isAbortError(error)) return;
    startupMode.value = previous;
    errorMessage.value = userErrorMessage(error, '无法保存开机启动设置');
  } finally {
    isSaving.value = false;
  }
}
</script>
<template>
  <section class="settings-view" :aria-busy="isLoading">
    <nav class="settings-tabs" aria-label="设置分页">
      <button
        v-for="tab in tabs"
        :key="tab.key"
        type="button"
        :class="{ active: activeKey === tab.key }"
        :aria-current="activeKey === tab.key ? 'page' : undefined"
        @click="selectTab(tab.key)"
      >
        {{ tab.label }}
      </button>
    </nav>
    <p v-if="activeKey !== 'general' && errorMessage" class="status-message error" role="alert">
      {{ errorMessage }}
    </p>
    <form class="settings-form" @submit.prevent>
      <div
        :class="{ 'safe-mode-locked': settingsReadOnly, 'safe-mode-blocked': settingsReadOnly }"
        :inert="settingsReadOnly"
      >
        <component
          :is="activeTab.component"
          v-model:port="port"
          :lan-access-enabled="lanAccessEnabled"
          @update:lan-access-enabled="saveLanAccess"
          :startup-mode="startupMode"
          @save-port="savePort"
          @update:startup-mode="saveStartupMode"
          :is-loading="isLoading || !settingsLoaded"
          :is-saving="isSaving"
          :is-port-valid="isPortValid"
          :error-message="activeKey === 'general' ? errorMessage : ''"
        />
      </div>
    </form>
  </section>
</template>
