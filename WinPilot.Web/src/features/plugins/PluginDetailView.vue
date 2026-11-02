<script setup lang="ts">
import AppLoadError from '../../components/AppLoadError.vue';
import { useModalState } from '../../ui/useModalState';
import AppLoading from '../../components/AppLoading.vue';
import { NButton, NCard, NEmpty, NText } from 'naive-ui';
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { getPageScroller } from '../../ui/scrollLock';
import {
  readApiError,
  isAbortError,
  userErrorMessage,
} from '../../api/client';
import { createPluginRequests } from './pluginRequests';
import { configurationError } from './pluginConfiguration';
import { showToast } from '../../ui/toast';
import AppModal from '../../components/AppModal.vue';
import {
  type PluginInfo,
  type PluginSettingsDisplaySettings,
  isPluginSetting,
  isPluginCollection,
  type PluginConfigurationDefinition,
  type PluginFeatureDefinition,
} from './pluginModels';
import PluginHeader from './PluginHeader.vue';
import PluginSettingField from './PluginSettingField.vue';
import { safeModeEnabled } from '../security/safeMode';


type PluginFeatureExecutionResult = { succeeded: boolean; message: string };

const requests = createPluginRequests();
const route = useRoute();
const router = useRouter();
const id = computed(() => String(route.params.pluginId ?? ''));
const plugin = ref<PluginInfo>();
const status = reactive<Record<string, unknown>>({});
const config = reactive<Record<string, unknown>>({});
const definitions = ref<PluginConfigurationDefinition[]>([]);
const features = ref<PluginFeatureDefinition[]>([]);
const settingsDisplay = ref<PluginSettingsDisplaySettings>({ registerOnHome: false });
const sections = reactive({
  status: { loading: false, loaded: false, error: '' },
  features: { loading: false, loaded: false, error: '' },
  settings: { loading: false, loaded: false, error: '' },
});
const loading = ref(false);
const hasLoaded = ref(false);
const error = ref('');
const saving = ref(false);
const toggling = ref(false);
const settingsLoaded = ref(false);
let loadGeneration = 0;
const executingFeatureId = ref('');
const busy = computed(() => loading.value || saving.value || toggling.value || Boolean(executingFeatureId.value));
const confirmingFeatureModal = useModalState<PluginFeatureDefinition | undefined>(() => undefined);
const confirmingFeature = confirmingFeatureModal.data;
let restoreTarget: { path: string; y: number } | undefined;
try {
  const saved = JSON.parse(sessionStorage.getItem('winpilot-plugin-detail-restore') ?? 'null');
  if (saved?.path === route.fullPath && Number.isFinite(saved.y)) restoreTarget = saved;
} catch {}
const restoringScroll = ref(Boolean(restoreTarget && restoreTarget.y > 0));
let stateEvents: EventSource | undefined;
let isPageActive = false;
const pluginRunning = computed(
  () => plugin.value?.enabled === true && plugin.value.state === 'Running',
);
function featureButtonLabel(feature: PluginFeatureDefinition): string {
  return feature.mode === 'page' ? '进入' : '执行';
}

function displayStatusValue(value: unknown, key?: string): string {
  if (value === null || value === undefined || value === '') return '—';
  if (typeof value === 'boolean') return value ? '是' : '否';
  if (typeof value === 'string' && (key === '开始时间' || key === '最后日志时间')) {
    const date = new Date(value);
    if (!Number.isNaN(date.getTime())) {
      const pad = (part: number) => String(part).padStart(2, '0');
      const day = `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
      const time = `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
      return `${day} ${time}`;
    }
  }
  if (Array.isArray(value)) return value.map((item) => displayStatusValue(item)).join('、') || '—';
  if (typeof value === 'object') {
    const record = value as Record<string, unknown>;
    if (
      typeof record.current === 'number' &&
      typeof record.total === 'number' &&
      typeof record.percent === 'number'
    ) {
      return `${record.current} / ${record.total} (${record.percent.toFixed(2).replace(/\.00$/, '')}%)`;
    }
    return JSON.stringify(value);
  }
  return String(value);
}

async function load(sectionKey?: keyof typeof sections) {
  const generation = ++loadGeneration;
  if (!sectionKey || sectionKey === 'status') {
    stateEvents?.close();
    stateEvents = undefined;
  }
  requests.cancelReads();
  for (const section of Object.values(sections)) section.loading = false;
  loading.value = true;
  try {
    const current = await requests.run<PluginInfo>(
      `/api/plugins/${encodeURIComponent(id.value)}?waitForLifecycle=true`,
      { cache: 'no-store' },
      (response) => {
        if (!response.ok) throw new Error('无法加载插件');
        return response.json();
      },
    );
    if (!requests.active || generation !== loadGeneration) return;
    plugin.value = current;
    error.value = '';
    const active = () => requests.active && generation === loadGeneration;
    const base = `/api/plugins/${encodeURIComponent(id.value)}`;
    const read = <T,>(path: string, message: string) => requests.run<T>(
      `${base}/${path}`, {}, (response) => {
        if (!response.ok) throw new Error(message);
        return response.json();
      },
    );
    async function loadSection<T>(
      key: keyof typeof sections,
      readData: () => Promise<T>,
      apply: (data: T) => void,
    ) {
      if (sectionKey && key !== sectionKey) return;
      const section = sections[key];
      section.loading = true;
      try {
        const data = await readData();
        if (!active()) return;
        apply(data);
        section.loaded = true;
        section.error = '';
      } catch (cause) {
        if (active() && !isAbortError(cause))
          section.error = userErrorMessage(cause, '无法加载插件内容');
      } finally {
        if (active()) section.loading = false;
      }
    }
    await Promise.all([
      loadSection('status', () => pluginRunning.value && current.manifest?.status_ui
        ? read<Record<string, unknown>>('status', '无法加载状态')
        : Promise.resolve({}), (state) => {
        Object.keys(status).forEach((key) => delete status[key]);
        Object.assign(status, state);
        if (pluginRunning.value && current.manifest?.status_ui) connectStateEvents();
      }),
      loadSection('features', () => pluginRunning.value && current.manifest?.features_ui
        ? read<{ features?: PluginFeatureDefinition[] }>('features', '无法加载功能')
        : Promise.resolve({ features: [] }), (declared) => {
        features.value = declared.features ?? [];
      }),
      loadSection('settings', async () => {
        if (!current.manifest?.settings_ui) return { configuration: {}, definitions: [] };
        const display = await read<PluginSettingsDisplaySettings>(
          'settings/display/configuration', '读取插件设置失败',
        );
        if (!active()) throw new DOMException('请求已取消', 'AbortError');
        settingsDisplay.value = display;
        if (!display.registerOnHome) return { configuration: {}, definitions: [] };
        const [configuration, definitions] = await Promise.all([
          read<Record<string, unknown>>('configuration', '读取插件设置失败'),
          read<unknown[]>('configuration/schema', '读取插件设置失败'),
        ]);
        return { configuration, definitions };
      }, (data) => {
        Object.keys(config).forEach((key) => delete config[key]);
        Object.assign(config, data.configuration);
        definitions.value = data.definitions.filter(
          (value) => isPluginSetting(value) || isPluginCollection(value),
        );
        settingsLoaded.value = true;
      }),
    ]);
    if (active()) hasLoaded.value = true;
  } catch (cause) {
    if (requests.active && generation === loadGeneration && !isAbortError(cause))
      error.value = userErrorMessage(cause, '无法加载插件主页');
  } finally {
    if (generation === loadGeneration) loading.value = false;
  }
}

function retryLoad(sectionKey?: keyof typeof sections) {
  if (busy.value) return;
  return load(sectionKey);
}

function connectStateEvents() {
  stateEvents?.close();
  stateEvents = new EventSource(`/api/plugins/${encodeURIComponent(id.value)}/events`);
  const connection = stateEvents;
  stateEvents.onmessage = (event) => {
    if (!requests.active || connection !== stateEvents) return;
    try {
      Object.keys(status).forEach((key) => delete status[key]);
      Object.assign(status, JSON.parse(event.data));
    } catch {}
  };
}

async function toggle() {
  if (busy.value) return;
  const current = plugin.value;
  if (!current) return;
  if (!current.canChangeEnabled) return;
  const failureMessage = current.enabled ? '插件禁用失败' : '插件启用失败';
  toggling.value = true;
  loadGeneration++;
  requests.cancelReads();
  try {
    const updated = await requests.run<PluginInfo>(
      `/api/plugins/${encodeURIComponent(id.value)}/${current.enabled ? 'disable' : 'enable'}`,
      { method: 'POST' },
      (response) => {
        if (!response.ok) throw new Error(failureMessage);
        return response.json();
      },
    );
    if (!requests.active || id.value !== current.id) return;
    plugin.value = updated;
    await load();
    if (!requests.active || id.value !== current.id) return;
    if (updated.state === 'Failed' || updated.state === 'Invalid')
      showToast(failureMessage, 'error');
    else showToast(`插件已${updated.enabled ? '启用' : '禁用'}`, 'success');
  } catch (cause) {
    if (
      requests.active &&
      id.value === current.id &&
      !isAbortError(cause)
    )
      showToast(failureMessage, 'error');
  } finally {
    toggling.value = false;
  }
}

async function saveSettings() {
  if (safeModeEnabled.value || busy.value || !settingsLoaded.value) return;
  const validationError = configurationError(definitions.value, config);
  if (validationError) {
    showToast(validationError, 'error');
    return;
  }
  saving.value = true;
  loadGeneration++;
  requests.cancelReads();
  try {
    await requests.run(
      `/api/plugins/${encodeURIComponent(id.value)}/configuration`,
      {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(config),
      },
      async (response) => {
        if (!response.ok) throw new Error(await readApiError(response, '配置保存失败，请重试'));
      },
    );
    showToast('配置已保存', 'success');
    await load();
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(userErrorMessage(cause, '配置保存失败，请重试'), 'error');
  } finally {
    saving.value = false;
  }
}

async function executeFeature(feature: PluginFeatureDefinition) {
  if (feature.mode !== 'action' || busy.value) return;
  executingFeatureId.value = feature.id;
  try {
    const result = await requests.run<PluginFeatureExecutionResult>(
      `/api/plugins/${encodeURIComponent(id.value)}/features/${encodeURIComponent(feature.id)}/execute`,
      { method: 'POST' },
      (response) => {
        if (!response.ok) throw new Error('执行功能失败');
        return response.json();
      },
    );
    showToast(result.message, result.succeeded ? 'success' : 'error');
    return result.succeeded;
  } catch (cause) {
    if (requests.active && !isAbortError(cause))
      showToast(userErrorMessage(cause, '执行功能失败'), 'error');
  } finally {
    executingFeatureId.value = '';
  }
}

function beginFeature(feature: PluginFeatureDefinition) {
  if (feature.mode === 'page') {
    sessionStorage.setItem(
      'winpilot-plugin-detail-scroll',
      JSON.stringify({ path: route.fullPath, y: getPageScroller().scrollTop }),
    );
    sessionStorage.setItem('winpilot-plugin-page-source', 'plugin-detail');
    void router.push(
      `/plugins/${encodeURIComponent(id.value)}/features/${encodeURIComponent(feature.id)}`,
    );
    return;
  }
  if (feature.mode !== 'action' || busy.value) return;
  if (feature.confirm === false) void executeFeature(feature);
  else confirmingFeatureModal.open(feature);
}

async function confirmFeature() {
  const feature = confirmingFeature.value;
  if (!feature || executingFeatureId.value) return;
  if (await executeFeature(feature)) confirmingFeatureModal.close();
}

onMounted(async () => {
  isPageActive = true;
  sessionStorage.removeItem('winpilot-plugin-detail-restore');
  try {
    await load();
    if (!isPageActive) return;
    if (restoreTarget) {
      await nextTick();
      if (isPageActive) getPageScroller().scrollTo({ top: restoreTarget.y, behavior: 'auto' });
    }
  } finally {
    restoringScroll.value = false;
  }
});
watch(id, async () => {
  requests.cancelAll();
  error.value = '';
  plugin.value = undefined;
  hasLoaded.value = false;
  settingsLoaded.value = false;
  settingsDisplay.value = { registerOnHome: false };
  for (const section of Object.values(sections)) {
    section.loading = false;
    section.loaded = false;
    section.error = '';
  }
  features.value = [];
  Object.keys(status).forEach((key) => delete status[key]);
  definitions.value = [];
  confirmingFeatureModal.close();
  await load();
});
watch(
  safeModeEnabled,
  () => {
    Object.keys(config).forEach((key) => delete config[key]);
    definitions.value = [];
    settingsLoaded.value = false;
    void load();
  },
  { flush: 'sync' },
);
onBeforeUnmount(() => {
  requests.dispose();
  loadGeneration++;
  isPageActive = false;
  stateEvents?.close();
});
</script>

<template>
  <section class="plugin-home-view load-error-layout" :style="restoringScroll ? { visibility: 'hidden' } : undefined">
    <AppLoadError :show="Boolean(error)" :disabled="busy" :retry="() => retryLoad()">{{ error }}</AppLoadError>
    <NCard v-if="error && !plugin"><NEmpty :show-icon="false" description="无法加载插件内容" /></NCard>
    <AppLoading v-if="loading && !hasLoaded && !error" label="正在加载插件" />
    <template v-if="plugin && hasLoaded">
      <PluginHeader :plugin="plugin" :changing="busy" @toggle="toggle" />
      <NCard tag="section" v-if="plugin.manifest?.status_ui" content-class="plugin-module">
        <div class="plugin-module-heading"><NText tag="h3" :depth="1">状态</NText></div>
        <AppLoadError :show="Boolean(sections.status.error)" :disabled="busy" :retry="() => retryLoad('status')">{{ sections.status.error }}</AppLoadError>
        <NText tag="p" :depth="3" v-if="!pluginRunning" class="plugin-empty">
          {{ plugin.enabled ? '插件未加载' : '插件未启用' }}
        </NText>
        <AppLoading v-else-if="sections.status.loading && !sections.status.loaded" label="正在加载状态" />
        <dl v-else-if="Object.keys(status).length" class="plugin-status-list">
          <div v-for="(value, key) in status" :key="key" class="plugin-status-row">
            <dt>{{ key }}</dt>
            <dd>{{ displayStatusValue(value, String(key)) }}</dd>
          </div>
        </dl>
        <NText tag="p" :depth="3" v-else-if="!sections.status.error" class="plugin-empty">暂无状态数据</NText>
      </NCard>
      <NCard tag="section" v-if="plugin.manifest?.features_ui" content-class="plugin-module">
        <div class="plugin-module-heading"><NText tag="h3" :depth="1">功能</NText></div>
        <AppLoadError :show="Boolean(sections.features.error)" :disabled="busy" :retry="() => retryLoad('features')">{{ sections.features.error }}</AppLoadError>
        <NText tag="p" :depth="3" v-if="!pluginRunning" class="plugin-empty">
          {{ plugin.enabled ? '插件未加载' : '插件未启用' }}
        </NText>
        <AppLoading v-else-if="sections.features.loading && !sections.features.loaded" label="正在加载功能" />
        <div v-else-if="features.length" class="plugin-features-list">
          <NCard
            tag="article"
            v-for="feature in features"
            :key="feature.id"
            content-class="plugin-feature-item"
            embedded
            size="small"
          >
            <div>
              <h4>{{ feature.name }}</h4>
              <p v-if="feature.description">{{ feature.description }}</p>
            </div>
            <div class="plugin-feature-controls">
              <NButton
                :loading="executingFeatureId === feature.id"
                attr-type="button"
                :aria-label="`${featureButtonLabel(feature)}${feature.name}`"
                :disabled="feature.mode === 'action' && busy"
                @click="beginFeature(feature)"
              >
                {{ executingFeatureId === feature.id ? '正在执行' : featureButtonLabel(feature) }}
              </NButton>
            </div>
          </NCard>
        </div>
        <NText tag="p" :depth="3" v-else-if="!sections.features.error" class="plugin-empty">暂无功能声明</NText>
      </NCard>
      <NCard
        tag="section"
        v-if="plugin.manifest?.settings_ui && (sections.settings.error || (settingsDisplay.registerOnHome && (sections.settings.loading || definitions.length)))"
        content-class="plugin-module"
        :class="{ 'safe-mode-locked': safeModeEnabled }"
        :inert="safeModeEnabled"
      >
        <div class="plugin-module-heading">
          <NText tag="h3" :depth="1">
            配置
          </NText>
          <NButton
            :loading="saving"
            attr-type="button"
            :disabled="busy || !settingsLoaded || safeModeEnabled"
            @click="saveSettings"
            type="primary"
          >
            {{ saving ? '正在保存' : '保存配置' }}
          </NButton>
        </div>
        <AppLoadError :show="Boolean(sections.settings.error)" :disabled="busy" :retry="() => retryLoad('settings')">{{ sections.settings.error }}</AppLoadError>
        <AppLoading v-if="sections.settings.loading && !settingsLoaded" label="正在读取插件设置" />
        <div v-else class="plugin-settings-fields" :inert="busy">
          <template v-for="setting in definitions" :key="setting.name">
            <PluginSettingField
            v-if="isPluginSetting(setting)"
              :field="setting"
              v-model="config[setting.name]"
              :field-id="`setting-${setting.name}`"
            />
            <PluginSettingField v-else :field="setting" v-model="config[setting.name]" />
          </template>
        </div>
      </NCard>
      <AppModal
        :show="confirmingFeatureModal.show"
        @after-leave="confirmingFeatureModal.afterLeave"
        v-if="confirmingFeature"
        title="提示"
        :dismissible="!executingFeatureId"
        @close="confirmingFeatureModal.close()"
      >
        <p>确定要执行“{{ confirmingFeature.name }}”吗？</p>
        <template #actions>
          <NButton
            attr-type="button"
            :disabled="Boolean(executingFeatureId)"
            @click="confirmingFeatureModal.close()"
          >
            取消
          </NButton>
          <NButton
            :loading="Boolean(executingFeatureId)"
            attr-type="button"
            :disabled="Boolean(executingFeatureId)"
            @click="confirmFeature"
            type="primary"
          >
            {{ executingFeatureId ? '正在执行' : '确认执行' }}
          </NButton>
        </template>
      </AppModal>
    </template>
  </section>
</template>

<style>
.plugin-home-view { display: grid; grid-template-columns: minmax(0, 1fr); min-width: 0; width: 100%; max-width: none; margin: 0 auto; gap: var(--page-card-row-gap) 18px; }
.plugin-module { display: grid; gap: 18px; }
.plugin-module-heading, .plugin-status-list, .plugin-status-row { transition: border-color .3s cubic-bezier(.4, 0, .2, 1); }
.plugin-module-heading { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding-bottom: 14px; border-bottom: 1px solid var(--border); }
.plugin-module-heading h3 { margin: 0; font-size: 16px; letter-spacing: -0.01em; }
.plugin-status-list { display: grid; margin: 0; border-top: 1px solid var(--border); }
.plugin-status-row { display: grid; grid-template-columns: minmax(160px, .3fr) minmax(0, 1fr); gap: 18px; padding: 13px 0; border-bottom: 1px solid var(--border); }
.plugin-status-row dt, .plugin-status-row dd { transition: color .3s cubic-bezier(.4, 0, .2, 1); }
.plugin-status-row dt { min-width: 0; color: var(--text-muted); font-size: 13px; overflow-wrap: anywhere; }
.plugin-status-row dd { min-width: 0; margin: 0; color: var(--text); font-size: 14px; line-height: 1.5; overflow-wrap: anywhere; white-space: pre-wrap; }
@media (max-width: 640px) {
  .plugin-module-heading { align-items: center; flex-direction: row; }
  .plugin-status-row { grid-template-columns: minmax(112px, .4fr) minmax(0, 1fr); gap: 12px; }
}
@media (max-width: 420px) {
  .plugin-status-row { grid-template-columns: 1fr; gap: 4px; }
}
</style>
