<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { getPageScroller } from '../../ui/scrollLock';
import { readApiError, isAbortError, userErrorMessage, WinPilotConnectionError } from '../../api/client';
import { createPluginRequests } from './PluginRequests';
import { showToast } from '../../ui/toast';
import AppModal from '../../components/AppModal.vue';
import type { PluginInfo, PluginSettingsDisplaySettings } from './PluginModels';
import PluginHeader from './PluginHeader.vue';
import PluginSettingField from './PluginSettingField.vue';
import { isPluginSetting as isDefinition, isPluginCollection as isCollection, type PluginCollectionDefinition as CollectionDefinition, type PluginSettingDefinition as SettingDefinition, type PluginFeatureDefinition } from './PluginModels';
import AppIcon from '../../components/AppIcon.vue';
import { safeModeEnabled, safeModeMessage } from '../security/safeMode';

type Definition = SettingDefinition | CollectionDefinition;

type PluginFeatureExecutionResult = { succeeded: boolean; message: string };

const requests = createPluginRequests();
const route = useRoute();
const router = useRouter();
const id = computed(() => String(route.params.pluginId ?? ''));
const plugin = ref<PluginInfo>();
const status = reactive<Record<string, unknown>>({});
const config = reactive<Record<string, any>>({});
const definitions = ref<Definition[]>([]);
const features = ref<PluginFeatureDefinition[]>([]);
const settingsDisplay = ref<PluginSettingsDisplaySettings>({ registerOnHome: true });
const loading = ref(false);
const error = ref('');
const saving = ref(false);
const toggling = ref(false);
const settingsLoaded = ref(false);
let loadGeneration = 0;
const executingFeatureId = ref('');
const confirmingFeature = ref<PluginFeatureDefinition>();
let restoreTarget: { path: string; y: number } | undefined;
try {
  const saved = JSON.parse(sessionStorage.getItem('winpilot-plugin-detail-restore') ?? 'null');
  if (saved?.path === route.fullPath && Number.isFinite(saved.y)) restoreTarget = saved;
} catch {
}
const restoringScroll = ref(Boolean(restoreTarget && restoreTarget.y > 0));
let stateEvents: EventSource | undefined;
let isPageActive = false;
const pluginRunning = computed(() => plugin.value?.enabled === true && plugin.value.state === 'Running');
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

async function load() {
  const generation = ++loadGeneration;
  stateEvents?.close();
  stateEvents = undefined;
  requests.cancelReads();
  loading.value = true;
  error.value = '';
  try {
    const current = await requests.run<PluginInfo>(`/api/plugins/${encodeURIComponent(id.value)}?waitForLifecycle=true`, { cache: 'no-store' }, response => {
      if (!response.ok) throw new Error('无法加载插件');
      return response.json();
    });
    if (!requests.active || generation !== loadGeneration) return;
    plugin.value = current;
    const isRunning = pluginRunning.value;
    const [state, savedConfig, rawDefinitions, display, declaredFeatures] =
      await Promise.all([
        isRunning && plugin.value.manifest?.status_ui
          ? requests.run<Record<string, unknown> | undefined>(`/api/plugins/${encodeURIComponent(id.value)}/status`, {}, response => response.ok ? response.json() : undefined)
          : Promise.resolve(undefined),
        requests.run<Record<string, unknown>>(`/api/plugins/${encodeURIComponent(id.value)}/configuration`, {}, response => {
          if (!response.ok) throw new Error('读取插件设置失败');
          return response.json();
        }),
        requests.run<unknown[]>(`/api/plugins/${encodeURIComponent(id.value)}/configuration/schema`, {}, response => {
          if (!response.ok) throw new Error('读取插件设置失败');
          return response.json();
        }),
        requests.run<PluginSettingsDisplaySettings>(`/api/plugins/${encodeURIComponent(id.value)}/settings/display/configuration`, {}, response => {
          if (!response.ok) throw new Error('读取插件设置失败');
          return response.json();
        }),
        isRunning && plugin.value.manifest?.features_ui
          ? requests.run<{ features?: PluginFeatureDefinition[] } | undefined>(`/api/plugins/${encodeURIComponent(id.value)}/features`, {}, response => response.ok ? response.json() : undefined)
          : Promise.resolve(undefined),
      ]);
    if (!requests.active || generation !== loadGeneration) return;
    Object.keys(status).forEach((key) => delete status[key]);
    if (state) Object.assign(status, state);
    Object.keys(config).forEach(key => delete config[key]);
    Object.assign(config, savedConfig);
    definitions.value = rawDefinitions.filter(value => isDefinition(value) || isCollection(value));
    settingsDisplay.value = display;
    features.value = declaredFeatures?.features ?? [];
    settingsLoaded.value = true;
    if (pluginRunning.value) connectStateEvents();
  } catch (cause) {
    if (requests.active && generation === loadGeneration && !isAbortError(cause)) error.value = userErrorMessage(cause, '无法加载插件主页');
  } finally {
    if (generation === loadGeneration) loading.value = false;
  }
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
    } catch {
    }
  };
}

async function toggle() {
  if (toggling.value) return;
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
      response => {
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
    if (requests.active && id.value === current.id && !isAbortError(cause) && !(cause instanceof WinPilotConnectionError))
      showToast(failureMessage, 'error');
  }
  finally { toggling.value = false; }
}

async function saveSettings() {
  if (safeModeEnabled.value || saving.value || loading.value || !settingsLoaded.value) return;
  const invalid = definitions.value.find(
    (setting) =>
      isDefinition(setting) &&
      setting.type !== 'number' &&
      setting.required &&
      (config[setting.name] === '' || config[setting.name] === undefined),
  ) as SettingDefinition | undefined;
  if (invalid) {
    showToast(`${invalid.label || invalid.name}不能为空`, 'error');
    return;
  }
  const invalidCollection = definitions.value
    .filter(isCollection)
    .flatMap((collection) => {
      if (collection.mode !== 'dynamic' || !collection.item) return [];
      if (!Array.isArray(config[collection.name])) return [];
      if (
        config[collection.name].some(
          (item: Record<string, any>) =>
            typeof item.__winpilot?.remark !== 'string' || !item.__winpilot.remark.trim(),
        )
      )
        return [{ name: '__winpilot.remark' }];
      const fields = collection.item.fields;
      return config[collection.name]
        .map((item: Record<string, any>) =>
          fields.find(
            (field) =>
              field.type !== 'number' &&
              field.required &&
              (item[field.name] === undefined ||
                item[field.name] === null ||
                item[field.name] === '' ||
                (Array.isArray(item[field.name]) && item[field.name].length === 0)),
          ),
        )
        .filter(Boolean);
    })
    .find(Boolean);
  if (invalidCollection) {
    showToast(
      invalidCollection.name === '__winpilot.remark' ? '备注不能为空' : `${invalidCollection.label || invalidCollection.name}不能为空`,
      'error',
    );
    return;
  }
  saving.value = true;
  loadGeneration++;
  requests.cancelReads();
  try {
    await requests.run(`/api/plugins/${encodeURIComponent(id.value)}/configuration`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(config),
    }, async response => {
      if (!response.ok) throw new Error(await readApiError(response, '配置保存失败，请重试'));
    });
    showToast('配置已保存', 'success');
    await load();
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '配置保存失败，请重试'), 'error');
  } finally {
    saving.value = false;
  }
}

async function executeFeature(feature: PluginFeatureDefinition) {
  if (feature.mode !== 'action' || executingFeatureId.value) return;
  executingFeatureId.value = feature.id;
  try {
    const result = await requests.run<PluginFeatureExecutionResult>(
      `/api/plugins/${encodeURIComponent(id.value)}/features/${encodeURIComponent(feature.id)}/execute`,
      { method: 'POST' },
      response => {
        if (!response.ok) throw new Error('执行功能失败');
        return response.json();
      },
    );
    showToast(result.message, result.succeeded ? 'success' : 'error');
    return result.succeeded;
  } catch (cause) {
    if (requests.active && !isAbortError(cause)) showToast(userErrorMessage(cause, '执行功能失败'), 'error');
  } finally {
    executingFeatureId.value = '';
  }
}

function beginFeature(feature: PluginFeatureDefinition) {
  if (feature.mode === 'page') {
    sessionStorage.setItem('winpilot-plugin-detail-scroll', JSON.stringify({ path: route.fullPath, y: getPageScroller().scrollTop }));
    sessionStorage.setItem('winpilot-plugin-page-source', 'plugin-detail');
    void router.push(`/plugins/${encodeURIComponent(id.value)}/features/${encodeURIComponent(feature.id)}`);
    return;
  }
  if (feature.mode !== 'action') return;
  if (feature.confirm === false) void executeFeature(feature);
  else confirmingFeature.value = feature;
}

async function confirmFeature() {
  const feature = confirmingFeature.value;
  if (!feature || executingFeatureId.value) return;
  if (await executeFeature(feature)) confirmingFeature.value = undefined;
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
  plugin.value = undefined;
  settingsLoaded.value = false;
  features.value = [];
  Object.keys(status).forEach((key) => delete status[key]);
  definitions.value = [];
  confirmingFeature.value = undefined;
  await load();
});
watch(safeModeEnabled, () => {
  Object.keys(config).forEach(key => delete config[key]);
  definitions.value = [];
  settingsLoaded.value = false;
  void load();
}, { flush: 'sync' });
onBeforeUnmount(() => {
  requests.dispose();
  loadGeneration++;
  isPageActive = false;
  stateEvents?.close();
});
</script>

<template>
  <section class="plugin-home-view" :style="restoringScroll ? { visibility: 'hidden' } : undefined">
    <p v-if="error" class="status-message error">{{ error }}</p>
    <p v-if="loading && !plugin" class="plugin-empty loading-hint">正在加载插件…</p>
    <template v-else-if="plugin">
      <PluginHeader :plugin="plugin" :changing="toggling" @toggle="toggle" />
      <section v-if="plugin.manifest?.status_ui" class="plugin-module">
        <div class="plugin-module-heading"><h3>状态</h3></div>
        <p v-if="!pluginRunning" class="plugin-empty">{{ plugin.enabled ? '插件未加载' : '插件未启用' }}</p>
        <dl v-else-if="Object.keys(status).length" class="plugin-status-list">
          <div v-for="(value, key) in status" :key="key" class="plugin-status-row">
            <dt>{{ key }}</dt>
            <dd>{{ displayStatusValue(value, String(key)) }}</dd>
          </div>
        </dl>
        <p v-else-if="settingsLoaded" class="plugin-empty">暂无状态数据</p>
      </section>
      <section v-if="plugin.manifest?.features_ui" class="plugin-module">
        <div class="plugin-module-heading"><h3>功能</h3></div>
        <p v-if="!pluginRunning" class="plugin-empty">{{ plugin.enabled ? '插件未加载' : '插件未启用' }}</p>
        <div v-else-if="features.length" class="plugin-features-list">
          <article v-for="feature in features" :key="feature.id" class="plugin-feature-item">
            <div>
              <h4>{{ feature.name }}</h4>
              <p v-if="feature.description">{{ feature.description }}</p>
            </div>
            <div class="plugin-feature-controls">
              <button
                class="button button-secondary"
                type="button"
                :aria-label="`${featureButtonLabel(feature)}${feature.name}`"
                :disabled="executingFeatureId === feature.id"
                @click="beginFeature(feature)"
              >
                {{ executingFeatureId === feature.id ? '正在打开…' : featureButtonLabel(feature) }}
              </button>
            </div>
          </article>
        </div>
        <p v-else-if="settingsLoaded" class="plugin-empty">暂无功能声明</p>
      </section>
      <section
        v-if="plugin.manifest?.settings_ui && settingsDisplay.registerOnHome && definitions.length"
        class="plugin-module"
        :class="{ 'safe-mode-locked': safeModeEnabled }"
        :inert="safeModeEnabled"
      >
        <div class="plugin-module-heading">
          <h3>
            配置
            <span v-if="safeModeEnabled" class="safe-mode-lock-icon" :title="safeModeMessage">
              <AppIcon name="lock" />
            </span>
          </h3>
          <button
            class="button button-primary"
            type="button"
            :disabled="saving || loading || !settingsLoaded || safeModeEnabled"
            @click="saveSettings"
          >
            {{ saving ? '正在保存…' : '保存配置' }}
          </button>
        </div>
        <div class="plugin-settings-fields">
          <template v-for="setting in definitions" :key="setting.name">
            <PluginSettingField
              v-if="isDefinition(setting)"
              :field="setting"
              v-model="config[setting.name]"
              :field-id="`setting-${setting.name}`"
            />
            <PluginSettingField v-else :field="setting" v-model="config[setting.name]" />
          </template>
        </div>
      </section>
      <AppModal
        v-if="confirmingFeature"
        title="提示"
        :dismiss-on-backdrop="!executingFeatureId"
        @close="confirmingFeature = undefined"
      >
        <p>确定要执行“{{ confirmingFeature.name }}”吗？</p>
        <template #actions>
          <button
            class="button button-secondary"
            type="button"
            :disabled="Boolean(executingFeatureId)"
            @click="confirmingFeature = undefined"
          >
            取消
          </button>
          <button
            class="button button-primary"
            type="button"
            :disabled="Boolean(executingFeatureId)"
            @click="confirmFeature"
          >
            {{ executingFeatureId ? '正在执行…' : '确认执行' }}
          </button>
        </template>
      </AppModal>
    </template>
  </section>
</template>
